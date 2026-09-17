using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
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
        Task<ModUpdateResult> UpdateModAsync(InstalledMod mod);

        /// <summary>
        /// Updates every supplied mod that has a newer version available on Thunderstore.
        /// When <paramref name="progress"/> is given, reports one "updating" step before each
        /// mod and one "done"/"failed" step after it, so a caller can follow the run live.
        /// </summary>
        Task<List<ModUpdateResult>> UpdateModsAsync(IEnumerable<InstalledMod> mods, IProgress<ModUpdateProgress> progress = null);

        /// <summary>
        /// Installs a mod from a parsed Thunderstore reference into the given
        /// BepInEx plugins directory. A null version installs the latest release
        /// (resolved via the Valheim community index, with the experimental
        /// per-package API as a fallback for packages outside that community).
        /// An existing install is backed up and replaced.
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
        private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(5);

        /// <summary>
        /// The most a Hexium download is allowed to weigh. The largest Valheim mod
        /// packages run to a few hundred megabytes, so this leaves room for a real
        /// one while stopping a download that never ends from filling the disk.
        /// Settable so a test can prove the cap with a small file.
        /// </summary>
        public long MaxHexiumDownloadBytes { get; set; } = 600L * 1024 * 1024;

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

        public async Task<List<ModUpdateResult>> UpdateModsAsync(IEnumerable<InstalledMod> mods, IProgress<ModUpdateProgress> progress = null)
        {
            var results = new List<ModUpdateResult>();
            if (mods == null) return results;

            // Materialize so Total is known up front and the source is only walked once.
            var list = new List<InstalledMod>(mods);
            var total = list.Count;
            var index = 0;

            foreach (var mod in list)
            {
                index++;
                progress?.Report(new ModUpdateProgress
                {
                    Index = index,
                    Total = total,
                    Mod = mod?.FullName,
                    Phase = "updating",
                });

                var result = await UpdateModAsync(mod);
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

        public async Task<ModUpdateResult> UpdateModAsync(InstalledMod mod)
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

            // Resolve the latest published version + download URL.
            ThunderstorePackage package;
            try
            {
                package = await Thunderstore.GetLatestAsync(mod.Author, mod.ModName);
            }
            catch (Exception e)
            {
                return ModUpdateResult.Failed(mod, $"Thunderstore lookup failed: {e.Message}");
            }

            if (package == null || string.IsNullOrWhiteSpace(package.DownloadUrl))
            {
                return ModUpdateResult.Failed(mod, "Could not find this mod on Thunderstore (no download URL).");
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

                await DownloadFileAsync(package.DownloadUrl, tempZip);

                Directory.CreateDirectory(tempExtract);
                ZipFile.ExtractToDirectory(tempZip, tempExtract, overwriteFiles: true);
                StripSourceMarkers(tempExtract);

                if (!HasAnyEntries(tempExtract))
                {
                    return ModUpdateResult.Failed(mod, "Downloaded package was empty.");
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

            if (!string.IsNullOrWhiteSpace(version))
            {
                // A pinned version's download URL is always constructable directly.
                downloadUrl = $"https://thunderstore.io/package/download/{reference.Owner}/{reference.Name}/{version}/";
            }
            else
            {
                // Latest: try the cached Valheim community index first.
                ThunderstorePackage package = null;
                try { package = await Thunderstore.GetLatestAsync(reference.Owner, reference.Name); }
                catch (Exception e) { Logger.Debug("Community-index lookup failed for {0}: {1}", reference.FolderName, e.Message); }

                if (!string.IsNullOrWhiteSpace(package?.DownloadUrl))
                {
                    downloadUrl = package.DownloadUrl;
                    version = package.LatestVersion;
                }
                else
                {
                    // Not in the Valheim index (e.g. a package from another community's
                    // page like old.thunderstore.io/package/bbepis/BepInExPack/) -
                    // fall back to the per-package experimental endpoint.
                    var (fallbackVersion, fallbackUrl, fallbackError) =
                        await FetchLatestViaExperimentalApiAsync(reference.Owner, reference.Name);
                    if (string.IsNullOrWhiteSpace(fallbackUrl))
                        return Fail(fallbackError ?? $"Could not find {reference.Owner}/{reference.Name} on Thunderstore.");

                    downloadUrl = fallbackUrl;
                    version = fallbackVersion;
                }
            }

            // --- Download + extract + install ---
            var targetDir = Path.Combine(pluginsDir, reference.FolderName);
            var replacing = Directory.Exists(targetDir) && HasAnyEntries(targetDir);

            var tempZip = Path.Combine(Path.GetTempPath(), $"bakaloader-{Guid.NewGuid():N}.zip");
            var tempExtract = Path.Combine(Path.GetTempPath(), $"bakaloader-{Guid.NewGuid():N}");
            string backupDir = null;

            try
            {
                Logger.Information("Installing {0} v{1} from Thunderstore ({2}).",
                    reference.FolderName, version ?? "?", downloadUrl);

                await DownloadFileAsync(downloadUrl, tempZip);

                Directory.CreateDirectory(tempExtract);
                // ZipFile.ExtractToDirectory rejects entries that resolve outside
                // the destination (zip-slip) on .NET Core/5+.
                ZipFile.ExtractToDirectory(tempZip, tempExtract, overwriteFiles: true);
                StripSourceMarkers(tempExtract);

                if (!HasAnyEntries(tempExtract))
                    return Fail("Downloaded package was empty.");

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

                var hint = !string.IsNullOrWhiteSpace(reference.Version)
                    ? " (check that this version exists on the mod's Versions page)"
                    : "";
                return Fail(e.Message + hint);
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
            client.Timeout = DownloadTimeout;
            client.DefaultRequestHeaders.UserAgent.ParseAdd(HexiumClient.UserAgent());

            var address = url;
            HttpResponseMessage response = null;

            try
            {
                for (var hop = 0; ; hop++)
                {
                    response?.Dispose();
                    response = await client.GetAsync(address, HttpCompletionOption.ResponseHeadersRead);

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

                await using var source = await response.Content.ReadAsStreamAsync();
                await using var destination = File.Create(destinationPath);

                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    total += read;
                    if (cap > 0 && total > cap)
                        throw new IOException($"That download passed the size BakaLoader will fetch ({cap} bytes), so it was stopped.");

                    await destination.WriteAsync(buffer, 0, read);
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
        /// Resolves a package's latest version + download URL via Thunderstore's
        /// experimental per-package API. Used only as a fallback for packages that
        /// aren't in the cached Valheim community index.
        /// </summary>
        private async Task<(string Version, string Url, string Error)> FetchLatestViaExperimentalApiAsync(
            string owner, string name)
        {
            try
            {
                using var client = HttpClientProvider.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(30);
                client.DefaultRequestHeaders.UserAgent.ParseAdd("ValheimBakaLoader");

                var url = $"https://thunderstore.io/api/experimental/package/{owner}/{name}/";
                using var response = await client.GetAsync(url);
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                    return (null, null, $"Thunderstore has no package named {owner}/{name}.");
                if (!response.IsSuccessStatusCode)
                    return (null, null, $"Thunderstore lookup failed (HTTP {(int)response.StatusCode}).");

                var json = JObject.Parse(await response.Content.ReadAsStringAsync());
                var latest = json["latest"];
                var version = latest?.Value<string>("version_number");
                var download = latest?.Value<string>("download_url");

                if (string.IsNullOrWhiteSpace(download))
                    return (null, null, $"Thunderstore returned no download URL for {owner}/{name}.");

                return (version, download, null);
            }
            catch (Exception e)
            {
                return (null, null, $"Thunderstore lookup failed: {e.Message}");
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
