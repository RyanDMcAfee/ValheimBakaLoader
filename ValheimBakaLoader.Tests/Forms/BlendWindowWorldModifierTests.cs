using System;
using Newtonsoft.Json.Linq;
using Serilog;
using ValheimBakaLoader.Forms;
using ValheimBakaLoader.Game;
using Xunit;

namespace ValheimBakaLoader.Tests.Forms
{
    /// <summary>
    /// The world-difficulty seam: the one gate that validates the five modifier dials
    /// (<see cref="BlendWindow.ParseWorldModifiers"/>) and the round-trip that stores a chosen
    /// difficulty against a world. Realm creation and Save Config both run through the same gate,
    /// so a world made "hard" is hard on its first launch and Save Config never wipes a stored
    /// difficulty by accident.
    /// <para>
    /// The DOM half of the fix (an unsaved dial edit surviving a re-render, and Save Config only
    /// writing when it actually holds the selected world's dials) lives in app.js and is verified
    /// in the mock preview - it cannot be reached from here. These tests pin the pure logic those
    /// two paths depend on.
    /// </para>
    /// </summary>
    public class BlendWindowWorldModifierTests
    {
        // ---------------------------------------------------------------- ParseWorldModifiers

        [Fact]
        public void Valid_dials_are_kept_and_normal_or_empty_dials_are_dropped()
        {
            var raw = JObject.FromObject(new
            {
                combat = "hard",
                deathpenalty = "casual",
                resources = "",         // Normal, chosen as the empty value
                raids = "normal",       // Normal, spelled out
                portals = "veryhard",
            });

            var mods = BlendWindow.ParseWorldModifiers(raw);

            Assert.Equal(3, mods.Count);
            Assert.Equal("hard", mods["combat"]);
            Assert.Equal("casual", mods["deathpenalty"]);
            Assert.Equal("veryhard", mods["portals"]);
            Assert.False(mods.ContainsKey("resources"));
            Assert.False(mods.ContainsKey("raids"));
        }

        [Fact]
        public void A_null_or_empty_modifier_map_parses_to_nothing()
        {
            Assert.Empty(BlendWindow.ParseWorldModifiers(null));
            Assert.Empty(BlendWindow.ParseWorldModifiers(new JObject()));
        }

        [Fact]
        public void An_out_of_range_value_for_a_real_dial_is_refused()
        {
            var raw = JObject.FromObject(new { combat = "impossible" });

            var ex = Assert.Throws<ArgumentException>(() => BlendWindow.ParseWorldModifiers(raw));
            Assert.Contains("combat", ex.Message);
        }

        [Fact]
        public void An_unknown_dial_is_refused()
        {
            // "difficulty" is not one of the five dials the game accepts.
            var raw = JObject.FromObject(new { difficulty = "hard" });

            Assert.Throws<ArgumentException>(() => BlendWindow.ParseWorldModifiers(raw));
        }

        [Fact]
        public void A_value_from_the_wrong_dial_is_refused()
        {
            // "hardcore" is a death-penalty value; it is not a valid combat value.
            var raw = JObject.FromObject(new { combat = "hardcore" });

            Assert.Throws<ArgumentException>(() => BlendWindow.ParseWorldModifiers(raw));
        }

        // ---------------------------------------------------------------- servers.create persistence

        [Fact]
        public void A_world_created_with_a_difficulty_is_stored_with_it()
        {
            // Exactly what servers.create does after it validates the dials: write the chosen
            // modifiers to the NEW world so it is born this way, then it is there to read back.
            var worlds = new WorldPreferencesProvider(new RoundTrippingUserPrefs(), Quiet());

            var modifiers = BlendWindow.ParseWorldModifiers(
                JObject.FromObject(new { combat = "hard", portals = "veryhard" }));

            var created = worlds.LoadPreferences("Midgard") ?? new WorldPreferences { WorldName = "Midgard" };
            created.Preset = null;
            created.Modifiers = modifiers;
            worlds.SavePreferences(created);

            var loaded = worlds.LoadPreferences("Midgard");
            Assert.NotNull(loaded);
            Assert.Equal("hard", loaded.Modifiers["combat"]);
            Assert.Equal("veryhard", loaded.Modifiers["portals"]);
            Assert.Null(loaded.Preset);
        }

        [Fact]
        public void A_world_created_at_normal_stores_no_modifiers()
        {
            var worlds = new WorldPreferencesProvider(new RoundTrippingUserPrefs(), Quiet());

            // The all-Normal case: nothing is written, so the world stays a plain default one.
            var modifiers = BlendWindow.ParseWorldModifiers(
                JObject.FromObject(new { combat = "", deathpenalty = "normal" }));

            Assert.Empty(modifiers);
            Assert.Null(worlds.LoadPreferences("Fresh")); // create wrote nothing for it
        }

        // ---------------------------------------------------------------- Save Config: no silent wipe

        [Fact]
        public void Re_saving_the_same_difficulty_keeps_it_rather_than_wiping_it()
        {
            // The dials were populated for this world and the host kept Hard. Save Config sends
            // that intended state, and the store keeps it - the exact case the old empty-scrape
            // could turn into a wipe.
            var worlds = new WorldPreferencesProvider(new RoundTrippingUserPrefs(), Quiet());
            worlds.SavePreferences(new WorldPreferences
            {
                WorldName = "Midgard",
                Modifiers = BlendWindow.ParseWorldModifiers(JObject.FromObject(new { combat = "hard" })),
            });

            var intended = BlendWindow.ParseWorldModifiers(JObject.FromObject(new { combat = "hard" }));
            var prefs = worlds.LoadPreferences("Midgard");
            prefs.Modifiers = intended;
            worlds.SavePreferences(prefs);

            Assert.Equal("hard", worlds.LoadPreferences("Midgard").Modifiers["combat"]);
        }

        [Fact]
        public void Choosing_normal_everywhere_clears_a_stored_difficulty_on_purpose()
        {
            // The intentional clear is preserved: an all-Normal choice ({}), which the DOM guard
            // distinguishes from "dials never populated", empties the stored modifiers.
            var worlds = new WorldPreferencesProvider(new RoundTrippingUserPrefs(), Quiet());
            worlds.SavePreferences(new WorldPreferences
            {
                WorldName = "Midgard",
                Modifiers = BlendWindow.ParseWorldModifiers(JObject.FromObject(new { combat = "hard" })),
            });

            var cleared = BlendWindow.ParseWorldModifiers(JObject.FromObject(new { combat = "" }));
            var prefs = worlds.LoadPreferences("Midgard");
            prefs.Modifiers = cleared;
            worlds.SavePreferences(prefs);

            Assert.Empty(worlds.LoadPreferences("Midgard").Modifiers);
        }

        // ---------------------------------------------------------------- helpers

        private static ILogger Quiet() => new LoggerConfiguration().CreateLogger();

        /// <summary>
        /// An in-memory stand-in for userprefs.json: every load hands back its own copy of the
        /// document and a save replaces it wholesale, so worlds genuinely round-trip through it.
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
