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

        /// <summary>
        /// Starts the quiet re-check that runs for as long as the app does. BakaLoader is left
        /// running for weeks at a time on a server box, so a check made only at launch means the
        /// host hears about a release months after it shipped. Safe to call more than once: the
        /// first call is the one that takes.
        /// </summary>
        void StartPeriodicChecks();
    }

    public class SoftwareUpdateProvider : ISoftwareUpdateProvider
    {
        private readonly IGitHubClient GitHub;
        private readonly IUserPreferencesProvider Prefs;
        private readonly IApplicationLogger Logger;

        private readonly TimeSpan CheckInterval = TimeSpan.Parse(Resources.UpdateCheckInterval);
        private DateTime NextAutomaticCheck = DateTime.MinValue;

        /// <summary>
        /// How often the running app wakes up to ask whether it is time for another check. The
        /// tick is modest and the check itself is throttled to <see cref="CheckInterval"/>, so
        /// most ticks cost nothing at all and GitHub sees one request per interval per instance.
        /// </summary>
        private static readonly TimeSpan PollInterval = TimeSpan.FromHours(1);

        private System.Threading.Timer PeriodicTimer;
        private readonly object PeriodicLock = new();

        /// <summary>
        /// The version this provider has already announced in this run. The check now repeats
        /// every few hours for as long as the app is open, and announcing the same release each
        /// time would put a standing row the host dismissed back on their screen twice a day.
        /// Dismissing it is an answer about that version, so only a different one asks again.
        /// </summary>
        private string LastAnnouncedVersion;

        public event EventHandler<AppUpdateAvailability> UpdateAvailable;

        public AppUpdateAvailability LatestAvailable { get; private set; }

        public SoftwareUpdateProvider(IGitHubClient gitHub, IUserPreferencesProvider prefs, IApplicationLogger logger)
        {
            GitHub = gitHub;
            Prefs = prefs;
            Logger = logger;
        }

        public void StartPeriodicChecks()
        {
            lock (PeriodicLock)
            {
                if (PeriodicTimer != null) return;

                // The first tick is a whole interval away: the launch check has just run, and
                // repeating it the moment the window opens would only hit GitHub twice for the
                // same answer. Every tick reads the switch fresh, so turning update checking off
                // in Upkeep stops the next one without any wiring between the two.
                PeriodicTimer = new System.Threading.Timer(_ => PeriodicTick(), null, PollInterval, PollInterval);
            }

            Logger.Debug("Update checks will keep running every {hours}h while the app is open.", PollInterval.TotalHours);
        }

        private void PeriodicTick()
        {
            try
            {
                if (!Prefs.LoadPreferences().CheckForUpdates) return;

                // Fire and forget on the timer's own thread. CheckForUpdatesAsync swallows its
                // own failures, and the throttle inside it decides whether this tick reaches
                // GitHub at all.
                _ = CheckForUpdatesAsync(false);
            }
            catch (Exception e)
            {
                Logger.Warning("A scheduled update check could not start: {message}", e.Message);
            }
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

                    // The first sight of a version is the announcement; every later check that
                    // finds the same one keeps LatestAvailable fresh and says nothing, so a row
                    // the host has already dealt with stays dealt with until a NEWER release
                    // turns up. Stamped before the event so a listener that throws cannot make
                    // the next check announce it all over again.
                    var alreadySaid = string.Equals(latest, LastAnnouncedVersion, StringComparison.OrdinalIgnoreCase);
                    LastAnnouncedVersion = latest;
                    if (alreadySaid)
                    {
                        Logger.Debug("Release {version} was already announced this run, so nothing was raised again.", latest);
                        return;
                    }

                    // Best effort: a listener that throws must not turn a routine check into
                    // a failed launch step.
                    try { UpdateAvailable?.Invoke(this, found); }
                    catch (Exception e) { Logger.Warning("Could not announce the available update: {message}", e.Message); }
                }
                else
                {
                    LatestAvailable = null;

                    // The check got through and there is nothing newer, so whatever was announced
                    // before is over: the release was pulled, or a re-upload briefly left it with
                    // no files on it. Forgetting it here is what lets the same version be
                    // announced again if it comes back; keeping it would leave the standing row
                    // cleared and nothing to put it back. A failed check never reaches this line,
                    // so an outage cannot make the app repeat itself.
                    LastAnnouncedVersion = null;
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
