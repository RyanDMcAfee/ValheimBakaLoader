// BakaLoader Commander v1.0.0 - native RCON server + command suite for BakaLoader.
//
// WHY THIS EXISTS:
// BakaLoader historically depended on THREE third-party mods for remote control:
//   AviiNL-RCON              - the Source-RCON TCP listener
//   JereKuusela-Server_devcommands - enables devcommands on dedicated servers
//   JereKuusela-Rcon_Commands      - routes RCON text into the Terminal
// Any of those going stale on a game update deprecates BakaLoader's remote features.
// Commander replaces all three with a single first-party companion plugin: it hosts
// its own Source-RCON listener and implements every command BakaLoader sends
// NATIVELY against the game API (no Terminal, no devcommands requirement).
//
// WIRE COMPATIBILITY (must match BakaLoader's Tools/RconClient.cs exactly):
//   - Source RCON packet frame: [int32 length][int32 id][int32 type][ASCII body][0][0],
//     little-endian. Client sanity-checks 10 <= length <= 4110, so response bodies are
//     split into chunks of <= 4000 bytes.
//   - The client opens a FRESH connection per request and drains packets until the
//     socket closes - so Commander answers exactly one request per connection and
//     then closes it (same externally-visible behavior as AviiNL-RCON).
//   - EXEC (type 2) requests arrive WITHOUT prior auth on the same connection
//     (the client only authenticates during its initial validation handshake), so
//     the password only gates the validation handshake - bind to 127.0.0.1 (the
//     default) unless you understand the exposure.
//   - AUTH (type 3): success => reply id echo + "Login Success"; failure => id -1 +
//     "Login Failed" (client detects failure via "fail" substring / id -1).
//
// COMMANDS (exact strings BakaLoader's ValheimServer.cs builders send):
//   broadcast center <message>       - HUD message to everyone (routed "ShowMessage")
//   playerlist                       - "{name}/{host}/{charId} (x, z, y)" per player
//   dmg <player> <amount>            - negative amount heals (RPC_Heal), positive
//                                      damages (RPC_Damage); +/-1000000 = heal/smite
//   tp <player> <dest>               - dest is "x,z,y" coords or another player name
//   kick <player>                    - ZNet.Kick (name / host id)
//   baka_spawn <prefab> <x,z,y> [amount] [level] - main-thread spawn (absorbed from
//                                      BakaLoaderSpawnHelper)
//   baka_killall                     - kill all non-player characters (absorbed from
//                                      BakaKillAll)
//   anything else                    - forwarded to the in-game console if present
//
// All game work is dispatched to the Unity main thread via a queue drained in
// Update() - Object.Instantiate()/game API calls from the socket thread crash
// headless servers ("Graphics device is null").
//
// COEXISTENCE: if AviiNL-RCON is still installed, whichever plugin binds the RCON
// port first serves it; both speak the same wire protocol and command set, so either
// outcome works. The loser logs a warning and stays dormant (no crash, no retries).

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;

namespace BakaLoaderCommander
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class CommanderPlugin : BaseUnityPlugin
    {
        private const string PluginGuid = "com.baka.commander";
        private const string PluginName = "BakaLoader Commander";
        private const string PluginVersion = "1.0.0";

        // Source RCON packet types
        private const int TypeAuth = 3;          // SERVERDATA_AUTH
        private const int TypeAuthResponse = 2;  // SERVERDATA_AUTH_RESPONSE
        private const int TypeExec = 2;          // SERVERDATA_EXECCOMMAND
        private const int TypeResponse = 0;      // SERVERDATA_RESPONSE_VALUE

        private const int MaxResponseBodyBytes = 4000; // client rejects frames > 4110 total
        private const int CommandTimeoutMs = 4500;     // client gives up at 5000
        private const int IoTimeoutMs = 5000;

        private static ManualLogSource Log;

        private ConfigEntry<bool> CfgEnabled;
        private ConfigEntry<int> CfgPort;
        private ConfigEntry<string> CfgPassword;
        private ConfigEntry<string> CfgBindAddress;

        private TcpListener _listener;
        private Thread _acceptThread;
        private volatile bool _running;

        private sealed class PendingCommand
        {
            public string Text;
            public string Response;
            public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
        }

        private static readonly ConcurrentQueue<PendingCommand> Pending = new ConcurrentQueue<PendingCommand>();

        // ------------------------------------------------------------------
        //  Lifecycle
        // ------------------------------------------------------------------

        private void Awake()
        {
            Log = Logger;

            CfgEnabled = Config.Bind("Server", "Enabled", true,
                "Enable the built-in RCON server.");
            CfgPort = Config.Bind("Server", "Port", 25575,
                "TCP port the RCON server listens on. Must not be the game port.");
            CfgPassword = Config.Bind("Server", "Password", "",
                "RCON password. Only gates the AUTH handshake (see plugin header); keep the bind address on loopback.");
            CfgBindAddress = Config.Bind("Server", "BindAddress", "127.0.0.1",
                "Address to listen on. 127.0.0.1 (default) = local only; 0.0.0.0 = all interfaces (NOT recommended).");

            if (!CfgEnabled.Value)
            {
                Log.LogInfo("BakaLoader Commander loaded but disabled by config.");
                return;
            }

            StartListener();
        }

        private void OnDestroy()
        {
            StopListener();
        }

        private void StartListener()
        {
            IPAddress addr;
            if (!IPAddress.TryParse(CfgBindAddress.Value, out addr))
            {
                Log.LogWarning("Invalid BindAddress '" + CfgBindAddress.Value + "' - falling back to 127.0.0.1");
                addr = IPAddress.Loopback;
            }

            try
            {
                _listener = new TcpListener(addr, CfgPort.Value);
                _listener.Start();
            }
            catch (SocketException ex)
            {
                // Another RCON server (e.g. AviiNL-RCON) already owns the port. That's a
                // supported coexistence state - it speaks the same protocol and command
                // set, so BakaLoader still works. Stay dormant.
                Log.LogWarning("Could not bind RCON port " + CfgPort.Value + " (" + ex.SocketErrorCode +
                               ") - another RCON server likely owns it. Commander staying dormant.");
                _listener = null;
                return;
            }

            _running = true;
            _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "BakaCommanderAccept" };
            _acceptThread.Start();

            Log.LogInfo("BakaLoader Commander v" + PluginVersion + " listening on " + addr + ":" + CfgPort.Value);
        }

        private void StopListener()
        {
            _running = false;
            try { _listener?.Stop(); } catch { }
            _listener = null;
        }

        // ------------------------------------------------------------------
        //  Socket side (background threads)
        // ------------------------------------------------------------------

        private void AcceptLoop()
        {
            while (_running)
            {
                TcpClient client;
                try
                {
                    client = _listener.AcceptTcpClient();
                }
                catch
                {
                    // Listener stopped or faulted - exit the loop.
                    return;
                }

                ThreadPool.QueueUserWorkItem(_ => HandleConnection(client));
            }
        }

        private void HandleConnection(TcpClient client)
        {
            try
            {
                client.ReceiveTimeout = IoTimeoutMs;
                client.SendTimeout = IoTimeoutMs;
                client.NoDelay = true;

                using (var stream = client.GetStream())
                {
                    int id, type;
                    string body;
                    if (!ReadPacket(stream, out id, out type, out body))
                        return;

                    if (type == TypeAuth)
                    {
                        var pw = CfgPassword.Value ?? "";
                        var ok = pw.Length == 0 || string.Equals(pw, body ?? "", StringComparison.Ordinal);
                        if (ok)
                            WritePacket(stream, id, TypeAuthResponse, "Login Success");
                        else
                            WritePacket(stream, -1, TypeAuthResponse, "Login Failed");
                        return; // connection closes via using
                    }

                    if (type == TypeExec)
                    {
                        var response = DispatchToMainThread(body);
                        WriteResponse(stream, id, response);
                        return;
                    }

                    WritePacket(stream, id, TypeResponse, "Error: unsupported packet type " + type);
                }
            }
            catch (Exception ex)
            {
                try { Log.LogDebug("RCON connection error: " + ex.Message); } catch { }
            }
            finally
            {
                try { client.Close(); } catch { }
            }
        }

        private static string DispatchToMainThread(string text)
        {
            var cmd = new PendingCommand { Text = text ?? "" };
            Pending.Enqueue(cmd);

            if (!cmd.Done.Wait(CommandTimeoutMs))
                return "Error: command timed out (server main thread busy)";

            return cmd.Response ?? "";
        }

        private static bool ReadPacket(NetworkStream stream, out int id, out int type, out string body)
        {
            id = 0; type = 0; body = null;

            var lenBuf = ReadExact(stream, 4);
            if (lenBuf == null) return false;
            var length = BitConverter.ToInt32(lenBuf, 0);
            if (length < 10 || length > 65536) return false;

            var payload = ReadExact(stream, length);
            if (payload == null) return false;

            id = BitConverter.ToInt32(payload, 0);
            type = BitConverter.ToInt32(payload, 4);
            body = Encoding.ASCII.GetString(payload, 8, length - 10); // strip 2 null terminators
            return true;
        }

        private static byte[] ReadExact(NetworkStream stream, int count)
        {
            var buf = new byte[count];
            var read = 0;
            while (read < count)
            {
                int n;
                try { n = stream.Read(buf, read, count - read); }
                catch { return null; }
                if (n <= 0) return null;
                read += n;
            }
            return buf;
        }

        private static void WritePacket(NetworkStream stream, int id, int type, string body)
        {
            var bodyBytes = Encoding.ASCII.GetBytes(body ?? "");
            var length = 4 + 4 + bodyBytes.Length + 2;
            var packet = new byte[4 + length];

            Buffer.BlockCopy(BitConverter.GetBytes(length), 0, packet, 0, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(id), 0, packet, 4, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(type), 0, packet, 8, 4);
            Buffer.BlockCopy(bodyBytes, 0, packet, 12, bodyBytes.Length);
            // last two bytes stay 0 (body terminator + packet terminator)

            stream.Write(packet, 0, packet.Length);
            stream.Flush();
        }

        /// <summary>Splits large responses across multiple packets (client drains until close).</summary>
        private static void WriteResponse(NetworkStream stream, int id, string response)
        {
            var bytes = Encoding.ASCII.GetBytes(response ?? "");
            if (bytes.Length == 0)
            {
                WritePacket(stream, id, TypeResponse, "");
                return;
            }

            for (var offset = 0; offset < bytes.Length; offset += MaxResponseBodyBytes)
            {
                var chunkLen = Math.Min(MaxResponseBodyBytes, bytes.Length - offset);
                WritePacket(stream, id, TypeResponse, Encoding.ASCII.GetString(bytes, offset, chunkLen));
            }
        }

        // ------------------------------------------------------------------
        //  Main-thread command execution
        // ------------------------------------------------------------------

        private void Update()
        {
            PendingCommand cmd;
            while (Pending.TryDequeue(out cmd))
            {
                try
                {
                    cmd.Response = ExecuteCommand(cmd.Text);
                }
                catch (Exception ex)
                {
                    cmd.Response = "Error: " + ex.Message;
                    Log.LogError("Command '" + cmd.Text + "' failed: " + ex);
                }
                finally
                {
                    cmd.Done.Set();
                }
            }
        }

        private string ExecuteCommand(string text)
        {
            text = (text ?? "").Trim();
            if (text.Length == 0) return "";

            var tokens = text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var verb = tokens[0].ToLowerInvariant();

            switch (verb)
            {
                case "broadcast": return CmdBroadcast(tokens, text);
                case "playerlist": return CmdPlayerList();
                case "dmg": return CmdDamage(tokens);
                case "tp": return CmdTeleport(tokens);
                case "kick": return CmdKick(text);
                case "baka_spawn": return CmdSpawn(tokens);
                case "baka_killall": return CmdKillAll();
                default: return CmdFallback(text);
            }
        }

        private static bool ServerReady(out string error)
        {
            if (ZNet.instance == null || ZRoutedRpc.instance == null)
            {
                error = "Error: server not ready (world still loading)";
                return false;
            }
            error = null;
            return true;
        }

        private static ZNetPeer FindPeer(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;

            var peer = ZNet.instance.GetPeerByPlayerName(name);
            if (peer != null) return peer;

            // Case-insensitive fallback (exact-match API is case-sensitive)
            foreach (var p in ZNet.instance.GetPeers())
            {
                if (p != null && string.Equals(p.m_playerName, name, StringComparison.OrdinalIgnoreCase))
                    return p;
            }
            return null;
        }

        // ---- broadcast center <message> -----------------------------------

        private static string CmdBroadcast(string[] tokens, string fullText)
        {
            string err;
            if (!ServerReady(out err)) return err;
            if (tokens.Length < 2) return "Usage: broadcast center <message>";

            // Optional style token (BakaLoader always sends "center")
            var msgStart = 1;
            var type = (int)MessageHud.MessageType.Center;
            var style = tokens[1].ToLowerInvariant();
            if (style == "center") { msgStart = 2; type = (int)MessageHud.MessageType.Center; }
            else if (style == "side" || style == "topleft" || style == "top_left") { msgStart = 2; type = (int)MessageHud.MessageType.TopLeft; }

            if (tokens.Length <= msgStart) return "Usage: broadcast center <message>";
            var message = string.Join(" ", tokens, msgStart, tokens.Length - msgStart);

            // Clients registered "ShowMessage" in MessageHud.Start - routed to Everybody.
            ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, "ShowMessage", type, message);
            return "Broadcasting message: " + message;
        }

        // ---- playerlist ----------------------------------------------------

        private static string CmdPlayerList()
        {
            string err;
            if (!ServerReady(out err)) return err;

            var peers = ZNet.instance.GetPeers();
            if (peers == null || peers.Count == 0) return "No players connected";

            // Format matches AviiNL-RCON's, which ValheimServer.ParsePositionForPlayer
            // parses: {name}/{hostId}/{charId} (x, z, y)
            var sb = new StringBuilder();
            foreach (var p in peers)
            {
                if (p == null) continue;
                var host = "unknown";
                try { host = p.m_socket != null ? p.m_socket.GetHostName() : "unknown"; } catch { }
                var pos = p.m_refPos;
                sb.Append(p.m_playerName ?? "unknown").Append('/')
                  .Append(host).Append('/')
                  .Append(p.m_characterID.ToString())
                  .Append(" (")
                  .Append(pos.x.ToString("F1", CultureInfo.InvariantCulture)).Append(", ")
                  .Append(pos.z.ToString("F1", CultureInfo.InvariantCulture)).Append(", ")
                  .Append(pos.y.ToString("F1", CultureInfo.InvariantCulture)).Append(")\n");
            }
            return sb.ToString().TrimEnd('\n');
        }

        // ---- dmg <player> <amount> (negative = heal) -----------------------

        private static string CmdDamage(string[] tokens)
        {
            string err;
            if (!ServerReady(out err)) return err;
            if (tokens.Length < 3) return "Usage: dmg <player> <amount>";

            float amount;
            if (!float.TryParse(tokens[tokens.Length - 1], NumberStyles.Float, CultureInfo.InvariantCulture, out amount))
                return "Error: could not parse damage amount '" + tokens[tokens.Length - 1] + "'";

            // Player names may contain spaces - everything between verb and amount.
            var name = string.Join(" ", tokens, 1, tokens.Length - 2);
            var peer = FindPeer(name);
            if (peer == null) return "Error: player '" + name + "' not found";
            if (peer.m_characterID.IsNone()) return "Error: player '" + name + "' has no character yet";

            if (amount < 0f)
            {
                // Character.RPC_Heal(float amount, bool showText) - owner-executed.
                ZRoutedRpc.instance.InvokeRoutedRPC(peer.m_uid, peer.m_characterID, "RPC_Heal", -amount, true);
                return "Healed " + name + " for " + (-amount).ToString("F0", CultureInfo.InvariantCulture);
            }

            var hit = new HitData();
            hit.m_damage.m_damage = amount;
            hit.m_point = peer.m_refPos;
            hit.m_dodgeable = false;
            hit.m_blockable = false;
            // Character.RPC_Damage(HitData) - HitData serializes natively over routed RPC.
            ZRoutedRpc.instance.InvokeRoutedRPC(peer.m_uid, peer.m_characterID, "RPC_Damage", hit);
            return "Damaged " + name + " for " + amount.ToString("F0", CultureInfo.InvariantCulture);
        }

        // ---- tp <player> <x,z,y | otherPlayer> ------------------------------

        private static string CmdTeleport(string[] tokens)
        {
            string err;
            if (!ServerReady(out err)) return err;
            if (tokens.Length < 3) return "Usage: tp <player> <x,z,y | player>";

            // Coordinate destination: last token is "x,z,y" (playerlist display order).
            var last = tokens[tokens.Length - 1];
            Vector3 dest;
            string destLabel;
            string name;

            if (TryParseCoords(last, out dest))
            {
                name = string.Join(" ", tokens, 1, tokens.Length - 2);
                destLabel = last;
            }
            else
            {
                // Destination is another player. Names may contain spaces, so try every
                // split point until both halves resolve to connected peers.
                name = null;
                ZNetPeer destPeer = null;
                for (var k = 2; k < tokens.Length; k++)
                {
                    var candidate = string.Join(" ", tokens, 1, k - 1);
                    var destName = string.Join(" ", tokens, k, tokens.Length - k);
                    var a = FindPeer(candidate);
                    var b = FindPeer(destName);
                    if (a != null && b != null)
                    {
                        name = candidate;
                        destPeer = b;
                        break;
                    }
                }

                if (name == null || destPeer == null)
                    return "Error: could not resolve player and destination from '" + string.Join(" ", tokens) + "'";

                dest = destPeer.m_refPos;
                destLabel = destPeer.m_playerName;
            }

            var target = FindPeer(name);
            if (target == null) return "Error: player '" + name + "' not found";
            if (target.m_characterID.IsNone()) return "Error: player '" + name + "' has no character yet";

            // Character.RPC_TeleportTo(Vector3 pos, Quaternion rot, bool distantTeleport)
            ZRoutedRpc.instance.InvokeRoutedRPC(target.m_uid, target.m_characterID, "RPC_TeleportTo",
                dest, Quaternion.identity, true);
            return "Teleporting " + name + " to " + destLabel;
        }

        /// <summary>Parses "x,z,y" (Valheim playerlist display order) into a Unity Vector3 (x, y, z).</summary>
        private static bool TryParseCoords(string s, out Vector3 pos)
        {
            pos = Vector3.zero;
            if (string.IsNullOrEmpty(s) || s.IndexOf(',') < 0) return false;
            var parts = s.Split(',');
            if (parts.Length != 3) return false;

            float x, z, y;
            if (!float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out x) ||
                !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out z) ||
                !float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out y))
                return false;

            pos = new Vector3(x, y, z);
            return true;
        }

        // ---- kick <player | hostId> -----------------------------------------

        private static string CmdKick(string fullText)
        {
            string err;
            if (!ServerReady(out err)) return err;

            var target = fullText.Substring("kick".Length).Trim();
            if (target.Length == 0) return "Usage: kick <player | hostId>";

            // ZNet.Kick(string) is public and resolves player name or host id itself.
            ZNet.instance.Kick(target);
            return "Kicked: " + target;
        }

        // ---- baka_spawn <prefab> <x,z,y> [amount] [level] --------------------
        // Absorbed from BakaLoaderSpawnHelper (main-thread spawn - safe headless).

        private static string CmdSpawn(string[] tokens)
        {
            if (tokens.Length < 3) return "Usage: baka_spawn <prefab> <x,z,y> [amount] [level]";

            var zns = ZNetScene.instance;
            if (zns == null) return "Error: server not ready (ZNetScene unavailable)";

            var prefabName = tokens[1];

            Vector3 pos;
            if (!TryParseCoords(tokens[2], out pos))
                return "Error: coords must be x,z,y (3 comma-separated numbers)";

            var amount = 1;
            if (tokens.Length > 3 && !int.TryParse(tokens[3], out amount)) amount = 1;
            amount = Mathf.Clamp(amount, 1, 9999); // match the BakaLoader UI's QuantityField max

            var level = 0;
            if (tokens.Length > 4 && !int.TryParse(tokens[4], out level)) level = 0;
            level = Mathf.Clamp(level, 0, 10);

            var prefab = zns.GetPrefab(prefabName.GetStableHashCode()) ?? zns.GetPrefab(prefabName);
            if (prefab == null) return "Error: prefab '" + prefabName + "' not found in ZNetScene";

            var spawned = 0;
            for (var i = 0; i < amount; i++)
            {
                var offset = Vector3.zero;
                if (amount > 1)
                    offset = new Vector3(UnityEngine.Random.Range(-1f, 1f), 0f, UnityEngine.Random.Range(-1f, 1f));

                var p = new Vector3(pos.x + offset.x, pos.y, pos.z + offset.z);

                float groundHeight;
                if (ZoneSystem.instance != null && ZoneSystem.instance.GetGroundHeight(p, out groundHeight))
                {
                    if (groundHeight > p.y - 5f)
                        p.y = groundHeight;
                }

                var obj = UnityEngine.Object.Instantiate(prefab, p, Quaternion.identity);
                if (obj == null) continue;

                if (level > 0)
                {
                    var character = obj.GetComponent<Character>();
                    if (character != null)
                        character.SetLevel(level + 1); // SetLevel is 1-indexed: 1=base, 2=1star
                }

                spawned++;
            }

            return "Spawned " + spawned + "x " + prefabName + " (level " + level + ") at (" +
                pos.x.ToString("F1", CultureInfo.InvariantCulture) + ", " +
                pos.z.ToString("F1", CultureInfo.InvariantCulture) + ", " +
                pos.y.ToString("F1", CultureInfo.InvariantCulture) + ")";
        }

        // ---- baka_killall -----------------------------------------------------
        // Absorbed from BakaKillAll (3 enumeration fallbacks). HOSTILES ONLY:
        // players, tamed pets, and friendly factions (AnimalsVeg passive wildlife,
        // Dverger allies, PlayerSpawned) are spared - modded servers add tons of
        // friendly NPCs/pets and this must never wipe them.

        private static string CmdKillAll()
        {
            var killed = 0;
            var spared = 0;

            var staticList = Character.GetAllCharacters();
            if (staticList != null && staticList.Count > 0)
            {
                for (var i = staticList.Count - 1; i >= 0; i--)
                {
                    var c = staticList[i];
                    if (c == null) continue;
                    if (ShouldSpare(c)) { spared++; continue; }
                    if (TryKill(c)) killed++;
                }
            }
            else
            {
                var sceneChars = Resources.FindObjectsOfTypeAll<Character>();
                if (sceneChars != null && sceneChars.Length > 0)
                {
                    foreach (var c in sceneChars)
                    {
                        if (c == null) continue;
                        if (ShouldSpare(c)) { spared++; continue; }
                        if (TryKill(c)) killed++;
                    }
                }
                else
                {
                    foreach (var m in UnityEngine.Object.FindObjectsOfType<MonoBehaviour>())
                    {
                        var c = m as Character;
                        if (c == null) continue;
                        if (ShouldSpare(c)) { spared++; continue; }
                        if (TryKill(c)) killed++;
                    }
                }
            }

            return "KillAll complete: " + killed + " hostiles slain, " + spared + " spared (players, pets & allies)";
        }

        private static bool ShouldSpare(Character c)
        {
            try
            {
                if (c.IsPlayer()) return true;
                if (c.IsTamed()) return true; // pets - wolves, lox, modded companions

                // Friendly / non-hostile factions. Everything else (ForestMonsters,
                // Undead, Demon, MountainMonsters, SeaMonsters, PlainsMonsters,
                // MistlandsMonsters, Boss) is a hostile mob and stays killable.
                switch (c.m_faction)
                {
                    case Character.Faction.Players:       // player-faction NPCs (many modded friendlies)
                    case Character.Faction.AnimalsVeg:    // passive wildlife (deer, gulls, hares…)
                    case Character.Faction.Dverger:       // dvergr allies
                    case Character.Faction.PlayerSpawned: // player-summoned allies
                        return true;
                }

                return false;
            }
            catch { return true; } // if in doubt, don't kill
        }

        private static bool TryKill(Character c)
        {
            try
            {
                var hit = new HitData();
                hit.m_damage.m_damage = 1e10f;
                hit.m_point = c.transform.position;
                hit.m_dodgeable = false;
                hit.m_blockable = false;
                c.Damage(hit);
                return true;
            }
            catch
            {
                return false;
            }
        }

        // ---- fallback: forward unknown commands to the in-game console --------

        private static string CmdFallback(string text)
        {
            try
            {
                var console = global::Console.instance;
                if (console != null)
                {
                    // (text, silentFail: true, skipAllowedCheck: true) - no devcommands mod required.
                    console.TryRunCommand(text, true, true);
                    return "Forwarded to console: " + text;
                }
            }
            catch (Exception ex)
            {
                return "Error forwarding '" + text + "' to console: " + ex.Message;
            }

            return "Unknown command: '" + text + "' (Commander natively supports: broadcast, playerlist, dmg, tp, kick, baka_spawn, baka_killall)";
        }
    }
}
