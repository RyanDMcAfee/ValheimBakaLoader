using System;
using System.Collections.Generic;
using System.Linq;
using ValheimBakaLoader.Game;
using Xunit;

namespace ValheimBakaLoader.Tests.Game
{
    /// <summary>
    /// Reading a world's OWN world-modifier settings back out of the starting keys its header
    /// carries.
    /// <para>
    /// The defect this closes: a world made in the game client, or set from the console, keeps
    /// its modifiers as keys in its own header and nowhere else. Every BakaLoader start clears
    /// that list with -resetmodifiers and writes back what the profile holds, which for a world
    /// nobody had configured here was nothing at all. The world came up vanilla and its settings
    /// were gone for good. None of the reading below existed in 1.2.0.
    /// </para>
    /// </summary>
    public class WorldKeyImportTests
    {
        // ------------------------------------------------------------------ reading a header

        [Fact]
        public void A_combat_key_set_reads_back_as_the_dial_that_wrote_it()
        {
            var read = WorldKeyImport.FromHeaderKeys(new[]
            {
                "playerdamage 85", "enemydamage 150", "enemyspeedsize 110", "enemyleveluprate 120",
            });

            Assert.Equal("hard", read.Modifiers["combat"]);
            Assert.Empty(read.Keys);          // every key was accounted for by the dial
            Assert.Empty(read.PassThrough);
        }

        /// <summary>
        /// The pair that makes shortest-first wrong. Death penalty Casual is deathkeepequip
        /// plus skillreductionrate 15, and Very easy is that second key on its own, so a reading
        /// that took the first match would call a Casual world Very easy AND drop the key that
        /// keeps a viking's gear on.
        /// </summary>
        [Fact]
        public void The_longest_matching_key_set_wins_so_casual_is_not_read_as_very_easy()
        {
            var read = WorldKeyImport.FromHeaderKeys(new[] { "deathkeepequip", "skillreductionrate 15" });

            Assert.Equal("casual", read.Modifiers["deathpenalty"]);
            Assert.Empty(read.Keys);
        }

        [Fact]
        public void Very_easy_on_its_own_still_reads_as_very_easy()
        {
            var read = WorldKeyImport.FromHeaderKeys(new[] { "skillreductionrate 15" });

            Assert.Equal("veryeasy", read.Modifiers["deathpenalty"]);
        }

        [Fact]
        public void A_switch_is_read_as_a_switch_and_nothing_else()
        {
            var read = WorldKeyImport.FromHeaderKeys(new[] { "nobuildcost", "nomap" });

            Assert.Empty(read.Modifiers);
            Assert.Equal(new[] { "nobuildcost", "nomap" }, read.Switches);
            Assert.Empty(read.PassThrough);
            Assert.Equal(new[] { "nobuildcost", "nomap" }, read.Keys.OrderBy(k => k, StringComparer.Ordinal));
        }

        /// <summary>
        /// The whole point of the pass-through. A host may already carry a key no toggle on the
        /// card represents, and nothing here is allowed to drop it.
        /// </summary>
        [Fact]
        public void A_key_no_dial_and_no_switch_accounts_for_is_carried_through_untouched()
        {
            var read = WorldKeyImport.FromHeaderKeys(new[]
            {
                "carryweightrate 150", "nobuildcost", "resourcerate 150",
            });

            Assert.Equal("more", read.Modifiers["resources"]);          // a dial claimed its own key
            Assert.Equal(new[] { "nobuildcost" }, read.Switches);
            Assert.Equal(new[] { "carryweightrate 150" }, read.PassThrough);
            Assert.Contains("carryweightrate 150", read.Keys);
        }

        /// <summary>
        /// The game writes one "preset ..." line describing the whole choice, for its own menu
        /// to read back. It is a label for the other keys rather than a setting, and sending it
        /// to the game as a -setkey would hand it a description instead of a choice.
        /// </summary>
        [Fact]
        public void The_games_own_summary_line_is_named_but_never_imported()
        {
            var read = WorldKeyImport.FromHeaderKeys(new[]
            {
                "preset combat_hard:deathpenalty_casual", "playerdamage 85", "enemydamage 150",
                "enemyspeedsize 110", "enemyleveluprate 120",
            });

            Assert.Equal("hard", read.Modifiers["combat"]);
            Assert.Equal(new[] { "preset combat_hard:deathpenalty_casual" }, read.Summaries);
            Assert.DoesNotContain(read.Keys, k => k.StartsWith("preset", StringComparison.Ordinal));
        }

        [Fact]
        public void A_bare_preset_word_is_a_summary_too()
        {
            var read = WorldKeyImport.FromHeaderKeys(new[] { "preset hard", "nomap" });

            Assert.Equal(new[] { "preset hard" }, read.Summaries);
            Assert.Equal(new[] { "nomap" }, read.Keys.ToArray());
        }

        [Fact]
        public void Case_and_spacing_from_a_hand_typed_console_key_are_normalised()
        {
            var read = WorldKeyImport.FromHeaderKeys(new[] { "  NoBuildCost  ", "NOMAP" });

            Assert.Equal(new[] { "nobuildcost", "nomap" }, read.Switches);
        }

        [Fact]
        public void An_empty_header_imports_nothing_and_says_so()
        {
            Assert.False(WorldKeyImport.FromHeaderKeys(Array.Empty<string>()).Anything);
            Assert.False(WorldKeyImport.FromHeaderKeys(null).Anything);
            Assert.False(WorldKeyImport.FromHeaderKeys(new[] { "preset hard" }).Anything);
        }

        /// <summary>
        /// A full house: every dial moved, both kinds of leftover key beside them. This is the
        /// shape a world set up in the game client's own menu really arrives in.
        /// </summary>
        [Fact]
        public void A_whole_world_of_settings_reads_back_dial_by_dial()
        {
            var read = WorldKeyImport.FromHeaderKeys(new[]
            {
                "preset combat_veryhard",
                "playerdamage 70", "enemydamage 200", "enemyspeedsize 120", "enemyleveluprate 140",
                "deathdeleteitems", "deathskillsreset",
                "resourcerate 300",
                "eventrate 0",
                "noportals",
                "passivemobs", "fire",
                "carryweightrate 150",
            });

            Assert.Equal("veryhard", read.Modifiers["combat"]);
            Assert.Equal("hardcore", read.Modifiers["deathpenalty"]);
            Assert.Equal("most", read.Modifiers["resources"]);
            Assert.Equal("none", read.Modifiers["raids"]);
            Assert.Equal("veryhard", read.Modifiers["portals"]);
            Assert.Equal(new[] { "fire", "passivemobs" }, read.Switches);
            Assert.Equal(new[] { "carryweightrate 150" }, read.PassThrough);
        }

        // ------------------------------------------------------------------ the other direction

        [Fact]
        public void What_a_profile_would_put_back_is_spelled_with_the_same_table()
        {
            var keys = WorldKeyImport.KeysFor(new WorldPreferences
            {
                WorldName = "Midgard",
                Modifiers = new Dictionary<string, string> { ["combat"] = "hard" },
                Keys = new HashSet<string> { "nobuildcost", "carryweightrate 150" },
            });

            Assert.Equal(
                new[]
                {
                    "carryweightrate 150", "enemydamage 150", "enemyleveluprate 120",
                    "enemyspeedsize 110", "nobuildcost", "playerdamage 85",
                },
                keys);
        }

        /// <summary>
        /// The round trip that makes the import safe: what is read out of a header is what a
        /// start would write back into it. Without this, a world could be "imported" into a set
        /// of dials that spell out a different set of keys from the ones it had.
        /// </summary>
        [Fact]
        public void Everything_read_out_of_a_header_is_written_straight_back_into_it()
        {
            var header = new[]
            {
                "playerdamage 85", "enemydamage 150", "enemyspeedsize 110", "enemyleveluprate 120",
                "deathkeepequip", "skillreductionrate 15",
                "resourcerate 50", "eventrate 30", "teleportall",
                "nobuildcost", "nomap", "carryweightrate 150",
            };

            var read = WorldKeyImport.FromHeaderKeys(header);
            var back = WorldKeyImport.KeysFor(new WorldPreferences
            {
                WorldName = "Midgard",
                Modifiers = read.Modifiers,
                Keys = read.Keys,
            });

            Assert.Equal(WorldKeyImport.ComparableHeaderKeys(header), back);
        }

        [Fact]
        public void A_summary_line_is_not_a_disagreement_when_the_two_lists_are_compared()
        {
            var header = new[] { "preset hard", "nomap" };

            Assert.Equal(new[] { "nomap" }, WorldKeyImport.ComparableHeaderKeys(header));
        }

        // ------------------------------------------------------------------ the step itself

        [Fact]
        public void A_first_meeting_stores_what_the_world_already_held()
        {
            var worlds = new InMemoryWorlds();

            var outcome = WorldKeyImportStep.Run(
                worlds, () => new[] { "nobuildcost", "resourcerate 150" }, "Midgard");

            Assert.Equal(WorldKeyImportKind.Imported, outcome.Kind);

            var stored = worlds.LoadPreferences("Midgard");
            Assert.Equal("more", stored.Modifiers["resources"]);
            Assert.Equal(new[] { "nobuildcost" }, stored.Keys.ToArray());
        }

        [Fact]
        public void A_world_the_profile_already_holds_is_never_read_over()
        {
            var worlds = new InMemoryWorlds();
            worlds.SavePreferences(new WorldPreferences
            {
                WorldName = "Midgard",
                Modifiers = new Dictionary<string, string> { ["combat"] = "easy" },
            });

            var outcome = WorldKeyImportStep.Run(
                worlds, () => throw new InvalidOperationException("the header must not be read"), "Midgard");

            Assert.Equal(WorldKeyImportKind.AlreadyKnown, outcome.Kind);
            Assert.Equal("easy", worlds.LoadPreferences("Midgard").Modifiers["combat"]);
        }

        [Fact]
        public void An_unreadable_header_imports_nothing_and_writes_nothing()
        {
            var worlds = new InMemoryWorlds();

            var outcome = WorldKeyImportStep.Run(worlds, () => null, "Midgard");

            Assert.Equal(WorldKeyImportKind.NoHeader, outcome.Kind);
            Assert.Null(worlds.LoadPreferences("Midgard"));
        }

        [Fact]
        public void A_world_with_no_modifiers_on_it_is_left_unclaimed()
        {
            var worlds = new InMemoryWorlds();

            var outcome = WorldKeyImportStep.Run(worlds, () => Array.Empty<string>(), "Midgard");

            Assert.Equal(WorldKeyImportKind.NothingToBringIn, outcome.Kind);
            Assert.Null(worlds.LoadPreferences("Midgard"));
        }

        /// <summary>
        /// The second start. Once a world's settings are in the profile they are the host's own,
        /// and the header is not read again: a key the host turned OFF in BakaLoader must not
        /// come back the next time the app opens.
        /// </summary>
        [Fact]
        public void A_second_run_changes_nothing()
        {
            var worlds = new InMemoryWorlds();
            WorldKeyImportStep.Run(worlds, () => new[] { "nobuildcost" }, "Midgard");

            var afterFirst = worlds.LoadPreferences("Midgard").Keys.ToArray();

            var again = WorldKeyImportStep.Run(worlds, () => new[] { "nomap", "passivemobs" }, "Midgard");

            Assert.Equal(WorldKeyImportKind.AlreadyKnown, again.Kind);
            Assert.Equal(afterFirst, worlds.LoadPreferences("Midgard").Keys.ToArray());
        }

        /// <summary>
        /// An in-memory world store: the one thing the step writes to, held in a list so the
        /// test can read back exactly what was stored.
        /// </summary>
        internal sealed class InMemoryWorlds : IWorldPreferencesProvider
        {
            private readonly List<WorldPreferences> Stored = new();

            public event EventHandler<List<WorldPreferences>> PreferencesSaved;

            public WorldPreferences LoadPreferences(string worldName)
                => Stored.FirstOrDefault(w => string.Equals(w.WorldName, worldName, StringComparison.Ordinal));

            public IEnumerable<WorldPreferences> LoadPreferences() => Stored.ToList();

            public void SavePreferences(WorldPreferences preferences)
            {
                if (preferences == null) return;
                Stored.RemoveAll(w => string.Equals(w.WorldName, preferences.WorldName, StringComparison.Ordinal));
                preferences.LastSaved = DateTime.UtcNow;
                Stored.Add(preferences);
                PreferencesSaved?.Invoke(this, Stored.ToList());
            }

            public void RemovePreferences(string worldName)
                => Stored.RemoveAll(w => string.Equals(w.WorldName, worldName, StringComparison.Ordinal));
        }
    }
}
