using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ValheimBakaLoader.Tools.Http;
using ValheimBakaLoader.Tools.Logging;
using ValheimBakaLoader.Tools.Models;

namespace ValheimBakaLoader.Tools
{
    /// <summary>
    /// Result of attempting to update a single installed mod.
    /// </summary>
    public class ModUpdateResult
    {
        public InstalledMod Mod { get; init; }
        public bool Updated { get; init; }
        public string FromVersion { get; init; }
        public string ToVersion { get; init; }
        public string Error { get; init; }

        public static ModUpdateResult Success(InstalledMod mod, string from, string to) =>
            new() { Mod = mod, Updated = true, FromVersion = from, ToVersion = to };

        public static ModUpdateResult Skipped(InstalledMod mod) =>
            new() { Mod = mod, Updated = false, FromVersion = mod?.InstalledVersion, ToVersion = mod?.InstalledVersion };

        public static ModUpdateResult Failed(InstalledMod mod, string error) =>
            new() { Mod = mod, Updated = false, FromVersion = mod?.InstalledVersion, Error = error };
    }

    /// <summary>
    /// One step of a bulk update, reported so the UI can show a determinate bar and a
    /// per-mod status. <see cref="Phase"/> is "updating" before a mod is worked on, then
    /// "done" (updated or already current) or "failed" after it. <see cref="Index"/> is
    /// 1-based within a run of <see cref="Total"/> mods.
    /// </summary>
    public class ModUpdateProgress
    {
        public int Index { get; init; }
        public int Total { get; init; }
        public string Mod { get; init; }
        public string Phase { get; init; }
        public string FromVersion { get; init; }
        public string ToVersion { get; init; }
        public string Error { get; init; }
    }

    /// <summary>
    /// Result of installing a mod from a pasted Thunderstore link.
    /// </summary>
    public class ModInstallResult
    {
        public string Owner { get; init; }
        public string Name { get; init; }
        public string Version { get; init; }
        public string Folder { get; init; }
        public bool Installed { get; init; }
        /// <summary>True when an existing install was backed up and replaced.</summary>
        public bool Replaced { get; init; }
        public string Error { get; init; }

        /// <summary>Which site the files came from: "thunderstore" or "hexium".</summary>
        public string Source { get; init; }

        /// <summary>
        /// The mods this package's own manifest says it needs, as the index listed
        /// them. Nothing here is installed for the host: it is handed back so the
        /// page can say what else this mod expects to find. Never null.
        /// </summary>
        public string[] Dependencies { get; init; } = Array.Empty<string>();
    }

    /// <summary>
    /// Everything needed to fetch one Hexium build, worked out before the host was
    /// asked and handed back unchanged once they accepted. The download address is
    /// the one the index gave, never a rebuilt one.
    /// </summary>
    public class HexiumInstallPlan
    {
        public string Owner { get; init; }
        public string Name { get; init; }
        public string Version { get; init; }
        public string DownloadUrl { get; init; }
        public long? FileSize { get; init; }
        public string[] Dependencies { get; init; } = Array.Empty<string>();

        public string FolderName => $"{Owner}-{Name}";
    }

    public interface IModUpdateService
    {
        /// <summary>
        /// Downloads and installs the latest Thunderstore release for a single mod,
        /// replacing the mod's plugin folder in place after backing up the old copy.
        /// Returns a result describing whether the mod was updated, skipped (already
        /// current), or failed. The server must be stopped before calling this.
        /// </summary>
        /// <param name="stop">
        /// Told to stop. Honoured before the mod's folder is backed up and cleared, so a walk
        /// that was given up on never leaves a half-replaced folder behind for a server that is
        /// about to start over it.
        /// </param>
        Task<ModUpdateResult> UpdateModAsync(InstalledMod mod, CancellationToken stop = default);

        /// <summary>
        /// Updates every supplied mod that has a newer version available on Thunderstore.
        /// When <paramref name="progress"/> is given, reports one "updating" step before each
        /// mod and one "done"/"failed" step after it, so a caller can follow the run live.
        /// <para>
        /// <paramref name="stop"/> is the restart window saying its clock ran out. The walk is
        /// sequential and each mod's folder is cleared and refilled, so the token is read
        /// between mods and again before any folder is touched: the relaunch waits on this
        /// call, and a server started over a folder still being written is a broken mod.
        /// </para>
        /// </summary>
        Task<List<ModUpdateResult>> UpdateModsAsync(
            IEnumerable<InstalledMod> mods,
            IProgress<ModUpdateProgress> progress = null,
            CancellationToken stop = default);

        /// <summary>
        /// Installs a mod from a parsed Thunderstore reference into the given
        /// BepInEx plugins directory. A version on the link is fetched exactly as
        /// named, with no lookup at all. A link with no version asks Thunderstore
        /// about that package directly, so what lands is what the package's own page
        /// shows; the held community index stands behind that for packages the
        /// per-package address does not answer for. An existing install is backed up
        /// and replaced.
        /// </summary>
        Task<ModInstallResult> InstallFromThunderstoreAsync(ThunderstoreModReference reference, string pluginsDir);

        /// <summary>
        /// Installs one already-resolved Hexium build into the given BepInEx plugins
        /// directory, backing up and replacing an existing folder the same way an
        /// update does, and leaving a note recording where the files came from.
        /// <para>
        /// This is only ever reached after the host has read what the download is and
        /// said yes; nothing in the app calls it on its own. The address is used
        /// exactly as the index gave it, its host is checked before and after any
        /// redirect, the download is capped, and a size the index named has to match
        /// or nothing is replaced.
        /// </para>
        /// </summary>
        Task<ModInstallResult> InstallFromHexiumAsync(HexiumInstallPlan plan, string pluginsDir);
    }

    /// <summary>
    /// Installs Thunderstore mod updates by downloading the package zip, backing up
    /// the existing plugin folder, and replacing it with the new contents. Backups are
    /// written OUTSIDE the BepInEx <c>plugins</c> directory so BepInEx never tries to
    /// load a backed-up copy. On any failure the previous folder is restored.
    ///
    /// IMPORTANT: the Valheim server must NOT be running while mods are replaced -
    /// callers are responsible for stopping it first (see the auto-update restart flow).
    /// </summary>
    public class ModUpdateService : IModUpdateService
    {
        private const string BackupDirName = ".bakaloader-mod-backups";

        /// <summary>
        /// The clocks every mod download is held to. HttpClient.Timeout used to be the only
        /// one and it never covered the body: a site that answered its headers and then stopped
        /// sending hung the scheduled restart's mod pass with the world already stopped, so the
        /// relaunch after it was never reached and the world stayed down. Settable so a test
        /// can prove a stalled address costs a second.
        /// </summary>
        public DownloadBudget DownloadBudget { get; set; } = new()
        {
            HeaderTimeout = TimeSpan.FromSeconds(30),
            TotalTimeout = TimeSpan.FromMinutes(10),
            IdleTimeout = TimeSpan.FromSeconds(30),
        };

        /// <summary>
        /// The most a Hexium download is allowed to weigh. The largest Valheim mod
        /// packages run to a few hundred megabytes, so this leaves room for a real
        /// one while stopping a download that never ends from filling the disk.
        /// Settable so a test can prove the cap with a small file.
        /// </summary>
        public long MaxHexiumDownloadBytes { get; set; } = 600L * 1024 * 1024;

        /// <summary>
        /// The most a Thunderstore package is allowed to weigh. The same number as the Hexium
        /// cap and a property of its own, because the two are separate sites and one of them
        /// changing its mind about how big a package may be is not a statement about the other.
        /// Before 1.2.6 this path had no cap at all: a link that answered with a hundred
        /// gigabytes would have filled the disk.
        /// </summary>
        public long MaxModDownloadBytes { get; set; } = 600L * 1024 * 1024;

        /// <summary>How many redirects a download is allowed to follow before it gives up.</summary>
        public int MaxDownloadRedirects { get; set; } = 5;

        private readonly IThunderstoreClient Thunderstore;
        private readonly IHttpClientProvider HttpClientProvider;
        private readonly IApplicationLogger Logger;

        public ModUpdateService(
            IThunderstoreClient thunderstore,
            IHttpClientProvider httpClientProvider,
            IApplicationLogger logger)
        {
            Thunderstore = thunderstore;
            HttpClientProvider = httpClientProvider;
            Logger = logger;
        }

        public async Task<List<ModUpdateResult>> UpdateModsAsync(
            IEnumerable<InstalledMod> mods,
            IProgress<ModUpdateProgress> progress = null,
            CancellationToken stop = default)
        {
            var results = new List<ModUpdateResult>();
            if (mods == null) return results;

            // Materialize so Total is known up front and the source is only walked once.
            var list = new List<InstalledMod>(mods);
            var total = list.Count;
            var index = 0;

            foreach (var mod in list)
            {
                // Between mods, which is the one place the walk can be left in a state where
                // nothing is half written. Everything already done stays done.
                if (stop.IsCancellationRequested)
                {
                    Logger.Information(
                        "The mod updates stopped after {0} of {1}: the restart window ran out.",
                        index, total);
                    break;
                }

                index++;
                progress?.Report(new ModUpdateProgress
                {
                    Index = index,
                    Total = total,
                    Mod = mod?.FullName,
                    Phase = "updating",
                });

                var result = await UpdateModAsync(mod, stop);
                results.Add(result);

                progress?.Report(result.Error != null
                    ? new ModUpdateProgress
                    {
                        Index = index,
                        Total = total,
                        Mod = mod?.FullName,
                        Phase = "failed",
                        Error = result.Error,
                    }
                    : new ModUpdateProgress
                    {
                        Index = index,
                        Total = total,
                        Mod = mod?.FullName,
                        Phase = "done",
                        FromVersion = result.FromVersion,
                        ToVersion = result.ToVersion,
                    });
            }

            return results;
        }

        public async Task<ModUpdateResult> UpdateModAsync(InstalledMod mod, CancellationToken stop = default)
        {
            if (mod == null) return ModUpdateResult.Failed(null, "No mod specified.");

            if (string.IsNullOrWhiteSpace(mod.Author) || string.IsNullOrWhiteSpace(mod.ModName))
            {
                return ModUpdateResult.Failed(mod, "Mod is missing an author or name (cannot look it up on Thunderstore).");
            }

            if (string.IsNullOrWhiteSpace(mod.PluginDirectory) || !Directory.Exists(mod.PluginDirectory))
            {
                return ModUpdateResult.Failed(mod, $"Mod folder not found: {mod.PluginDirectory}");
            }

            // A copy the host took from Hexium is left alone by every update path.
            // The row offers the swap back to Thunderstore as its own action, and that
            // one asks first; nothing here replaces those files on its own.
            if (mod.IsHexiumInstalled)
            {
                Logger.Information("Mod {0} was installed from Hexium, so the Thunderstore update is not applied.", mod.FullName);
                return ModUpdateResult.Skipped(mod);
            }

            // Resolve the latest published version + download URL. Thunderstore is asked
            // about this one package directly before any held index is read, so a release
            // published minutes ago is the one that gets installed.
            LatestLookup lookup;
            try
            {
                lookup = await ResolveLatestAsync(mod.Author, mod.ModName);
            }
            catch (Exception e)
            {
                return ModUpdateResult.Failed(mod, $"Thunderstore lookup failed: {e.Message}");
            }

            var package = lookup.Package;
            var downloadFrom = DownloadAddressFor(package, mod.Author, mod.ModName);
            if (downloadFrom == null)
            {
                // Same distinction the install path makes: a site that would not speak is
                // not a mod that has been pulled.
                return ModUpdateResult.Failed(mod, lookup.SiteAnswered
                    ? "Could not find this mod on Thunderstore (no download URL)."
                    : "Thunderstore did not answer, so this mod could not be checked. Try again in a little while.");
            }

            var fromVersion = mod.InstalledVersion;
            var toVersion = package.LatestVersion;

            // Nothing to do if we're already on (or ahead of) the latest version.
            if (!SemVer.IsNewer(toVersion, fromVersion))
            {
                Logger.Information("Mod {0} is already up to date ({1}).", mod.FullName, fromVersion);
                return ModUpdateResult.Skipped(mod);
            }

            var tempZip = Path.Combine(Path.GetTempPath(), $"bakaloader-{Guid.NewGuid():N}.zip");
            var tempExtract = Path.Combine(Path.GetTempPath(), $"bakaloader-{Guid.NewGuid():N}");
            string backupDir = null;

            try
            {
                Logger.Information("Updating {0}: {1} -> {2}", mod.FullName, fromVersion, toVersion);

                var written = await DownloadFileAsync(downloadFrom, tempZip, stop);

                // Everything that could turn this download away happens before the folder on
                // disk is touched, the way the Hexium path beside it already did.
                var damaged = DescribeDamage(lookup.Published, tempZip, written);
                if (damaged != null)
                    return ModUpdateResult.Failed(mod,
                        $"The download did not arrive whole: {damaged}. Nothing was replaced.");

                Directory.CreateDirectory(tempExtract);
                ZipFile.ExtractToDirectory(tempZip, tempExtract, overwriteFiles: true);
                StripSourceMarkers(tempExtract);

                if (!HasAnyEntries(tempExtract))
                {
                    return ModUpdateResult.Failed(mod, "Downloaded package was empty.");
                }

                // The last moment before anything on disk moves. A restart that gave up
                // waiting is about to start the server over this very folder, and a clear that
                // is still running when it does leaves the mod half replaced with the new file
                // locked by the server that just loaded it.
                if (stop.IsCancellationRequested)
                {
                    Logger.Information(
                        "{0} was left as it is: the restart window ran out before it was replaced.",
                        mod.FullName);
                    return ModUpdateResult.Skipped(mod);
                }

                // Back up the current folder OUTSIDE plugins/ so BepInEx won't load it.
                backupDir = BackupModFolder(mod.PluginDirectory);

                // Replace the folder contents with the freshly extracted package.
                ClearDirectory(mod.PluginDirectory);
                CopyDirectory(tempExtract, mod.PluginDirectory);

                // Reflect the new version on the in-memory model.
                mod.InstalledVersion = toVersion;
                mod.LatestVersion = toVersion;

                Logger.Information("Updated {0} to {1} (backup at {2}).", mod.FullName, toVersion, backupDir);
                return ModUpdateResult.Success(mod, fromVersion, toVersion);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                // The download is the only thing here that a token can interrupt, and it runs
                // before the folder is backed up or cleared, so nothing on disk has moved.
                Logger.Information(
                    "{0} was not updated: the restart window ran out while it was downloading.",
                    mod.FullName);
                return ModUpdateResult.Skipped(mod);
            }
            catch (Exception e)
            {
                Logger.Error(e, "Failed to update {0}; attempting to restore the previous version.", mod.FullName);

                // Roll back if we got far enough to back up + start replacing.
                if (backupDir != null && Directory.Exists(backupDir))
                {
                    try
                    {
                        ClearDirectory(mod.PluginDirectory);
                        CopyDirectory(backupDir, mod.PluginDirectory);
                        Logger.Information("Restored previous version of {0}.", mod.FullName);
                    }
                    catch (Exception restoreError)
                    {
                        Logger.Error(restoreError,
                            "Could not restore {0}. The previous files are preserved at {1}.",
                            mod.FullName, backupDir);
                        return ModUpdateResult.Failed(mod,
                            $"Update failed AND automatic restore failed. Previous files are at: {backupDir}");
                    }
                }

                return ModUpdateResult.Failed(mod, e.Message);
            }
            finally
            {
                TryDelete(tempZip);
                TryDeleteDirectory(tempExtract);
            }
        }

        public async Task<ModInstallResult> InstallFromThunderstoreAsync(ThunderstoreModReference reference, string pluginsDir)
        {
            ModInstallResult Fail(string error) => new()
            {
                Owner = reference?.Owner,
                Name = reference?.Name,
                Version = reference?.Version,
                Installed = false,
                Error = error,
            };

            if (reference == null) return Fail("No mod reference supplied.");
            if (string.IsNullOrWhiteSpace(pluginsDir) || !Directory.Exists(pluginsDir))
                return Fail("BepInEx plugins folder not found. Set a valid server .exe path first.");

            // --- Resolve the download URL + concrete version ---
            string downloadUrl;
            var version = reference.Version;

            // What the site published about the build being fetched, when it published
            // anything. The size on it is what the download is held to before a folder the
            // server loads from is replaced.
            ThunderstorePackageVersion published = null;

            if (!string.IsNullOrWhiteSpace(version))
            {
                // A pinned version's download URL is always constructable directly, and
                // nothing is looked up at all: the link already said which build to fetch.
                downloadUrl = ConstructedDownloadUrl(reference.Owner, reference.Name, version);
            }
            else
            {
                // Latest: ask Thunderstore about this package directly, and read the held
                // index only if that did not answer. A link with no version on it used to
                // resolve through the index alone, which is how a host who deleted a mod
                // and pasted its link back got yesterday's build handed to them.
                var lookup = await ResolveLatestAsync(reference.Owner, reference.Name);

                downloadUrl = DownloadAddressFor(lookup.Package, reference.Owner, reference.Name);
                if (downloadUrl == null)
                {
                    // "Thunderstore has no such package" and "Thunderstore did not answer"
                    // are different sentences, and a host adding a brand new mod during a
                    // bad minute on the internet should not be told their mod is not real.
                    return Fail(lookup.SiteAnswered
                        ? $"Could not find {reference.Owner}/{reference.Name} on Thunderstore."
                        : $"Thunderstore did not answer about {reference.Owner}/{reference.Name}. Try again in a little while.");
                }

                version = lookup.Package.LatestVersion;
                published = lookup.Published;
            }

            // --- Download + extract + install ---
            var targetDir = Path.Combine(pluginsDir, reference.FolderName);
            var replacing = Directory.Exists(targetDir) && HasAnyEntries(targetDir);

            var tempZip = Path.Combine(Path.GetTempPath(), $"bakaloader-{Guid.NewGuid():N}.zip");
            var tempExtract = Path.Combine(Path.GetTempPath(), $"bakaloader-{Guid.NewGuid():N}");
            string backupDir = null;

            // Which part of the work was running when something went wrong. The catch below
            // used to hand the host a raw zip-internals sentence and then send them to the
            // mod's Versions page, which had nothing to tell them: the version existed, the
            // bytes did not all arrive. A hint is only true for the stage it describes.
            var stage = "download";

            try
            {
                Logger.Information("Installing {0} v{1} from Thunderstore ({2}).",
                    reference.FolderName, version ?? "?", downloadUrl);

                var written = await DownloadFileAsync(downloadUrl, tempZip);

                // Held to what the site said about it before anything on disk is touched,
                // the way the Hexium path sixty lines below already was.
                var damaged = DescribeDamage(published, tempZip, written);
                if (damaged != null)
                    return Fail($"The download did not arrive whole: {damaged}. Nothing was replaced.");

                stage = "unpack";

                Directory.CreateDirectory(tempExtract);
                // ZipFile.ExtractToDirectory rejects entries that resolve outside
                // the destination (zip-slip) on .NET Core/5+.
                ZipFile.ExtractToDirectory(tempZip, tempExtract, overwriteFiles: true);
                StripSourceMarkers(tempExtract);

                if (!HasAnyEntries(tempExtract))
                    return Fail("Downloaded package was empty.");

                stage = "install";

                if (replacing)
                {
                    backupDir = BackupModFolder(targetDir);
                    ClearDirectory(targetDir);
                }

                CopyDirectory(tempExtract, targetDir);

                Logger.Information("Installed {0} v{1} to {2}{3}.",
                    reference.FolderName, version ?? "?", targetDir,
                    backupDir != null ? $" (previous copy backed up to {backupDir})" : "");

                return new ModInstallResult
                {
                    Owner = reference.Owner,
                    Name = reference.Name,
                    Version = version,
                    Folder = targetDir,
                    Installed = true,
                    Replaced = replacing,
                    Source = "thunderstore",
                };
            }
            catch (Exception e)
            {
                Logger.Error(e, "Failed to install {0} from Thunderstore.", reference.FolderName);

                // Roll back a partially-replaced existing install.
                if (backupDir != null && Directory.Exists(backupDir))
                {
                    try
                    {
                        ClearDirectory(targetDir);
                        CopyDirectory(backupDir, targetDir);
                        Logger.Information("Restored previous copy of {0}.", reference.FolderName);
                    }
                    catch (Exception restoreError)
                    {
                        Logger.Error(restoreError,
                            "Could not restore {0}. The previous files are preserved at {1}.",
                            reference.FolderName, backupDir);
                        return Fail($"Install failed AND automatic restore failed. Previous files are at: {backupDir}");
                    }
                }
                else if (!replacing)
                {
                    // Don't leave a half-written brand-new folder for BepInEx to trip on.
                    TryDeleteDirectory(targetDir);
                }

                return Fail(InstallFailureSentence(stage, e, reference.Version));
            }
            finally
            {
                TryDelete(tempZip);
                TryDeleteDirectory(tempExtract);
            }
        }

        public async Task<ModInstallResult> InstallFromHexiumAsync(HexiumInstallPlan plan, string pluginsDir)
        {
            ModInstallResult Fail(string error) => new()
            {
                Owner = plan?.Owner,
                Name = plan?.Name,
                Version = plan?.Version,
                Installed = false,
                Source = ModSourceMarkerFile.HexiumSource,
                Dependencies = plan?.Dependencies ?? Array.Empty<string>(),
                Error = error,
            };

            if (plan == null) return Fail("No mod was named.");
            if (string.IsNullOrWhiteSpace(plan.Owner) || string.IsNullOrWhiteSpace(plan.Name))
                return Fail("No owner and mod name to install.");
            if (string.IsNullOrWhiteSpace(plan.Version))
                return Fail("No version to install.");
            if (string.IsNullOrWhiteSpace(pluginsDir) || !Directory.Exists(pluginsDir))
                return Fail("BepInEx plugins folder not found. Set a valid server .exe path first.");

            // The address came from the index and is used as it was given. It still has
            // to be an address on Hexium: a link that points anywhere else is refused
            // before a single byte is asked for.
            if (!HexiumUrlParser.IsDownloadAddress(plan.DownloadUrl, out var downloadUri))
                return Fail("That download address is not on hexium.gg, so it was not fetched.");

            var targetDir = Path.Combine(pluginsDir, plan.FolderName);
            var replacing = Directory.Exists(targetDir) && HasAnyEntries(targetDir);

            var tempZip = Path.Combine(Path.GetTempPath(), $"bakaloader-{Guid.NewGuid():N}.zip");
            var tempExtract = Path.Combine(Path.GetTempPath(), $"bakaloader-{Guid.NewGuid():N}");
            string backupDir = null;

            try
            {
                Logger.Information("Installing {0} v{1} from Hexium ({2}).",
                    plan.FolderName, plan.Version, downloadUri);

                // Everything that could turn the download away happens before the folder
                // on disk is touched, so a refused download leaves the install as it was.
                var written = await DownloadHexiumFileAsync(downloadUri, tempZip);

                if (plan.FileSize is { } expected && expected > 0 && written != expected)
                {
                    return Fail($"The download did not match the size Hexium listed ({written} bytes against {expected}). Nothing was replaced.");
                }

                Directory.CreateDirectory(tempExtract);
                // ZipFile.ExtractToDirectory turns down entries that resolve outside the
                // destination (zip slip) on .NET Core and later.
                ZipFile.ExtractToDirectory(tempZip, tempExtract, overwriteFiles: true);

                // A package cannot ship its own history: any note inside the archive goes
                // before the folder is placed, and BakaLoader writes its own afterwards.
                StripSourceMarkers(tempExtract);

                if (!HasAnyEntries(tempExtract))
                    return Fail("The downloaded package was empty.");

                if (replacing)
                {
                    backupDir = BackupModFolder(targetDir);
                    ClearDirectory(targetDir);
                }

                CopyDirectory(tempExtract, targetDir);

                ModSourceMarkerFile.Write(targetDir, new ModSourceMarker
                {
                    Schema = ModSourceMarkerFile.CurrentSchema,
                    Writer = $"{ModSourceMarkerFile.WriterPrefix} {AssemblyHelper.GetApplicationVersion()}",
                    Source = ModSourceMarkerFile.HexiumSource,
                    Owner = plan.Owner,
                    Name = plan.Name,
                    Version = plan.Version,
                    DownloadUrl = plan.DownloadUrl,
                    FileSize = plan.FileSize,
                    InstalledUtc = DateTime.UtcNow,
                });

                Logger.Information("Installed {0} v{1} from Hexium to {2}{3}.",
                    plan.FolderName, plan.Version, targetDir,
                    backupDir != null ? $" (previous copy backed up to {backupDir})" : "");

                return new ModInstallResult
                {
                    Owner = plan.Owner,
                    Name = plan.Name,
                    Version = plan.Version,
                    Folder = targetDir,
                    Installed = true,
                    Replaced = replacing,
                    Source = ModSourceMarkerFile.HexiumSource,
                    Dependencies = plan.Dependencies ?? Array.Empty<string>(),
                };
            }
            catch (Exception e)
            {
                Logger.Error(e, "Failed to install {0} from Hexium.", plan.FolderName);

                if (backupDir != null && Directory.Exists(backupDir))
                {
                    try
                    {
                        ClearDirectory(targetDir);
                        CopyDirectory(backupDir, targetDir);
                        Logger.Information("Restored previous copy of {0}.", plan.FolderName);
                    }
                    catch (Exception restoreError)
                    {
                        Logger.Error(restoreError,
                            "Could not restore {0}. The previous files are preserved at {1}.",
                            plan.FolderName, backupDir);
                        return Fail($"Install failed AND automatic restore failed. Previous files are at: {backupDir}");
                    }
                }
                else if (!replacing)
                {
                    // Never leave a half-written new folder for BepInEx to trip on.
                    TryDeleteDirectory(targetDir);
                }

                return Fail(e.Message);
            }
            finally
            {
                TryDelete(tempZip);
                TryDeleteDirectory(tempExtract);
            }
        }

        /// <summary>
        /// Streams a Hexium package to a file, following any redirect by hand so each
        /// hop is checked against the same rule the first address was, and stopping the
        /// moment the file passes the cap. Answers how many bytes were written.
        /// </summary>
        private async Task<long> DownloadHexiumFileAsync(Uri url, string destinationPath)
        {
            using var client = HttpClientProvider.CreateClient();
            BoundedDownload.Unbounded(client);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(HexiumClient.UserAgent());

            // Copy(), not the property itself: the Hexium cap belongs to this one download.
            var budget = DownloadBudget.Copy();
            budget.MaxBytes = MaxHexiumDownloadBytes;

            var address = url;
            HttpResponseMessage response = null;

            try
            {
                for (var hop = 0; ; hop++)
                {
                    response?.Dispose();
                    response = await BoundedDownload.HeadersAsync(client, address, budget, CancellationToken.None);

                    // A real HttpClient follows redirects itself, so this loop usually runs
                    // once; the final address is checked below either way.
                    var location = IsRedirect(response) ? response.Headers.Location : null;
                    if (location == null) break;

                    if (hop >= MaxDownloadRedirects)
                        throw new IOException("That download redirected too many times.");

                    address = location.IsAbsoluteUri ? location : new Uri(address, location);
                    if (!HexiumUrlParser.IsDownloadAddress(address.ToString(), out _))
                        throw new IOException("That download redirected off hexium.gg, so it was stopped.");
                }

                // Whoever followed the redirects, the address the bytes are coming from
                // has to still be Hexium's. This is read before the body is touched.
                var finalAddress = response.RequestMessage?.RequestUri ?? address;
                if (!HexiumUrlParser.IsDownloadAddress(finalAddress.ToString(), out _))
                    throw new IOException("That download ended up off hexium.gg, so it was stopped.");

                response.EnsureSuccessStatusCode();

                var cap = MaxHexiumDownloadBytes;
                if (response.Content.Headers.ContentLength is { } declared && cap > 0 && declared > cap)
                    throw new IOException($"That download is larger than BakaLoader will fetch ({declared} bytes against a limit of {cap}).");

                // The body's own two clocks: a total, and a watchdog that gives up when nothing
                // has arrived for a while. The 120 second request timeout this used to lean on
                // was released the moment the headers landed.
                using var whole = BoundedDownload.Deadline(budget.TotalTimeout, CancellationToken.None);
                await using var raw = await response.Content.ReadAsStreamAsync(whole.Token);
                await using var source = new IdleWatchdogStream(
                    raw, budget.IdleTimeout, whole.Token, Logger, "Hexium download");
                await using var destination = File.Create(destinationPath);

                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, 0, buffer.Length, whole.Token)) > 0)
                {
                    total += read;
                    if (cap > 0 && total > cap)
                        throw new IOException($"That download passed the size BakaLoader will fetch ({cap} bytes), so it was stopped.");

                    await destination.WriteAsync(buffer, 0, read, CancellationToken.None);
                }

                return total;
            }
            finally
            {
                response?.Dispose();
            }
        }

        private static bool IsRedirect(HttpResponseMessage response)
        {
            var status = (int)response.StatusCode;
            return status is 301 or 302 or 303 or 307 or 308;
        }

        /// <summary>
        /// Clears any BakaLoader source note out of a freshly-unpacked archive, so a
        /// downloaded package can never claim to have come from somewhere it did not.
        /// </summary>
        private void StripSourceMarkers(string extractedRoot)
        {
            var removed = ModSourceMarkerFile.StripFrom(extractedRoot);
            if (removed > 0)
                Logger.Warning("Removed {0} source note(s) that came inside the downloaded package.", removed);
        }

        /// <summary>
        /// The newest release of a package, asked of Thunderstore directly first and read
        /// from the held index only when that did not answer.
        /// <para>
        /// The order matters. The community listing BakaLoader holds is a snapshot the
        /// site rebuilds on a timer, so it can name yesterday's version while the package's
        /// own page names today's. The per-package address is the one the page reads, so it
        /// is never behind, and it answers for every community rather than this one. The
        /// held list stays behind it for the times that address does not answer at all: a
        /// blip, a rate limit, a machine that has gone offline since the list was read.
        /// </para>
        /// The package is null when neither answered. Never throws.
        /// </summary>
        private async Task<LatestLookup> ResolveLatestAsync(string owner, string name)
        {
            ThunderstoreLiveLookup live = null;
            try { live = await Thunderstore.LookupLiveAsync(owner, name); }
            catch (Exception e) { Logger.Debug("Live Thunderstore lookup failed for {0}-{1}: {2}", owner, name, e.Message); }

            if (!string.IsNullOrWhiteSpace(live?.Package?.LatestVersion))
                return new LatestLookup
                {
                    Package = live.Package,
                    SiteAnswered = true,
                    Published = await WeighedByTheListingAsync(owner, name, live.Package),
                };

            var answered = live?.Answered ?? false;

            ThunderstorePackage fromList = null;
            try { fromList = await Thunderstore.GetLatestAsync(owner, name); }
            catch (Exception e)
            {
                Logger.Debug("Thunderstore index lookup failed for {0}-{1}: {2}", owner, name, e.Message);
            }

            // A list that had the package in it is Thunderstore speaking too, even when
            // the package's own address did not.
            return new LatestLookup
            {
                Package = fromList,
                SiteAnswered = answered || fromList != null,
                // The list is the one answer that carries a size, so when it is what named
                // the version it is also what the download is held to, with no second hop.
                Published = fromList?.Latest,
            };
        }

        /// <summary>
        /// What the site published about the build being fetched, with a size on it wherever one
        /// can be had.
        /// <para>
        /// The package page names the version and leaves out what it weighs: its "latest" object
        /// carries version_number, download_url, date_created and a handful of display fields,
        /// and no file_size and no sha256. That address is the one asked FIRST and the one that
        /// answers on every ordinary install and update, so the check that holds a download to a
        /// published size had nothing to compare with on the one path every host walks: a
        /// damaged archive was backed up over, cleared and written with no eyebrow raised.
        /// </para>
        /// <para>
        /// The community listing carries a file_size per version, so it is asked for the size of
        /// the very version the page named. The answer is only taken when the listing's newest
        /// version IS that version, because a size belongs to one archive and a size off a
        /// different build would turn every install into a failed integrity check. This is the
        /// same second hop the BepInEx installer has made since it was written. With a size from
        /// neither, the install goes ahead and the log says it was held to nothing: a guard that
        /// turns away every host is worse than the defect.
        /// </para>
        /// </summary>
        private async Task<ThunderstorePackageVersion> WeighedByTheListingAsync(
            string owner, string name, ThunderstorePackage fromThePage)
        {
            var page = fromThePage?.Latest;
            if (page == null || page.FileSize != null) return page;

            ThunderstorePackage listed = null;
            try { listed = await Thunderstore.GetLatestAsync(owner, name); }
            catch (Exception e)
            {
                Logger.Debug("The package listing did not answer about {0}-{1}: {2}", owner, name, e.Message);
            }

            var sameBuild = string.Equals(listed?.LatestVersion, page.VersionNumber, StringComparison.OrdinalIgnoreCase)
                ? listed.Latest
                : null;

            if (sameBuild?.FileSize == null)
            {
                Logger.Information(
                    "Thunderstore published no size for {0}-{1} {2}, so what came down was checked against nothing.",
                    owner, name, page.VersionNumber);
                return page;
            }

            // A copy of the page's answer with the listing's size on it. The objects either
            // answer with are held by the client, so nothing that came from it is written into.
            return new ThunderstorePackageVersion
            {
                VersionNumber = page.VersionNumber,
                DownloadUrl = page.DownloadUrl,
                WebsiteUrl = page.WebsiteUrl,
                FullName = page.FullName,
                DateCreated = page.DateCreated ?? sameBuild.DateCreated,
                FileSize = sameBuild.FileSize,
                Sha256 = string.IsNullOrWhiteSpace(page.Sha256) ? sameBuild.Sha256 : page.Sha256,
                IsActiveRaw = page.IsActiveRaw,
            };
        }

        /// <summary>
        /// What resolving a package's newest release came to. The package is the answer;
        /// <see cref="SiteAnswered"/> is what separates "Thunderstore has no such package"
        /// from "Thunderstore did not answer", which are two different things to tell a
        /// host who is watching an install fail.
        /// </summary>
        private sealed class LatestLookup
        {
            public ThunderstorePackage Package { get; init; }

            public bool SiteAnswered { get; init; }

            /// <summary>
            /// What the site published about the build that is about to be fetched, with a size
            /// on it wherever one could be had. Read this rather than
            /// <c>Package.Latest</c>: the package page answers with no size at all, so
            /// <c>Package.Latest</c> is what left every ordinary download held to nothing.
            /// Null when nothing was resolved.
            /// </summary>
            public ThunderstorePackageVersion Published { get; init; }
        }

        /// <summary>
        /// Where to fetch a resolved package from: the address Thunderstore itself gave,
        /// used exactly as it came, and only when it gave none is one built from the
        /// version number. Null when there is no version to fetch at all.
        /// </summary>
        private static string DownloadAddressFor(ThunderstorePackage package, string owner, string name)
        {
            if (!string.IsNullOrWhiteSpace(package?.DownloadUrl)) return package.DownloadUrl;

            var version = package?.LatestVersion;
            return string.IsNullOrWhiteSpace(version) ? null : ConstructedDownloadUrl(owner, name, version);
        }

        /// <summary>The address of one named build, built by BakaLoader rather than accepted from anywhere.</summary>
        private static string ConstructedDownloadUrl(string owner, string name, string version) =>
            $"https://thunderstore.io/package/download/{owner}/{name}/{version}/";

        /// <summary>
        /// A Thunderstore package, into a file, under a header deadline, a total deadline and
        /// an idle watchdog. Answers how many bytes were written so the caller can hold them
        /// against the size the site published before it replaces a folder the server loads.
        /// </summary>
        private async Task<long> DownloadFileAsync(
            string url, string destinationPath, CancellationToken cancellationToken = default)
        {
            using var client = HttpClientProvider.CreateClient();
            client.DefaultRequestHeaders.UserAgent.ParseAdd("ValheimBakaLoader");

            // Copy(), not the property itself: the Thunderstore cap belongs to this one
            // download, and the Hexium path above sets a different one on the same service.
            var budget = DownloadBudget.Copy();
            budget.MaxBytes = MaxModDownloadBytes;

            return await BoundedDownload.ToFileAsync(
                client, url, destinationPath, budget, cancellationToken, tracer: Logger, what: "mod download");
        }

        /// <summary>
        /// What is wrong with the archive that arrived, in a sentence, or null when nothing is.
        /// <para>
        /// The Hexium path has held its downloads to the listed size since it was written, and
        /// the BepInEx installer to the size and the digest. The Thunderstore install and
        /// update paths replaced a folder the server loads from and were held to nothing: a
        /// package damaged in transit unpacked without a raised eyebrow, the host's working mod
        /// was backed up and cleared, and what went in its place was a file BepInEx would fail
        /// to load with no record anywhere of why.
        /// </para>
        /// </summary>
        internal static string DescribeDamage(ThunderstorePackageVersion published, string zipPath, long written)
        {
            if (published == null) return null;

            if (published.FileSize is { } expected && expected > 0 && written != expected)
                return $"it arrived as {written} bytes where Thunderstore published {expected}";

            var sha = published.Sha256?.Trim();
            if (!string.IsNullOrEmpty(sha))
            {
                var actual = Sha256Of(zipPath);
                if (actual != null && !string.Equals(actual, sha, StringComparison.OrdinalIgnoreCase))
                    return "it did not match the digest Thunderstore published";
            }

            return null;
        }

        /// <summary>
        /// What the host reads when an install failed, chosen by the stage it failed in.
        /// <para>
        /// The version hint is only true for a lookup or a 404, which is the download stage.
        /// It used to be added to every failure from the download onward, so a package whose
        /// bytes did not all arrive handed the host ".NET's End of Central Directory record
        /// could not be found" and then sent them to a Versions page that would tell them
        /// nothing at all. The raw exception is already in the log line above the caller.
        /// </para>
        /// </summary>
        internal static string InstallFailureSentence(string stage, Exception problem, string pinnedVersion)
        {
            if (stage == "unpack")
                return "The package arrived but could not be unpacked, so nothing was replaced. "
                    + "That is nearly always a download that did not come whole: try again in a little while.";

            if (stage == "install")
                return "The package could not be put in place, so the files that were there before "
                    + "were restored. The log line beside this one says what stopped it.";

            var hint = !string.IsNullOrWhiteSpace(pinnedVersion)
                ? " (check that this version exists on the mod's Versions page)"
                : "";

            return (problem?.Message ?? "The download did not get through.") + hint;
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
        /// Copies the mod folder to a timestamped backup directory under BepInEx
        /// (a sibling of <c>plugins</c>, so it is never scanned as a plugin). Returns
        /// the backup path.
        /// </summary>
        private static string BackupModFolder(string pluginDirectory)
        {
            // pluginDirectory = ...\BepInEx\plugins\{Author-ModName}
            var pluginsRoot = Directory.GetParent(pluginDirectory)?.FullName; // ...\BepInEx\plugins
            var bepInExRoot = Directory.GetParent(pluginsRoot)?.FullName ?? pluginsRoot; // ...\BepInEx
            var modFolderName = new DirectoryInfo(pluginDirectory).Name;

            var backupRoot = Path.Combine(bepInExRoot, BackupDirName, modFolderName);
            Directory.CreateDirectory(backupRoot);

            var backupDir = Path.Combine(backupRoot, DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            CopyDirectory(pluginDirectory, backupDir);
            return backupDir;
        }

        private static bool HasAnyEntries(string directory)
        {
            return Directory.Exists(directory) &&
                   Directory.EnumerateFileSystemEntries(directory).GetEnumerator().MoveNext();
        }

        private static void ClearDirectory(string directory)
        {
            if (!Directory.Exists(directory)) return;

            foreach (var file in Directory.GetFiles(directory))
            {
                File.SetAttributes(file, FileAttributes.Normal);
                File.Delete(file);
            }
            foreach (var dir in Directory.GetDirectories(directory))
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        private static void CopyDirectory(string sourceDir, string destinationDir)
        {
            Directory.CreateDirectory(destinationDir);

            foreach (var file in Directory.GetFiles(sourceDir))
            {
                var target = Path.Combine(destinationDir, Path.GetFileName(file));
                File.Copy(file, target, overwrite: true);
            }

            foreach (var dir in Directory.GetDirectories(sourceDir))
            {
                CopyDirectory(dir, Path.Combine(destinationDir, Path.GetFileName(dir)));
            }
        }

        private void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception e) { Logger.Debug("Could not delete temp file {0}: {1}", path, e.Message); }
        }

        private void TryDeleteDirectory(string path)
        {
            try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
            catch (Exception e) { Logger.Debug("Could not delete temp dir {0}: {1}", path, e.Message); }
        }
    }
}
