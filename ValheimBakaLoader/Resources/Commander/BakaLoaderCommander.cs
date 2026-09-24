// BakaLoader Commander v1.8.0 - native RCON server + command suite for BakaLoader.
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
//   - Source RCON packet frame: [int32 length][int32 id][int32 type][UTF-8 body][0][0],
//     little-endian. Client sanity-checks 10 <= length <= 4110, so response bodies are
//     split into chunks of <= 4000 bytes, and a chunk never ends in the middle of a
//     multi-byte letter. The body was ASCII until 1.3.1, which turned every letter
//     outside the first 128 into a question mark in both directions.
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
//   kick <player | hostId>           - resolves the peer itself (host id, then name) and
//                                      answers "Kicked: <name>" only when one matched
//   baka_spawn <prefab> <x,z,y> [amount] [level] - main-thread spawn (absorbed from
//                                      BakaLoaderSpawnHelper). The 4th argument is a
//                                      creature's star level (0 based) or an item's
//                                      quality (1 based, 0 leaves it alone).
//   baka_killall [<prefab> | near <player> <radius>] - kill hostiles, sparing players,
//                                      pets and allies. The sweep itself lives in
//                                      Resources\KillAll\BakaKillAllSweep.cs and is
//                                      compiled into this DLL and into BakaKillAll.dll,
//                                      so either plugin serves the command alone.
//   anything else                    - forwarded to the in-game console if present
//
// CHEATED MARK (v1.6.0):
// Valheim 1.0 shows "This item was summoned through cheating means." on anything its own
// spawn command conjured, and pauses a player's achievement progress while such an item
// sits in their inventory. baka_spawn does NOT mark its spawns any more. The behaviour is
// one config entry, Spawning/MarkSpawnedAsCheated, default false, and the rule behind it
// lives in ..\SpawnHelper\BakaSpawnMark.cs, compiled into this DLL and into the Spawn
// Helper so the two can never drift into marking spawns differently.
// The entry is read off disk once, while the server is starting. BepInEx 5.4 keeps no
// watcher on the .cfg, so changing the setting while the server runs does nothing until
// the next start.
// TURNING IT ON: write MarkSpawnedAsCheated = true under [Spawning] in
// BepInEx/config/com.baka.commander.cfg and start the server. That value is the one this
// plugin binds as it loads, and a spawn issued through the app, which arrives at CmdSpawn
// below and reads this plugin's entry, comes out marked. BakaLoader rewrites this file from the
// server profile on every start, and from BakaLoader 1.2.0 Tools\CommanderInstaller.cs
// carries the whole [Spawning] section across that rewrite, every line inside it as the
// host wrote it, comments and all, the way it has always carried BindAddress. Only the
// header line is written back in the app's own spelling of it, which BepInEx normalises
// anyway and which no setting rides on. A file with no such section keeps none, and the
// plugin writes its own default entry out on that start.
// (Before 1.2.0 the rewrite kept only BindAddress, so a hand-written [Spawning] section
// was destroyed before BepInEx parsed the file and this mark could not be turned on at
// all while Commander was the plugin answering.)
// com.baka.spawnhelper.cfg is NOT rewritten by BakaLoader at all and holds its value the
// same way, but it governs only the spawns the Spawn Helper's own console command serves,
// never CmdSpawn's. That command is reached from the game console and from a third-party
// RCON plugin that forwards to it, so in the COEXISTENCE case below, where AviiNL-RCON won
// the port and this plugin is dormant, the Spawn Helper's entry is the one in force.
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
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using BakaLoaderKillAll;
using BakaLoaderSpawn;
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
        private const string PluginVersion = "1.8.0";

        // Source RCON packet types
        private const int TypeAuth = 3;          // SERVERDATA_AUTH
        private const int TypeAuthResponse = 2;  // SERVERDATA_AUTH_RESPONSE
        private const int TypeExec = 2;          // SERVERDATA_EXECCOMMAND
        private const int TypeResponse = 0;      // SERVERDATA_RESPONSE_VALUE

        private const int MaxResponseBodyBytes = 4000; // client rejects frames > 4110 total
        private const int CommandTimeoutMs = 4500;     // client gives up at 5000
        private const int IoTimeoutMs = 5000;

        // How packet bodies are written and read, matching BakaLoader's RconClient exactly.
        // UTF-8 with no byte-order mark: identical to ASCII for plain English, and it carries
        // every other alphabet as well. Player names go out in playerlist and come back in
        // spawn, tp and kick; broadcasts and restart warnings go the other way. All of them
        // used to lose any letter outside the first 128 to a question mark.
        private static readonly Encoding BodyEncoding = new UTF8Encoding(false);

        private static ManualLogSource Log;

        private ConfigEntry<bool> CfgEnabled;
        private ConfigEntry<int> CfgPort;
        private ConfigEntry<string> CfgPassword;
        private ConfigEntry<string> CfgBindAddress;

        // Bound in Awake, read from MarkAsSpawnedIn, which is static because the spawn loop
        // that calls it is. Held as the entry rather than as a copied bool only so the value
        // lives in one place. That is not a way to pick up a live .cfg edit: BepInEx 5.4
        // parses the file once, while the ConfigFile is being constructed, and keeps no
        // watcher on it, so a setting changed mid-session takes effect at the next start.
        private static ConfigEntry<bool> CfgMarkSpawnedAsCheated;

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

            // The section, the key, the default and the wording all come from SpawnMark so the
            // two plugins that serve baka_spawn cannot bind the same setting under different
            // names, and so the suite can pin all four without a dedicated server to run. Bound
            // BEFORE the disabled-plugin return below: a host who turned the RCON listener off
            // should still find the entry written out with its default in the config file.
            CfgMarkSpawnedAsCheated = Config.Bind(
                SpawnMark.ConfigSection,
                SpawnMark.ConfigKey,
                SpawnMark.ConfigDefault,
                SpawnMark.ConfigDescription);

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
            body = BodyEncoding.GetString(payload, 8, length - 10); // strip 2 null terminators
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
            // Counted in BYTES, never in characters: the length field is what the reader on
            // the other end trusts, and one letter outside the first 128 is two or more bytes.
            var bodyBytes = BodyEncoding.GetBytes(body ?? "");
            WritePacket(stream, id, type, bodyBytes, 0, bodyBytes.Length);
        }

        private static void WritePacket(NetworkStream stream, int id, int type, byte[] body, int offset, int count)
        {
            var length = 4 + 4 + count + 2;
            var packet = new byte[4 + length];

            Buffer.BlockCopy(BitConverter.GetBytes(length), 0, packet, 0, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(id), 0, packet, 4, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(type), 0, packet, 8, 4);
            if (count > 0) Buffer.BlockCopy(body, offset, packet, 12, count);
            // last two bytes stay 0 (body terminator + packet terminator)

            stream.Write(packet, 0, packet.Length);
            stream.Flush();
        }

        /// <summary>
        /// Splits large responses across multiple packets (client drains until close).
        /// <para>
        /// The split is on bytes, because the frame's length field is bytes, and a UTF-8
        /// letter can be up to four of them. A cut landing inside one of those letters would
        /// hand the client half a letter at the end of one packet and half at the start of
        /// the next, and both halves would be read as damage. So a chunk that would end mid
        /// letter is walked back to the last whole one, and the bytes are handed on as they
        /// are rather than decoded and re-encoded around the seam.
        /// </para>
        /// </summary>
        private static void WriteResponse(NetworkStream stream, int id, string response)
        {
            var bytes = BodyEncoding.GetBytes(response ?? "");
            if (bytes.Length == 0)
            {
                WritePacket(stream, id, TypeResponse, "");
                return;
            }

            var offset = 0;
            while (offset < bytes.Length)
            {
                var chunkLen = SafeChunkLength(bytes, offset, Math.Min(MaxResponseBodyBytes, bytes.Length - offset));
                WritePacket(stream, id, TypeResponse, bytes, offset, chunkLen);
                offset += chunkLen;
            }
        }

        /// <summary>
        /// The most of <paramref name="wanted"/> bytes that can be sent without cutting a
        /// UTF-8 letter in half. A byte of the form 10xxxxxx continues the letter before it,
        /// so a chunk must never begin with one: the length is walked back until the byte
        /// that would start the next chunk begins a letter of its own. A chunk that reaches
        /// the end of the text needs no walking back, and one letter can never be more than
        /// four bytes, so this gives up at most three.
        /// <para>
        /// A cap under four bytes could not hold the longest letter at all, so rather than
        /// cut one in half the chunk is allowed to grow to four and the cap is exceeded by
        /// at most three bytes. The only caller's cap is <see cref="MaxResponseBodyBytes"/>
        /// (4000), so this arm never runs today; it is here so that lowering the cap can
        /// never turn into a split letter.
        /// </para>
        /// </summary>
        private static int SafeChunkLength(byte[] bytes, int offset, int wanted)
        {
            if (wanted <= 0) return 0;
            if (offset + wanted >= bytes.Length) return wanted;

            var len = wanted < 4 ? 4 : wanted;
            if (offset + len >= bytes.Length) return bytes.Length - offset;

            var walked = 0;
            while (len > 1 && walked < 3 && (bytes[offset + len] & 0xC0) == 0x80)
            {
                len--;
                walked++;
            }

            return len;
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

            // A kill-all too big to finish inside its own answer carries on here, a few
            // milliseconds a frame, and posts its result line to the server log when it
            // lands. It has to sit outside the drain loop above: the drain is what the RCON
            // thread is waiting on, and holding it open for the rest of a sweep would put
            // every other command behind the very timeout this arrangement exists to cure.
            KillAllSweep.Pump();
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
                case "baka_killall": return CmdKillAll(tokens);
                case "baka_cleanse": return CmdCleanse();
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

        /// <summary>
        /// A connected player by name, exact first and then ignoring case, because the game's
        /// own lookup hashes the exact spelling.
        /// <para>
        /// BOTH halves require the peer to be READY, which is exactly what the game's own
        /// ZNet.GetPeerByPlayerName requires of every peer it walks past. The fallback did not,
        /// so it could answer where the game's own lookup refuses, and what it handed back was a
        /// peer the game does not consider to be in the world yet. ZNetPeer.IsReady() is
        /// m_uid != 0, and m_uid, m_playerName and m_refPos are all written together at the end
        /// of the PeerInfo handshake. Until then m_uid is 0, and 0 is ZRoutedRpc.Everybody: a
        /// dmg or a tp routed at such a peer goes to EVERY CLIENT ON THE SERVER rather than to
        /// the one the host named. m_refPos is Vector3.zero for the same stretch, which is what
        /// put a kill radius on the world origin.
        /// </para>
        /// </summary>
        private static ZNetPeer FindPeer(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;

            var peer = ZNet.instance.GetPeerByPlayerName(name);
            if (peer != null) return peer;

            foreach (var p in ZNet.instance.GetPeers())
            {
                if (p != null && p.IsReady() &&
                    string.Equals(p.m_playerName, name, StringComparison.OrdinalIgnoreCase))
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

            // Clients registered "ShowMessage" in MessageHud.Start - routed to everybody.
            // The literal 0 IS ZRoutedRpc.Everybody. It is written out instead of read from
            // the field because Valheim 1.0 turned that field into a const, and a compiled
            // field read (ldsfld) against a const throws MissingFieldException at runtime.
            // A literal is correct on every game version, old and new.
            ZRoutedRpc.instance.InvokeRoutedRPC(0L, "ShowMessage", type, message);
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

            // Resolved here first, in the order the game resolves it. ZNet.Kick(string) looks
            // the text up as a host id and then as a player name and simply RETURNS when
            // neither finds anybody, so this used to answer "Kicked: <text>" for a kick that
            // reached no one. A name that had come to the app through the wrong encoding, or
            // one typed with a letter out, read as done with the player standing right there.
            string host;
            var peer = FindPeerByHostId(target, out host);
            var kickText = host;

            if (peer == null)
            {
                peer = FindPeer(target);

                // The peer's OWN spelling of its name goes to the game, not the text that was
                // typed: the name lookup above accepts a difference in case and the game's own
                // does not, so handing the typed text back would find nobody after all.
                if (peer != null) kickText = peer.m_playerName;
            }

            if (peer == null) return "Error: no player named '" + target + "' is online";

            ZNet.instance.Kick(kickText);
            return "Kicked: " + (peer.m_playerName ?? target);
        }

        /// <summary>
        /// The connected peer a kick target names, matched on the id the SERVER knows a peer by
        /// rather than on the name a person typed, with that peer's own host id handed back so
        /// the game is given a spelling its own lookup will find.
        /// </summary>
        private static ZNetPeer FindPeerByHostId(string target, out string host)
        {
            host = null;
            if (string.IsNullOrEmpty(target)) return null;

            var peers = ZNet.instance.GetPeers();
            if (peers == null) return null;

            foreach (var p in peers)
            {
                // READY for the same reason FindPeer requires it: the game's own lookups walk
                // past a peer that is not in the world yet, and so must this one.
                if (p == null || !p.IsReady() || p.m_socket == null) continue;

                string name = null;
                try { name = p.m_socket.GetHostName(); } catch { }
                if (string.IsNullOrEmpty(name)) continue;

                if (!HostIdMatches(name, target)) continue;

                host = name;
                return p;
            }

            return null;
        }

        /// <summary>
        /// Whether a kick target stands for the peer whose socket answers with this host id.
        /// <para>
        /// Exact first, and then the one difference between the two kinds of server: a Steam
        /// socket answers the bare steamid64 while a crossplay socket answers the whole
        /// "Steam_&lt;id&gt;", and the game papers over that by reading any text it cannot parse
        /// as a platform id as a Steam id. Only the Steam prefix is taken off here, because
        /// that is the only one the game treats this way: a console id handed to a Steam server
        /// resolves to nobody there, and a match this side that the game would not make is a
        /// kick reported as done that never happened. The compare is case sensitive, which is
        /// what the game's own list and peer lookups are.
        /// </para>
        /// </summary>
        private static bool HostIdMatches(string host, string target)
        {
            var left = WithoutSteamPrefix(host);
            var right = WithoutSteamPrefix(target);
            return left.Length > 0 && string.Equals(left, right, StringComparison.Ordinal);
        }

        private static string WithoutSteamPrefix(string id)
        {
            if (string.IsNullOrEmpty(id)) return "";
            return id.StartsWith("Steam_", StringComparison.Ordinal) ? id.Substring("Steam_".Length) : id;
        }

        // ---- baka_spawn <prefab> <x,z,y> [amount] [level] --------------------
        // Absorbed from BakaLoaderSpawnHelper (main-thread spawn - safe headless).
        // The 4th argument carries two meanings, the same way the game's own spawn command
        // does: for a creature it is the star level (0 based, so 1 means one star), and for
        // an item it is the quality (1 based, so 3 means a quality 3 tool). 0 leaves both
        // alone. Stackable items arrive as stacks, not as one drop per unit.

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

            // Read the item template once. A stackable item spawns as stacks rather than as
            // one loose drop per unit, so the count of objects and the count of units are two
            // different numbers from here on.
            var isItem = prefab.GetComponent<ItemDrop>() != null;
            var maxStack = MaxStackSizeOf(prefab);
            var drops = maxStack > 1 ? Mathf.CeilToInt(amount / (float)maxStack) : amount;

            var remaining = amount;
            var units = 0;
            var objects = 0;
            var quality = 0;

            for (var i = 0; i < drops; i++)
            {
                var offset = Vector3.zero;
                if (drops > 1)
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

                MarkAsSpawnedIn(obj);

                var item = obj.GetComponent<ItemDrop>();
                if (item != null)
                {
                    var stack = maxStack > 1 ? Mathf.Clamp(remaining, 1, maxStack) : 1;
                    remaining -= stack;
                    quality = SetUpSpawnedItem(obj, item, level, stack);
                    units += stack;
                }
                else
                {
                    if (level > 0)
                    {
                        var character = obj.GetComponent<Character>();
                        if (character != null)
                            character.SetLevel(level + 1); // SetLevel is 1-indexed: 1=base, 2=1star
                    }
                    units++;
                }

                objects++;
            }

            var text = "Spawned " + units + "x " + prefabName;
            if (maxStack > 1 && units > objects)
                text += " as " + objects + (objects == 1 ? " stack" : " stacks");
            // A clamped quality says so. The host asked for 5 and got 4 because that is all a
            // bronze axe has, and a line that just says "at quality 4" looks like the request
            // was misread rather than met as far as the item allows.
            if (quality > 0)
            {
                text += " at quality " + quality;
                if (quality < level) text += " (the most this item allows)";
            }
            else if (!isItem && level > 0)
            {
                text += " at level " + level;
            }

            return text + ", placed at (" +
                pos.x.ToString("F1", CultureInfo.InvariantCulture) + ", " +
                pos.z.ToString("F1", CultureInfo.InvariantCulture) + ", " +
                pos.y.ToString("F1", CultureInfo.InvariantCulture) + ")";
        }

        /// <summary>
        /// The item's own maximum stack size, or 1 for anything that is not a stackable item.
        /// Read off the prefab before the loop because it decides how many objects to make.
        /// </summary>
        private static int MaxStackSizeOf(GameObject prefab)
        {
            try
            {
                var item = prefab.GetComponent<ItemDrop>();
                if (item == null || item.m_itemData == null || item.m_itemData.m_shared == null) return 1;
                return Mathf.Max(1, item.m_itemData.m_shared.m_maxStackSize);
            }
            catch
            {
                return 1;
            }
        }

        /// <summary>
        /// Finishes a freshly conjured item the way the game's own spawn command finishes one:
        /// full durability, and the requested quality when one was asked for. Quality is set
        /// FIRST because maximum durability grows with it (ItemData.GetMaxDurability(quality)
        /// is m_maxDurability plus m_durabilityPerLevel per level above 1), so a quality 4 axe
        /// handed out at quality 1 durability would arrive visibly worn. The stack size is ours
        /// rather than vanilla's: the console command has a player to hand items to, and this
        /// one drops them on the ground, where 150 separate wood drops is a lag spike and three
        /// stacks of 50 is what the player wanted. Returns the quality that was applied, or 0.
        /// </summary>
        private static int SetUpSpawnedItem(GameObject obj, ItemDrop item, int level, int stack)
        {
            var applied = 0;
            try
            {
                var data = item.m_itemData;
                if (data == null) return 0;

                if (level > 0)
                {
                    var maxQuality = 1;
                    if (data.m_shared != null) maxQuality = Mathf.Max(1, data.m_shared.m_maxQuality);

                    // Most things in the game have no upgrade track at all: a mead, a pile of
                    // wood, a trophy. Setting a quality on one is a write with nothing behind
                    // it, and saying "at quality 1" about it is chatter about a property the
                    // item does not have, so neither happens. Anything that does upgrade is
                    // clamped to its own ceiling rather than to a hardcoded vanilla 4, because
                    // modded items go past it.
                    if (maxQuality > 1)
                    {
                        applied = Mathf.Clamp(level, 1, maxQuality);
                        item.SetQuality(applied);
                    }
                }

                data.m_durability = data.GetMaxDurability();

                if (stack > 1)
                {
                    var maxStack = 1;
                    if (data.m_shared != null) maxStack = Mathf.Max(1, data.m_shared.m_maxStackSize);
                    data.m_stack = Mathf.Clamp(stack, 1, maxStack);
                }

                // An ItemDrop keeps its truth in its ZDO, and SaveToZDO is the public path the
                // game's own private Save() takes. Only the owner may write it, and the index
                // is passed as vanilla passes it (-1 = the drop's own slot, not an inventory
                // one), written out so a game update that inserts a parameter fails the plugin
                // verifier instead of silently landing the value in the wrong slot.
                var view = obj.GetComponent<ZNetView>();
                if (view != null && view.IsValid() && view.IsOwner())
                    ItemDrop.SaveToZDO(data, view.GetZDO(), -1);
            }
            catch (Exception ex)
            {
                // Never lose the spawn over the bookkeeping.
                Log.LogWarning("Could not finish the spawned item: " + ex.Message);
            }
            return applied;
        }

        /// <summary>
        /// Applies the per-object bookkeeping the game's own "spawn" command applies, minus the
        /// one part of it a host asked not to have.
        /// <para>
        /// ItemDrop.OnCreateNew runs on every spawn whatever the setting says, because it does
        /// two jobs: it records the world level the item was made at, and it writes the cheated
        /// flag. Skipping the call to avoid the flag would leave every conjured item carrying
        /// whatever world level happened to be on the prefab. So the call stays and the flag is
        /// handed to it, true or false, exactly as SpawnMark.ShouldMark decides.
        /// </para>
        /// <para>
        /// The ZDO key is written either way for the same reason: a creature's drops read
        /// ZDOVars.s_cheated off its record, and an explicit false is the only thing that says
        /// "not cheated" rather than "nobody looked". Whether the console command itself is
        /// cheat-flagged is a separate thing; this marks the objects, not the console.
        /// </para>
        /// </summary>
        private static void MarkAsSpawnedIn(GameObject obj)
        {
            var mark = ShouldMarkSpawn();

            // Three guards rather than one, because the two records are independent and the
            // second one is not optional. OnCreateNew is also what stamps the item's world
            // level, so a ZDO write that throws must not be allowed to carry that call down
            // with it and leave the item on whatever level sat on the prefab. Never lose the
            // spawn over either piece of bookkeeping.
            try
            {
                var view = obj.GetComponent<ZNetView>();
                if (view != null && view.IsValid())
                    view.GetZDO().Set(ZDOVars.s_cheated, mark);
            }
            catch (Exception ex)
            {
                Log.LogWarning("Could not record the cheated flag on the spawned object: " + ex.Message);
            }

            try
            {
                ItemDrop.OnCreateNew(obj, mark);
            }
            catch (Exception ex)
            {
                Log.LogWarning("Could not stamp the spawned item's world level: " + ex.Message);
            }
        }

        /// <summary>
        /// The host's answer, run through <see cref="SpawnMark.ShouldMark"/>. Read through the
        /// entry rather than a copied bool so the value lives in one place. A .cfg edited while
        /// the server runs is not picked up here; BepInEx reads that file only at startup, so
        /// such a change needs a restart. Null only while Awake has not run, which a queued
        /// spawn cannot outrun, and the default answers for it if it ever did. A throw lands on
        /// the default too: an unreadable setting must not decide to mark.
        /// </summary>
        private static bool ShouldMarkSpawn()
        {
            try
            {
                var wanted = CfgMarkSpawnedAsCheated != null
                    ? CfgMarkSpawnedAsCheated.Value
                    : SpawnMark.ConfigDefault;
                return SpawnMark.ShouldMark(wanted, CheatChecksBypassed());
            }
            catch (Exception ex)
            {
                Log.LogWarning("Could not read the spawn mark setting, using the default: " + ex.Message);
                return SpawnMark.ShouldMark(SpawnMark.ConfigDefault, false);
            }
        }

        // PlayerProfile.s_bypassCheatChecks was a plain static FIELD until Valheim 1.0.12
        // (build 25253791) turned it into a static PROPERTY. A compiled field read is an
        // ldsfld against a member that no longer exists, and Mono raises that
        // MissingFieldException when it JITs the method holding the read - and it raises it at
        // that method's CALL SITE, so a try/catch written inside the method holding the read
        // never runs at all. The whole spawn loop died after the first object with no level,
        // no quality and one lonely item on the ground. Asking the live assembly what the
        // member is today survives both shapes and any future third one.
        private static bool _bypassProbed;
        private static PropertyInfo _bypassProperty;
        private static FieldInfo _bypassField;

        /// <summary>
        /// Reads PlayerProfile.s_bypassCheatChecks without compiling a reference to it.
        /// Property first, then field, looked up once and cached. False when it is neither,
        /// which reads as "the bypass is off" and leaves the decision entirely with the host's
        /// setting, the same way vanilla treats a server running without the bypass.
        /// </summary>
        private static bool CheatChecksBypassed()
        {
            try
            {
                if (!_bypassProbed)
                {
                    _bypassProbed = true;
                    const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
                    var property = typeof(PlayerProfile).GetProperty("s_bypassCheatChecks", flags);
                    if (property != null && property.CanRead && property.PropertyType == typeof(bool))
                        _bypassProperty = property;
                    else
                        _bypassField = typeof(PlayerProfile).GetField("s_bypassCheatChecks", flags);
                }

                if (_bypassProperty != null) return (bool)_bypassProperty.GetValue(null, null);
                if (_bypassField != null && _bypassField.FieldType == typeof(bool)) return (bool)_bypassField.GetValue(null);
            }
            catch (Exception ex)
            {
                try { Log.LogDebug("Could not read the cheat check bypass: " + ex.Message); } catch { }
            }
            return false;
        }

        // ---- baka_killall [<prefab> | near <player> <radius>] -----------------
        //
        // HOSTILES ONLY: players, tamed pets, and the friendly factions (AnimalsVeg
        // passive wildlife, Dverger allies, PlayerSpawned summons, TrainingDummy
        // training posts) are spared. Modded servers add plenty of friendly NPCs and
        // pets, and a training post is a player-built structure, so a sweep must never
        // wipe any of them.
        //
        // The work is in KillAllSweep (Resources\KillAll\BakaKillAllSweep.cs), compiled
        // into this DLL and into BakaKillAll.dll so both companion plugins answer the
        // command the same way and either one works installed alone. Commander gets here
        // on the Unity main thread already: Update() drains the RCON queue.
        //
        // What it used to do was call Character.GetAllCharacters() and kill what came
        // back. That is every creature in the world on a listen server and very nearly
        // none of them on a dedicated one, because creatures around players are
        // instantiated and owned by those players' clients. The sweep walks the world's
        // own object records now and sends the damage to each creature's owner.
        //
        // THE ANSWER NEVER WAITS FOR THE WHOLE WALK. DispatchToMainThread gives up at
        // CommandTimeoutMs and answers "Error: command timed out", and it has no way to
        // call the work back: Update() goes right on and finishes the sweep. So a sweep
        // that ran long told the host it had failed while it killed everything on the
        // server. Start() answers with the whole result line when the walk fits inside one
        // slice, which is every world a closed test will run on, and with "KillAll started:
        // N candidates" when it does not. Pump(), at the bottom of Update(), carries the
        // rest and puts the real "KillAll complete" line in the server log.

        // ---- clear the cheat marks a 1.0.9 to 1.1.2 spawn left behind ----------
        //
        // One pass over every ZDO in the world, on the main thread, refused while anybody
        // is connected. It is not sliced the way the kill-all sweep is: the server is empty
        // by the time it runs, so a frame it takes to itself costs nobody anything, and a
        // host wants the counts in the answer rather than in a line that lands later. A
        // world big enough for the pass to outlast this client's patience still finishes,
        // and the same counts go to the server log, which is what a timed-out host reads.

        private static string CmdCleanse()
        {
            return CleanseSweep.Run(delegate(string line) { Log.LogInfo(line); });
        }

        private static string CmdKillAll(string[] tokens)
        {
            return KillAllSweep.Start(
                tokens,
                delegate(string message) { Log.LogWarning(message); },
                delegate(string line) { Log.LogInfo(line); });
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

            return "Unknown command: '" + text + "' (Commander natively supports: broadcast, playerlist, dmg, tp, kick, baka_spawn, baka_killall, baka_cleanse)";
        }
    }
}
