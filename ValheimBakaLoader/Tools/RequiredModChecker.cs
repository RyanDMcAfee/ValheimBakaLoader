using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using ValheimBakaLoader.Tools.Logging;

namespace ValheimBakaLoader.Tools
{
    /// <summary>
    /// Describes a BepInEx mod that BakaLoader requires for full functionality.
    /// </summary>
    public class RequiredMod
    {
        public string Author { get; set; }
        public string ModName { get; set; }
        public string Description { get; set; }
        public string ThunderstoreUrl { get; set; }

        /// <summary>Thunderstore-style folder name in BepInEx/plugins (e.g. "AviiNL-RCON").</summary>
        public string FolderName => $"{Author}-{ModName}";

        /// <summary>Identifies which BakaLoader features depend on this mod.</summary>
        public string RequiredFor { get; set; }
    }

    public interface IRequiredModChecker
    {
        /// <summary>
        /// Returns any required mods that are NOT installed in the given plugins directory.
        /// Returns an empty list when everything is present or the directory doesn't exist.
        /// </summary>
        List<RequiredMod> GetMissingMods(string pluginsDir);

        /// <summary>
        /// Downloads and installs a required mod from Thunderstore into the given plugins directory.
        /// Returns true on success.
        /// </summary>
        Task<bool> InstallModAsync(RequiredMod mod, string pluginsDir);
    }

    public class RequiredModChecker : IRequiredModChecker
    {
        private readonly IApplicationLogger Logger;
        private readonly IThunderstoreClient Thunderstore;

        /// <summary>
        /// The mods BakaLoader needs for RCON communication and server-side commands.
        /// BepInExPack is assumed to be present (the server won't even load mods without it).
        /// </summary>
        public static readonly List<RequiredMod> RequiredMods = new()
        {
            new RequiredMod
            {
                Author = "AviiNL",
                ModName = "RCON",
                Description = "RCON (Remote Console) interface - lets BakaLoader send commands to the running server.",
                ThunderstoreUrl = "https://thunderstore.io/c/valheim/p/AviiNL/RCON/",
                RequiredFor = "All remote server control (broadcasts, spawning, player management)"
            },
            new RequiredMod
            {
                Author = "JereKuusela",
                ModName = "Server_devcommands",
                Description = "Enables devcommands on the dedicated server (broadcast, damage, teleport, etc.).",
                ThunderstoreUrl = "https://thunderstore.io/c/valheim/p/JereKuusela/Server_devcommands/",
                RequiredFor = "In-game broadcasts, player heal/damage/teleport, restart countdown messages"
            },
            new RequiredMod
            {
                Author = "JereKuusela",
                ModName = "Rcon_Commands",
                Description = "Bridges devcommands to the RCON interface so they can be executed remotely.",
                ThunderstoreUrl = "https://thunderstore.io/c/valheim/p/JereKuusela/Rcon_Commands/",
                RequiredFor = "Executing devcommands (broadcast, dmg, tp) via RCON from BakaLoader"
            }
        };

        public RequiredModChecker(IApplicationLogger logger, IThunderstoreClient thunderstore)
        {
            Logger = logger;
            Thunderstore = thunderstore;
        }

        public List<RequiredMod> GetMissingMods(string pluginsDir)
        {
            if (string.IsNullOrWhiteSpace(pluginsDir) || !Directory.Exists(pluginsDir))
                return new List<RequiredMod>();

            // BakaLoaderCommander (our bundled companion plugin) natively provides everything
            // the third-party trio did: its own Source-RCON listener + broadcast/playerlist/
            // dmg/tp/kick/baka_spawn/baka_killall implemented against the game API directly.
            // When it's installed, nothing is "missing" even without the third-party mods.
            if (CommanderInstaller.IsInstalled(pluginsDir))
                return new List<RequiredMod>();

            var missing = new List<RequiredMod>();
            foreach (var mod in RequiredMods)
            {
                var modDir = Path.Combine(pluginsDir, mod.FolderName);
                // Check for the folder AND at least one .dll inside it
                if (!Directory.Exists(modDir) || !Directory.GetFiles(modDir, "*.dll", SearchOption.TopDirectoryOnly).Any())
                {
                    missing.Add(mod);
                }
            }

            return missing;
        }

        public async Task<bool> InstallModAsync(RequiredMod mod, string pluginsDir)
        {
            try
            {
                Logger.Information("Looking up latest version of {author}/{mod} on Thunderstore...", mod.Author, mod.ModName);

                var package = await Thunderstore.GetLatestAsync(mod.Author, mod.ModName);
                if (package == null || string.IsNullOrEmpty(package.DownloadUrl))
                {
                    Logger.Error("Could not find {author}/{mod} on Thunderstore.", mod.Author, mod.ModName);
                    return false;
                }

                Logger.Information("Downloading {author}/{mod} v{version}...", mod.Author, mod.ModName, package.LatestVersion);

                // Download the zip
                using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
                var zipBytes = await http.GetByteArrayAsync(package.DownloadUrl);
                if (zipBytes == null || zipBytes.Length == 0)
                {
                    Logger.Error("Downloaded empty file for {author}/{mod}.", mod.Author, mod.ModName);
                    return false;
                }

                Logger.Information("Downloaded {size} bytes. Extracting...", zipBytes.Length);

                // Extract to a temp dir first
                var tempDir = Path.Combine(Path.GetTempPath(), $"BakaLoader_ModInstall_{Guid.NewGuid():N}");
                Directory.CreateDirectory(tempDir);

                try
                {
                    using (var ms = new MemoryStream(zipBytes))
                    using (var zip = new ZipArchive(ms, ZipArchiveMode.Read))
                    {
                        ExtractSafely(zip, tempDir);
                    }

                    // Copy to the plugins directory
                    var targetDir = Path.Combine(pluginsDir, mod.FolderName);
                    if (!Directory.Exists(targetDir))
                        Directory.CreateDirectory(targetDir);

                    CopyDirectory(tempDir, targetDir);

                    Logger.Information("Installed {author}/{mod} v{version} to {dir}",
                        mod.Author, mod.ModName, package.LatestVersion, targetDir);

                    return true;
                }
                finally
                {
                    try { Directory.Delete(tempDir, recursive: true); }
                    catch { /* cleanup is best-effort */ }
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Failed to install {author}/{mod}: {error}", mod.Author, mod.ModName, ex.Message);
                return false;
            }
        }

        private static void CopyDirectory(string sourceDir, string targetDir)
        {
            foreach (var file in Directory.GetFiles(sourceDir))
            {
                var destFile = Path.Combine(targetDir, Path.GetFileName(file));
                File.Copy(file, destFile, overwrite: true);
            }

            foreach (var dir in Directory.GetDirectories(sourceDir))
            {
                var destDir = Path.Combine(targetDir, Path.GetFileName(dir));
                if (!Directory.Exists(destDir))
                    Directory.CreateDirectory(destDir);
                CopyDirectory(dir, destDir);
            }
        }

        /// <summary>
        /// Extracts a zip archive while guarding against path-traversal ("zip slip"):
        /// each entry's resolved path must stay inside <paramref name="destDir"/>.
        /// </summary>
        private static void ExtractSafely(ZipArchive zip, string destDir)
        {
            var destRoot = Path.GetFullPath(destDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

            foreach (var entry in zip.Entries)
            {
                // Directory entries have an empty name; just ensure the folder exists.
                if (string.IsNullOrEmpty(entry.Name))
                    continue;

                var targetPath = Path.GetFullPath(Path.Combine(destRoot, entry.FullName));
                if (!targetPath.StartsWith(destRoot, StringComparison.OrdinalIgnoreCase))
                    throw new IOException($"Zip entry '{entry.FullName}' would extract outside the target directory.");

                var parent = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrEmpty(parent))
                    Directory.CreateDirectory(parent);

                entry.ExtractToFile(targetPath, overwrite: true);
            }
        }
    }
}
