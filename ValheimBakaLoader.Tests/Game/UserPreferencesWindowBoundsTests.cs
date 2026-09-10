using ValheimBakaLoader.Game;
using Xunit;

namespace ValheimBakaLoader.Tests.Game
{
    /// <summary>
    /// The remembered window size and state have to survive a round trip through
    /// userprefs.json, and an install written before the keys existed has to keep
    /// opening at the designed default rather than at whatever a null parses to.
    /// See BlendWindow.ApplyDpiSizing / SaveWindowBounds.
    /// </summary>
    public class UserPreferencesWindowBoundsTests
    {
        [Fact]
        public void WindowKeysRoundTripThroughTheFile()
        {
            var prefs = UserPreferences.GetDefault();
            prefs.WindowBounds = "1600x900";
            prefs.WindowMaximized = true;

            var restored = UserPreferences.FromFile(prefs.ToFile());

            Assert.Equal("1600x900", restored.WindowBounds);
            Assert.True(restored.WindowMaximized);
        }

        [Fact]
        public void AFileWrittenBeforeTheKeysExistedFallsBackToTheDefault()
        {
            // Every key nullable: an older userprefs.json simply has neither of these.
            var restored = UserPreferences.FromFile(new UserPreferencesFile());

            Assert.Null(restored.WindowBounds);
            Assert.False(restored.WindowMaximized);
        }

        [Fact]
        public void TheTerminologyPreferenceKeepsItsStoredMeaning()
        {
            // The switch was relabelled "Show Norse names" and shows the INVERSE of this
            // flag, so nothing on disk had to be migrated: PlainTerminology still means
            // "plain English only".
            var prefs = UserPreferences.GetDefault();
            Assert.False(prefs.PlainTerminology);

            prefs.PlainTerminology = true;
            Assert.True(UserPreferences.FromFile(prefs.ToFile()).PlainTerminology);
        }
    }
}
