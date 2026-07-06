using System;
using ValheimBakaLoader.Game;

namespace ValheimBakaLoader.Tests.Tools
{
    /// <summary>
    /// In-memory IUserPreferencesProvider: always serves default preferences
    /// and never touches disk. Saving only raises the saved event.
    /// </summary>
    public class MockUserPreferencesProvider : IUserPreferencesProvider
    {
        private readonly UserPreferences Current = UserPreferences.GetDefault();

        public event EventHandler<UserPreferences> PreferencesSaved;

        public UserPreferences LoadPreferences() => Current;

        public void SavePreferences(UserPreferences preferences)
            => PreferencesSaved?.Invoke(this, preferences);
    }
}
