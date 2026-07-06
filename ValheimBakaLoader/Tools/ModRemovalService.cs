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
        /// Removes a mod by moving its plugin folder (and, when requested, its matched config
        /// files) into a recoverable backup under <c>BepInEx/.bakaloader-removed</c>, then
        /// deleting the originals. The Valheim server must be STOPPED first - a running server
        /// locks loaded plugin DLLs.
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

            if (string.IsNullOrWhiteSpace(mod.PluginDirectory) || !Directory.Exists(mod.PluginDirectory))
            {
                return ModRemovalResult.Failed(mod, $"Mod folder not found: {mod.PluginDirectory}");
            }

            // Companion plugins are managed by BakaLoader itself (and MMHOOK is patcher-generated);
            // they never appear in the mods list, but reject removal defensively anyway.
            if (ModScanner.IsCompanionFolder(new DirectoryInfo(mod.PluginDirectory).Name))
            {
                return ModRemovalResult.Failed(mod, "This plugin is managed by BakaLoader and cannot be removed here.");
            }

            var configs = includeConfig ? FindConfigFiles(mod) : new List<string>();

            try
            {
                var backupDir = CreateBackupRoot(mod);

                // Back up then remove the plugin folder.
                var pluginBackup = Path.Combine(backupDir, "plugin", new DirectoryInfo(mod.PluginDirectory).Name);
                CopyDirectory(mod.PluginDirectory, pluginBackup);
                Directory.Delete(mod.PluginDirectory, recursive: true);

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
        /// Creates a timestamped backup root under <c>BepInEx/.bakaloader-removed/{Author-ModName}/</c>.
        /// </summary>
        private static string CreateBackupRoot(InstalledMod mod)
        {
            var bepInExRoot = GetBepInExRoot(mod);
            var modFolderName = new DirectoryInfo(mod.PluginDirectory).Name;

            var root = Path.Combine(
                bepInExRoot ?? Path.GetDirectoryName(mod.PluginDirectory),
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

        /// <summary>Resolves <c>...\BepInEx</c> from a mod's <c>...\BepInEx\plugins\{folder}</c> path.</summary>
        private static string GetBepInExRoot(InstalledMod mod)
        {
            if (string.IsNullOrWhiteSpace(mod?.PluginDirectory)) return null;

            var pluginsRoot = Directory.GetParent(mod.PluginDirectory)?.FullName; // ...\BepInEx\plugins
            return Directory.GetParent(pluginsRoot ?? string.Empty)?.FullName;     // ...\BepInEx
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
