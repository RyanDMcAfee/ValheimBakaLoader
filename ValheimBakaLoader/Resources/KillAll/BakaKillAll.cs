// BakaLoader KillAll v1.5.0 - compiled against SERVER assembly_valheim
// Multiple fallback approaches for finding creatures on dedicated servers.
using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Logging;
using UnityEngine;

namespace BakaLoaderKillAll
{
    [BepInPlugin("com.baka.killall", "BakaLoader KillAll", PluginVersion)]
    public class KillAllPlugin : BaseUnityPlugin
    {
        private const string PluginVersion = "1.5.0";

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
                // isCheat MUST stay false. From Valheim 1.0 the game refuses to run any
                // cheat-flagged console command unless the world is already flagged as
                // cheated, and running one flags the profile as having used cheats. This
                // command is a server-operator tool, so it is registered as a plain
                // server-only command and never taints the world or the achievements.
                isCheat: false,
                isNetwork: false,
                onlyServer: true,
                // hideBehindDevCommands is new in Valheim 1.0 and sits between
                // allowInDevBuild and optionsFetcher. Every argument here is named, so the
                // insertion cannot silently shift a value into the wrong slot.
                hideBehindDevCommands: false
            );

            Log.LogInfo("BakaLoader KillAll v" + PluginVersion + " loaded - 'baka_killall' command registered.");
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
                    if (ShouldSpare(c)) { players++; continue; }
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
                    if (ShouldSpare(c)) { players++; continue; }
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
                    if (ShouldSpare(c)) { players++; continue; }
                    if (TryKill(c)) killed++;
                }
            }

            Log.LogInfo("KillAll complete: " + killed + " creatures killed, " + players + " spared (players, pets and allies), " + allMono.Length + " total MonoBehaviours in scene.");
        }

        /// <summary>
        /// Kill-all is for hostiles. Players, tamed animals and the friendly factions are never
        /// targets, and neither is a player-built training post: it is its own faction in the
        /// game and it is a structure somebody put up, not a creature that wandered in.
        /// <para>
        /// This is a copy of Commander's rule on purpose. Either plugin can be the one that
        /// answers baka_killall (Commander answers it over its own RCON, this one registers the
        /// in-game console command), and BakaLoader's own button promises "players, pets and
        /// allies spared" whichever answers. A shorter rule here meant an operator who typed the
        /// command at the server console lost every wolf, boar and lox their players had raised.
        /// Change one of these and change the other.
        /// </para>
        /// </summary>
        private static bool ShouldSpare(Character c)
        {
            try
            {
                if (c.IsPlayer()) return true;
                if (c.IsTamed()) return true; // pets: wolves, lox, modded companions

                // Friendly and non-hostile factions. Everything else (ForestMonsters, Undead,
                // Demon, MountainMonsters, SeaMonsters, PlainsMonsters, MistlandsMonsters,
                // DeepNorth, Boss) is a hostile mob and stays killable.
                switch (c.m_faction)
                {
                    case Character.Faction.Players:       // player-faction NPCs (many modded friendlies)
                    case Character.Faction.AnimalsVeg:    // passive wildlife (deer, gulls, hares)
                    case Character.Faction.Dverger:       // dvergr allies
                    case Character.Faction.PlayerSpawned: // player-summoned allies
                    case Character.Faction.TrainingDummy: // player-built training posts
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
