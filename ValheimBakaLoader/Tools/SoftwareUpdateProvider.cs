using System;
using System.Threading.Tasks;
using ValheimBakaLoader.Game;
using ValheimBakaLoader.Properties;
using ValheimBakaLoader.Tools.Logging;

namespace ValheimBakaLoader.Tools
{
    /// <summary>
    /// Startup-time "is there a newer build?" check. The actual
    /// download/install flow lives in <see cref="AppUpdateService"/>; this
    /// just logs what it finds.
    /// </summary>
    public interface ISoftwareUpdateProvider
    {
        Task CheckForUpdatesAsync(bool isManualCheck);
    }

    public class SoftwareUpdateProvider : ISoftwareUpdateProvider
    {
        private readonly IGitHubClient GitHub;
        private readonly IUserPreferencesProvider Prefs;
        private readonly IApplicationLogger Logger;

        private readonly TimeSpan CheckInterval = TimeSpan.Parse(Resources.UpdateCheckInterval);
        private DateTime NextAutomaticCheck = DateTime.MinValue;

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
                }
                else
                {
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
