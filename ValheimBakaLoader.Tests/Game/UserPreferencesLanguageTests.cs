using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Linq;
using System.Reflection;
using ValheimBakaLoader.Game;
using ValheimBakaLoader.Tests.Tools;
using Xunit;

namespace ValheimBakaLoader.Tests.Game
{
    /// <summary>
    /// The language preferences, and the gate that stops the next one being lost.
    /// <para>
    /// FromFile and ToFile are hand written maps. A property added to the model and to only
    /// one of them is read back as its default forever, silently, on the next save: the host
    /// picks Japanese, the app writes the whole document, and the language key is simply not
    /// in it. That has no failing symptom anybody would report as a bug, so it is caught here
    /// by reflection over the model rather than by anybody remembering.
    /// </para>
    /// </summary>
    public class UserPreferencesLanguageTests
    {
        [Fact]
        public void A_fresh_install_is_in_english_and_speaks_to_players_in_the_same_language()
        {
            var defaults = UserPreferences.GetDefault();

            Assert.Equal("en", defaults.Language);
            Assert.Equal("same", defaults.PlayerMessageLanguage);
        }

        [Theory]
        [InlineData("ru", "same")]
        [InlineData("ja", "en")]
        [InlineData("zh-Hans", "ja")]
        [InlineData("zh-Hant", "zh-Hant")]
        public void The_chosen_language_survives_a_trip_to_disk_and_back(string language, string players)
        {
            var saved = UserPreferences.GetDefault();
            saved.Language = language;
            saved.PlayerMessageLanguage = players;

            var read = UserPreferences.FromFile(saved.ToFile());

            Assert.Equal(language, read.Language);
            Assert.Equal(players, read.PlayerMessageLanguage);
        }

        /// <summary>
        /// The JSON key names are a compatibility contract with installs that already exist.
        /// These two are new, so this is the moment they are fixed.
        /// </summary>
        [Fact]
        public void The_keys_written_to_the_file_are_the_ones_the_file_format_names()
        {
            var prefs = UserPreferences.GetDefault();
            prefs.Language = "ja";
            prefs.PlayerMessageLanguage = "en";

            var written = JObject.Parse(JsonConvert.SerializeObject(prefs.ToFile()));

            Assert.Equal("ja", (string)written["language"]);
            Assert.Equal("en", (string)written["playerMessageLanguage"]);
        }

        /// <summary>An older file has neither key, and an older file must still open.</summary>
        [Fact]
        public void A_file_written_before_these_existed_opens_in_english()
        {
            var older = JsonConvert.DeserializeObject<UserPreferencesFile>("{\"darkMode\":true}");

            var read = UserPreferences.FromFile(older);

            Assert.Equal("en", read.Language);
            Assert.Equal("same", read.PlayerMessageLanguage);
        }

        /// <summary>
        /// The language is written from a download completion callback, which is the exact
        /// thread shape that lost a launch record once: a whole document write racing another
        /// whole document write. It goes through Mutate, under the one gate, like every other
        /// read modify write of that file.
        /// </summary>
        [Fact]
        public void The_language_can_be_written_through_the_gate_without_losing_its_neighbours()
        {
            IUserPreferencesProvider prefs = new RoundTripping();

            prefs.Mutate(p => p.DiscordStatusMessageId = "1234567890");
            prefs.Mutate(p =>
            {
                p.Language = "ru";
                p.PlayerMessageLanguage = "en";
            });

            var landed = prefs.LoadPreferences();
            Assert.Equal("ru", landed.Language);
            Assert.Equal("en", landed.PlayerMessageLanguage);
            Assert.Equal("1234567890", landed.DiscordStatusMessageId);
        }

        // ------------------------------------------------------------------ the gate

        /// <summary>
        /// Every public property of the model appears in both maps, and has somewhere to live
        /// in the file. This is the whole class of bug caught once: the next person to add a
        /// preference cannot half add it.
        /// </summary>
        [Fact]
        public void Every_preference_is_carried_both_ways_and_has_a_home_on_disk()
        {
            var source = AppSourceTree.Files()["UserPreferences.cs"];

            var fromFile = source.IndexOf("public static UserPreferences FromFile", StringComparison.Ordinal);
            var toFile = source.IndexOf("public UserPreferencesFile ToFile", StringComparison.Ordinal);

            Assert.True(fromFile > 0, "FromFile is not where this gate expects it");
            Assert.True(toFile > fromFile, "ToFile is not where this gate expects it");

            var reading = source[fromFile..toFile];
            var writing = source[toFile..];

            var fileProperties = typeof(UserPreferencesFile)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(p => p.Name)
                .ToHashSet(StringComparer.Ordinal);

            foreach (var property in typeof(UserPreferences)
                         .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                         .Where(p => p.CanRead && p.CanWrite))
            {
                Assert.True(
                    reading.Contains(property.Name + " =", StringComparison.Ordinal),
                    property.Name + " is not read back in UserPreferences.FromFile, so a saved value is lost on load.");

                Assert.True(
                    writing.Contains(property.Name + " =", StringComparison.Ordinal),
                    property.Name + " is not written in UserPreferences.ToFile, so it is dropped on every save.");

                Assert.True(
                    fileProperties.Contains(property.Name),
                    property.Name + " has no place in UserPreferencesFile, so it has nowhere on disk to go.");
            }
        }

        [Fact]
        public void The_two_language_preferences_are_covered_by_that_gate()
        {
            // Named out loud, so that the reflection above cannot pass by holding no opinion
            // about these two in particular.
            var names = typeof(UserPreferences)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(p => p.Name)
                .ToList();

            Assert.Contains("Language", names);
            Assert.Contains("PlayerMessageLanguage", names);
        }

        /// <summary>The file, in memory: every load its own copy, every save the whole document.</summary>
        private sealed class RoundTripping : IUserPreferencesProvider
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
