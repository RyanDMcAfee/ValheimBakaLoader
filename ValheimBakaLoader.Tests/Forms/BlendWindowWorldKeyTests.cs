using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Serilog;
using ValheimBakaLoader.Forms;
using ValheimBakaLoader.Game;
using ValheimBakaLoader.Tests.Tools;
using ValheimBakaLoader.Tools;
using Xunit;

namespace ValheimBakaLoader.Tests.Forms
{
    /// <summary>
    /// The world-SWITCH seam: the gate that validates the five toggles
    /// (<see cref="BlendWindow.ParseWorldKeys"/>), the merge that keeps everything the toggles
    /// do not speak for (<see cref="BlendWindow.MergeWorldKeys"/>), and the round trip that
    /// stores them against a world.
    /// <para>
    /// In 1.2.0 there was no writer at all: <c>worldgen.save</c> and <c>servers.create</c> took
    /// no keys, <c>worldgen.get</c> returned none, and the only way a key ever reached the
    /// command line was for somebody to have written one into userprefs.json by hand. A host
    /// could not keep No build cost, and the reset at every start took it away.
    /// </para>
    /// </summary>
    public class BlendWindowWorldKeyTests
    {
        private static string Bridge() => AppSourceTree.Files()["BlendWindow.Bridge.cs"];

        // ---------------------------------------------------------------- ParseWorldKeys

        [Fact]
        public void Every_one_of_the_five_switches_is_accepted()
        {
            var keys = BlendWindow.ParseWorldKeys(JArray.FromObject(WorldGen.Switches.ToArray()));

            Assert.Equal(WorldGen.Switches.OrderBy(k => k, StringComparer.Ordinal),
                keys.OrderBy(k => k, StringComparer.Ordinal));
        }

        /// <summary>
        /// Absent is not empty, and the difference is a world's switches. A page that knows
        /// nothing about them sends no list at all, and that has to leave the stored ones alone;
        /// an empty list is a host who turned every switch off, and that clears them.
        /// </summary>
        [Fact]
        public void An_absent_list_and_an_empty_one_are_different_answers()
        {
            Assert.Null(BlendWindow.ParseWorldKeys(null));
            Assert.Null(BlendWindow.ParseWorldKeys(JValue.CreateNull()));

            var empty = BlendWindow.ParseWorldKeys(new JArray());
            Assert.NotNull(empty);
            Assert.Empty(empty);
        }

        [Fact]
        public void A_name_that_is_not_a_switch_is_refused_by_name()
        {
            var refused = Assert.Throws<HostFacingException>(
                () => BlendWindow.ParseWorldKeys(JArray.FromObject(new[] { "nobuildcost", "nocraftcost" })));

            Assert.Equal("worldgen.unknownKey", refused.MessageId);
            Assert.Equal("nocraftcost", refused.Params["key"]);
        }

        /// <summary>
        /// A value key is not a switch either, whatever name it carries. The card draws five
        /// toggles and this list is those five; a key with a value in it reaches the store
        /// through the pass-through and never through here.
        /// </summary>
        [Fact]
        public void A_value_key_wearing_a_switch_name_is_refused_too()
        {
            var refused = Assert.Throws<HostFacingException>(
                () => BlendWindow.ParseWorldKeys(JArray.FromObject(new[] { "nomap 5" })));

            Assert.Equal("worldgen.unknownKey", refused.MessageId);
        }

        [Fact]
        public void Something_that_is_not_a_list_at_all_is_refused()
        {
            var refused = Assert.Throws<HostFacingException>(
                () => BlendWindow.ParseWorldKeys(JToken.FromObject("nobuildcost")));

            Assert.Equal("worldgen.keysNotAList", refused.MessageId);
        }

        [Fact]
        public void Case_and_spacing_are_normalised_rather_than_refused()
        {
            var keys = BlendWindow.ParseWorldKeys(JArray.FromObject(new[] { " NoBuildCost ", "NOMAP", "" }));

            Assert.Equal(new[] { "nobuildcost", "nomap" }, keys.OrderBy(k => k, StringComparer.Ordinal));
        }

        // ---------------------------------------------------------------- MergeWorldKeys

        /// <summary>
        /// The pass-through, which is the whole reason the merge exists rather than a plain
        /// replace. A host carrying a console-set key has never been shown a toggle for it, so
        /// saving the toggles cannot be read as a statement about it.
        /// </summary>
        [Fact]
        public void A_key_no_toggle_represents_survives_a_save_of_the_toggles()
        {
            var merged = BlendWindow.MergeWorldKeys(
                new[] { "carryweightrate 150", "nomap" },
                new HashSet<string> { "nobuildcost" });

            Assert.Equal(new[] { "carryweightrate 150", "nobuildcost" },
                merged.OrderBy(k => k, StringComparer.Ordinal));
        }

        [Fact]
        public void An_absent_list_leaves_the_whole_stored_set_alone()
        {
            var merged = BlendWindow.MergeWorldKeys(new[] { "nomap", "carryweightrate 150" }, null);

            Assert.Equal(new[] { "carryweightrate 150", "nomap" },
                merged.OrderBy(k => k, StringComparer.Ordinal));
        }

        [Fact]
        public void An_empty_list_clears_the_switches_and_only_the_switches()
        {
            var merged = BlendWindow.MergeWorldKeys(
                new[] { "nomap", "fire", "carryweightrate 150" },
                new HashSet<string>());

            Assert.Equal(new[] { "carryweightrate 150" }, merged.ToArray());
        }

        // ---------------------------------------------------------------- the contract

        /// <summary>
        /// THE contract, and the one 1.2.0 could not meet: every switch saved comes back. It is
        /// run through the real world-preferences store over a real userprefs document, because
        /// the place a "saved but never returned" bug actually lives is the on-disk shape, where
        /// a field that is not serialised looks perfect on both sides and is gone in the middle.
        /// </summary>
        [Fact]
        public void Every_switch_saved_through_worldgen_save_comes_back_from_worldgen_get()
        {
            var worlds = new WorldPreferencesProvider(new RoundTrippingUserPrefs(), Quiet());

            foreach (var chosen in WorldGen.Switches)
            {
                // worldgen.save, line for line.
                var switches = BlendWindow.ParseWorldKeys(JArray.FromObject(new[] { chosen }));
                var prefs = worlds.LoadPreferences("Midgard") ?? new WorldPreferences { WorldName = "Midgard" };
                prefs.Preset = null;
                prefs.Modifiers = BlendWindow.ParseWorldModifiers(JObject.FromObject(new { combat = "hard" }));
                prefs.Keys = BlendWindow.MergeWorldKeys(prefs.Keys, switches);
                worlds.SavePreferences(prefs);

                // worldgen.get, line for line.
                var back = worlds.LoadPreferences("Midgard");
                Assert.NotNull(back);
                Assert.Contains(chosen, back.Keys);
                Assert.Equal("hard", back.Modifiers["combat"]);
            }
        }

        [Fact]
        public void All_five_at_once_come_back_all_five_at_once()
        {
            var worlds = new WorldPreferencesProvider(new RoundTrippingUserPrefs(), Quiet());

            var switches = BlendWindow.ParseWorldKeys(JArray.FromObject(WorldGen.Switches.ToArray()));
            worlds.SavePreferences(new WorldPreferences
            {
                WorldName = "Midgard",
                Keys = BlendWindow.MergeWorldKeys(null, switches),
            });

            Assert.Equal(
                WorldGen.Switches.OrderBy(k => k, StringComparer.Ordinal),
                worlds.LoadPreferences("Midgard").Keys.OrderBy(k => k, StringComparer.Ordinal));
        }

        [Fact]
        public void A_pass_through_key_survives_the_whole_round_trip_to_disk_and_back()
        {
            var worlds = new WorldPreferencesProvider(new RoundTrippingUserPrefs(), Quiet());
            worlds.SavePreferences(new WorldPreferences
            {
                WorldName = "Midgard",
                Keys = new HashSet<string> { "carryweightrate 150" },
            });

            var saved = worlds.LoadPreferences("Midgard");
            saved.Keys = BlendWindow.MergeWorldKeys(
                saved.Keys, BlendWindow.ParseWorldKeys(JArray.FromObject(new[] { "nobuildcost" })));
            worlds.SavePreferences(saved);

            Assert.Equal(new[] { "carryweightrate 150", "nobuildcost" },
                worlds.LoadPreferences("Midgard").Keys.OrderBy(k => k, StringComparer.Ordinal));
        }

        // ---------------------------------------------------------------- the wiring

        /// <summary>
        /// The three calls really do take and answer with the keys. These are gates on the
        /// source because the handlers live inside a window this suite cannot build, and a
        /// handler that quietly stopped reading <c>keys</c> would fail nothing above.
        /// </summary>
        [Fact]
        public void The_three_calls_that_touch_a_world_all_speak_switches()
        {
            var bridge = Bridge();

            // worldgen.save and servers.create both go through the one gate.
            Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(bridge, @"ParseWorldKeys\(p\[""keys""\]\)").Count);

            // and both write the merge rather than a replace
            Assert.Contains("prefs.Keys = MergeWorldKeys(prefs.Keys, chosenSwitches);", bridge, StringComparison.Ordinal);
            Assert.Contains("worldPrefs.Keys = MergeWorldKeys(worldPrefs.Keys, worldSwitches);", bridge, StringComparison.Ordinal);

            // worldgen.get hands the stored keys back whole, and draws the two split views off
            // that one list rather than off a second read that could disagree with it
            Assert.Contains("var keys = WorldKeyList(prefs);", bridge, StringComparison.Ordinal);
            Assert.Contains("switches = keys.Where(WorldGen.IsSwitch).ToList(),", bridge, StringComparison.Ordinal);
            Assert.Contains("passThrough = keys.Where(k => !WorldGen.IsSwitch(k)).ToList(),", bridge, StringComparison.Ordinal);
        }

        /// <summary>
        /// The first meeting hangs off the ONE place every start path builds its options, which
        /// is what puts it on auto-start, a crash relaunch and a scheduled restart without three
        /// separate call sites to keep in step. A realm forged over an existing world gets its
        /// own call, because that path writes a world entry before any start happens.
        /// </summary>
        [Fact]
        public void The_first_meeting_sits_on_every_start_path()
        {
            var bridge = Bridge();

            var inOptions = bridge.IndexOf("private ValheimServerOptions BuildServerOptions(", StringComparison.Ordinal);
            Assert.True(inOptions > 0, "BuildServerOptions is gone");
            Assert.Contains("ImportWorldKeysOnFirstMeeting(",
                bridge.Substring(inOptions, Math.Min(1400, bridge.Length - inOptions)), StringComparison.Ordinal);

            // and realm creation runs it before it writes the wizard's own choices
            var create = bridge.IndexOf("RegisterRpc(\"servers.create\"", StringComparison.Ordinal);
            Assert.True(create > 0, "servers.create is gone");
            var body = bridge.Substring(create, bridge.IndexOf("RegisterRpc(\"worlds.listOrphans\"", StringComparison.Ordinal) - create);
            var imported = body.IndexOf("ImportWorldKeysOnFirstMeeting(", StringComparison.Ordinal);
            var written = body.IndexOf("worldPrefs.Keys = MergeWorldKeys(", StringComparison.Ordinal);
            Assert.True(imported > 0, "a realm forged over an existing world never meets it");
            Assert.True(written > imported, "the wizard's choices must land on top of the import");
        }

        /// <summary>
        /// The import must never be the reason a world does not come back up. Every one of its
        /// paths is unattended: nobody is there to answer a card at 4am.
        /// </summary>
        [Fact]
        public void The_first_meeting_never_throws_and_never_blocks()
        {
            var bridge = Bridge();
            var at = bridge.IndexOf("private void ImportWorldKeysOnFirstMeeting(", StringComparison.Ordinal);
            Assert.True(at > 0, "the first meeting is gone");

            var body = bridge.Substring(at, bridge.IndexOf("private void LogWorldKeyDisagreement(", StringComparison.Ordinal) - at);

            Assert.Contains("try", body, StringComparison.Ordinal);
            Assert.Contains("catch (Exception e)", body, StringComparison.Ordinal);
            // nothing that waits for a person
            Assert.DoesNotContain("MessageBox", body, StringComparison.Ordinal);
            Assert.DoesNotContain("TaskDialog", body, StringComparison.Ordinal);
            Assert.DoesNotContain("await ", body, StringComparison.Ordinal);
        }

        /// <summary>
        /// A later disagreement is ONE line and no dialog, and it is said once rather than on
        /// every status poll: the window holds what it last said so a poll every two seconds
        /// cannot turn one fact into a wall of Saga.
        /// </summary>
        [Fact]
        public void A_later_disagreement_is_one_line_and_is_not_repeated()
        {
            var bridge = Bridge();
            var at = bridge.IndexOf("private void LogWorldKeyDisagreement(", StringComparison.Ordinal);
            Assert.True(at > 0, "the disagreement line is gone");

            var body = bridge.Substring(at, Math.Min(2200, bridge.Length - at));

            Assert.Contains("_worldKeyDisagreements", body, StringComparison.Ordinal);
            Assert.Contains("The start writes the profile's set.", body, StringComparison.Ordinal);
            Assert.DoesNotContain("SavePreferences", body, StringComparison.Ordinal);   // never writes
            Assert.DoesNotContain("MessageBox", body, StringComparison.Ordinal);
        }

        /// <summary>The notice the page draws, and the call that takes it away once it has.</summary>
        [Fact]
        public void What_was_brought_in_is_offered_to_the_page_once()
        {
            var bridge = Bridge();

            Assert.Contains("PostEvent(\"worldgen.imported\", notice);", bridge, StringComparison.Ordinal);
            Assert.Contains("imported = WorldKeyImportNotice(world),", bridge, StringComparison.Ordinal);
            Assert.Contains("RegisterRpc(\"worldgen.noticeSeen\"", bridge, StringComparison.Ordinal);
        }

        // ---------------------------------------------------------------- helpers

        private static ILogger Quiet() => new LoggerConfiguration().CreateLogger();

        /// <summary>
        /// An in-memory stand-in for userprefs.json: every load hands back its own copy of the
        /// document and a save replaces it wholesale, so worlds genuinely round-trip through the
        /// same file shape the app writes.
        /// </summary>
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
