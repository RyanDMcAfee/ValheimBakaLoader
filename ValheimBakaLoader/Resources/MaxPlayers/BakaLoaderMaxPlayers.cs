// BakaLoader MaxPlayers v1.0.0 - compiled against SERVER assembly_valheim.
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
// once the game has picked one (FejdStartup.Start postfix).
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
        public const string PluginVersion = "1.0.0";

        private static ManualLogSource Log;
        private static ConfigEntry<int> MaxPlayers;
        private static Harmony Patcher;

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
                        Log.LogInfo("ZNet.RPC_PeerInfo: admission cap " + codes[j].operand + " -> " + MaxPlayers.Value);
                        codes[j].opcode = OpCodes.Ldc_I4;
                        codes[j].operand = MaxPlayers.Value;
                        patched = true;
                        break;
                    }
                }

                if (!patched)
                    Log.LogWarning("ZNet.RPC_PeerInfo: expected IL pattern not found - admission cap NOT raised (game update may have changed the method).");
                return codes;
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

            // CreateLobby carries the cap itself; CreateAndJoinNetwork keeps the game's
            // +1 reserved-slot convention (the 11 both embed is vanilla's 10 + 1).
            private static IEnumerable<CodeInstruction> CreateLobbyTranspiler(IEnumerable<CodeInstruction> instructions)
                => ReplaceLobbyCap(instructions, MaxPlayers.Value, "CreateLobby");

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
