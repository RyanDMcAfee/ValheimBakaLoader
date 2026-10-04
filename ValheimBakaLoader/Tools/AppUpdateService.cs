using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
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

        /// <summary>
        /// A newer release was found and its asset did not arrive whole: the byte count
        /// came up short against the size GitHub published, or the digest on the release
        /// did not match what landed. Nothing was staged and the running version stands.
        /// <para>
        /// It is its own answer because "already on the newest release" is a false
        /// statement about the host's machine, and it is the one they used to be handed:
        /// a half-arrived zip fell into NoRelease, and NoRelease says there is nothing
        /// newer while the pill on the same screen names the newer version.
        /// </para>
        /// </summary>
        DownloadDamaged,

        /// <summary>The settings say not to, so nothing was asked of GitHub.</summary>
        SwitchedOff,

        /// <summary>
        /// A newer release was there and the moment for installing it had gone. The one caller
        /// that can answer this is the launch: its step is allowed to be given up on, and
        /// staging is only safe while no window is open, because the swap works by closing the
        /// app and a window that is open may be holding a live server through the Job Object.
        /// Nothing was armed and nothing was written; the update is staged again next launch.
        /// </summary>
        Held,
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
        /// <para>
        /// <paramref name="stillWanted"/> is asked, when it is given, before the download starts
        /// and again in the moment before the watchdog is armed. A caller whose own deadline may
        /// run out while this is running hands one in and answers false once staging would no
        /// longer be safe; see <see cref="StageBudget"/>.
        /// </para>
        /// </summary>
        Task<bool> CheckAndStageUpdateAsync(bool userInitiated = false, Func<bool> stillWanted = null);

        /// <summary>
        /// The longest staging one update can take by its own clocks: the header wait and the
        /// body deadline the download is held to, and a minute over the top of them for the
        /// release call before it and reading the zip after it. A caller that puts a deadline of
        /// its own around this has to allow at least this long, or the deadline bounds nothing
        /// and only guarantees the work is given up on while it is still going.
        /// </summary>
        TimeSpan StageBudget { get; }

        /// <summary>
        /// The same check, saying WHY when it did not stage anything. The dialog's own button
        /// puts that reason into a sentence, so a host whose machine is offline is told that
        /// rather than being told they are already up to date.
        /// </summary>
        Task<StageOutcome> TryStageUpdateAsync(bool userInitiated = false, Func<bool> stillWanted = null);
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

        /// <summary>
        /// The clocks the release asset is held to. HttpClient.Timeout used to be the only
        /// one, and it is released the moment the headers are in, so a site that answered
        /// its headers and then stopped sending held the splash screen open for the life of
        /// the process: no window, no server, and nothing to do but end the app from Task
        /// Manager. Settable so a test can prove the stall in a second rather than in ten
        /// minutes.
        /// </summary>
        public DownloadBudget Budget { get; set; } = new()
        {
            HeaderTimeout = TimeSpan.FromSeconds(30),
            TotalTimeout = TimeSpan.FromMinutes(10),
            IdleTimeout = TimeSpan.FromSeconds(30),
            MaxBytes = 512L * 1024 * 1024,
        };

        /// <summary>
        /// The version this install reads as its own, which is the DLL's.
        /// <para>
        /// None of that moved in 1.2.8. <see cref="AssemblyHelper.GetApplicationVersion"/> reads
        /// the informational version off the assembly it is compiled into, which is
        /// ValheimBakaLoader.dll, and this comparison has always been handed that rather than
        /// anything out of the exe. What 1.2.8 adds is a seam that PINS it there, because the exe
        /// is now a frozen launcher: the same bytes in every release, with a version resource
        /// that reads 1.2.6 forever. A reading taken off the exe would put every host two
        /// releases behind and never done being updated, so the installed side is named here
        /// rather than implied, the log line says which version it compared, and
        /// FrozenAppHostTests refuses any reading of a version off the launcher anywhere in the
        /// code that ships.
        /// </para>
        /// <para>
        /// Internal, and null in every shipped path. The suite sets it to put a host on a
        /// version this build is not, which is the only way to prove that a 1.2.7 install reads
        /// itself as current against a 1.2.6 stable release and is offered 1.2.8 when 1.2.8 is
        /// published.
        /// </para>
        /// </summary>
        internal string InstalledVersion { get; set; }

        private string Installed() => string.IsNullOrWhiteSpace(InstalledVersion)
            ? AssemblyHelper.GetApplicationVersion()
            : InstalledVersion.Trim();

        /// <summary>
        /// The longest staging one update can take by its own clocks. The header wait and the
        /// body deadline are separate: the body's clock starts once the headers are in, so the
        /// two add up. The minute on top covers the release call before the download and reading
        /// the zip after it.
        /// <para>
        /// The launch step that runs this used to be held to the plain 45 second per-step clock
        /// while the download inside it was allowed all of the above, so on any link slower than
        /// about 90 KB a second the step was given up on every single time, and the work carried
        /// on behind the opened window and armed the watchdog anyway.
        /// </para>
        /// </summary>
        public TimeSpan StageBudget =>
            Budget.HeaderTimeout + Budget.TotalTimeout + TimeSpan.FromMinutes(1);

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
        public async Task<bool> CheckAndStageUpdateAsync(bool userInitiated = false, Func<bool> stillWanted = null)
            => await TryStageUpdateAsync(userInitiated, stillWanted) == StageOutcome.Staged;

        public async Task<StageOutcome> TryStageUpdateAsync(bool userInitiated = false, Func<bool> stillWanted = null)
        {
            // Never a reason to stop: a caller that cannot answer has nothing that could have
            // changed under it, which is every caller a host is standing in front of.
            bool Wanted()
            {
                if (stillWanted == null) return true;
                try { return stillWanted(); }
                catch (Exception e)
                {
                    Logger.Debug("Could not ask whether the update is still wanted: {0}", e.Message);
                    return true;
                }
            }

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

                // CompareVersion returns 1 when the release is newer than the running app. The
                // installed side is the DLL's version, never the exe's: see InstalledVersion.
                if (AssemblyHelper.CompareVersion(release.TagName, Installed()) != 1)
                {
                    Logger.Information(
                        "Self-update: already current (installed {0} vs release {1}).",
                        Installed(), release.TagName);
                    return StageOutcome.AlreadyCurrent;
                }

                var asset = ChooseAppAsset(release);

                if (asset == null)
                {
                    // Worded for what it now means. A release can carry several zips and still
                    // have no app on it: the language packs are zips, and so is the client
                    // companion. What is missing is the app's own zip, and the line says so
                    // rather than claiming the release has no zip at all.
                    Logger.Information(
                        "Self-update: release {0} carries no ValheimBakaLoader-<version>-win-x64.zip, "
                        + "so there is no app on it to install.", release.TagName);
                    return StageOutcome.NoRelease;
                }

                var workDir = Path.Combine(Path.GetTempPath(), "BakaLoaderUpdate");
                Directory.CreateDirectory(workDir);
                var zipPath = Path.Combine(workDir, "update.zip");
                TryDelete(zipPath);

                // Asked before a single byte moves. The launch is the caller with a clock on
                // it, and once its window is open the swap is no longer a safe thing to arm:
                // it works by closing the app, and by then a profile may have auto started and
                // the Job Object would take a live server down with it.
                if (!Wanted())
                {
                    Logger.Information(
                        "Self-update: release {0} was not fetched. The launch went on without it, so it "
                        + "installs the next time BakaLoader starts.", release.TagName);
                    return StageOutcome.Held;
                }

                Logger.Information("Self-update: downloading {0} -> {1}", asset.BrowserDownloadUrl, zipPath);
                long written;
                try
                {
                    written = await DownloadFileAsync(asset.BrowserDownloadUrl, zipPath);
                }
                catch (TimeoutException e)
                {
                    // A deadline that ran out is the stalled site, not a missing release.
                    // It is reported as the connection problem it is, and the running
                    // version stands.
                    Logger.Error("Self-update: the download did not arrive: {0}", e.Message);
                    TryDelete(zipPath);
                    return StageOutcome.NetworkError;
                }

                // The one download in this app that writes over the app itself, and it used
                // to be the only one held to nothing. GitHub publishes the size on the same
                // object the address is read from, and newer releases publish a digest
                // beside it; a zip that does not match either of them is not installed.
                var damaged = DescribeDamage(asset, zipPath, written);
                if (damaged != null)
                {
                    Logger.Error("Self-update: the download did not arrive whole: {0}. The installed version was left alone.", damaged);
                    TryDelete(zipPath);
                    return StageOutcome.DownloadDamaged;
                }

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

                // And asked again here, because the download above is the part that takes
                // minutes: the answer can have changed since it started, which is the whole
                // shape of this defect. Nothing has been written outside the work folder yet.
                if (!Wanted())
                {
                    Logger.Information(
                        "Self-update: release {0} arrived after the launch had gone on, so nothing was "
                        + "armed. It installs the next time BakaLoader starts.", release.TagName);
                    return StageOutcome.Held;
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

        /// <summary>
        /// The release asset, into a file, under a header deadline, a total deadline and an
        /// idle watchdog. Answers how many bytes were written so the caller can hold them
        /// against the size GitHub published.
        /// </summary>
        private async Task<long> DownloadFileAsync(string url, string destinationPath)
        {
            using var client = HttpClientProvider.CreateClient();
            client.DefaultRequestHeaders.UserAgent.ParseAdd("ValheimBakaLoader");

            return await BoundedDownload.ToFileAsync(
                client, url, destinationPath, Budget, CancellationToken.None,
                tracer: Logger, what: "self-update download");
        }

        /// <summary>
        /// What is wrong with the file that arrived, in a sentence, or null when nothing is.
        /// <para>
        /// Two checks, in the order they can be made. The byte count against the size on the
        /// release entry, which every release has carried since the API first answered; and
        /// the SHA256 against the digest, which GitHub publishes on newer releases as
        /// "sha256:..." and which is the only thing that catches a zip that arrived at the
        /// right length with the wrong bytes in it. A release that publishes neither is
        /// still held to the zip's own readability further down, as it always was.
        /// </para>
        /// </summary>
        internal static string DescribeDamage(GitHubReleaseAsset asset, string zipPath, long written)
        {
            if (asset == null) return null;

            if (asset.Size > 0 && written != asset.Size)
                return $"it arrived as {written} bytes where the release published {asset.Size}";

            var digest = ReadSha256Digest(asset.Digest);
            if (digest != null)
            {
                var actual = Sha256Of(zipPath);
                if (actual != null && !string.Equals(actual, digest, StringComparison.OrdinalIgnoreCase))
                    return "it did not match the digest the release published";
            }

            return null;
        }

        /// <summary>
        /// The hex part of a "sha256:abc..." digest, or null when the release published
        /// something else. An algorithm this app cannot take is not a failed comparison, so
        /// it is left to the size check rather than turned into a refusal of its own.
        /// </summary>
        internal static string ReadSha256Digest(string digest)
        {
            var value = digest?.Trim();
            if (string.IsNullOrEmpty(value)) return null;

            const string prefix = "sha256:";
            if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;

            var hex = value.Substring(prefix.Length).Trim();
            return hex.Length == 64 ? hex : null;
        }

        private static string Sha256Of(string path)
        {
            try
            {
                using var sha = System.Security.Cryptography.SHA256.Create();
                using var stream = File.OpenRead(path);
                return Convert.ToHexString(sha.ComputeHash(stream));
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// The zip that is the app, out of everything a release carries. Since 1.2.0 a release
        /// also holds four language packs, and GitHub lists assets by name, so "the first .zip"
        /// was lang-ja and every host on 1.2.0 failed to update.
        /// <para>
        /// The app's own full name wins when the release carries it. The fallback under that
        /// used to be "the first .zip that is not a language pack", which was only ever right
        /// while the app and the packs were the only things on a release. 1.2.6 puts a second
        /// product there, BakaLoaderUncheat, a plugin for a player's own game client; it sorts
        /// before everything else by name, so it stood exactly where lang-ja stood, and the
        /// only thing keeping it out was the exact-name match landing. A tag the release was
        /// cut under with a fourth part, or an asset renamed by hand, is enough to miss that
        /// match, and the fallback would then have handed the self-updater the companion.
        /// </para>
        /// <para>
        /// So the fallback is the app's own NAME SHAPE rather than "not a pack": ValheimBakaLoader-
        /// at the front and -win-x64.zip at the back, which a release under any version string
        /// still satisfies and which nothing else this project publishes can. When nothing
        /// qualifies the answer is null, and the caller says the release has no app to install
        /// rather than installing whatever else was lying on it.
        /// </para>
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
                ?? zips.FirstOrDefault(a => a.Name != null
                    && a.Name.StartsWith("ValheimBakaLoader-", StringComparison.OrdinalIgnoreCase)
                    && a.Name.EndsWith("-win-x64.zip", StringComparison.OrdinalIgnoreCase));
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
$reportPath = '{Escape(UpdateReportPath())}'
$rollbackDir = Join-Path $workDir 'rollback'
$rollbackTaken = $false

# The note the app reads on its next launch. Every way out of this script that is not a
# finished update goes through here, because a host whose BakaLoader did not come back has
# no other way of finding out why: the script deletes itself and its own folder, and the
# app that would have written a log line is not running.
function Write-Note($note) {{
  try {{
    $reportDir = Split-Path -Parent $reportPath
    if ($reportDir -and -not (Test-Path $reportDir)) {{ New-Item -ItemType Directory -Path $reportDir -Force | Out-Null }}
    Set-Content -LiteralPath $reportPath -Value $note -Encoding UTF8
  }} catch {{}}
}}

# 1. Wait for BakaLoader to fully exit so its files unlock.
try {{ Wait-Process -Id $appPid -Timeout 120 -ErrorAction SilentlyContinue }} catch {{}}
Start-Sleep -Seconds 1

# 1b. Still running after all that? Then leave the install alone and do nothing at all.
#     The wait above gives up after two minutes, and writing the new files over an app that
#     is still holding them is the worst of both: the unlocked files are replaced, the locked
#     exe is not, and what is left on disk is half one version and half the other. Skipping
#     costs the host nothing; the update is staged again the next time BakaLoader looks.
#     Nothing is relaunched here, because the copy that would not close is still running.
if (Get-Process -Id $appPid -ErrorAction SilentlyContinue) {{
  Write-Note 'BakaLoader was still running two minutes after it was asked to close, so the update was not written and nothing on disk was touched. Close BakaLoader, then check for updates again.'
  exit 1
}}

# The one way out for every failure from here on. Whatever this script had already written
# over goes back first, then the reason, then the copy of BakaLoader that was on disk before
# any of this started. Before this existed, three paths (no exe in the download, an
# Expand-Archive that threw, a Copy-Item of the exe that threw) left the host with no
# running BakaLoader, an install that might be half replaced, and not one word about it.
function Stop-Update($note) {{
  if ($rollbackTaken) {{ robocopy $rollbackDir $installDir /E /R:1 /W:1 | Out-Null }}
  Write-Note $note
  if (Test-Path $exePath) {{ Start-Process -FilePath $exePath -WorkingDirectory $installDir }}
  Start-Sleep -Seconds 1
  try {{ Remove-Item $workDir -Recurse -Force -ErrorAction SilentlyContinue }} catch {{}}
  exit 1
}}

try {{
  # 2. Fresh extraction of the downloaded release.
  if (Test-Path $extractDir) {{ Remove-Item $extractDir -Recurse -Force -ErrorAction SilentlyContinue }}
  New-Item -ItemType Directory -Path $extractDir -Force | Out-Null
  Expand-Archive -Path $zipPath -DestinationPath $extractDir -Force

  # 3. Find the folder that actually contains the exe (zips may nest it one level deep).
  $exeFile = Get-ChildItem -Path $extractDir -Filter '{ExeName}' -Recurse -File | Select-Object -First 1
  if (-not $exeFile) {{
    Stop-Update 'The downloaded release did not contain BakaLoader, so nothing was replaced and the copy you had is still in place. Trying again later is safe.'
  }}
  $srcDir = $exeFile.DirectoryName

  # 4. Copy the new files over the install directory. /R + /W keep retries short if a
  #    handle lingers; the exe itself is excluded on this pass then copied last.
  #    Everything about to be written over is kept aside first, because the only way to
  #    undo a half-finished copy is to have the old files still on disk.
  if (Test-Path $rollbackDir) {{ Remove-Item $rollbackDir -Recurse -Force -ErrorAction SilentlyContinue }}
  New-Item -ItemType Directory -Path $rollbackDir -Force | Out-Null
  robocopy $installDir $rollbackDir /E /R:1 /W:1 | Out-Null
  $keepCode = $LASTEXITCODE

  # 4a. The copy that makes the undo possible. When it could not be taken there is nothing to
  #     put back, so the update is not started at all rather than started without a net.
  if ($keepCode -ge 8) {{
    Stop-Update ('The files that were there before could not be copied aside (robocopy answered ' + $keepCode + '), so the update was not started and nothing was replaced. Trying again later is safe.')
  }}
  $rollbackTaken = $true

  robocopy $srcDir $installDir /E /R:3 /W:2 /XF '{ExeName}' | Out-Null
  $copyCode = $LASTEXITCODE

  # 4b. Robocopy says what it did with its exit code, and anything from 8 up means files
  #     it could not write. PowerShell's own error handling never sees a native exit code,
  #     so this used to sail past: a file that could not be replaced left half one version
  #     and half the other on disk, and the app was relaunched on it with nothing said.
  #     Eight or more puts the old files back and writes the reason where the app can read
  #     it on the next launch.
  if ($copyCode -ge 8) {{
    Stop-Update ('The update could not be written: robocopy answered ' + $copyCode + '. The files that were there before were put back, and BakaLoader is still on the version it was. Trying again later is safe.')
  }}

  # 4c. The launcher, and only when it is genuinely a different file.
  #     From 1.2.8 the exe is frozen: the same bytes in every release, because it is the SDK's
  #     generic apphost and the only thing in it that ever changed was the version resource. On
  #     2026-10-04 that was enough to end an update badly: Defender's local model looked at the
  #     brand new 1.2.7 hash on a host's PC, called it a trojan, quarantined the file and took
  #     the app and the live Valheim server down with it. So when the download carries the same
  #     launcher that is already installed, the file is not written at all. No new bytes, no new
  #     write time, nothing for a local model to form an opinion about. A future deliberate
  #     launcher change hashes differently and is copied exactly as before, and a hash that
  #     cannot be read falls back to copying rather than to skipping.
  $sameHost = $false
  try {{
    if (Test-Path $exePath) {{
      $here = (Get-FileHash -LiteralPath $exePath -Algorithm SHA256).Hash
      $there = (Get-FileHash -LiteralPath $exeFile.FullName -Algorithm SHA256).Hash
      $sameHost = $here -eq $there
    }}
  }} catch {{ $sameHost = $false }}

  if ($sameHost) {{
    Write-Note '{RoutineNoteMark}The update is in place. The launcher was already these exact bytes, so it was left alone: BakaLoader ships the same launcher in every release and reads its own version from the library beside it.'
  }} else {{
    Copy-Item -Path $exeFile.FullName -Destination $exePath -Force
  }}
}} catch {{
  # Anything that threw: a zip that would not open, a folder that could not be made, the exe
  # itself that could not be replaced. The last of those happens after the rest of the install
  # was already written, which is exactly when putting the old files back matters.
  Stop-Update ('The update could not be written: ' + $_.Exception.Message + ' The files that were there before were put back, and BakaLoader is still on the version it was. Trying again later is safe.')
}}

# 5. Relaunch the updated app.
Start-Process -FilePath $exePath -WorkingDirectory $installDir

# 6. Clean up our own temp working folder.
Start-Sleep -Seconds 1
try {{ Remove-Item $workDir -Recurse -Force -ErrorAction SilentlyContinue }} catch {{}}
";
        }

        private static string Escape(string path) => path?.Replace("'", "''");

        /// <summary>
        /// What a note starts with when it is an account of an update that WORKED rather than a
        /// problem to be put in front of the host.
        /// <para>
        /// Every note the watchdog left used to be a failure, so the launch logged all of them
        /// as warnings. From 1.2.8 one note is written on the way through a good update, the one
        /// that says the launcher was already the right bytes and was left alone, and that is an
        /// ordinary line about a release going the way it was designed to. The mark is stripped
        /// before the line is logged, so nothing in the log reads like a code.
        /// </para>
        /// </summary>
        internal const string RoutineNoteMark = "ok: ";

        /// <summary>Whether this note is an account of a good update rather than a problem.</summary>
        public static bool IsRoutineNote(string note) =>
            note != null && note.StartsWith(RoutineNoteMark, StringComparison.Ordinal);

        /// <summary>The note as a host should read it, with the mark taken off the front.</summary>
        public static string NoteText(string note) =>
            IsRoutineNote(note) ? note.Substring(RoutineNoteMark.Length).Trim() : note;

        /// <summary>
        /// Where the watchdog leaves a note about an update it could not write. It sits
        /// outside the work folder on purpose, because step six deletes the work folder.
        /// </summary>
        internal static string UpdateReportPath() => ReportPathOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ValheimBakaLoader", "update-report.txt");

        /// <summary>
        /// Somewhere else to keep the note, for a test. It exists so proving the note is read
        /// once and taken away never has to write into the folder a running copy of the app
        /// reads from. Null in every shipped path.
        /// </summary>
        internal static string ReportPathOverride { get; set; }

        /// <summary>
        /// The note the last update attempt left, if it left one, and the note is taken away
        /// as it is read so the same line is never reported twice. Null when there is none.
        /// </summary>
        public static string ReadAndClearUpdateReport()
        {
            var path = UpdateReportPath();
            try
            {
                if (!File.Exists(path)) return null;

                var note = File.ReadAllText(path)?.Trim();
                try { File.Delete(path); } catch { /* read once is what matters */ }
                return string.IsNullOrWhiteSpace(note) ? null : note;
            }
            catch
            {
                return null;
            }
        }

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
