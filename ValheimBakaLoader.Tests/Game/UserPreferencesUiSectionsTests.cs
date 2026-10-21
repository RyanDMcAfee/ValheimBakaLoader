using System.Collections.Generic;
using ValheimBakaLoader.Game;
using Xunit;

namespace ValheimBakaLoader.Tests.Game
{
    /// <summary>
    /// Which sections of the page stand open (1.2.18, the simpler sidebar) has to survive a
    /// round trip through userprefs.json, and a document written before the key existed has
    /// to come back empty rather than null, so the page's own defaults apply exactly once.
    /// See UserPreferences.UiSections and SECTIONS in app.js.
    /// </summary>
    public class UserPreferencesUiSectionsTests
    {
        [Fact]
        public void OpenSectionsRoundTripThroughTheFile()
        {
            var prefs = UserPreferences.GetDefault();
            prefs.UiSections = new Dictionary<string, bool> { ["saga"] = true, ["skald"] = false, ["herald"] = true };

            var restored = UserPreferences.FromFile(prefs.ToFile());

            Assert.Equal(prefs.UiSections, restored.UiSections);
        }

        [Fact]
        public void AFileWrittenBeforeTheKeyExistedComesBackEmpty()
        {
            var restored = UserPreferences.FromFile(new UserPreferencesFile());

            Assert.NotNull(restored.UiSections);
            Assert.Empty(restored.UiSections);
        }

        [Fact]
        public void ANamelessEntryIsDroppedOnTheWayIn()
        {
            var restored = UserPreferences.FromFile(new UserPreferencesFile
            {
                UiSections = new Dictionary<string, bool> { [""] = true, ["   "] = false, ["runes"] = true },
            });

            Assert.Equal(new Dictionary<string, bool> { ["runes"] = true }, restored.UiSections);
        }
    }
}
