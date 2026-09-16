using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ValheimBakaLoader.Tools.Logging;
using ValheimBakaLoader.Tools.Models;

namespace ValheimBakaLoader.Tools
{
    /// <summary>
    /// Result of attempting to remove a single installed mod (and optionally its config).
    /// </summary>
    public class ModRemovalResult
    {
        public InstalledMod Mod { get; init; }
        public bool Removed { get; init; }
        public string BackupDirectory { get; init; }
        public List<string> DeletedConfigFiles { get; init; } = new();
        public string Error { get; init; }

        public static ModRemovalResult Success(InstalledMod mod, string backup, List<string> configs) =>
            new() { Mod = mod, Removed = true, BackupDirectory = backup, DeletedConfigFiles = configs ?? new List<string>() };

        public static ModRemovalResult Failed(InstalledMod mod, string error) =>
            new() { Mod = mod, Removed = false, Error = error };
    }

    public interface IModRemovalService
    {
        /// <summary>
        /// Heuristically finds the BepInEx <c>config</c> (.cfg) files that belong to a mod,
        /// matching config file names against the mod's name (BepInEx config files are named
        /// after the plugin GUID, e.g. "author.modname.cfg" or "modname.cfg"). Read-only.
        /// </summary>
        List<string> FindConfigFiles(InstalledMod mod);

        /// <summary>
        /// Removes a mod by moving its plugin folder, its matching patcher folder (patcher-type
        /// mods install under <c>BepInEx/patchers</c>), and, when requested, its matched config
        /// files into a recoverable backup under <c>BepInEx/.bakaloader-removed</c>, then deleting
        /// the originals. Works for a plugin-only, a patcher-only, or a plugin-plus-patcher mod.
        /// The Valheim server must be STOPPED first, since a running server locks loaded DLLs.
        /// </summary>
        ModRemovalResult RemoveMod(InstalledMod mod, bool includeConfig);
    }

    /// <summary>
    /// Deletes installed mods (and optionally their config) from a BepInEx install. Rather
    /// than hard-deleting, the removed files are first copied to a timestamped backup folder
    /// OUTSIDE <c>plugins</c> (so BepInEx never loads them), making the removal recoverable.
    /// </summary>
    public class ModRemovalService : IModRemovalService
    {
        private const string RemovedDirName = ".bakaloader-removed";

        private readonly IApplicationLogger Logger;

        public ModRemovalService(IApplicationLogger logger)
        {
            Logger = logger;
        }

        public List<string> FindConfigFiles(InstalledMod mod)
        {
            var results = new List<string>();

            var configDir = GetConfigDirectory(mod);
            if (configDir == null || !Directory.Exists(configDir)) return results;

            var modToken = Normalize(mod?.ModName);
            if (modToken.Length == 0) return results;

            foreach (var file in Directory.EnumerateFiles(configDir, "*.cfg", SearchOption.TopDirectoryOnly))
            {
                // BepInEx config files are named after the plugin GUID. Normalizing both sides
                // (lowercase, alphanumeric-only) lets "author.modname.cfg", "com.author.mod.cfg",
                // and bare "modname.cfg" all match the mod name token.
                var name = Normalize(Path.GetFileNameWithoutExtension(file));
                if (name.Contains(modToken))
                {
                    results.Add(file);
                }
            }

            return results;
        }

        public ModRemovalResult RemoveMod(InstalledMod mod, bool includeConfig)
        {
            if (mod == null) return ModRemovalResult.Failed(null, "No mod specified.");

            var hasPlugin = !string.IsNullOrWhiteSpace(mod.PluginDirectory) && Directory.Exists(mod.PluginDirectory);

            // A patcher-type mod installs under BepInEx/patchers. Removing only the plugin folder
            // leaves the patcher DLL loading on every boot, so the patcher folder has to go too.
            var patcherDir = ResolvePatcherDirectory(mod);
            var hasPatcher = !string.IsNullOrWhiteSpace(patcherDir) && Directory.Exists(patcherDir);

            if (!hasPlugin && !hasPatcher)
            {
                return ModRemovalResult.Failed(mod, $"Mod folder not found: {mod.PluginDirectory ?? mod.PatcherDirectory}");
            }

            // The folder name identifies the mod. Companion plugins and framework/auto-generated
            // patchers are managed by BakaLoader and never appear in the list, but reject removal
            // defensively anyway so a malformed request can never touch them.
            var primaryDir = hasPlugin ? mod.PluginDirectory : patcherDir;
            var folderName = new DirectoryInfo(primaryDir).Name;
            if (ModScanner.IsCompanionFolder(folderName) || ModScanner.IsProtectedPatcher(folderName))
            {
                return ModRemovalResult.Failed(mod, "This plugin is managed by BakaLoader and cannot be removed here.");
            }

            var configs = includeConfig ? FindConfigFiles(mod) : new List<string>();

            try
            {
                var backupDir = CreateBackupRoot(primaryDir);

                // Back up then remove the plugin folder, when the mod has one.
                if (hasPlugin)
                {
                    var pluginBackup = Path.Combine(backupDir, "plugin", new DirectoryInfo(mod.PluginDirectory).Name);
                    CopyDirectory(mod.PluginDirectory, pluginBackup);
                    Directory.Delete(mod.PluginDirectory, recursive: true);
                }

                // Back up then remove the matching patcher folder, when the mod has one. Only a
                // NAMED subfolder inside patchers is ever removed; the shared "patchers" junction
                // itself (which points at the base install) is never deleted or moved.
                if (hasPatcher)
                {
                    var patcherFolderName = new DirectoryInfo(patcherDir).Name;
                    var patcherBackup = Path.Combine(backupDir, "patcher", patcherFolderName);
                    CopyDirectory(patcherDir, patcherBackup);
                    Directory.Delete(patcherDir, recursive: true);
                }

                // Back up then remove each matched config file.
                var deleted = new List<string>();
                foreach (var cfg in configs)
                {
                    try
                    {
                        var cfgBackupDir = Path.Combine(backupDir, "config");
                        Directory.CreateDirectory(cfgBackupDir);
                        File.Copy(cfg, Path.Combine(cfgBackupDir, Path.GetFileName(cfg)), overwrite: true);
                        File.SetAttributes(cfg, FileAttributes.Normal);
                        File.Delete(cfg);
                        deleted.Add(cfg);
                    }
                    catch (Exception e)
                    {
                        Logger.Warning("Could not delete config '{0}': {1}", cfg, e.Message);
                    }
                }

                Logger.Information("Removed mod {0} ({1} config file(s)); backup at {2}.",
                    mod.FullName, deleted.Count, backupDir);
                return ModRemovalResult.Success(mod, backupDir, deleted);
            }
            catch (Exception e)
            {
                Logger.Error(e, "Failed to remove mod {0}.", mod.FullName);
                return ModRemovalResult.Failed(mod, e.Message);
            }
        }

        /// <summary>
        /// Creates a timestamped backup root under <c>BepInEx/.bakaloader-removed/{Author-ModName}/</c>,
        /// derived from the mod's primary folder (its plugin folder, or its patcher folder for a
        /// patcher-only mod).
        /// </summary>
        private static string CreateBackupRoot(string primaryDir)
        {
            var bepInExRoot = GetBepInExRootFromDir(primaryDir);
            var modFolderName = new DirectoryInfo(primaryDir).Name;

            var root = Path.Combine(
                bepInExRoot ?? Path.GetDirectoryName(primaryDir),
                RemovedDirName,
                modFolderName,
                DateTime.Now.ToString("yyyyMMdd-HHmmss"));

            Directory.CreateDirectory(root);
            return root;
        }

        private static string GetConfigDirectory(InstalledMod mod)
        {
            var bepInExRoot = GetBepInExRoot(mod);
            return bepInExRoot == null ? null : Path.Combine(bepInExRoot, "config");
        }

        /// <summary>
        /// Resolves <c>...\BepInEx</c> from a mod, using its plugin folder when present and
        /// otherwise its patcher folder. Both sit at the same depth
        /// (<c>...\BepInEx\plugins\{folder}</c> or <c>...\BepInEx\patchers\{folder}</c>).
        /// </summary>
        private static string GetBepInExRoot(InstalledMod mod)
        {
            var anchor = !string.IsNullOrWhiteSpace(mod?.PluginDirectory)
                ? mod.PluginDirectory
                : mod?.PatcherDirectory;
            return GetBepInExRootFromDir(anchor);
        }

        /// <summary>Resolves <c>...\BepInEx</c> from a <c>...\BepInEx\{plugins|patchers}\{folder}</c> path.</summary>
        private static string GetBepInExRootFromDir(string modFolder)
        {
            if (string.IsNullOrWhiteSpace(modFolder)) return null;

            var subRoot = Directory.GetParent(modFolder)?.FullName;            // ...\BepInEx\plugins or ...\BepInEx\patchers
            return Directory.GetParent(subRoot ?? string.Empty)?.FullName;     // ...\BepInEx
        }

        /// <summary>
        /// The mod's patcher folder: the model's <see cref="InstalledMod.PatcherDirectory"/> when
        /// the scan set it, otherwise <c>BepInEx/patchers/{folderName}</c> computed from the plugin
        /// folder name. Returns null when neither a patcher path nor a plugin folder is known.
        /// </summary>
        private static string ResolvePatcherDirectory(InstalledMod mod)
        {
            if (!string.IsNullOrWhiteSpace(mod?.PatcherDirectory)) return mod.PatcherDirectory;

            if (string.IsNullOrWhiteSpace(mod?.PluginDirectory)) return null;

            var bepInExRoot = GetBepInExRootFromDir(mod.PluginDirectory);
            if (string.IsNullOrWhiteSpace(bepInExRoot)) return null;

            var folderName = new DirectoryInfo(mod.PluginDirectory).Name;
            return Path.Combine(bepInExRoot, "patchers", folderName);
        }

        private static string Normalize(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            return new string(value.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
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
    }
}
