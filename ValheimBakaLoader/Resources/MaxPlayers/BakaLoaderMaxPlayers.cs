// BakaLoader MaxPlayers v1.3.0 - compiled against SERVER assembly_valheim.
// Raises Valheim's built-in 10-player cap so BakaLoader no longer depends on a
// third-party mod for the World hall's Max Players setting.
//
// Three patch points, mirroring the approach the community has run in production
// for years (Azumatt's MaxPlayerCount, MIT-0). All constants are baked into the
// IL when BepInEx loads the plugin, so config changes apply on the NEXT server start:
//   1. ZNet.RPC_PeerInfo - the admission check that rejects joiners with "server full"
//      once GetNrOfPlayers() reaches a hardcoded 10 (all backends).
//   2. SteamGameServer.SetMaxPlayerCount - the capacity the Steam backend advertises.
//   3. ZPlayFabMatchmaking.CreateLobby / CreateAndJoinNetwork - the PlayFab/crossplay
//      lobby caps (hardcoded 11 on dedicated servers: vanilla 10 + one reserved slot).
// Points 2 and 3 only exist for their respective backend, so they are patched lazily
// once the game has picked one (FejdStartup.Start postfix), and only when point 1
// actually took: a server that advertises more slots than it will admit is worse off
// than one that stayed at the vanilla number.
//
// Non-public game members (RPC_PeerInfo, FejdStartup.Start, the ZPlayFabMatchmaking
// methods) are addressed by string name because this compiles against the raw,
// non-publicized server assemblies.
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Steamworks;

namespace BakaLoaderMaxPlayers
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    // These patch the same IL constants - loading alongside them would double-patch.
    // If the old Azumatt mod is still installed (migration not run yet), we bow out
    // and it keeps working exactly as before.
    [BepInIncompatibility("org.bepinex.plugins.valheim_plus")]
    [BepInIncompatibility("Azumatt.MaxPlayerCount")]
    public class MaxPlayersPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.baka.maxplayers";
        public const string PluginName = "BakaLoader MaxPlayers";
        public const string PluginVersion = "1.3.0";

        /// <summary>
        /// The constant RPC_PeerInfo compares GetNrOfPlayers() against in an unmodded game.
        /// The transpiler below refuses to rewrite anything else, so a game update that puts
        /// a different sbyte constant in that spot leaves the method exactly as it shipped
        /// instead of quietly changing the wrong number.
        /// </summary>
        private const int VanillaAdmissionCap = 10;

        /// <summary>
        /// The one sentence an operator needs when a patch is refused. It goes on every refusal
        /// line so the server log the interface shows says what the server will actually do,
        /// rather than leaving the number in the settings screen looking like the truth.
        /// </summary>
        private const string CapStaysAtVanilla =
            "BakaLoader Max Players: this server keeps the vanilla limit of 10 players for this start. "
            + "The number saved in the settings is not in force.";

        private static ManualLogSource Log;
        private static ConfigEntry<int> MaxPlayers;
        private static Harmony Patcher;

        /// <summary>
        /// True once the admission check in ZNet.RPC_PeerInfo has actually been rewritten.
        /// The backend advertisements are only allowed to go up once this is true. Raising the
        /// advertised and lobby capacity while admission still rejects at the vanilla cap is the
        /// worst of both worlds: the server offers slots it then refuses, and the players who
        /// are bounced see an open server. Volatile because the transpiler and the FejdStartup
        /// postfix do not have to run on the same thread.
        /// </summary>
        private static volatile bool AdmissionCapRaised;

        private void Awake()
        {
            Log = Logger;
            MaxPlayers = Config.Bind("General", "MaxPlayers", 10,
                "Maximum number of players allowed on the server. Vanilla cap is 10. Applied when the server starts.");

            Patcher = new Harmony(PluginGuid);
            Patcher.PatchAll(Assembly.GetExecutingAssembly());
            Log.LogInfo(PluginName + " " + PluginVersion + " loaded - max players = " + MaxPlayers.Value + ".");
        }

        /// <summary>
        /// Admission check. RPC_PeerInfo calls GetNrOfPlayers() and compares it against a
        /// hardcoded 10 (an Ldc_I4_S right after the call); at or past it, joiners get
        /// "server full". Swap that constant for the configured cap. The existing
        /// instruction is mutated in place (opcode + operand) so any branch labels
        /// attached to it stay intact; widening to Ldc_I4 lifts the sbyte 127 ceiling.
        /// </summary>
        [HarmonyPatch(typeof(ZNet), "RPC_PeerInfo")]
        internal static class AdmissionCapPatch
        {
            [HarmonyTranspiler]
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                var codes = new List<CodeInstruction>(instructions);
                var patched = false;

                for (int i = 0; i < codes.Count && !patched; i++)
                {
                    if (codes[i].opcode != OpCodes.Call
                        || !(codes[i].operand is MethodInfo method)
                        || method.Name != "GetNrOfPlayers") continue;

                    for (int j = i + 1; j < codes.Count; j++)
                    {
                        if (codes[j].opcode != OpCodes.Ldc_I4_S) continue;

                        // The first sbyte constant after the call is the admission cap only for
                        // as long as the game keeps that shape. Check the value before touching
                        // it: rewriting some other constant would report a clean patch while
                        // changing something nobody asked to change.
                        if (!IsVanillaAdmissionCap(codes[j].operand))
                        {
                            Log.LogWarning("ZNet.RPC_PeerInfo: expected the vanilla cap of "
                                + VanillaAdmissionCap + " after GetNrOfPlayers() but found "
                                + codes[j].operand + ". The admission cap was NOT raised and the method was left untouched. "
                                + CapStaysAtVanilla);
                            return codes;
                        }

                        Log.LogInfo("ZNet.RPC_PeerInfo: admission cap " + codes[j].operand + " to " + MaxPlayers.Value);
                        codes[j].opcode = OpCodes.Ldc_I4;
                        codes[j].operand = MaxPlayers.Value;
                        patched = true;
                        AdmissionCapRaised = true;
                        break;
                    }
                }

                if (!patched)
                    Log.LogWarning("ZNet.RPC_PeerInfo: the expected IL pattern was not found, so the admission cap was NOT raised. "
                        + "A game update may have changed the method. " + CapStaysAtVanilla);
                return codes;
            }

            /// <summary>
            /// True when an Ldc_I4_S operand carries the vanilla cap. Harmony hands the operand
            /// over boxed, and the exact numeric type depends on how the instruction was read,
            /// so every plausible integer form is accepted.
            /// </summary>
            private static bool IsVanillaAdmissionCap(object operand)
            {
                if (operand is sbyte sbyteValue) return sbyteValue == VanillaAdmissionCap;
                if (operand is byte byteValue) return byteValue == VanillaAdmissionCap;
                if (operand is short shortValue) return shortValue == VanillaAdmissionCap;
                if (operand is int intValue) return intValue == VanillaAdmissionCap;
                return false;
            }
        }

        /// <summary>
        /// Backend capacity advertisements. Which one exists depends on the backend the
        /// game picked, so patch after FejdStartup.Start has made the choice.
        /// </summary>
        [HarmonyPatch(typeof(FejdStartup), "Start")]
        internal static class BackendCapPatch
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                // Admission is the one that decides who actually gets in. If that rewrite was
                // refused, raising what the browser and the lobby advertise would have the
                // server offer slots it then turns away at the door, which is worse than simply
                // staying at the vanilla number. So the backend caps follow admission or they
                // do not move at all.
                if (!AdmissionCapRaised)
                {
                    Log.LogWarning("The admission cap was not raised, so the advertised and lobby capacity "
                        + "are left at the vanilla numbers as well. " + CapStaysAtVanilla);
                    return;
                }

                Log.LogInfo("Backend: " + ZNet.m_onlineBackend);
                switch (ZNet.m_onlineBackend)
                {
                    case OnlineBackendType.Steamworks:
                        Patcher.Patch(
                            AccessTools.DeclaredMethod(typeof(SteamGameServer), "SetMaxPlayerCount"),
                            prefix: new HarmonyMethod(AccessTools.DeclaredMethod(typeof(BackendCapPatch), nameof(SteamMaxPlayersPrefix))));
                        break;
                    case OnlineBackendType.PlayFab:
                        Patcher.Patch(
                            AccessTools.DeclaredMethod(typeof(ZPlayFabMatchmaking), "CreateLobby"),
                            transpiler: new HarmonyMethod(AccessTools.DeclaredMethod(typeof(BackendCapPatch), nameof(CreateLobbyTranspiler))));
                        Patcher.Patch(
                            AccessTools.DeclaredMethod(typeof(ZPlayFabMatchmaking), "CreateAndJoinNetwork"),
                            transpiler: new HarmonyMethod(AccessTools.DeclaredMethod(typeof(BackendCapPatch), nameof(CreateAndJoinNetworkTranspiler))));
                        break;
                }
            }

            private static void SteamMaxPlayersPrefix(ref int cPlayersMax)
            {
                if (MaxPlayers.Value >= 1) cPlayersMax = MaxPlayers.Value;
            }

            // Both methods embed 11, and in both it is vanilla's 10 plus one: the server itself
            // takes a lobby member slot in CreateLobbyRequest (its own entity is the owner and
            // the first member), and CreateAndJoinNetwork keeps the same reserved slot on the
            // network configuration. So both get MaxPlayers.Value + 1. Passing the bare value to
            // CreateLobby left the lobby one seat short of the number the server admits, and the
            // last player to try could not get in.
            private static IEnumerable<CodeInstruction> CreateLobbyTranspiler(IEnumerable<CodeInstruction> instructions)
                => ReplaceLobbyCap(instructions, MaxPlayers.Value + 1, "CreateLobby");

            private static IEnumerable<CodeInstruction> CreateAndJoinNetworkTranspiler(IEnumerable<CodeInstruction> instructions)
                => ReplaceLobbyCap(instructions, MaxPlayers.Value + 1, "CreateAndJoinNetwork");

            private static IEnumerable<CodeInstruction> ReplaceLobbyCap(
                IEnumerable<CodeInstruction> instructions, int cap, string label)
            {
                var patched = false;
                foreach (var ins in instructions)
                {
                    if (!patched && ins.opcode == OpCodes.Ldc_I4_S && ins.operand is sbyte b && b == 11)
                    {
                        Log.LogInfo("ZPlayFabMatchmaking." + label + ": lobby cap 11 -> " + cap);
                        ins.opcode = OpCodes.Ldc_I4;
                        ins.operand = cap;
                        patched = true;
                    }
                    yield return ins;
                }

                if (!patched)
                    Log.LogWarning("ZPlayFabMatchmaking." + label + ": expected IL pattern not found - lobby cap NOT raised.");
            }
        }
    }
}
