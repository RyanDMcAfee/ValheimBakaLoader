using System;
using System.IO;
using ValheimBakaLoader.Game;

namespace ValheimBakaLoader.Tests.Tools
{
    /// <summary>
    /// In-memory IUserPreferencesProvider: always serves default preferences
    /// and never touches disk. Saving only raises the saved event.
    /// <para>
    /// One default is not left alone. WriteApplicationLogsToFile is true out of the box and
    /// LogsFolderPath is empty, which sends the file sink to the folder the installed app writes
    /// its real log to. That is how a test run on 2026-09-20 wrote test lines into the owner's
    /// live ApplicationLogs file. The run has no business in that folder, so this hands every
    /// logger a folder of its own under the temp directory instead. ValheimServerOptions reads the
    /// same preference for the per-session server log, so both sinks land there.
    /// </para>
    /// </summary>
    public class MockUserPreferencesProvider : IUserPreferencesProvider
    {
        /// <summary>
        /// The folder every test's log file goes to. One per process, so a run cleans up as one
        /// thing and two tests writing at once cannot fight over a name.
        /// </summary>
        public static readonly string TestLogsFolder = Path.Combine(
            Path.GetTempPath(), "vbl-test-logs-" + Environment.ProcessId);

        private readonly UserPreferences Current = Defaults();

        private static UserPreferences Defaults()
        {
            var prefs = UserPreferences.GetDefault();
            prefs.LogsFolderPath = TestLogsFolder;
            return prefs;
        }

        public event EventHandler<UserPreferences> PreferencesSaved;

        public UserPreferences LoadPreferences() => Current;

        public void SavePreferences(UserPreferences preferences)
            => PreferencesSaved?.Invoke(this, preferences);
    }
}
