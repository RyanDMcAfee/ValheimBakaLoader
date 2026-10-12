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
        /// <summary>
        /// Downloaded, checked, unpacked beside the install, and the updater started: the app must
        /// now close.
        /// </summary>
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

        /// <summary>
        /// The release arrived whole and could not be made ready: it would not unpack beside the
        /// install, or the updater would not start. Nothing in the install was written over and
        /// the running version stands.
        /// </summary>
        StagingFailed,
    }

    public interface IAppUpdateService
    {
        /// <summary>
        /// Checks GitHub for a newer BakaLoader release and, if one is found, downloads it,
        /// unpacks it into update-pending beside the install and starts the updater
        /// (<see cref="ManagedUpdater"/>), which waits for this app to exit, copies the new files
        /// in and starts BakaLoader again. Returns <c>true</c> when an update was staged (the
        /// caller should then close the app so the updater can take over), or <c>false</c> when
        /// the app is already current or the update could not be staged.
        /// <para>
        /// <paramref name="userInitiated"/> is set by the one path where the host asked for this
        /// by name (the update dialog's own button). An unattended check still needs both Upkeep
        /// switches; a host who just clicked Update has said the second one out loud, so being
        /// turned away because auto-update is off would be the app arguing with them.
        /// </para>
        /// </summary>
        /// <para>
        /// <paramref name="stillWanted"/> is asked, when it is given, before the download starts
        /// and again in the moment before the updater is started. A caller whose own deadline may
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
    /// Self-update for BakaLoader, the half that runs while the app is still up. The swap itself
    /// has to happen while the app is NOT running (its own files are in use and the child Valheim
    /// server is tied to this process through a Job Object), so it is handed to the updater:
    ///
    ///   1. BakaLoader downloads the release zip into the work folder and checks it: the size and
    ///      digest GitHub publishes, and SHA256SUMS.txt when the release carries one.
    ///   2. It unpacks the zip into update-pending in the install, refusing any entry that would
    ///      land outside that folder and leaving out a launcher that is the frozen one.
    ///   3. It starts the updater, the same library through dotnet with --apply-update, and closes.
    ///   4. The updater (<see cref="ManagedUpdater"/>) waits for this process to end, copies the
    ///      new files over the install with the old ones set aside, writes RESULT.txt and starts
    ///      BakaLoader again.
    ///
    /// Nothing in any of it is a script, and nothing in it writes a Run key. On relaunch the
    /// normal startup path auto-starts any profile whose AutoStart pref is on, so the Valheim
    /// server comes back up without any special relaunch arguments.
    /// </summary>
    public class AppUpdateService : IAppUpdateService
    {
        private const string ExeName = LauncherRoute.LauncherName;

        /// <summary>
        /// The checksum list a release may carry beside its zips. The publish script writes it from
        /// 1.2.13 on; a release without it is still held to the size and the digest GitHub
        /// publishes, as it always was.
        /// </summary>
        public const string SumsAssetName = "SHA256SUMS.txt";

        /// <summary>The clocks the checksum list is held to. It is a few lines of text.</summary>
        internal static DownloadBudget SumsBudget => new()
        {
            HeaderTimeout = TimeSpan.FromSeconds(30),
            TotalTimeout = TimeSpan.FromMinutes(1),
            IdleTimeout = TimeSpan.FromSeconds(30),
            MaxBytes = 64 * 1024,
        };

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

        /// <summary>
        /// Where staging works and where it unpacks, and what starts the updater, for the suite.
        /// Null in every shipped path, which means %LOCALAPPDATA%\BakaLoaderUpdate, the app's own
        /// folder and the real updater. A test that drives staging end to end points the first two
        /// at a temp tree and hands in a starter that records the call, so nothing a test does ever
        /// starts an updater, opens a window or writes into a folder the app runs from.
        /// </summary>
        internal string WorkDirectory { get; set; }

        /// <inheritdoc cref="WorkDirectory"/>
        internal string InstallDirectory { get; set; }

        /// <inheritdoc cref="WorkDirectory"/>
        internal Action<string, string> UpdaterStarter { get; set; }

        /// <summary>
        /// The environment variable that points an update check at a release zip on this machine
        /// instead of at GitHub, so the owner can run the whole update on a throwaway copy before
        /// there is a release to update to. When it names a .zip that exists, that zip stands in
        /// for the newest release: GitHub is not asked, no checksum list is fetched, the version
        /// check is skipped (the zip is usually the very build the copy already runs), and the zip
        /// is copied into the work folder as the download and taken through the same staging and
        /// the same updater as a real update. Unset, or naming nothing usable, and the check is the
        /// GitHub one exactly as it always was; the variable is never read for anything else, so
        /// it cannot make a real host skip an update.
        /// </summary>
        public const string UpdateFromZipVariable = "BAKALOADER_UPDATE_FROM_ZIP";

        /// <summary>
        /// How <see cref="UpdateFromZipVariable"/> is read, for the suite, so a test never sets a
        /// variable every other test in the process would see. Null in every shipped path, which
        /// reads the process environment.
        /// </summary>
        internal Func<string, string> EnvironmentReader { get; set; }

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
        /// <para>
        /// From 1.2.13 a release may carry SHA256SUMS.txt as well, fetched after the zip under
        /// clocks of its own, so those are added on.
        /// </para>
        /// </summary>
        public TimeSpan StageBudget =>
            Budget.HeaderTimeout + Budget.TotalTimeout
            + SumsBudget.HeaderTimeout + SumsBudget.TotalTimeout
            + TimeSpan.FromMinutes(1);

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

                // The owner's test seam: a zip named in the environment stands in for GitHub. Null,
                // and everything below is the GitHub path it has always been.
                var localZip = LocalReleaseZip();
                var release = localZip != null ? LocalRelease(localZip) : await GitHub.GetLatestReleaseAsync();
                if (release == null)
                {
                    Logger.Information("Self-update: no GitHub release available.");
                    return StageOutcome.NoRelease;
                }

                // CompareVersion returns 1 when the release is newer than the running app. The
                // installed side is the DLL's version, never the exe's: see InstalledVersion.
                if (localZip == null && AssemblyHelper.CompareVersion(release.TagName, Installed()) != 1)
                {
                    Logger.Information(
                        "Self-update: already current (installed {0} vs release {1}).",
                        Installed(), release.TagName);
                    return StageOutcome.AlreadyCurrent;
                }

                var asset = localZip != null ? release.Assets[0] : ChooseAppAsset(release);

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

                var workDir = WorkDirectory ?? ManagedUpdater.WorkFolder();
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

                long written;
                try
                {
                    if (localZip != null)
                    {
                        File.Copy(localZip, zipPath, overwrite: true);
                        written = new FileInfo(zipPath).Length;
                    }
                    else
                    {
                        Logger.Information("Self-update: downloading {0} -> {1}", asset.BrowserDownloadUrl, zipPath);
                        written = await DownloadFileAsync(asset.BrowserDownloadUrl, zipPath);
                    }
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

                // And the checksum list the release carries from 1.2.13 on, when it carries one.
                // A release without it is held to the two checks above, as every release before
                // it was, and nothing is said about the list being absent.
                var summed = localZip != null
                    ? SumCheck.NotPublished
                    : await CheckPublishedSumAsync(release, asset, zipPath, workDir);
                if (summed == SumCheck.Mismatch)
                {
                    Logger.Error(
                        "Self-update: the download did not match the SHA256 that {0} on release {1} gives it. "
                        + "The installed version was left alone.", SumsAssetName, release.TagName);
                    TryDelete(zipPath);
                    return StageOutcome.DownloadDamaged;
                }

                if (summed == SumCheck.Unreachable)
                {
                    TryDelete(zipPath);
                    return StageOutcome.NetworkError;
                }

                if (summed == SumCheck.NotListed)
                {
                    Logger.Information(
                        "Self-update: {0} on release {1} does not list {2}, so the download was held to the "
                        + "size and digest GitHub publishes.", SumsAssetName, release.TagName, asset.Name);
                }

                if (!ZipContainsExe(zipPath))
                {
                    Logger.Error("Self-update: downloaded zip holds neither {0} nor {1}; aborting.", ExeName, LauncherRoute.LibraryName);
                    TryDelete(zipPath);
                    return StageOutcome.NoRelease;
                }

                // The app's own folder, not the folder of the process image. Through the
                // launcher those are the same folder; through dotnet the process image is
                // dotnet.exe, and the update would have been written over the .NET install.
                var installDir = InstallDirectory ?? LauncherRoute.InstallFolder();
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

                // Unpacked beside the install, where the updater copies from. Whatever an earlier
                // update left there, a half unpacked release from a crash or the files the last
                // one set aside, goes first.
                var pending = ManagedUpdater.PendingFolder(installDir);
                ManagedUpdater.CleanLeftovers(installDir);
                Extraction unpacked;
                try
                {
                    unpacked = ExtractToPending(zipPath, pending);
                }
                catch (Exception e)
                {
                    Logger.Error(e,
                        "Self-update: release {0} could not be unpacked into {1}. Nothing in the install was written over.",
                        release.TagName, pending);
                    ManagedUpdater.TryDeleteFolder(pending);
                    return StageOutcome.StagingFailed;
                }
                finally
                {
                    // Unpacked or refused, the zip has done its job.
                    TryDelete(zipPath);
                }

                if (unpacked.Refused != null)
                {
                    Logger.Error(
                        "Self-update: release {0} was refused, because the zip names {1}, which would land outside "
                        + "the folder it is unpacked into. Nothing was written.", release.TagName, unpacked.Refused);
                    ManagedUpdater.TryDeleteFolder(pending);
                    return StageOutcome.DownloadDamaged;
                }

                if (unpacked.LauncherSkipped)
                {
                    Logger.Information(
                        "Self-update: the launcher in release {0} is the frozen {1} every release ships, so the "
                        + "update leaves it out and nothing writes it.", release.TagName, ExeName);
                }

                if (unpacked.LauncherRefused != null)
                {
                    Logger.Warning(
                        "Self-update: the launcher in release {0} could not be unpacked ({1}), which is what a "
                        + "security tool taking it looks like. The update goes on without it.",
                        release.TagName, unpacked.LauncherRefused);
                }

                // Once more, for the unpacking. The updater is the step that cannot be taken back,
                // because it is counting down to writing over the install from the moment it starts.
                if (!Wanted())
                {
                    Logger.Information(
                        "Self-update: release {0} was unpacked after the launch had gone on, so the updater "
                        + "was not started. It installs the next time BakaLoader starts.", release.TagName);
                    ManagedUpdater.TryDeleteFolder(pending);
                    return StageOutcome.Held;
                }

                try
                {
                    if (UpdaterStarter != null) UpdaterStarter(installDir, pending);
                    else if (localZip != null) StartUpdater(installDir, pending, dropZipVariable: true);
                    else StartUpdater(installDir, pending);
                }
                catch (Exception e)
                {
                    Logger.Error(e,
                        "Self-update: the updater for release {0} would not start. Nothing in the install was written over.",
                        release.TagName);
                    ManagedUpdater.TryDeleteFolder(pending);
                    return StageOutcome.StagingFailed;
                }

                Logger.Information("Self-update: updater started for release {0}; app will now close to update.", release.TagName);
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
        /// The zip <see cref="UpdateFromZipVariable"/> names, as a full path, or null. Null when it
        /// is unset, which is every host; null with a warning when it names something that is not
        /// a .zip that exists, and the check then goes to GitHub as it always does.
        /// </summary>
        private string LocalReleaseZip()
        {
            string named;
            try
            {
                named = (EnvironmentReader ?? Environment.GetEnvironmentVariable)(UpdateFromZipVariable);
            }
            catch
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(named)) return null;

            var path = named.Trim().Trim('"');
            try
            {
                if (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && File.Exists(path))
                    return Path.GetFullPath(path);
            }
            catch
            {
                // A path Windows will not take is the same as one that is not there.
            }

            Logger.Warning(
                "Self-update: {0} names {1}, which is not a .zip that exists, so GitHub was asked as usual.",
                UpdateFromZipVariable, path);
            return null;
        }

        /// <summary>
        /// A release made from the local zip: one asset, the zip itself, and the version of the
        /// library inside it. Logs the one line that says the seam is in use.
        /// </summary>
        private GitHubRelease LocalRelease(string zipPath)
        {
            var workDir = WorkDirectory ?? ManagedUpdater.WorkFolder();
            Directory.CreateDirectory(workDir);
            var version = LocalZipVersion(zipPath, workDir);

            Logger.Information(
                "Self-update: {0} names {1}, so that zip stands in for the newest release (version {2}). "
                + "GitHub was not asked, no checksum list was fetched and the version check was skipped.",
                UpdateFromZipVariable, zipPath, version);

            return new GitHubRelease
            {
                TagName = version,
                Assets = new[]
                {
                    new GitHubReleaseAsset
                    {
                        Name = Path.GetFileName(zipPath),
                        BrowserDownloadUrl = zipPath,
                        Size = new FileInfo(zipPath).Length,
                    },
                },
            };
        }

        /// <summary>
        /// The product version of the ValheimBakaLoader.dll inside a release zip, or "local" when
        /// there is none or it cannot be read. The library is unpacked into a folder of its own in
        /// the work folder to be read, and the folder is gone again before this returns.
        /// </summary>
        internal static string LocalZipVersion(string zipPath, string workDir)
        {
            var probeDir = Path.Combine(workDir, "version-probe-" + Guid.NewGuid().ToString("N"));
            try
            {
                using (var archive = ZipFile.OpenRead(zipPath))
                {
                    var entry = Shallowest(archive, LauncherRoute.LibraryName);
                    if (entry == null) return "local";
                    Directory.CreateDirectory(probeDir);
                    entry.ExtractToFile(Path.Combine(probeDir, LauncherRoute.LibraryName), overwrite: true);
                }

                var product = FileVersionInfo.GetVersionInfo(Path.Combine(probeDir, "ValheimBakaLoader.dll")).ProductVersion;
                return string.IsNullOrWhiteSpace(product) ? "local" : product.Trim();
            }
            catch
            {
                return "local";
            }
            finally
            {
                ManagedUpdater.TryDeleteFolder(probeDir);
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

        /// <summary>
        /// Whether the zip holds BakaLoader: the launcher, or the library that IS the app. The
        /// launcher alone used to be the test, so a release zip a security tool had taken the
        /// launcher out of read as a zip with no BakaLoader in it, and unpacking finds the app
        /// by the library first now (see ExtractToPending).
        /// </summary>
        private static bool ZipContainsExe(string zipPath)
        {
            try
            {
                using var archive = ZipFile.OpenRead(zipPath);
                return archive.Entries.Any(e =>
                    string.Equals(Path.GetFileName(e.FullName), ExeName, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(Path.GetFileName(e.FullName), LauncherRoute.LibraryName, StringComparison.OrdinalIgnoreCase));
            }
            catch
            {
                return false;
            }
        }

        /// <summary>What came of holding the download to the release's checksum list.</summary>
        internal enum SumCheck
        {
            /// <summary>The release carries no SHA256SUMS.txt.</summary>
            NotPublished,

            /// <summary>The list names the zip and the SHA256 matches.</summary>
            Matched,

            /// <summary>The list is there and does not name the zip.</summary>
            NotListed,

            /// <summary>The list names the zip with a different SHA256.</summary>
            Mismatch,

            /// <summary>The list is on the release and could not be fetched.</summary>
            Unreachable,
        }

        /// <summary>
        /// The download, held to SHA256SUMS.txt when the release carries one. The list comes from
        /// the same release as the zip, so it catches a zip damaged on the way rather than one
        /// somebody swapped; a signature is what will catch that.
        /// </summary>
        private async Task<SumCheck> CheckPublishedSumAsync(
            GitHubRelease release, GitHubReleaseAsset asset, string zipPath, string workDir)
        {
            var sums = release?.Asset(SumsAssetName);
            if (string.IsNullOrWhiteSpace(sums?.BrowserDownloadUrl)) return SumCheck.NotPublished;

            var path = Path.Combine(workDir, SumsAssetName);
            TryDelete(path);
            try
            {
                using var client = HttpClientProvider.CreateClient();
                client.DefaultRequestHeaders.UserAgent.ParseAdd("ValheimBakaLoader");
                await BoundedDownload.ToFileAsync(
                    client, sums.BrowserDownloadUrl, path, SumsBudget, CancellationToken.None,
                    tracer: Logger, what: "self-update checksum list");

                return CompareSum(File.ReadAllText(path), asset?.Name, zipPath);
            }
            catch (Exception e)
            {
                Logger.Error("Self-update: {0} on release {1} could not be fetched: {2}", SumsAssetName, release.TagName, e.Message);
                return SumCheck.Unreachable;
            }
            finally
            {
                TryDelete(path);
            }
        }

        /// <summary>
        /// The SHA256 the list gives a file, or null. Lines are the usual
        /// <c>&lt;hex&gt;  &lt;name&gt;</c>, with or without the star that marks binary mode.
        /// </summary>
        internal static string ReadPublishedSum(string sumsText, string fileName)
        {
            if (string.IsNullOrWhiteSpace(sumsText) || string.IsNullOrWhiteSpace(fileName)) return null;

            foreach (var raw in sumsText.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;

                var space = line.IndexOfAny(new[] { ' ', '\t' });
                if (space <= 0) continue;

                var hex = line.Substring(0, space);
                var name = line.Substring(space).Trim().TrimStart('*').Trim();
                if (hex.Length != 64 || !hex.All(Uri.IsHexDigit)) continue;

                if (string.Equals(name, fileName.Trim(), StringComparison.OrdinalIgnoreCase)) return hex;
            }

            return null;
        }

        /// <summary>The list held against the zip on disk.</summary>
        internal static SumCheck CompareSum(string sumsText, string fileName, string zipPath)
        {
            var expected = ReadPublishedSum(sumsText, fileName);
            if (expected == null) return SumCheck.NotListed;

            var actual = Sha256Of(zipPath);
            return actual != null && string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase)
                ? SumCheck.Matched
                : SumCheck.Mismatch;
        }

        /// <summary>What unpacking a release into update-pending came to.</summary>
        internal sealed class Extraction
        {
            /// <summary>The first entry that would land outside the folder. Nothing is used when one does.</summary>
            public string Refused { get; init; }

            /// <summary>The launcher was the frozen one, so it was left out.</summary>
            public bool LauncherSkipped { get; init; }

            /// <summary>Why the launcher could not be unpacked, when it could not. The rest went in.</summary>
            public string LauncherRefused { get; init; }

            /// <summary>How many files were unpacked.</summary>
            public int Files { get; init; }
        }

        /// <summary>
        /// The release, unpacked into update-pending in the install, laid out the way the install
        /// is laid out: when the zip nests the app a folder deep, that folder is the root.
        /// <para>
        /// Every name in the zip is checked before a byte is written, and one that would land
        /// outside the folder (a path climbing out with "..", a rooted path, a drive letter, a
        /// stream name) refuses the whole release. So does a name that would land in the updater's
        /// own folders. A launcher that is the frozen one is left out, so no update writes that
        /// file at all, and a launcher that will not unpack is let go with the reason, because the
        /// app is the library and the release is whole without it. Anything else that will not
        /// unpack throws, and staging stops with nothing in the install written over.
        /// </para>
        /// </summary>
        internal static Extraction ExtractToPending(string zipPath, string pendingDir)
        {
            // Whatever a crash left here goes first, and when it will not go the real reason is
            // what stops the update, rather than a release mixed into the leftovers.
            ManagedUpdater.TryDeleteFolder(pendingDir);
            if (Directory.Exists(pendingDir)) Directory.Delete(pendingDir, recursive: true);

            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(pendingDir)) + Path.DirectorySeparatorChar;

            using var archive = ZipFile.OpenRead(zipPath);

            foreach (var entry in archive.Entries)
            {
                if (Landing(root, entry.FullName) == null) return new Extraction { Refused = entry.FullName };
            }

            var app = Shallowest(archive, LauncherRoute.LibraryName) ?? Shallowest(archive, ExeName);
            if (app == null) return new Extraction();

            var appName = Normalize(app.FullName);
            var prefix = appName.Substring(0, appName.Length - app.Name.Length);

            Directory.CreateDirectory(root);
            var skipped = false;
            string launcherRefused = null;
            var files = 0;

            foreach (var entry in archive.Entries)
            {
                var name = Normalize(entry.FullName);
                if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;

                var relative = name.Substring(prefix.Length);
                if (relative.Length == 0) continue;

                var first = relative.Split('/')[0];
                if (string.Equals(first, ManagedUpdater.PendingFolderName, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(first, ManagedUpdater.RollbackFolderName, StringComparison.OrdinalIgnoreCase))
                {
                    ManagedUpdater.TryDeleteFolder(root);
                    return new Extraction { Refused = entry.FullName };
                }

                var target = Landing(root, relative);
                if (target == null)
                {
                    ManagedUpdater.TryDeleteFolder(root);
                    return new Extraction { Refused = entry.FullName };
                }

                if (name.EndsWith("/", StringComparison.Ordinal))
                {
                    Directory.CreateDirectory(target);
                    continue;
                }

                if (string.Equals(relative, ExeName, StringComparison.OrdinalIgnoreCase))
                {
                    bool frozen;
                    using (var stream = entry.Open()) frozen = LauncherRoute.IsFrozenLauncher(stream);
                    if (frozen)
                    {
                        skipped = true;
                        continue;
                    }

                    try
                    {
                        entry.ExtractToFile(target, overwrite: true);
                        files++;
                    }
                    catch (Exception e)
                    {
                        // Never left half written, where it would read as a launcher that arrived.
                        try { if (File.Exists(target)) File.Delete(target); } catch { /* the updater reads it whole or not at all */ }
                        launcherRefused = e.Message;
                    }

                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target));
                entry.ExtractToFile(target, overwrite: true);
                files++;
            }

            return new Extraction { LauncherSkipped = skipped, LauncherRefused = launcherRefused, Files = files };
        }

        private static ZipArchiveEntry Shallowest(ZipArchive archive, string fileName) =>
            archive.Entries
                .Where(e => string.Equals(e.Name, fileName, StringComparison.OrdinalIgnoreCase))
                .OrderBy(e => Normalize(e.FullName).Count(c => c == '/'))
                .FirstOrDefault();

        private static string Normalize(string name) => (name ?? string.Empty).Replace('\\', '/');

        /// <summary>
        /// Where a zip entry would land under the root, or null when that is anywhere else.
        /// </summary>
        internal static string Landing(string root, string entryName)
        {
            var name = Normalize(entryName);
            if (name.Length == 0 || name.StartsWith("/", StringComparison.Ordinal) || name.Contains(':')) return null;

            string full;
            try
            {
                full = Path.GetFullPath(Path.Combine(root, name.Replace('/', Path.DirectorySeparatorChar)));
            }
            catch
            {
                return null;
            }

            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full : null;
        }

        /// <summary>
        /// How the updater is started: the dotnet host, the app's own library from the install,
        /// and <c>--apply-update &lt;pid&gt; &lt;pending&gt; &lt;install&gt;</c>. No shell, no
        /// script and nothing hidden: the window the updater opens is the one the host sees.
        /// </summary>
        internal static ProcessStartInfo UpdaterStartInfo(
            string dotnet, string installDir, string pendingDir, int pid, bool dropZipVariable = false)
        {
            var psi = new ProcessStartInfo
            {
                FileName = dotnet,
                WorkingDirectory = installDir,
                UseShellExecute = false,
            };

            psi.ArgumentList.Add(Path.Combine(installDir, LauncherRoute.LibraryName));
            foreach (var argument in ManagedUpdater.Arguments(pid, pendingDir, installDir))
            {
                psi.ArgumentList.Add(argument);
            }

            // An update the owner pointed at a local zip must not hand that variable on: the
            // updater starts the app again, and an app that still saw it would stage the same zip
            // at its next check, because that path skips the version comparison. Only touched when
            // the seam was used, so a real update starts the updater exactly as it always did.
            if (dropZipVariable) psi.Environment.Remove(UpdateFromZipVariable);

            return psi;
        }

        private void StartUpdater(string installDir, string pendingDir, bool dropZipVariable = false)
        {
            var dotnet = ManagedUpdater.FindDotnet()
                ?? throw new FileNotFoundException("The dotnet host was not found to start the updater with.", LauncherRoute.DotnetHostName);

            using var self = Process.GetCurrentProcess();
            var psi = UpdaterStartInfo(dotnet, installDir, pendingDir, self.Id, dropZipVariable);
            using var started = Process.Start(psi)
                ?? throw new InvalidOperationException("The updater did not start.");

            Logger.Information("Self-update: started the updater: {0} {1}",
                dotnet, string.Join(" ", psi.ArgumentList.Select(a => "\"" + a + "\"")));
        }

        /// <summary>
        /// What a note starts with when it is an account of an update that WORKED rather than a
        /// problem to be put in front of the host.
        /// <para>
        /// The notes are the PowerShell watchdog's, which 1.2.12 and every release before it
        /// started. The update INTO 1.2.13 is still swapped by that watchdog, because the part of
        /// an update that swaps the files belongs to the version being left, so this reader stays
        /// for the one start that follows it. From 1.2.13 on the updater writes RESULT.txt instead
        /// (<see cref="ManagedUpdater.ReadAndClearResult"/>).
        /// </para>
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
        /// Where the 1.2.12 watchdog left a note about an update. It sits outside the watchdog's
        /// work folder on purpose, because the watchdog deleted that folder when it was done.
        /// </summary>
        internal static string UpdateReportPath() => ReportPathOverride ?? Path.Combine(
            AppDataRoot.LocalRoot,
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
