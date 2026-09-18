using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ValheimBakaLoader.Game;
using Xunit;

namespace ValheimBakaLoader.Tests.Game
{
    /// <summary>
    /// The two BepInEx preferences, and the rule that catches the next one added after them.
    /// <para>
    /// A preference is not one thing, it is six: a property with a default, a nullable field
    /// on the file model, a line in FromFile, a line in ToFile, a line in the save handler and
    /// a key in the dto the page reads. Miss the FromFile line and the setting reads as its
    /// default on the next launch; miss the ToFile line and it is never written at all. Both
    /// failures look exactly like "the switch does not stick", and neither one is a compile
    /// error. So the round trip is walked by reflection over the whole model rather than
    /// asserted key by key: the test then covers preferences nobody has written yet.
    /// </para>
    /// </summary>
    public class UserPreferencesBepInExTests
    {
        [Fact]
        public void Looking_after_bepinex_is_on_by_default_and_the_question_starts_unasked()
        {
            var defaults = UserPreferences.GetDefault();

            Assert.True(defaults.BepInExMaintained);
            Assert.False(defaults.BepInExMaintenanceAsked);
        }

        [Fact]
        public void A_file_that_predates_the_setting_reads_as_the_default()
        {
            // Every key null is exactly what an older userprefs.json looks like.
            var prefs = UserPreferences.FromFile(new UserPreferencesFile());

            Assert.True(prefs.BepInExMaintained);
            Assert.False(prefs.BepInExMaintenanceAsked);
        }

        [Theory]
        [InlineData(true, true)]
        [InlineData(false, true)]
        [InlineData(false, false)]
        [InlineData(true, false)]
        public void Both_answers_survive_a_write_and_a_read(bool maintained, bool asked)
        {
            var prefs = UserPreferences.GetDefault();
            prefs.BepInExMaintained = maintained;
            prefs.BepInExMaintenanceAsked = asked;

            var again = UserPreferences.FromFile(prefs.ToFile());

            Assert.Equal(maintained, again.BepInExMaintained);
            Assert.Equal(asked, again.BepInExMaintenanceAsked);
        }

        /// <summary>
        /// Every simple preference, walked. Each one is set to something that is NOT its
        /// default, written, read back, and compared: a property the file model does not carry,
        /// or that FromFile or ToFile forgets, comes back as its default and is named here.
        /// </summary>
        [Fact]
        public void Every_preference_survives_the_round_trip_to_the_file_model_and_back()
        {
            var prefs = UserPreferences.GetDefault();
            var changed = new Dictionary<string, object>(StringComparer.Ordinal);

            foreach (var property in Simple())
            {
                var current = property.GetValue(prefs);
                var swapped = Different(property.PropertyType, current);
                property.SetValue(prefs, swapped);
                changed[property.Name] = swapped;
            }

            var again = UserPreferences.FromFile(prefs.ToFile());

            var lost = Simple()
                .Where(property => !Equals(property.GetValue(again), changed[property.Name]))
                .Select(property => property.Name)
                .ToList();

            Assert.True(lost.Count == 0,
                "these preferences do not survive ToFile then FromFile: " + string.Join(", ", lost));
        }

        /// <summary>
        /// The same walk from the other side: every simple preference has a field on the file
        /// model to be written into. A property with no field can never be saved at all, and
        /// the round trip above would only catch it because the value came back wrong.
        /// </summary>
        [Fact]
        public void Every_preference_has_a_place_on_disk_to_live()
        {
            var onDisk = typeof(UserPreferencesFile)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(p => p.Name)
                .ToHashSet(StringComparer.Ordinal);

            var homeless = Simple()
                .Where(property => !onDisk.Contains(property.Name))
                .Select(property => property.Name)
                .ToList();

            Assert.True(homeless.Count == 0,
                "these preferences have no field on the file model: " + string.Join(", ", homeless));
        }

        /// <summary>
        /// The bool and string preferences. The two lists (servers, worlds) round trip through
        /// their own models and have their own tests; this walk is about the flat ones, which
        /// is where a forgotten line hides.
        /// </summary>
        private static IEnumerable<PropertyInfo> Simple() =>
            typeof(UserPreferences)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanRead && p.CanWrite)
                .Where(p => p.PropertyType == typeof(bool) || p.PropertyType == typeof(string))
                // Paths are validated on the way in by the save handler, not by the model, and
                // a made-up one is still a string: nothing here is excluded for being a path.
                .OrderBy(p => p.Name, StringComparer.Ordinal);

        private static object Different(Type type, object current)
        {
            if (type == typeof(bool)) return !(bool)current;
            return (current as string) == "a value that was not there before"
                ? "a second value that was not there before"
                : "a value that was not there before";
        }
    }
}
