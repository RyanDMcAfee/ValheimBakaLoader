// BakaLoader KillAll v1.2.0 - compiled against SERVER assembly_valheim
// Multiple fallback approaches for finding creatures on dedicated servers.
using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Logging;
using UnityEngine;

namespace BakaLoaderKillAll
{
    [BepInPlugin("com.baka.killall", "BakaLoader KillAll", "1.2.0")]
    public class KillAllPlugin : BaseUnityPlugin
    {
        private static ManualLogSource Log;
        private static volatile bool KillPending;

        private void Awake()
        {
            Log = Logger;

            new Terminal.ConsoleCommand(
                "baka_killall",
                "baka_killall - Kill all non-player creatures in loaded zones (main-thread dispatch)",
                delegate(Terminal.ConsoleEventArgs args)
                {
                    KillPending = true;
                    args.Context.AddString("Queued kill-all creatures...");
                },
                true,
                false,
                true
            );

            Log.LogInfo("BakaLoader KillAll v1.2.0 loaded - 'baka_killall' command registered.");
        }

        private void Update()
        {
            if (!KillPending) return;
            KillPending = false;

            try
            {
                ExecuteKillAll();
            }
            catch (Exception ex)
            {
                Log.LogError("KillAll failed: " + ex.Message + "\n" + ex.StackTrace);
            }
        }

        private void ExecuteKillAll()
        {
            // Approach 1: static list
            List<Character> staticList = Character.GetAllCharacters();
            Log.LogInfo("KillAll diag: GetAllCharacters() = " + staticList.Count);

            // Approach 2: scene scan
            Character[] sceneChars = Resources.FindObjectsOfTypeAll<Character>();
            Log.LogInfo("KillAll diag: FindObjectsOfTypeAll<Character> = " + sceneChars.Length);

            // Approach 3: broader MonoBehaviour scan to verify Unity scene scan works at all
            MonoBehaviour[] allMono = UnityEngine.Object.FindObjectsOfType<MonoBehaviour>();
            Log.LogInfo("KillAll diag: FindObjectsOfType<MonoBehaviour> = " + allMono.Length);

            // Use whichever found more characters
            int killed = 0;
            int players = 0;

            // If static list has entries, prefer it (fastest)
            if (staticList.Count > 0)
            {
                Log.LogInfo("KillAll: using GetAllCharacters path");
                for (int i = staticList.Count - 1; i >= 0; i--)
                {
                    Character c = staticList[i];
                    if (c == null) continue;
                    if (IsPlayerSafe(c)) { players++; continue; }
                    if (TryKill(c)) killed++;
                }
            }
            // Else try scene scan
            else if (sceneChars.Length > 0)
            {
                Log.LogInfo("KillAll: using FindObjectsOfTypeAll path");
                for (int i = 0; i < sceneChars.Length; i++)
                {
                    Character c = sceneChars[i];
                    if (c == null) continue;
                    if (IsPlayerSafe(c)) { players++; continue; }
                    if (TryKill(c)) killed++;
                }
            }
            // Else brute force: scan all MonoBehaviours for Character
            else if (allMono.Length > 0)
            {
                Log.LogInfo("KillAll: using MonoBehaviour brute force path");
                for (int i = 0; i < allMono.Length; i++)
                {
                    Character c = allMono[i] as Character;
                    if (c == null) continue;
                    if (IsPlayerSafe(c)) { players++; continue; }
                    if (TryKill(c)) killed++;
                }
            }

            Log.LogInfo("KillAll complete: " + killed + " creatures killed, " + players + " players spared, " + allMono.Length + " total MonoBehaviours in scene.");
        }

        private static bool IsPlayerSafe(Character c)
        {
            try { return c.IsPlayer(); }
            catch { return true; } // if in doubt, don't kill
        }

        private static bool TryKill(Character c)
        {
            try
            {
                HitData hit = new HitData();
                hit.m_damage.m_damage = 1e10f;
                hit.m_point = c.transform.position;
                hit.m_dodgeable = false;
                hit.m_blockable = false;
                c.Damage(hit);
                return true;
            }
            catch (Exception ex)
            {
                Log.LogWarning("Failed to kill " + c.name + ": " + ex.Message);
                return false;
            }
        }
    }
}
