using Serilog;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ValheimBakaLoader.Game;
using Xunit;

namespace ValheimBakaLoader.Tests.Game
{
    /// <summary>
    /// Server profiles, worlds and the user's own settings all live in one userprefs.json, so a
    /// caller that loads the document, edits its own corner and saves is not editing one field:
    /// it is writing every field, with whatever the rest of them looked like when it loaded.
    /// <para>
    /// The section writers took a gate for exactly that reason. The whole document writers did
    /// not, and one of them is the Discord status post, which saves its message id from a timer
    /// thread while the server's own thread records the build a launch ran. The later of the two
    /// put the other back the way it was, and a profile that lost its launch history got asked
    /// about a build change that had already happened. Every read modify write of that file now
    /// goes through <see cref="IUserPreferencesProvider.Mutate(Action{UserPreferences})"/>.
    /// </para>
    /// </summary>
    public class UserPreferencesMutateTests
    {
        [Fact]
        public async Task A_whole_document_write_racing_a_profile_save_keeps_both()
        {
            var root = new RoundTrippingUserPrefs { LoadDelayMs = 120 };
            IUserPreferencesProvider prefsFile = root;
            var servers = new ServerPreferencesProvider(root, Quiet());

            // Exactly the pairing that lost the launch history: the Discord status id going in
            // on one thread while a profile is written on another.
            var statusPost = Task.Run(() => prefsFile.Mutate(prefs => prefs.DiscordStatusMessageId = "1234567890"));
            var launchRecord = Task.Run(() => servers.SavePreferences(
                new ServerPreferences { ProfileName = "Alpha", Name = "Alpha", LastLaunchedServerBuild = "18452031" }));

            await Task.WhenAll(statusPost, launchRecord);

            var landed = root.LoadPreferences();
            Assert.Equal("1234567890", landed.DiscordStatusMessageId);

            var profile = Assert.Single(landed.Servers);
            Assert.Equal("Alpha", profile.ProfileName);
            Assert.Equal("18452031", profile.LastLaunchedServerBuild);
        }

        [Fact]
        public async Task Two_whole_document_writes_do_not_overwrite_each_other()
        {
            IUserPreferencesProvider prefsFile = new RoundTrippingUserPrefs { LoadDelayMs = 120 };

            var windowSize = Task.Run(() => prefsFile.Mutate(prefs => prefs.WindowBounds = "1600x1000"));
            var statusPost = Task.Run(() => prefsFile.Mutate(prefs => prefs.DiscordStatusMessageId = "abc"));

            await Task.WhenAll(windowSize, statusPost);

            var landed = prefsFile.LoadPreferences();
            Assert.Equal("1600x1000", landed.WindowBounds);
            Assert.Equal("abc", landed.DiscordStatusMessageId);
        }

        [Fact]
        public void A_change_that_answers_false_writes_nothing()
        {
            // The window size is saved on every resize and on close, and nearly always has
            // nothing new to say. A no-op must not cost a disk write or a saved event.
            IUserPreferencesProvider prefsFile = new RoundTrippingUserPrefs();
            var saves = 0;
            prefsFile.PreferencesSaved += (_, _) => saves++;

            Assert.False(prefsFile.Mutate(_ => false));
            Assert.Equal(0, saves);

            Assert.True(prefsFile.Mutate(prefs =>
            {
                prefs.WindowBounds = "1280x800";
                return true;
            }));
            Assert.Equal(1, saves);
            Assert.Equal("1280x800", prefsFile.LoadPreferences().WindowBounds);
        }

        private static ILogger Quiet() => new LoggerConfiguration().CreateLogger();

        /// <summary>
        /// Stands in for the real file: every load hands back its own copy of the document and a
        /// save replaces it wholesale, exactly like reading and rewriting userprefs.json. The
        /// delay widens the window between the two so the race is deterministic rather than a
        /// coin flip.
        /// </summary>
        private sealed class RoundTrippingUserPrefs : IUserPreferencesProvider
        {
            private UserPreferencesFile Stored = UserPreferences.GetDefault().ToFile();

            public int LoadDelayMs;

            public event EventHandler<UserPreferences> PreferencesSaved;

            public UserPreferences LoadPreferences()
            {
                var snapshot = Stored;
                if (LoadDelayMs > 0) Thread.Sleep(LoadDelayMs);
                return UserPreferences.FromFile(snapshot);
            }

            public void SavePreferences(UserPreferences preferences)
            {
                Stored = preferences.ToFile();
                PreferencesSaved?.Invoke(this, preferences);
            }
        }
    }
}
