using System;
using System.Threading.Tasks;
using ValheimBakaLoader.Game;
using ValheimBakaLoader.Properties;
using ValheimBakaLoader.Tools.Logging;

namespace ValheimBakaLoader.Tools
{
    /// <summary>
    /// What the check found: a release newer than the running build. Handed to
    /// <see cref="ISoftwareUpdateProvider.UpdateAvailable"/> and kept on the provider
    /// so a window that was not on screen yet can still ask for it later.
    /// </summary>
    public sealed class AppUpdateAvailability
    {
        /// <summary>The release tag, as GitHub reports it ("1.0.2").</summary>
        public string Version { get; init; }

        /// <summary>The release page on github.com, or null when GitHub did not give one.</summary>
        public string NotesUrl { get; init; }
    }

    /// <summary>
    /// Startup-time "is there a newer build?" check. The actual
    /// download/install flow lives in <see cref="AppUpdateService"/>; this
    /// finds the newest release, keeps it, and says so.
    /// </summary>
    public interface ISoftwareUpdateProvider
    {
        Task CheckForUpdatesAsync(bool isManualCheck);

        /// <summary>
        /// Fires once per check that found a newer release. The check runs while the app is
        /// still on the splash, so a listener that arrives later reads
        /// <see cref="LatestAvailable"/> instead of waiting for another event.
        /// </summary>
        event EventHandler<AppUpdateAvailability> UpdateAvailable;

        /// <summary>The newest release this session found, or null when there is nothing newer.</summary>
        AppUpdateAvailability LatestAvailable { get; }
    }

    public class SoftwareUpdateProvider : ISoftwareUpdateProvider
    {
        private readonly IGitHubClient GitHub;
        private readonly IUserPreferencesProvider Prefs;
        private readonly IApplicationLogger Logger;

        private readonly TimeSpan CheckInterval = TimeSpan.Parse(Resources.UpdateCheckInterval);
        private DateTime NextAutomaticCheck = DateTime.MinValue;

        public event EventHandler<AppUpdateAvailability> UpdateAvailable;

        public AppUpdateAvailability LatestAvailable { get; private set; }

        public SoftwareUpdateProvider(IGitHubClient gitHub, IUserPreferencesProvider prefs, IApplicationLogger logger)
        {
            GitHub = gitHub;
            Prefs = prefs;
            Logger = logger;
        }

        public async Task CheckForUpdatesAsync(bool isManualCheck)
        {
            if (!isManualCheck)
            {
                // Automatic checks are throttled and honor the user's opt-out.
                var now = DateTime.UtcNow;
                if (now < NextAutomaticCheck) return;
                NextAutomaticCheck = now + CheckInterval;

                if (!Prefs.LoadPreferences().CheckForUpdates) return;
            }

            try
            {
                // A repo with no releases yet reports null; that counts as
                // up to date.
                var release = await GitHub.GetLatestReleaseAsync();
                var latest = release?.TagName;

                if (latest != null && AssemblyHelper.CompareVersion(latest) == 1)
                {
                    Logger.Information("A newer ValheimBakaLoader release is available: {version}", latest);

                    var found = new AppUpdateAvailability
                    {
                        Version = latest,
                        NotesUrl = release.HtmlUrl,
                    };
                    LatestAvailable = found;

                    // Best effort: a listener that throws must not turn a routine check into
                    // a failed launch step.
                    try { UpdateAvailable?.Invoke(this, found); }
                    catch (Exception e) { Logger.Warning("Could not announce the available update: {message}", e.Message); }
                }
                else
                {
                    LatestAvailable = null;
                    Logger.Information("ValheimBakaLoader is up to date ({version})", AssemblyHelper.GetApplicationVersion());
                }
            }
            catch (Exception e)
            {
                Logger.Warning("Update check failed: {message}", e.Message);
            }
        }
    }
}
