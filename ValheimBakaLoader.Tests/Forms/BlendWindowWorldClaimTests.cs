using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Serilog;
using ValheimBakaLoader.Forms;
using ValheimBakaLoader.Game;
using ValheimBakaLoader.Tests.Tools;
using Xunit;

namespace ValheimBakaLoader.Tests.Forms
{
    /// <summary>
    /// CLAIMING A WORLD. Every handler that writes a world-prefs entry has to meet the world
    /// first, because writing that entry is the very thing that makes a world already known.
    /// <para>
    /// A world made in the game client keeps its modifiers as starting keys in its own header,
    /// and BakaLoader clears that whole list at every start and writes back what the profile
    /// holds. The first meeting is what puts the world's own settings into the profile before
    /// that happens. Get it out of order once and the world is claimed with an entry nobody
    /// read the header for: every later start finds the world already known, never opens the
    /// header again, and the next -resetmodifiers takes the world's settings off it for good.
    /// </para>
    /// <para>
    /// Two handlers had it wrong when 1.2.1 was first written. <c>worldgen.save</c> never met
    /// the world at all, and it is reached by the FIRST-TIME SETUP WIZARD on a brand new
    /// install, which is exactly the population the import exists for. <c>servers.create</c>
    /// met it and then replaced the whole dial map with the one dial the forge wizard had
    /// moved, throwing away the four it had just read off the world.
    /// </para>
    /// </summary>
    public class BlendWindowWorldClaimTests
    {
        private static string Bridge() => AppSourceTree.Files()["BlendWindow.Bridge.cs"];

        private static string HandlerBody(string open, string next)
        {
            var bridge = Bridge();
            var at = bridge.IndexOf(open, StringComparison.Ordinal);
            Assert.True(at > 0, open + " is gone");
            var end = bridge.IndexOf(next, at, StringComparison.Ordinal);
            Assert.True(end > at, next + " is gone");
            return bridge.Substring(at, end - at);
        }

        // ---------------------------------------------------------------- worldgen.save

        /// <summary>
        /// The save meets the world BEFORE it loads the entry it is about to write. Not merely
        /// somewhere in the handler: the load below it is what decides whether there is an entry,
        /// and an import that ran after the save would find the world already known.
        /// </summary>
        [Fact]
        public void A_world_is_met_before_a_save_can_claim_it()
        {
            var body = HandlerBody("RegisterRpc(\"worldgen.save\"", "RegisterRpc(\"worldgen.noticeSeen\"");

            var met = body.IndexOf(
                "ImportWorldKeysOnFirstMeeting(world, ResolveSaveDataFolder(null));", StringComparison.Ordinal);
            var loaded = body.IndexOf("WorldPrefsProvider.LoadPreferences(world)", StringComparison.Ordinal);
            var saved = body.IndexOf("WorldPrefsProvider.SavePreferences(prefs);", StringComparison.Ordinal);

            Assert.True(met > 0, "worldgen.save claims a world it never read the header of");
            Assert.True(loaded > met, "the entry is loaded before the world is met, so the meeting is too late");
            Assert.True(saved > met, "the entry is written before the world is met");
        }

        /// <summary>
        /// And the one call that reaches it on a brand new install asks for a MERGE. The wizard
        /// sends only the dials the host moved, so a replace would clear the four the world was
        /// just read to be carrying while the wizard's own screen still showed them as Normal.
        /// </summary>
        [Fact]
        public void The_setup_wizard_saves_the_dials_it_moved_without_clearing_the_rest()
        {
            var page = AppSourceTree.Web("app.js");
            Assert.Contains(
                "await rpc(\"worldgen.save\",{world,modifiers:WIZ.mods,mergeModifiers:true});",
                page, StringComparison.Ordinal);

            var body = HandlerBody("RegisterRpc(\"worldgen.save\"", "RegisterRpc(\"worldgen.noticeSeen\"");
            Assert.Contains("p.Value<bool?>(\"mergeModifiers\") == true", body, StringComparison.Ordinal);
            Assert.Contains("foreach (var pair in modifiers) merged[pair.Key] = pair.Value;", body, StringComparison.Ordinal);
            // and Save Config still replaces, because that page sends the whole dial state
            Assert.Contains("prefs.Modifiers = modifiers;", body, StringComparison.Ordinal);
        }

        /// <summary>
        /// The order, driven rather than read. A save that claims the world first leaves the
        /// header unread for ever, which is the 1.2.0 outcome the feature exists to stop; the
        /// save that meets it first finds the dials and switches already there.
        /// </summary>
        [Fact]
        public void A_save_that_claims_first_loses_the_world_and_one_that_meets_first_keeps_it()
        {
            // The live world's own shape: Combat on Hard, and a switch beside it.
            var header = new List<string>
            {
                "playerdamage 85", "enemydamage 150", "enemyspeedsize 110", "enemyleveluprate 120",
                "nobuildcost", "preset combat_hard",
            };

            // THE WRONG ORDER: the save writes its entry and the meeting happens afterwards.
            var claimedFirst = new WorldPreferencesProvider(new RoundTrippingUserPrefs(), Quiet());
            claimedFirst.SavePreferences(new WorldPreferences
            {
                WorldName = "Testbed",
                Modifiers = BlendWindow.ParseWorldModifiers(new JObject()),
                Keys = BlendWindow.MergeWorldKeys(null, BlendWindow.ParseWorldKeys(new JArray())),
            });
            var tooLate = WorldKeyImportStep.Run(claimedFirst, () => header, "Testbed");

            Assert.Equal(WorldKeyImportKind.AlreadyKnown, tooLate.Kind);
            Assert.Empty(claimedFirst.LoadPreferences("Testbed").Modifiers);
            Assert.Empty(claimedFirst.LoadPreferences("Testbed").Keys);

            // THE ORDER THE HANDLER TAKES NOW: met first, then the save lands on top.
            var metFirst = new WorldPreferencesProvider(new RoundTrippingUserPrefs(), Quiet());
            var inTime = WorldKeyImportStep.Run(metFirst, () => header, "Testbed");
            Assert.Equal(WorldKeyImportKind.Imported, inTime.Kind);

            var prefs = metFirst.LoadPreferences("Testbed");
            var merged = new Dictionary<string, string>(prefs.Modifiers);
            foreach (var pair in BlendWindow.ParseWorldModifiers(JObject.FromObject(new { raids = "less" })))
                merged[pair.Key] = pair.Value;
            prefs.Modifiers = merged;
            prefs.Keys = BlendWindow.MergeWorldKeys(prefs.Keys, null);
            metFirst.SavePreferences(prefs);

            var kept = metFirst.LoadPreferences("Testbed");
            Assert.Equal("hard", kept.Modifiers["combat"]);   // read off the world
            Assert.Equal("less", kept.Modifiers["raids"]);    // chosen in the wizard
            Assert.Contains("nobuildcost", kept.Keys);
            Assert.DoesNotContain("preset combat_hard", kept.Keys);   // the game's own summary line
        }

        // ---------------------------------------------------------------- servers.create

        /// <summary>
        /// Forging a realm over an existing world writes only the dials the forge wizard moved,
        /// OVER what the import just read, rather than in place of it. All five of the forge's
        /// dials start at Normal and only the moved ones are sent, so silence about a dial there
        /// is not a statement about it: a one-dial wizard is no more a statement about the other
        /// four than an all-Normal one is about all five.
        /// </summary>
        [Fact]
        public void Forging_a_realm_over_a_world_keeps_the_dials_it_just_imported()
        {
            var body = HandlerBody("RegisterRpc(\"servers.create\"", "RegisterRpc(\"worlds.listOrphans\"");

            Assert.Contains(
                "foreach (var pair in worldModifiers) worldPrefs.Modifiers[pair.Key] = pair.Value;",
                body, StringComparison.Ordinal);
            Assert.DoesNotContain(
                "if (worldModifiers.Count > 0) worldPrefs.Modifiers = worldModifiers;", body, StringComparison.Ordinal);
        }

        /// <summary>
        /// The same thing driven, against the shape a real world carries: five dials and two
        /// switches read off the header, one dial moved in the forge, everything else still
        /// there afterwards. Before the fix the stored map held the one moved dial and nothing
        /// else, while the dialog still showed Normal for the four it had thrown away.
        /// </summary>
        [Fact]
        public void One_dial_moved_in_the_forge_does_not_take_the_other_four_with_it()
        {
            var header = new List<string>
            {
                // Combat very hard
                "playerdamage 70", "enemydamage 200", "enemyspeedsize 120", "enemyleveluprate 140",
                // Resources more, Raids less, Portals hard
                "resourcerate 150", "eventrate 150", "nobossportals",
                // and a switch plus a key nothing draws
                "nomap", "carryweightrate 150",
            };

            var worlds = new WorldPreferencesProvider(new RoundTrippingUserPrefs(), Quiet());
            Assert.Equal(WorldKeyImportKind.Imported, WorldKeyImportStep.Run(worlds, () => header, "Midgard").Kind);

            // servers.create, line for line, with Combat moved to Hard in the forge.
            var worldModifiers = BlendWindow.ParseWorldModifiers(JObject.FromObject(new { combat = "hard" }));
            var worldSwitches = BlendWindow.ParseWorldKeys(null);   // the forge sends no switch list
            var prefs = worlds.LoadPreferences("Midgard") ?? new WorldPreferences { WorldName = "Midgard" };
            prefs.Preset = null;
            if (worldModifiers.Count > 0)
            {
                if (prefs.Modifiers == null) prefs.Modifiers = new Dictionary<string, string>();
                foreach (var pair in worldModifiers) prefs.Modifiers[pair.Key] = pair.Value;
            }
            prefs.Keys = BlendWindow.MergeWorldKeys(prefs.Keys, worldSwitches);
            worlds.SavePreferences(prefs);

            var stored = worlds.LoadPreferences("Midgard");
            Assert.Equal("hard", stored.Modifiers["combat"]);       // the forge's own choice wins
            Assert.Equal("more", stored.Modifiers["resources"]);    // and the rest are still here
            Assert.Equal("less", stored.Modifiers["raids"]);
            Assert.Equal("hard", stored.Modifiers["portals"]);
            Assert.Contains("nomap", stored.Keys);
            Assert.Contains("carryweightrate 150", stored.Keys);
        }

        // ---------------------------------------------------------------- helpers

        private static ILogger Quiet() => new LoggerConfiguration().CreateLogger();

        private sealed class RoundTrippingUserPrefs : IUserPreferencesProvider
        {
            private UserPreferencesFile Stored = UserPreferences.GetDefault().ToFile();

            public event EventHandler<UserPreferences> PreferencesSaved;

            public UserPreferences LoadPreferences() => UserPreferences.FromFile(Stored);

            public void SavePreferences(UserPreferences preferences)
            {
                Stored = preferences.ToFile();
                PreferencesSaved?.Invoke(this, preferences);
            }
        }
    }
}
