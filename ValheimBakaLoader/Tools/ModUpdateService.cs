using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
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
        /// </summary>
        Task<List<ModUpdateResult>> UpdateModsAsync(IEnumerable<InstalledMod> mods);
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

        public async Task<List<ModUpdateResult>> UpdateModsAsync(IEnumerable<InstalledMod> mods)
        {
            var results = new List<ModUpdateResult>();
            if (mods == null) return results;

            foreach (var mod in mods)
            {
                results.Add(await UpdateModAsync(mod));
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
