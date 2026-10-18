using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ValheimBakaLoader.Tools.Logging;

namespace ValheimBakaLoader.Tools
{
    /// <summary>
    /// Puts an unpacked package where a mod manager would, under one realm's BepInEx folder,
    /// and can put everything back the way it was if any step fails.
    /// <para>
    /// The rule for each file is <see cref="PackageLayout.Map"/>. This is the part with a disk
    /// under it: the package's own folders under plugins, patchers, monomod and core are
    /// backed up and cleared first, so an old nested install is replaced by the managed one on
    /// the next update or a pinned reinstall, and a config file that is already there is never
    /// written over, because what is in it is the host's. A config file this install wrote new
    /// is the only config file a rollback ever removes.
    /// </para>
    /// <para>
    /// Nothing is ever written through a link to another install. A realm's patchers folder
    /// that is still the old junction to the base install is turned into its own copy first
    /// (<see cref="InstallIsolationService.EnsureOwnPatchersFolder"/>); any other folder this
    /// package would land in that is a link (core is one on every isolated realm) refuses the
    /// whole install before a single file moves.
    /// </para>
    /// </summary>
    internal sealed class PackagePlacement
    {
        private const string BackupDirName = ".bakaloader-mod-backups";

        private sealed class PlannedFile
        {
            public string Source;
            public string Destination;
            public PackageFileKind Kind;
        }

        private sealed class PackageFolder
        {
            public PackageFileKind Kind;
            public string Path;
            public bool Existed;
            public string Backup;
            public bool Cleared;
        }

        private readonly string BepInExRoot;
        private readonly string PackageFolderName;
        private readonly IApplicationLogger Logger;
        private readonly List<PlannedFile> Files;
        private readonly List<PackageFolder> Folders = new();
        private readonly List<string> NewConfigFiles = new();

        /// <summary>Called with each destination just before it is written. A test throws from it to prove the rollback.</summary>
        internal Action<string> BeforeEachFile { get; set; }

        /// <summary>The plugins folder backup, which is where an older build kept its one backup. Null when there was nothing to back up.</summary>
        public string PluginsBackup => Folders.FirstOrDefault(f => f.Kind == PackageFileKind.Plugins)?.Backup;

        /// <summary>The package's plugins folder, where the manifest and BakaLoader's source note sit.</summary>
        public string PluginsFolder => Path.Combine(BepInExRoot, "plugins", PackageFolderName);

        /// <summary>The package's patchers folder, whether or not this package has one.</summary>
        public string PatchersFolder => Path.Combine(BepInExRoot, "patchers", PackageFolderName);

        /// <summary>The package's monomod folder, whether or not this package has one.</summary>
        public string MonomodFolder => Path.Combine(BepInExRoot, "monomod", PackageFolderName);

        public int Placed { get; private set; }
        public int ConfigKept { get; private set; }
        public int ConfigWritten => NewConfigFiles.Count;

        /// <summary>Why this package cannot go in at all, worked out before anything moved. Null when it can.</summary>
        public string Refusal { get; private set; }

        private PackagePlacement(string bepInExRoot, string packageFolderName, IApplicationLogger logger, List<PlannedFile> files)
        {
            BepInExRoot = bepInExRoot;
            PackageFolderName = packageFolderName;
            Logger = logger;
            Files = files;
        }

        /// <summary>
        /// Maps every file under <paramref name="extractedRoot"/> and decides whether the package
        /// can be placed. Reads only; nothing under BepInEx is touched until <see cref="Apply"/>.
        /// </summary>
        public static PackagePlacement Plan(string extractedRoot, string bepInExRoot, string packageFolderName, IApplicationLogger logger)
        {
            var byDestination = new Dictionary<string, PlannedFile>(StringComparer.OrdinalIgnoreCase);
            var order = new List<string>();

            // The package's own manifest.json and friends at the archive root go last, so a
            // bundled copy of some other mod's manifest that flattens onto the same name can
            // never be the one the Mods page reads the version from.
            var sources = Directory.EnumerateFiles(extractedRoot, "*", SearchOption.AllDirectories)
                .Select(path => (Path: path, Relative: Path.GetRelativePath(extractedRoot, path).Replace('\\', '/')))
                .OrderBy(e => PackageLayout.IsRootMetadata(e.Relative) ? 1 : 0)
                .ThenBy(e => e.Relative, StringComparer.Ordinal)
                .ToList();

            foreach (var (path, relative) in sources)
            {
                var (destination, kind) = PackageLayout.Map(relative, packageFolderName);
                if (destination == null || kind == PackageFileKind.Ignored) continue;

                if (byDestination.ContainsKey(destination))
                    logger?.Debug("Two files in {0} land on {1}; the later one ({2}) is kept.", packageFolderName, destination, relative);
                else
                    order.Add(destination);

                byDestination[destination] = new PlannedFile
                {
                    Source = path,
                    Destination = Path.Combine(bepInExRoot, destination.Replace('/', Path.DirectorySeparatorChar)),
                    Kind = kind,
                };
            }

            var placement = new PackagePlacement(bepInExRoot, packageFolderName, logger,
                order.Select(d => byDestination[d]).ToList());

            foreach (var kind in placement.Files.Select(f => f.Kind).Distinct())
            {
                if (kind == PackageFileKind.Patchers) continue; // converted to the realm's own in Apply
                var root = Path.Combine(bepInExRoot, PackageLayout.FolderOf(kind));
                if (BepInExService.IsReparsePoint(root))
                {
                    placement.Refusal =
                        $"This package puts files in BepInEx/{PackageLayout.FolderOf(kind)}, which this server shares with another install, so nothing was changed.";
                    break;
                }
            }

            return placement;
        }

        /// <summary>
        /// Backs up and clears the package's own folders, then writes every planned file. On a
        /// throw the caller runs <see cref="Rollback"/>; everything done so far is recorded, so
        /// that is safe at any point.
        /// </summary>
        public void Apply(DateTime nowLocal)
        {
            if (Refusal != null) throw new InvalidOperationException(Refusal);

            var touchesPatchers = Files.Any(f => f.Kind == PackageFileKind.Patchers)
                || Directory.Exists(PatchersFolder);
            if (touchesPatchers)
                InstallIsolationService.EnsureOwnPatchersFolder(BepInExRoot, Logger);

            var stamp = nowLocal.ToString("yyyyMMdd-HHmmss");
            var backupBase = Path.Combine(BepInExRoot, BackupDirName, PackageFolderName, stamp);

            foreach (var kind in new[] { PackageFileKind.Plugins, PackageFileKind.Patchers, PackageFileKind.Monomod, PackageFileKind.Core })
            {
                var path = Path.Combine(BepInExRoot, PackageLayout.FolderOf(kind), PackageFolderName);

                // A folder reached through a link belongs to another install. Core is one on
                // every isolated realm; Plan already refused a package that writes there.
                if (BepInExService.IsReparsePoint(Path.GetDirectoryName(path))) continue;

                var folder = new PackageFolder { Kind = kind, Path = path, Existed = Directory.Exists(path) };
                Folders.Add(folder);
                if (!folder.Existed) continue;

                // Plugins keeps the address an older build backed up to, so a failure sentence
                // that names the backup still names the plugins copy.
                folder.Backup = kind == PackageFileKind.Plugins ? backupBase : backupBase + "-" + PackageLayout.FolderOf(kind);
                CopyDirectory(path, folder.Backup);
                ClearDirectory(path);
                folder.Cleared = true;
            }

            foreach (var file in Files)
            {
                BeforeEachFile?.Invoke(file.Destination);

                if (file.Kind == PackageFileKind.Config && File.Exists(file.Destination))
                {
                    // The host's settings are the host's. A mod that wants new keys adds them
                    // itself at its next start.
                    ConfigKept++;
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(file.Destination)!);

                if (file.Kind == PackageFileKind.Config)
                {
                    // Written with no overwrite, so a file that appeared between the check and
                    // the write is still never replaced; recorded only once it exists.
                    File.Copy(file.Source, file.Destination, overwrite: false);
                    NewConfigFiles.Add(file.Destination);
                }
                else
                {
                    File.Copy(file.Source, file.Destination, overwrite: true);
                }

                Placed++;
            }
        }

        /// <summary>
        /// Puts every folder back as it was and takes away the config files this install wrote
        /// new. Config files that were there before were never touched, so they are not touched
        /// here either. Answers null when everything is back, or the folder holding the backup
        /// when something could not be restored, so the host can be told where it is.
        /// </summary>
        public string Rollback()
        {
            string failedAt = null;

            foreach (var config in NewConfigFiles)
            {
                try
                {
                    if (File.Exists(config))
                    {
                        File.SetAttributes(config, FileAttributes.Normal);
                        File.Delete(config);
                    }
                }
                catch (Exception e)
                {
                    Logger?.Warning("Could not take back the new config file {0}: {1}", config, e.Message);
                }
            }

            foreach (var folder in Folders)
            {
                try
                {
                    if (!folder.Existed)
                    {
                        if (Directory.Exists(folder.Path)) Directory.Delete(folder.Path, recursive: true);
                    }
                    else if (folder.Cleared && folder.Backup != null)
                    {
                        ClearDirectory(folder.Path);
                        CopyDirectory(folder.Backup, folder.Path);
                    }
                }
                catch (Exception e)
                {
                    Logger?.Error(e, "Could not restore {0}. The previous files are preserved at {1}.", folder.Path, folder.Backup ?? "(none)");
                    failedAt ??= folder.Backup ?? folder.Path;
                }
            }

            return failedAt;
        }

        internal static void ClearDirectory(string directory)
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

        internal static void CopyDirectory(string sourceDir, string destinationDir)
        {
            Directory.CreateDirectory(destinationDir);

            foreach (var file in Directory.GetFiles(sourceDir))
                File.Copy(file, Path.Combine(destinationDir, Path.GetFileName(file)), overwrite: true);

            foreach (var dir in Directory.GetDirectories(sourceDir))
                CopyDirectory(dir, Path.Combine(destinationDir, Path.GetFileName(dir)));
        }
    }
}
