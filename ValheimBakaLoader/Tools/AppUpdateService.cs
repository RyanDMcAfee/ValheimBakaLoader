using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using ValheimBakaLoader.Game;
using ValheimBakaLoader.Tools.Http;
using ValheimBakaLoader.Tools.Logging;

namespace ValheimBakaLoader.Tools
{
    /// <summary>
    /// What came of asking GitHub for a newer BakaLoader.
    /// <para>
    /// The unattended callers only ever needed "did it stage one", and a plain false was enough
    /// for them. The host standing in front of the update dialog needs more than that: a machine
    /// with no internet and a machine that is already on the newest release both answered false,
    /// so an offline app told the host it was up to date, which it had no way of knowing.
    /// </para>
    /// </summary>
    public enum StageOutcome
    {
        /// <summary>Downloaded, checked, and the watchdog is armed: the app must now close.</summary>
        Staged,

        /// <summary>The check got through and this build is the newest one there is.</summary>
        AlreadyCurrent,

        /// <summary>
        /// The check got through and there was nothing installable at the other end: no release
        /// at all, no zip on the one it found, or a zip without BakaLoader in it.
        /// </summary>
        NoRelease,

        /// <summary>The check or the download never got through.</summary>
        NetworkError,

        /// <summary>The settings say not to, so nothing was asked of GitHub.</summary>
        SwitchedOff,
    }

    public interface IAppUpdateService
    {
        /// <summary>
        /// Checks GitHub for a newer BakaLoader release and, if one is found, downloads it
        /// and stages a headless watchdog process that will wait for this app to exit,
        /// replace the installed files with the new version, relaunch BakaLoader, and then
        /// clean up after itself. Returns <c>true</c> when an update was staged (the caller
        /// should then close the app to let the watchdog take over), or <c>false</c> when the
        /// app is already current or the update could not be staged.
        /// <para>
        /// <paramref name="userInitiated"/> is set by the one path where the host asked for this
        /// by name (the update dialog's own button). An unattended check still needs both Upkeep
        /// switches; a host who just clicked Update has said the second one out loud, so being
        /// turned away because auto-update is off would be the app arguing with them.
        /// </para>
        /// </summary>
        Task<bool> CheckAndStageUpdateAsync(bool userInitiated = false);

        /// <summary>
        /// The same check, saying WHY when it did not stage anything. The dialog's own button
        /// puts that reason into a sentence, so a host whose machine is offline is told that
        /// rather than being told they are already up to date.
        /// </summary>
        Task<StageOutcome> TryStageUpdateAsync(bool userInitiated = false);
    }

    /// <summary>
    /// Self-update installer for BakaLoader. The download-and-swap has to happen while the
    /// app is NOT running (its own files are locked and the child Valheim server is tied to
    /// this process via a Job Object), so the actual file replacement is delegated to a
    /// detached, windowless PowerShell "watchdog" that outlives the app:
    ///
    ///   1. BakaLoader downloads the new release zip and verifies it contains the exe.
    ///   2. BakaLoader writes + launches the hidden watchdog, then closes.
    ///   3. The watchdog waits for BakaLoader's PID to exit, extracts the zip, robocopies
    ///      the new files over the install directory, relaunches BakaLoader, and deletes
    ///      its own temp working folder.
    ///
    /// On relaunch, the normal startup path auto-starts any profile whose AutoStart pref is
    /// on, so the Valheim server comes back up without any special relaunch arguments.
    /// </summary>
    public class AppUpdateService : IAppUpdateService
    {
        private const string ExeName = "ValheimBakaLoader.exe";
        private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(5);

        private readonly IGitHubClient GitHub;
        private readonly IHttpClientProvider HttpClientProvider;
        private readonly IUserPreferencesProvider Prefs;
        private readonly IApplicationLogger Logger;

        public AppUpdateService(
            IGitHubClient gitHub,
            IHttpClientProvider httpClientProvider,
            IUserPreferencesProvider prefs,
            IApplicationLogger logger)
        {
            GitHub = gitHub;
            HttpClientProvider = httpClientProvider;
            Prefs = prefs;
            Logger = logger;
        }

        /// <summary>
        /// Whether BakaLoader may go and look for a newer release of itself right now. Both
        /// switches have to be on: the one that lets it ask GitHub at all, and the one that
        /// lets it install what it finds. Installing while the host has turned checking off
        /// would be the one path that still reaches out after they said not to, which is why
        /// the rule sits here rather than in each of the two callers.
        /// </summary>
        public static bool MaySelfUpdate(bool checkForUpdates, bool autoUpdateBakaLoader)
            => MaySelfUpdate(checkForUpdates, autoUpdateBakaLoader, userInitiated: false);

        /// <summary>
        /// The same rule, with the one exception that a host standing in front of the app makes.
        /// Update checking still has the last word: it is the switch that says whether BakaLoader
        /// may reach out at all, and no button on any screen may talk over it. Auto-update only
        /// says whether an unattended run may install what it finds, so a host who asked for this
        /// update themselves has already answered that question.
        /// </summary>
        public static bool MaySelfUpdate(bool checkForUpdates, bool autoUpdateBakaLoader, bool userInitiated)
            => checkForUpdates && (autoUpdateBakaLoader || userInitiated);

        /// <summary>
        /// The plain answer the unattended callers have always read: true when an update was
        /// staged and the app must now close, false for every other ending.
        /// </summary>
        public async Task<bool> CheckAndStageUpdateAsync(bool userInitiated = false)
            => await TryStageUpdateAsync(userInitiated) == StageOutcome.Staged;

        public async Task<StageOutcome> TryStageUpdateAsync(bool userInitiated = false)
        {
            try
            {
                var prefs = Prefs?.LoadPreferences();
                if (prefs != null && !MaySelfUpdate(prefs.CheckForUpdates, prefs.AutoUpdateBakaLoader, userInitiated))
                {
                    Logger.Information("Self-update: switched off in settings, so nothing was checked.");
                    return StageOutcome.SwitchedOff;
                }

                var release = await GitHub.GetLatestReleaseAsync();
                if (release == null)
                {
                    Logger.Information("Self-update: no GitHub release available.");
                    return StageOutcome.NoRelease;
                }

                // CompareVersion returns 1 when the release is newer than the running app.
                if (AssemblyHelper.CompareVersion(release.TagName) != 1)
                {
                    Logger.Information("Self-update: already current (installed vs release {0}).", release.TagName);
                    return StageOutcome.AlreadyCurrent;
                }

                var asset = ChooseAppAsset(release);

                if (asset == null)
                {
                    Logger.Information("Self-update: release {0} has no .zip asset to install.", release.TagName);
                    return StageOutcome.NoRelease;
                }

                var workDir = Path.Combine(Path.GetTempPath(), "BakaLoaderUpdate");
                Directory.CreateDirectory(workDir);
                var zipPath = Path.Combine(workDir, "update.zip");
                TryDelete(zipPath);

                Logger.Information("Self-update: downloading {0} -> {1}", asset.BrowserDownloadUrl, zipPath);
                await DownloadFileAsync(asset.BrowserDownloadUrl, zipPath);

                if (!ZipContainsExe(zipPath))
                {
                    Logger.Error("Self-update: downloaded zip does not contain {0}; aborting.", ExeName);
                    TryDelete(zipPath);
                    return StageOutcome.NoRelease;
                }

                var installDir = Path.GetDirectoryName(GetCurrentExePath());
                if (string.IsNullOrWhiteSpace(installDir))
                {
                    Logger.Error("Self-update: could not resolve the install directory; aborting.");
                    return StageOutcome.NoRelease;
                }

                LaunchWatchdog(workDir, zipPath, installDir);
                Logger.Information("Self-update: watchdog launched for release {0}; app will now close to update.", release.TagName);
                return StageOutcome.Staged;
            }
            catch (Exception e)
            {
                // Everything that reaches here went out to GitHub and did not come back: the
                // check itself, or the download after it. The unattended callers still see a
                // plain false; the host who asked for this gets told the connection is the
                // problem rather than being told there is nothing newer.
                Logger.Error(e, "Self-update: failed to check or stage an update.");
                return StageOutcome.NetworkError;
            }
        }

        private async Task DownloadFileAsync(string url, string destinationPath)
        {
            using var client = HttpClientProvider.CreateClient();
            client.Timeout = DownloadTimeout;
            client.DefaultRequestHeaders.UserAgent.ParseAdd("ValheimBakaLoader");

            using var response = await client.GetAsync(url, System.Net.Http.HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            await using var source = await response.Content.ReadAsStreamAsync();
            await using var destination = File.Create(destinationPath);
            await source.CopyToAsync(destination);
        }

        /// <summary>
        /// The zip that is the app, out of everything a release carries. Since 1.2.0 a release
        /// also holds four language packs, and GitHub lists assets by name, so "the first .zip"
        /// was lang-ja and every host on 1.2.0 failed to update. The app's own name wins when
        /// the release has it; otherwise the first .zip that is not a language pack.
        /// </summary>
        internal static GitHubReleaseAsset ChooseAppAsset(GitHubRelease release)
        {
            var zips = release?.Assets?
                .Where(a => a?.BrowserDownloadUrl != null
                    && a.BrowserDownloadUrl.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (zips == null || zips.Count == 0) return null;

            var version = (release.TagName ?? string.Empty).TrimStart('v', 'V');
            var wanted = $"ValheimBakaLoader-{version}-win-x64.zip";

            return zips.FirstOrDefault(a => string.Equals(a.Name, wanted, StringComparison.OrdinalIgnoreCase))
                ?? zips.FirstOrDefault(a => a.Name == null
                    || !a.Name.StartsWith("lang-", StringComparison.OrdinalIgnoreCase));
        }

        private static bool ZipContainsExe(string zipPath)
        {
            try
            {
                using var archive = ZipFile.OpenRead(zipPath);
                return archive.Entries.Any(e =>
                    string.Equals(Path.GetFileName(e.FullName), ExeName, StringComparison.OrdinalIgnoreCase));
            }
            catch
            {
                return false;
            }
        }

        private static string GetCurrentExePath()
        {
            using var proc = Process.GetCurrentProcess();
            return proc.MainModule?.FileName;
        }

        /// <summary>
        /// Writes the watchdog PowerShell script to the work directory and launches it as a
        /// detached, hidden process. The script waits for this app's PID to exit before it
        /// touches any files, so the app is safe to close immediately after this returns.
        /// </summary>
        private void LaunchWatchdog(string workDir, string zipPath, string installDir)
        {
            var appPid = Process.GetCurrentProcess().Id;
            var exePath = Path.Combine(installDir, ExeName);
            var extractDir = Path.Combine(workDir, "extracted");
            var scriptPath = Path.Combine(workDir, "update-watchdog.ps1");

            var script = BuildWatchdogScript(appPid, zipPath, extractDir, installDir, exePath, workDir);
            File.WriteAllText(scriptPath, script);

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = workDir,
            };

            Process.Start(psi);
        }

        private static string BuildWatchdogScript(
            int appPid, string zipPath, string extractDir, string installDir, string exePath, string workDir)
        {
            // A self-deleting, windowless updater. Every path is passed as a literal so the
            // script has no external dependencies beyond stock PowerShell + robocopy.
            return $@"
$ErrorActionPreference = 'Stop'
$appPid     = {appPid}
$zipPath    = '{Escape(zipPath)}'
$extractDir = '{Escape(extractDir)}'
$installDir = '{Escape(installDir)}'
$exePath    = '{Escape(exePath)}'
$workDir    = '{Escape(workDir)}'

# 1. Wait for BakaLoader to fully exit so its files unlock.
try {{ Wait-Process -Id $appPid -Timeout 120 -ErrorAction SilentlyContinue }} catch {{}}
Start-Sleep -Seconds 1

# 1b. Still running after all that? Then leave the install alone and do nothing at all.
#     The wait above gives up after two minutes, and writing the new files over an app that
#     is still holding them is the worst of both: the unlocked files are replaced, the locked
#     exe is not, and what is left on disk is half one version and half the other. Skipping
#     costs the host nothing; the update is staged again the next time BakaLoader looks.
if (Get-Process -Id $appPid -ErrorAction SilentlyContinue) {{ exit 1 }}

# 2. Fresh extraction of the downloaded release.
if (Test-Path $extractDir) {{ Remove-Item $extractDir -Recurse -Force -ErrorAction SilentlyContinue }}
New-Item -ItemType Directory -Path $extractDir -Force | Out-Null
Expand-Archive -Path $zipPath -DestinationPath $extractDir -Force

# 3. Find the folder that actually contains the exe (zips may nest it one level deep).
$exeFile = Get-ChildItem -Path $extractDir -Filter '{ExeName}' -Recurse -File | Select-Object -First 1
if (-not $exeFile) {{ exit 1 }}
$srcDir = $exeFile.DirectoryName

# 4. Copy the new files over the install directory. /R + /W keep retries short if a
#    handle lingers; the exe itself is excluded on this pass then copied last.
robocopy $srcDir $installDir /E /R:3 /W:2 /XF '{ExeName}' | Out-Null
Copy-Item -Path $exeFile.FullName -Destination $exePath -Force

# 5. Relaunch the updated app.
Start-Process -FilePath $exePath -WorkingDirectory $installDir

# 6. Clean up our own temp working folder.
Start-Sleep -Seconds 1
try {{ Remove-Item $workDir -Recurse -Force -ErrorAction SilentlyContinue }} catch {{}}
";
        }

        private static string Escape(string path) => path?.Replace("'", "''");

        private void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception e)
            {
                Logger.Debug("Self-update: could not delete {0}: {1}", path, e.Message);
            }
        }
    }
}
