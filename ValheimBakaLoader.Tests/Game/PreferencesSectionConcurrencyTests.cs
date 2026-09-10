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
    /// Server profiles, worlds and the user's own settings all live in one userprefs.json, so
    /// saving any one of them is really "load the whole document, change one entry, write the
    /// whole document back". Two of those running at once used to lose one of the changes: both
    /// loaded the same document, and whichever wrote last threw the other away. The server's own
    /// stdout thread does one of these (recording the launched build) while the UI thread does
    /// another (saving a profile edit), so this is not a theoretical pairing.
    /// </summary>
    public class PreferencesSectionConcurrencyTests
    {
        [Fact]
        public async Task Two_profiles_saved_at_once_both_survive()
        {
            var root = new RoundTrippingUserPrefs { LoadDelayMs = 120 };
            var servers = new ServerPreferencesProvider(root, Quiet());

            var first = Task.Run(() => servers.SavePreferences(Profile("Alpha")));
            var second = Task.Run(() => servers.SavePreferences(Profile("Beta")));
            await Task.WhenAll(first, second);

            var names = servers.LoadPreferences().Select(p => p.ProfileName).OrderBy(n => n).ToList();
            Assert.Equal(new[] { "Alpha", "Beta" }, names);
        }

        [Fact]
        public async Task A_profile_save_and_a_world_save_do_not_overwrite_each_other()
        {
            // The two sections are different generic types over the same physical file, which
            // is exactly the pair a per-type lock would fail to serialize.
            var root = new RoundTrippingUserPrefs { LoadDelayMs = 120 };
            var servers = new ServerPreferencesProvider(root, Quiet());
            var worlds = new WorldPreferencesProvider(root, Quiet());

            var first = Task.Run(() => servers.SavePreferences(Profile("Alpha")));
            var second = Task.Run(() => worlds.SavePreferences(new WorldPreferences { WorldName = "Midgard" }));
            await Task.WhenAll(first, second);

            Assert.Single(servers.LoadPreferences());
            Assert.Single(worlds.LoadPreferences());
        }

        [Fact]
        public async Task A_removal_racing_a_save_keeps_the_saved_profile()
        {
            var root = new RoundTrippingUserPrefs();
            var servers = new ServerPreferencesProvider(root, Quiet());
            servers.SavePreferences(Profile("Gamma"));

            root.LoadDelayMs = 120;
            var save = Task.Run(() => servers.SavePreferences(Profile("Alpha")));
            var remove = Task.Run(() => servers.RemovePreferences("Gamma"));
            await Task.WhenAll(save, remove);

            var names = servers.LoadPreferences().Select(p => p.ProfileName).ToList();
            Assert.Contains("Alpha", names);
            Assert.DoesNotContain("Gamma", names);
        }

        private static ServerPreferences Profile(string name) => new() { ProfileName = name, Name = name };

        private static ILogger Quiet() => new LoggerConfiguration().CreateLogger();

        /// <summary>
        /// Stands in for the real file: every load hands back its own copy of the document, and
        /// a save replaces it wholesale, exactly like reading and rewriting userprefs.json. The
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
