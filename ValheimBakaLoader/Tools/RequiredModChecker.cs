using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ValheimBakaLoader.Tools.Http;
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
                Description = "RCON (Remote Console) interface. It lets BakaLoader send commands to the running server.",
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

        public RequiredModChecker(
            IApplicationLogger logger, IThunderstoreClient thunderstore, IHttpClientProvider httpClientProvider)
        {
            Logger = logger;
            Thunderstore = thunderstore;
            HttpClientProvider = httpClientProvider;
        }

        private readonly IHttpClientProvider HttpClientProvider;

        /// <summary>
        /// The clocks this download is held to, and the most it may weigh. It used to be an
        /// HttpClient of its own with a two minute timeout and GetByteArrayAsync, which means
        /// it missed the Connection card's two switches, the fifteen second connect timeout
        /// and the wire trace, and took the whole body into memory with no cap at all.
        /// </summary>
        public DownloadBudget DownloadBudget { get; set; } = new()
        {
            HeaderTimeout = TimeSpan.FromSeconds(30),
            TotalTimeout = TimeSpan.FromMinutes(5),
            IdleTimeout = TimeSpan.FromSeconds(30),
            MaxBytes = 256L * 1024 * 1024,
        };

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

                // The app's own client, so the Connection card's two switches, the connect
                // timeout and the wire trace reach this download like every other one, and to a
                // file under a declared cap rather than into one unbounded byte array.
                using var http = HttpClientProvider.CreateClient();
                http.DefaultRequestHeaders.UserAgent.ParseAdd("ValheimBakaLoader");

                var tempZip = Path.Combine(
                    Path.GetTempPath(), $"BakaLoader_ModInstall_{Guid.NewGuid():N}.zip");

                // The finally below owns the temp zip from the moment the name is chosen. A
                // download that threw part way through still wrote a file, and the stall
                // watchdog, the size cap and a connection that died in the middle of the body
                // all end that way: whatever had arrived used to sit in the temp folder until
                // Windows cleaned it out, once per failed press.
                try
                {
                    var written = await BoundedDownload.ToFileAsync(
                        http, package.DownloadUrl, tempZip, DownloadBudget, CancellationToken.None,
                        tracer: Logger, what: "mod download");

                    if (written <= 0)
                    {
                        Logger.Error("Downloaded empty file for {author}/{mod}.", mod.Author, mod.ModName);
                        return false;
                    }

                    // Held to the size and the digest Thunderstore published, the way the mod
                    // update path beside it is. This path writes into the folder BepInEx loads
                    // from and checked neither, so a package damaged in transit went in whole
                    // and the server failed to load it with no record anywhere of why.
                    var damaged = ModUpdateService.DescribeDamage(package.Latest, tempZip, written);
                    if (damaged != null)
                    {
                        Logger.Error(
                            "The download of {author}/{mod} did not arrive whole: {reason}. Nothing was installed.",
                            mod.Author, mod.ModName, damaged);
                        return false;
                    }

                    Logger.Information("Downloaded {size} bytes. Extracting...", written);

                    // Extract to a temp dir first
                    var tempDir = Path.Combine(Path.GetTempPath(), $"BakaLoader_ModInstall_{Guid.NewGuid():N}");
                    Directory.CreateDirectory(tempDir);

                    try
                    {
                        using (var zip = ZipFile.OpenRead(tempZip))
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
                finally
                {
                    TryDelete(tempZip);
                }
            }
            catch (Exception ex)
            {
                // The exception OBJECT, not only its message. On a timeout the message names
                // the clock and nothing else ("the configured HttpClient.Timeout elapsed"),
                // while the type and the innermost reason are what say what actually failed,
                // and both used to be dropped on the floor here.
                Logger.Error(ex, "Failed to install {author}/{mod}: {error}",
                    mod.Author, mod.ModName, Http.WireTrace.Innermost(ex));
                return false;
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
                Logger.Debug("Could not delete {0}: {1}", path, e.Message);
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
