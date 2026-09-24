using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace ValheimBakaLoader.Tools
{
    /// <summary>On-disk shape of a world save.</summary>
    public enum WorldFormat
    {
        /// <summary>Pre-1.0 pair of files at the root of worlds_local/worlds: "{name}.fwl" + "{name}.db".</summary>
        Legacy = 0,

        /// <summary>Valheim 1.0 world DIRECTORY holding "_main.{N}.fwl2/.db2/.chunks/.ok" plus chunk files.</summary>
        Chunked = 1,
    }

    /// <summary>What a backup layer beside a world actually is.</summary>
    public enum WorldBackupKind
    {
        /// <summary>"{world}_backup_auto-{stamp}" - the game's own rolling snapshot.</summary>
        Auto = 0,

        /// <summary>"{world}_backup_restore-{stamp}" - the safety copy laid down before a restore.</summary>
        Restore = 1,

        /// <summary>"{world}_backup_cloud-{stamp}" - a cloud-sync conflict copy.</summary>
        Cloud = 2,

        /// <summary>
        /// "{world}_backup_{stamp}.fwl/.db" - the untouched pre-conversion files the game
        /// renames aside when it migrates an old world to the 1.0 directory format.
        /// </summary>
        Legacy = 3,

        /// <summary>"{world}.fwl.old" + "{world}.db.old" - the pre-1.0 last-known-good pair.</summary>
        Old = 4,

        /// <summary>
        /// Backup-shaped but none of the above. Kept as the value an unread name falls back to.
        /// </summary>
        Other = 5,

        /// <summary>
        /// "{world}_backup_preupdate-{stamp}" - the snapshot BakaLoader lays down before it
        /// starts a server on a build the world has not been opened by yet. Once a newer
        /// Valheim saves a world there is no way back, so this is the copy that survives it.
        /// </summary>
        PreUpdate = 6,
    }

    /// <summary>One world on disk, in either save format.</summary>
    public sealed class WorldInfo
    {
        /// <summary>World name as the game shows it (directory name, or the .fwl stem).</summary>
        public string Name { get; init; }

        public WorldFormat Format { get; init; }

        /// <summary>
        /// Directory the world's files live in: the world's own directory for a chunked
        /// world, the worlds_local/worlds directory itself for a legacy pair.
        /// </summary>
        public string Folder { get; init; }

        /// <summary>The save-data root this world was found under.</summary>
        public string SaveFolder { get; init; }

        /// <summary>"worlds_local" or "worlds".</summary>
        public string Sub { get; init; }

        /// <summary>The world metadata file: "{name}.fwl", or the committed "_main.{N}.fwl2".</summary>
        public string MetaPath { get; init; }

        /// <summary>The world database: "{name}.db", or the committed "_main.{N}.db2". Null when absent.</summary>
        public string DbPath { get; init; }

        /// <summary>Generation number of a chunked save (the N in "_main.N.*"). Null for legacy worlds.</summary>
        public int? SaveNumber { get; init; }

        /// <summary>Total bytes on disk (directory total for a chunked world).</summary>
        public long SizeBytes { get; init; }

        public DateTime LastWriteUtc { get; init; }

        /// <summary>
        /// True when the world has a finished generation. A chunked world is committed only
        /// when its .fwl2, .db2, .chunks AND .ok all exist: a lone "_main.0.fwl2" means the
        /// world was created but never saved.
        /// </summary>
        public bool IsCommitted { get; init; }

        /// <summary>Short label for the UI: "Legacy" or "1.0".</summary>
        public string FormatLabel => Format == WorldFormat.Chunked ? "1.0" : "Legacy";
    }

    /// <summary>One backup layer beside a world (a file pair, or a whole directory).</summary>
    public sealed class WorldBackupInfo
    {
        /// <summary>The world this layer belongs to.</summary>
        public string World { get; init; }

        /// <summary>
        /// The layer's on-disk name, relative to worlds_local/worlds: the .fwl file name for a
        /// legacy layer, the directory name for a chunked one. This is the token the Barrow
        /// round-trips through the UI.
        /// </summary>
        public string Name { get; init; }

        public WorldBackupKind Kind { get; init; }

        public bool IsDirectory { get; init; }

        /// <summary>Full path of the layer (the .fwl file, or the directory).</summary>
        public string Path { get; init; }

        /// <summary>Metadata file inside the layer ("_main.{N}.fwl2" for a directory).</summary>
        public string MetaPath { get; init; }

        /// <summary>Database file inside the layer, or null when the layer has none.</summary>
        public string DbPath { get; init; }

        public int? SaveNumber { get; init; }

        /// <summary>False for a chunked layer whose newest generation was never committed (.ok missing).</summary>
        public bool IsCommitted { get; init; }

        /// <summary>
        /// True when the layer is missing either half of the pair that makes it loadable: a
        /// "{world}_backup_{stamp}.db" whose ".fwl" partner is gone, or the ".fwl" whose ".db"
        /// is gone. It is listed so the space it takes can be seen and reclaimed, and it can be
        /// deleted, but there is nothing whole to restore from it. Restoring half a pair leaves
        /// the world's metadata and its database describing two different saves.
        /// </summary>
        public bool IsDamaged { get; init; }

        public long SizeBytes { get; init; }

        public DateTime LastWriteUtc { get; init; }

        /// <summary>Timestamp parsed out of the layer name, when it carries one.</summary>
        public DateTime? Stamp { get; init; }

        public bool HasDb => !string.IsNullOrEmpty(DbPath);
    }

    /// <summary>
    /// The single place that knows what a Valheim world looks like on disk, in BOTH save
    /// formats: the pre-1.0 "{name}.fwl" + "{name}.db" pair, and the 1.0 world DIRECTORY
    /// holding "_main.{N}.fwl2 / .db2 / .chunks / .ok" plus its chunk files.
    ///
    /// Two rules drive everything here:
    ///  * a chunked generation only counts once its .ok commit marker is on disk (the game
    ///    writes .ok last and deletes the previous generation after), so the live world is
    ///    the HIGHEST N whose four files all exist;
    ///  * backups are never worlds, and worlds are never backups. A name is a backup only
    ///    when the game itself would call it one: "{world}.fwl.old"/".db.old", or a name the
    ///    game can read a stamp out of. Carrying the "_backup_" marker at its second-to-last
    ///    underscore makes it that world's layer; carrying no marker makes it what the game
    ///    calls a rolling save, which the game files under the name trimmed at its LAST
    ///    underscore and never elects as a world in its own right, so neither do we. What
    ///    such a folder IS owed is a mention: the pre-update pass names it in its skipped
    ///    list so a host is told it was left where it is.
    /// </summary>
    public static class WorldStore
    {
        /// <summary>Both spellings of the world layout, most-preferred first.</summary>
        public static readonly string[] WorldSubfolders = { "worlds_local", "worlds" };

        /// <summary>Sibling folder the game drops "{world}_biomedatacache.bin" into.</summary>
        public const string CacheFolderName = "cache";

        public const string BiomeCacheSuffix = "_biomedatacache.bin";

        // "_main.<N>.fwl2" - the game's own save-number regex.
        private static readonly Regex MainFwl2 = new(@"^_main\.(\d+)\.fwl2$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// The game's own save-stamp pattern (SaveSystem.GetTimestampFromPath). It is NOT
        /// anchored, and every one of the six date and time groups may be followed by any
        /// number of hyphens, so "20260909180000", "20260909-180000" and "2026-09-09-18-00-00"
        /// are all the same stamp and a stamp may sit anywhere in the name. Matching the
        /// game's own shape is what keeps a layer it wrote from being listed as a world.
        /// </summary>
        private static readonly Regex GameStamp = new(
            @".+(\d{4})-*(\d{2})-*(\d{2})-*(\d{2})-*(\d{2})-*(\d{2})",
            RegexOptions.Compiled);

        /// <summary>The marker the game looks for at the second-to-last underscore.</summary>
        private const string BackupMarker = "_backup_";

        /// <summary>The infix BakaLoader stamps onto a pre-update snapshot.</summary>
        public const string PreUpdateInfix = "preupdate-";

        /// <summary>The timestamp shape Valheim 1.0 uses for its own backup names.</summary>
        public const string BackupStampFormat = "yyyyMMdd-HHmmss";

        /// <summary>
        /// The suffix every staged copy wears while it is being made: ".copying-" and a
        /// stamp. Nothing under one of these names is a world, and nothing reads one
        /// again once the copy that made it has either landed or been cleaned up.
        /// </summary>
        private const string CopyStagingMarker = ".copying-";

        /// <summary>
        /// True when a name is a staged copy's rather than a world's. The stamp is matched
        /// as well as the marker, so a world a host really did call "Old.copying-notes" is
        /// still a world.
        /// </summary>
        private static bool IsCopyStagingName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            var at = name.LastIndexOf(CopyStagingMarker, StringComparison.OrdinalIgnoreCase);
            if (at < 0) return false;
            var stamp = name[(at + CopyStagingMarker.Length)..];
            return DateTime.TryParseExact(stamp, BackupStampFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out _);
        }

        // ---------------------------------------------------------------- enumeration

        /// <summary>
        /// Every world under a save folder, worlds_local before worlds, deduped by name
        /// (the first spelling found wins, and a chunked world always beats a legacy file
        /// of the same name). Never throws: an unreadable folder yields no worlds.
        /// </summary>
        public static IReadOnlyList<WorldInfo> Enumerate(string saveFolder)
        {
            var seen = new Dictionary<string, WorldInfo>(StringComparer.OrdinalIgnoreCase);
            var order = new List<string>();

            foreach (var sub in WorldSubfolders)
            {
                foreach (var world in EnumerateIn(saveFolder, sub))
                {
                    if (seen.TryGetValue(world.Name, out var existing))
                    {
                        // A chunked world supersedes a same-named legacy leftover.
                        if (existing.Format == WorldFormat.Chunked || world.Format == WorldFormat.Legacy) continue;
                        seen[world.Name] = world;
                        continue;
                    }
                    seen[world.Name] = world;
                    order.Add(world.Name);
                }
            }

            return order.Select(n => seen[n]).ToList();
        }

        /// <summary>Every world in ONE of the two world subfolders (no cross-subfolder dedupe).</summary>
        public static IReadOnlyList<WorldInfo> EnumerateIn(string saveFolder, string sub)
        {
            var results = new List<WorldInfo>();
            var dir = WorldsDir(saveFolder, sub);
            if (dir == null || !Directory.Exists(dir)) return results;

            var chunkedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                foreach (var child in Directory.EnumerateDirectories(dir))
                {
                    var name = System.IO.Path.GetFileName(child);
                    if (string.IsNullOrWhiteSpace(name) || IsBackupDirectoryName(name)) continue;
                    // A copy half way through, or one a kill caught between the last file
                    // landing and the rename. It holds a whole generation whose header still
                    // names the world it came from, so listing it would be the two worlds
                    // under one name that the copy exists to prevent.
                    if (IsCopyStagingName(name)) continue;

                    var world = ReadChunkedWorld(saveFolder, sub, child);
                    if (world == null) continue;
                    chunkedNames.Add(world.Name);
                    results.Add(world);
                }
            }
            catch { /* unreadable worlds dir: fall through to whatever we already have */ }

            try
            {
                foreach (var file in Directory.EnumerateFiles(dir))
                {
                    var fileName = System.IO.Path.GetFileName(file);
                    if (!string.Equals(System.IO.Path.GetExtension(fileName), ".fwl", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (IsBackupFileName(fileName, out _)) continue;

                    var name = System.IO.Path.GetFileNameWithoutExtension(fileName);
                    if (string.IsNullOrWhiteSpace(name) || chunkedNames.Contains(name)) continue;

                    results.Add(ReadLegacyWorld(saveFolder, sub, dir, name));
                }
            }
            catch { /* ignore */ }

            return results;
        }

        /// <summary>
        /// The world by that name under the save folder, or null when it does not exist.
        /// <para>
        /// Deliberately built on <see cref="Enumerate"/> rather than on its own folder walk, so
        /// there is exactly ONE answer to "which world is this name". When the two disagreed,
        /// the list the host was reading and the world a restore wrote to could be different
        /// worlds in different subfolders.
        /// </para>
        /// </summary>
        public static WorldInfo Find(string saveFolder, string worldName)
        {
            if (string.IsNullOrWhiteSpace(saveFolder) || string.IsNullOrWhiteSpace(worldName)) return null;

            // A world name is a plain folder/file name. Refusing anything else here means no
            // caller can probe outside the save folder by passing a crafted world name.
            if (!IsSafeReferenceToken(worldName)) return null;

            return Enumerate(saveFolder)
                .FirstOrDefault(w => Resolves(w, worldName));
        }

        /// <summary>
        /// The world by that name inside ONE worlds subfolder, or null. This is what a caller
        /// wants whenever it already knows which subfolder it is working in, such as a restore
        /// that read its backup layer out of a particular one: the live world it overwrites has
        /// to be the one that sits beside that layer, not a same-named world next door.
        /// </summary>
        public static WorldInfo FindIn(string saveFolder, string sub, string worldName)
        {
            if (string.IsNullOrWhiteSpace(saveFolder) || string.IsNullOrWhiteSpace(worldName)) return null;
            if (!IsSafeReferenceToken(worldName)) return null;

            return EnumerateIn(saveFolder, sub)
                .FirstOrDefault(w => Resolves(w, worldName));
        }

        /// <summary>
        /// True when an enumerated world really is the one that was asked for. The name has to
        /// match, and so does the last segment of the path it resolved to: Windows folds a
        /// trailing dot or space and resolves ".", so a token can otherwise name one thing and
        /// land on another.
        /// </summary>
        private static bool Resolves(WorldInfo world, string worldName)
        {
            if (world == null) return false;
            if (!string.Equals(world.Name, worldName, StringComparison.OrdinalIgnoreCase)) return false;

            // The last segment of what the name actually resolved to on disk: the world's own
            // directory for a chunked world, the ".fwl" stem for a legacy pair.
            var leaf = world.Format == WorldFormat.Chunked
                ? System.IO.Path.GetFileName(
                    System.IO.Path.TrimEndingDirectorySeparator(world.Folder ?? string.Empty))
                : System.IO.Path.GetFileNameWithoutExtension(world.MetaPath ?? string.Empty);

            return string.Equals(leaf, worldName, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>True when a world by that name exists on disk in EITHER format.</summary>
        public static bool Exists(string saveFolder, string worldName) => Find(saveFolder, worldName) != null;

        /// <summary>
        /// The path of anything already using this exact world name under a save folder, found by
        /// looking at the filesystem instead of at the world list. Null when the name is free.
        /// <para>
        /// <see cref="Find"/> answers "which world does the host see", and it deliberately leaves
        /// names out: a backup-shaped name is never returned, and neither is a name Windows would
        /// rewrite on its way to a path. "Is this name taken" has to stay true for every one of
        /// those, because writing a fresh .fwl beside a world the list cannot see is how a live
        /// world ends up with a second, conflicting seed sitting next to it.
        /// </para>
        /// </summary>
        public static string FindWorldFilesOnDisk(string saveFolder, string worldName)
        {
            if (string.IsNullOrWhiteSpace(saveFolder) || string.IsNullOrWhiteSpace(worldName)) return null;

            foreach (var sub in WorldSubfolders)
            {
                var hit = FindWorldFilesIn(WorldsDir(saveFolder, sub), worldName);
                if (hit != null) return hit;
            }
            return null;
        }

        /// <summary>
        /// The same raw probe inside ONE worlds folder: the world's own directory, its ".fwl", or
        /// even a lone ".db" left behind, whichever is there first. Null when the name is free.
        /// </summary>
        private static string FindWorldFilesIn(string worldsDir, string worldName)
        {
            if (string.IsNullOrWhiteSpace(worldsDir) || string.IsNullOrWhiteSpace(worldName)) return null;
            if (worldName.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0) return null;

            try
            {
                var folder = System.IO.Path.Combine(worldsDir, worldName);
                if (Directory.Exists(folder) && FindGeneration(folder).Exists) return folder;

                foreach (var ext in new[] { ".fwl", ".db" })
                {
                    var path = System.IO.Path.Combine(worldsDir, worldName + ext);
                    if (File.Exists(path)) return path;
                }
            }
            catch { /* an unreadable folder is not a claim on the name */ }

            return null;
        }

        /// <summary>Names of every world under the save folder, deduped, worlds_local first.</summary>
        public static List<string> GetWorldNames(string saveFolder)
            => Enumerate(saveFolder).Select(w => w.Name).ToList();

        // ---------------------------------------------------------------- backups

        /// <summary>
        /// Every backup layer of one world inside a single worlds subfolder: legacy .fwl pairs,
        /// the ".fwl.old" pair, and chunked backup directories. Newest first.
        /// <para>
        /// A layer that has lost either half of its pair is listed too, marked
        /// <see cref="WorldBackupInfo.IsDamaged"/>: the ".db" whose ".fwl" is gone, and the
        /// ".fwl" whose ".db" is gone. There is nothing whole to restore from either, but a
        /// half can be as large as the world itself and nothing else on the machine would ever
        /// show it.
        /// </para>
        /// </summary>
        public static IReadOnlyList<WorldBackupInfo> EnumerateBackups(string saveFolder, string sub, string worldName)
        {
            var results = new List<WorldBackupInfo>();
            var dir = WorldsDir(saveFolder, sub);
            if (dir == null || !Directory.Exists(dir) || string.IsNullOrWhiteSpace(worldName)) return results;

            try
            {
                foreach (var child in Directory.EnumerateDirectories(dir))
                {
                    var name = System.IO.Path.GetFileName(child);
                    if (!TryParseBackupName(name, out var owner, out var kind, out var stamp)) continue;
                    if (!string.Equals(owner, worldName, StringComparison.OrdinalIgnoreCase)) continue;

                    var gen = FindGeneration(child);
                    results.Add(new WorldBackupInfo
                    {
                        World = worldName,
                        Name = name,
                        Kind = kind,
                        IsDirectory = true,
                        Path = child,
                        MetaPath = gen.MetaPath,
                        DbPath = gen.DbPath,
                        SaveNumber = gen.Number,
                        IsCommitted = gen.IsCommitted,
                        SizeBytes = DirectorySize(child),
                        LastWriteUtc = SafeLastWriteUtc(gen.DbPath ?? gen.MetaPath ?? child),
                        Stamp = stamp,
                    });
                }
            }
            catch { /* ignore */ }

            try
            {
                foreach (var file in Directory.EnumerateFiles(dir))
                {
                    var fileName = System.IO.Path.GetFileName(file);
                    if (!IsBackupFileName(fileName, out var owner)) continue;
                    if (!string.Equals(owner, worldName, StringComparison.OrdinalIgnoreCase)) continue;

                    // A layer is keyed on its .fwl; the paired .db rides along.
                    var isOldPair = fileName.EndsWith(".fwl.old", StringComparison.OrdinalIgnoreCase);
                    var isMeta = isOldPair
                        || string.Equals(System.IO.Path.GetExtension(fileName), ".fwl", StringComparison.OrdinalIgnoreCase);
                    if (!isMeta)
                    {
                        // A ".db"/".db.old" whose ".fwl" partner is gone. Nothing keys a layer on
                        // it, so it used to be invisible: a whole world's worth of bytes sitting
                        // in the worlds folder that no screen could show and no delete could take.
                        var partner = LegacyLayerMeta(dir, worldName, fileName);
                        if (partner == null || File.Exists(partner)) continue;

                        var orphanStem = fileName.EndsWith(".db.old", StringComparison.OrdinalIgnoreCase)
                            ? fileName[..^".db.old".Length]
                            : System.IO.Path.GetFileNameWithoutExtension(fileName);

                        TryParseBackupName(orphanStem, out _, out var orphanKind, out var orphanStamp);
                        if (fileName.EndsWith(".db.old", StringComparison.OrdinalIgnoreCase))
                            orphanKind = WorldBackupKind.Old;

                        results.Add(new WorldBackupInfo
                        {
                            World = worldName,
                            Name = fileName,
                            Kind = orphanKind,
                            IsDirectory = false,
                            Path = file,
                            MetaPath = null,
                            DbPath = file,
                            SizeBytes = SafeFileSize(file),
                            IsCommitted = false,
                            IsDamaged = true,
                            LastWriteUtc = SafeLastWriteUtc(file),
                            Stamp = orphanStamp,
                        });
                        continue;
                    }

                    var dbPath = LegacyLayerDb(dir, worldName, fileName);
                    var hasDb = File.Exists(dbPath);
                    long size = SafeFileSize(file) + (hasDb ? SafeFileSize(dbPath) : 0);

                    TryParseBackupName(System.IO.Path.GetFileNameWithoutExtension(
                        isOldPair ? fileName[..^4] : fileName), out _, out var kind, out var stamp);
                    if (isOldPair) kind = WorldBackupKind.Old;

                    results.Add(new WorldBackupInfo
                    {
                        World = worldName,
                        Name = fileName,
                        Kind = kind,
                        IsDirectory = false,
                        Path = file,
                        MetaPath = file,
                        DbPath = hasDb ? dbPath : null,
                        SizeBytes = size,
                        IsCommitted = true,
                        // Half a pair, the other way round: the ".fwl" is here and the ".db"
                        // it describes is not. Copying it over a live world would leave that
                        // world's metadata and its database from two different saves, with a
                        // seed and a uid that no longer match the terrain on disk.
                        IsDamaged = !hasDb,
                        LastWriteUtc = SafeLastWriteUtc(hasDb ? dbPath : file),
                        Stamp = stamp,
                    });
                }
            }
            catch { /* ignore */ }

            return results.OrderByDescending(b => b.LastWriteUtc).ToList();
        }

        /// <summary>
        /// One backup set whose world is gone: every layer on disk that names a world no
        /// worlds folder holds any more.
        /// </summary>
        public sealed class OrphanBackupSet
        {
            /// <summary>The world the layers name, which no longer exists on disk.</summary>
            public string WorldName { get; init; }

            /// <summary>"worlds_local" or "worlds": the folder the layers sit in.</summary>
            public string Sub { get; init; }

            /// <summary>The layers themselves, newest first, exactly as a live world's are.</summary>
            public IReadOnlyList<WorldBackupInfo> Layers { get; init; }

            /// <summary>Total bytes the set takes on disk.</summary>
            public long SizeBytes { get; init; }
        }

        /// <summary>
        /// Every backup set under a save folder whose owning world is gone from BOTH worlds
        /// subfolders.
        /// <para>
        /// Layers are only ever asked for by the name of a world that is still there, so a set
        /// left behind by a world deleted outside BakaLoader, renamed, or removed with its
        /// layers kept had no screen at all: it could not be seen, sized or reclaimed, and it
        /// can be as large as the world it came from. This walks the layer names themselves and
        /// groups them by the world each one names, so a set with nobody left to claim it is
        /// still something a host can look at, restore from, or take back the disk of.
        /// </para>
        /// </summary>
        public static IReadOnlyList<OrphanBackupSet> EnumerateOrphanBackups(string saveFolder)
        {
            var results = new List<OrphanBackupSet>();
            if (string.IsNullOrWhiteSpace(saveFolder)) return results;

            // A world in EITHER subfolder claims its layers wherever they sit, so every live
            // name is gathered before any set is called owner less.
            var live = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var sub in WorldSubfolders)
            {
                foreach (var world in EnumerateIn(saveFolder, sub)) live.Add(world.Name);
            }

            foreach (var sub in WorldSubfolders)
            {
                var dir = WorldsDir(saveFolder, sub);
                if (dir == null || !Directory.Exists(dir)) continue;

                var owners = new List<string>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                void Claim(string owner)
                {
                    if (string.IsNullOrWhiteSpace(owner)) return;
                    if (live.Contains(owner)) return;
                    // The name is handed back to the UI and comes back as a reference, so it
                    // has to be a name a reference is allowed to carry.
                    if (!IsSafeReferenceToken(owner)) return;
                    if (!seen.Add(owner)) return;
                    owners.Add(owner);
                }

                try
                {
                    foreach (var child in Directory.EnumerateDirectories(dir))
                    {
                        if (TryParseBackupName(System.IO.Path.GetFileName(child), out var owner, out _, out _))
                            Claim(owner);
                    }
                }
                catch { /* an unreadable folder hides nothing that was visible before */ }

                try
                {
                    foreach (var file in Directory.EnumerateFiles(dir))
                    {
                        if (IsBackupFileName(System.IO.Path.GetFileName(file), out var owner)) Claim(owner);
                    }
                }
                catch { /* ignore */ }

                foreach (var owner in owners)
                {
                    var layers = EnumerateBackups(saveFolder, sub, owner);
                    if (layers.Count == 0) continue;

                    results.Add(new OrphanBackupSet
                    {
                        WorldName = owner,
                        Sub = sub,
                        Layers = layers,
                        SizeBytes = layers.Sum(l => l.SizeBytes),
                    });
                }
            }

            return results;
        }

        /// <summary>
        /// Names Win32 keeps reserved whatever the extension is, so a folder cannot really be
        /// called any of them and a path built from one goes to a device instead.
        /// </summary>
        private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        };

        /// <summary>
        /// True when a token from the UI is a plain file or directory name that is safe to
        /// combine with a save path: no separators, no "..", no character that is illegal in a
        /// file name (which also rules out a drive colon and every wildcard). This is the first
        /// gate on any world/backup reference that arrives from outside.
        /// <para>
        /// It also refuses every token Windows quietly rewrites on its way to a path: "." and
        /// ".." themselves, anything with leading or trailing whitespace, anything ending in a
        /// dot or a space (Win32 strips both), and the reserved device names. Those all name one
        /// thing and land on another, which for a delete is the difference between one world and
        /// the whole worlds folder.
        /// </para>
        /// </summary>
        public static bool IsSafeReferenceToken(string token)
        {
            if (string.IsNullOrWhiteSpace(token)) return false;
            if (token == "." || token == "..") return false;
            if (token.Contains("..")) return false;
            if (token.IndexOfAny(new[] { '/', '\\' }) >= 0) return false;
            if (token.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0) return false;

            // Padding is not part of a name on Windows; the path it builds is the trimmed one.
            if (token.Trim() != token) return false;

            var last = token[token.Length - 1];
            if (last == '.' || last == ' ') return false;

            // "NUL" and "NUL.anything" both reach the device, so the stem is what matters.
            var stem = token;
            var dot = stem.IndexOf('.');
            if (dot >= 0) stem = stem.Substring(0, dot);
            if (ReservedDeviceNames.Contains(stem)) return false;

            return true;
        }

        /// <summary>
        /// The longest name BakaLoader will take for a NEW world. Nothing on disk is
        /// measured against it, so a world that already carries a longer name keeps it and
        /// keeps working; this is the gate on a name a host is typing right now.
        /// <para>
        /// Sixty four is chosen so the game's own longest sibling of a world file still fits
        /// comfortably in a path: it writes "{world}_backup_20260918-123456.fwl" beside the
        /// world, which is another twenty eight characters on top of whatever the save folder
        /// costs.
        /// </para>
        /// </summary>
        public const int WorldNameMaxLength = 64;

        /// <summary>
        /// What is wrong with a name a host typed for a new world, or null when nothing is.
        /// The answer is a stable token rather than a sentence, because the sentence a host
        /// reads is the page's to word in the page's own language.
        /// <para>
        /// "required" for nothing at all, "tooLong" for past <see cref="WorldNameMaxLength"/>,
        /// and "badCharacters" for anything <see cref="IsSafeReferenceToken"/> turns down:
        /// a separator, a character Windows will not put in a file name, a trailing dot, or a
        /// reserved device name.
        /// </para>
        /// <para>
        /// The name is judged AS IT WILL BE USED, which is trimmed. A space either side of a
        /// typed name is not a mistake to tell a host about, it is whitespace the name never
        /// carries by the time it lands, and every caller here saves or copies under the
        /// trimmed name for exactly that reason. The window's own rule (app.js
        /// worldNameProblem) trims before it judges too, so the two answer the same question:
        /// a rule that turned "Midgard " down while the box beside it accepted it would be
        /// refusing a name that cannot reach disk in the first place. What is left of a name
        /// after trimming is still held to the whole rule, so padding around nothing at all is
        /// "required" and padding around a reserved device name is still "badCharacters".
        /// </para>
        /// </summary>
        public static string WorldNameProblem(string name)
        {
            var typed = (name ?? string.Empty).Trim();
            if (typed.Length == 0) return "required";
            if (typed.Length > WorldNameMaxLength) return "tooLong";
            if (!IsSafeReferenceToken(typed)) return "badCharacters";
            return null;
        }

        /// <summary>
        /// Resolves a backup reference from the UI to a real layer of that world, or null.
        /// A reference can ONLY ever name a layer the world actually owns: the name is matched
        /// against the layers found on disk, and the resolved path is re-checked to sit directly
        /// inside that worlds folder. There is no path built from caller-supplied text, so the
        /// live world (file pair or 1.0 directory) can never be reached through here.
        /// <para>
        /// A layer that is missing the half it would be restored FROM is refused by default. It
        /// is still listed, and a delete can still be pointed at it with <paramref name="allowDamaged"/>,
        /// but a restore that took a lone ".db" for a ".fwl" would write database bytes over the
        /// live world's metadata file.
        /// </para>
        /// </summary>
        public static WorldBackupInfo ResolveBackupLayer(
            string saveFolder, string sub, string worldName, string name, bool allowDamaged = false)
        {
            if (!IsSafeReferenceToken(worldName) || !IsSafeReferenceToken(name)) return null;
            if (sub != WorldSubfolders[0] && sub != WorldSubfolders[1]) return null;
            if (string.IsNullOrWhiteSpace(saveFolder)) return null;

            var dir = WorldsDir(saveFolder, sub);
            if (dir == null || !Directory.Exists(dir)) return null;

            var layer = EnumerateBackups(saveFolder, sub, worldName)
                .FirstOrDefault(b => string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase));
            if (layer == null) return null;
            if (layer.IsDamaged && !allowDamaged) return null;

            try
            {
                var parent = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(layer.Path));
                var expected = System.IO.Path.GetFullPath(dir);
                if (!string.Equals(
                        parent?.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar),
                        expected.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar),
                        StringComparison.OrdinalIgnoreCase))
                    return null;
            }
            catch { return null; }

            return layer;
        }

        /// <summary>
        /// True when a name is backup-shaped for THAT world, whatever its extension. Used to
        /// tell a bad reference apart from a layer that simply vanished off disk.
        /// </summary>
        public static bool IsBackupShapedFor(string name, string worldName)
        {
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(worldName)) return false;
            if (IsBackupFileName(name, out var fileOwner)
                && string.Equals(fileOwner, worldName, StringComparison.OrdinalIgnoreCase)) return true;
            return TryParseBackupName(name, out var dirOwner, out _, out _)
                   && string.Equals(dirOwner, worldName, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>The .db that pairs with a legacy backup layer's .fwl file name.</summary>
        public static string LegacyLayerDb(string worldsDir, string worldName, string fwlFileName)
        {
            if (fwlFileName.EndsWith(".fwl.old", StringComparison.OrdinalIgnoreCase))
                return System.IO.Path.Combine(worldsDir, worldName + ".db.old");
            return System.IO.Path.Combine(worldsDir, System.IO.Path.GetFileNameWithoutExtension(fwlFileName) + ".db");
        }

        /// <summary>The .fwl that pairs with a legacy backup layer's .db file name, or null.</summary>
        public static string LegacyLayerMeta(string worldsDir, string worldName, string dbFileName)
        {
            if (string.IsNullOrWhiteSpace(dbFileName)) return null;
            if (dbFileName.EndsWith(".db.old", StringComparison.OrdinalIgnoreCase))
                return System.IO.Path.Combine(worldsDir, worldName + ".fwl.old");
            if (!string.Equals(System.IO.Path.GetExtension(dbFileName), ".db", StringComparison.OrdinalIgnoreCase))
                return null;
            return System.IO.Path.Combine(worldsDir, System.IO.Path.GetFileNameWithoutExtension(dbFileName) + ".fwl");
        }

        /// <summary>
        /// True when a DIRECTORY sitting in worlds_local/worlds is a backup rather than a world.
        /// </summary>
        public static bool IsBackupDirectoryName(string directoryName)
            => TryParseBackupName(directoryName, out _, out _, out _);

        /// <summary>
        /// True when a FILE sitting in worlds_local/worlds is a backup rather than a live world
        /// file: "{world}_backup_*.fwl/.db", "{world}.fwl.old"/".db.old", or the game's rolling
        /// shape "{world}_{stamp}.fwl/.db", which the game files under the trimmed name and
        /// never opens as a world.
        /// </summary>
        public static bool IsBackupFileName(string fileName, out string worldName)
        {
            worldName = null;
            if (string.IsNullOrWhiteSpace(fileName)) return false;

            if (fileName.EndsWith(".fwl.old", StringComparison.OrdinalIgnoreCase))
            {
                worldName = fileName[..^".fwl.old".Length];
                return worldName.Length > 0;
            }
            if (fileName.EndsWith(".db.old", StringComparison.OrdinalIgnoreCase))
            {
                worldName = fileName[..^".db.old".Length];
                return worldName.Length > 0;
            }

            var ext = System.IO.Path.GetExtension(fileName);
            if (!string.Equals(ext, ".fwl", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(ext, ".db", StringComparison.OrdinalIgnoreCase)) return false;

            var stem = System.IO.Path.GetFileNameWithoutExtension(fileName);
            return TryParseBackupName(stem, out worldName, out _, out _);
        }

        /// <summary>
        /// The timestamp the game reads out of a save name, or null when there is none it can
        /// use. This is SaveSystem.GetTimestampFromPath: the pattern is unanchored, the hyphens
        /// between the six groups are all optional, and a stamp whose numbers are not a real
        /// date (month 13, say) counts as no stamp at all, exactly as the game counts it.
        /// </summary>
        public static DateTime? TryReadStamp(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;

            var m = GameStamp.Match(name);
            if (!m.Success) return null;

            try
            {
                return new DateTime(
                    int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
                    int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture),
                    int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture),
                    int.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture),
                    int.Parse(m.Groups[5].Value, CultureInfo.InvariantCulture),
                    int.Parse(m.Groups[6].Value, CultureInfo.InvariantCulture));
            }
            catch
            {
                // The game swallows the same failure and carries on with no timestamp, which
                // makes the name a plain save name rather than a layer.
                return null;
            }
        }

        /// <summary>
        /// Splits a backup-shaped name (no extension) into the world it belongs to, its kind,
        /// and its timestamp. False when the name is not backup-shaped at all.
        /// <para>
        /// This follows the game's own two-step decision (SaveSystem.GetSaveInfo into
        /// UpdateSaveNameAndReturnSaveFileType) rather than a shape of our own. The game reads
        /// a stamp out of the name; if it finds one and there is at least one underscore, the
        /// name is never a save of its own:
        /// <list type="bullet">
        /// <item>the literal "_backup_" marker at the second-to-last underscore names the
        /// world in front of it and the infix after it names the kind;</item>
        /// <item>no marker is what the game calls a ROLLING save. The game trims the name at
        /// its LAST underscore, files it under that world, and never elects it as a primary
        /// file, so "Midgard_20220620-101500" is a layer of "Midgard" and is not a world.
        /// Listing it as one would offer a save the game itself will not open.</item>
        /// </list>
        /// </para>
        /// </summary>
        public static bool TryParseBackupName(string name, out string worldName, out WorldBackupKind kind, out DateTime? stamp)
        {
            worldName = null;
            kind = WorldBackupKind.Other;
            stamp = null;
            if (string.IsNullOrWhiteSpace(name)) return false;

            var read = TryReadStamp(name);
            if (read == null) return false;

            // The game reads the marker off the second-to-last underscore, so a world whose own
            // name ends in "_backup" keeps its layers and a name like "My_backup_World_{stamp}"
            // is read as a rolling save of "My_backup_World" rather than a layer of "My".
            var marker = SecondToLastIndexOfUnderscore(name);
            var marked = marker >= 0
                         && name.Length - marker >= BackupMarker.Length
                         && string.Equals(name.Substring(marker, BackupMarker.Length), BackupMarker, StringComparison.Ordinal);

            var cut = marked ? marker : name.LastIndexOf('_');
            if (cut < 0) return false;

            worldName = name.Substring(0, cut);
            if (worldName.Length == 0)
            {
                // The game falls back to a plain save name when the trim would leave nothing.
                worldName = null;
                return false;
            }

            kind = marked ? KindFromMarker(name, marker) : WorldBackupKind.Other;
            stamp = read;
            return true;
        }

        /// <summary>
        /// True when a name is what the game calls a ROLLING save: a stamp it can read, at
        /// least one underscore, and no "_backup_" marker at the second-to-last one. The game
        /// files such a file under <paramref name="worldName"/>, the name trimmed at its last
        /// underscore, and never opens it as a world of its own.
        /// <para>
        /// It is still a whole save on disk, so the pre-update pass names it rather than
        /// letting it vanish out of both lists at once.
        /// </para>
        /// </summary>
        public static bool IsRollingSaveName(string name, out string worldName)
        {
            worldName = null;
            if (!TryParseBackupName(name, out var owner, out var kind, out _)) return false;
            if (kind != WorldBackupKind.Other) return false;
            worldName = owner;
            return true;
        }

        /// <summary>Index of the second-to-last '_' in a name, or -1 when there are fewer than two.</summary>
        private static int SecondToLastIndexOfUnderscore(string name)
        {
            var last = -1;
            var previous = -1;
            for (var i = 0; i < name.Length; i++)
            {
                if (name[i] != '_') continue;
                previous = last;
                last = i;
            }
            return previous;
        }

        /// <summary>
        /// Which layer a "_backup_" marker names, read the way the game reads it: the infix that
        /// follows the marker, or the untouched pre-conversion originals when there is none.
        /// "preupdate-" is BakaLoader's own addition; the game files it as a standard backup.
        /// </summary>
        private static WorldBackupKind KindFromMarker(string name, int marker)
        {
            if (StartsAt(name, marker, BackupMarker + "auto-")) return WorldBackupKind.Auto;
            if (StartsAt(name, marker, BackupMarker + "cloud-")) return WorldBackupKind.Cloud;
            if (StartsAt(name, marker, BackupMarker + "restore-")) return WorldBackupKind.Restore;
            if (StartsAt(name, marker, BackupMarker + PreUpdateInfix)) return WorldBackupKind.PreUpdate;
            return WorldBackupKind.Legacy;
        }

        private static bool StartsAt(string name, int index, string expected)
            => name.Length - index >= expected.Length
               && string.Equals(name.Substring(index, expected.Length), expected, StringComparison.Ordinal);

        /// <summary>The wire token for a backup kind (what the Barrow DTO carries).</summary>
        public static string KindToken(WorldBackupKind kind) => kind switch
        {
            WorldBackupKind.Auto => "auto",
            WorldBackupKind.Restore => "restore",
            WorldBackupKind.Cloud => "cloud",
            WorldBackupKind.Legacy => "legacy",
            WorldBackupKind.Old => "old",
            WorldBackupKind.PreUpdate => "preupdate",
            _ => "other",
        };

        // ---------------------------------------------------------------- chunked internals

        /// <summary>One "_main.{N}.*" generation inside a chunked world directory.</summary>
        public readonly struct Generation
        {
            public Generation(int? number, string metaPath, string dbPath, string chunksPath, bool isCommitted)
            {
                Number = number;
                MetaPath = metaPath;
                DbPath = dbPath;
                ChunksPath = chunksPath;
                IsCommitted = isCommitted;
            }

            public int? Number { get; }
            public string MetaPath { get; }
            public string DbPath { get; }
            public string ChunksPath { get; }
            public bool IsCommitted { get; }
            public bool Exists => MetaPath != null;
        }

        /// <summary>
        /// The commit marker that sits beside a given "_main.{N}.fwl2", in the same folder.
        /// Its presence is what says the game finished writing that generation.
        /// </summary>
        private static string NeighbourOk(string metaPath, string number) => System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(metaPath) ?? string.Empty, $"_main.{number}.ok");

        /// <summary>
        /// Picks the live generation of a chunked world directory: the HIGHEST N whose .fwl2,
        /// .db2, .chunks and .ok all exist. When nothing is committed yet (a world created but
        /// never saved) the highest .fwl2 is returned with IsCommitted false and no .db2, so
        /// callers never copy or read a half-written generation.
        /// </summary>
        public static Generation FindGeneration(string worldDirectory)
        {
            if (string.IsNullOrWhiteSpace(worldDirectory) || !Directory.Exists(worldDirectory))
                return new Generation(null, null, null, null, false);

            var numbers = new List<int>();
            try
            {
                foreach (var file in Directory.EnumerateFiles(worldDirectory))
                {
                    var m = MainFwl2.Match(System.IO.Path.GetFileName(file));
                    if (m.Success && int.TryParse(m.Groups[1].Value, out var n)) numbers.Add(n);
                }
            }
            catch { return new Generation(null, null, null, null, false); }

            if (numbers.Count == 0) return new Generation(null, null, null, null, false);
            numbers.Sort();

            for (var i = numbers.Count - 1; i >= 0; i--)
            {
                var n = numbers[i];
                var meta = System.IO.Path.Combine(worldDirectory, $"_main.{n}.fwl2");
                var db = System.IO.Path.Combine(worldDirectory, $"_main.{n}.db2");
                var chunks = System.IO.Path.Combine(worldDirectory, $"_main.{n}.chunks");
                var ok = System.IO.Path.Combine(worldDirectory, $"_main.{n}.ok");
                if (File.Exists(db) && File.Exists(chunks) && File.Exists(ok))
                    return new Generation(n, meta, db, chunks, true);
            }

            var highest = numbers[^1];
            return new Generation(highest,
                System.IO.Path.Combine(worldDirectory, $"_main.{highest}.fwl2"),
                null, null, false);
        }

        private static WorldInfo ReadChunkedWorld(string saveFolder, string sub, string worldDirectory)
        {
            var gen = FindGeneration(worldDirectory);
            if (!gen.Exists) return null;

            var name = System.IO.Path.GetFileName(worldDirectory);
            return new WorldInfo
            {
                Name = name,
                Format = WorldFormat.Chunked,
                Folder = worldDirectory,
                SaveFolder = saveFolder,
                Sub = sub,
                MetaPath = gen.MetaPath,
                DbPath = gen.DbPath,
                SaveNumber = gen.Number,
                IsCommitted = gen.IsCommitted,
                SizeBytes = DirectorySize(worldDirectory),
                LastWriteUtc = SafeLastWriteUtc(gen.DbPath ?? gen.MetaPath),
            };
        }

        private static WorldInfo ReadLegacyWorld(string saveFolder, string sub, string worldsDir, string name)
        {
            var fwl = System.IO.Path.Combine(worldsDir, name + ".fwl");
            var db = System.IO.Path.Combine(worldsDir, name + ".db");
            var hasDb = File.Exists(db);

            return new WorldInfo
            {
                Name = name,
                Format = WorldFormat.Legacy,
                Folder = worldsDir,
                SaveFolder = saveFolder,
                Sub = sub,
                MetaPath = fwl,
                DbPath = hasDb ? db : null,
                SaveNumber = null,
                IsCommitted = true,
                SizeBytes = SafeFileSize(fwl) + (hasDb ? SafeFileSize(db) : 0),
                LastWriteUtc = SafeLastWriteUtc(hasDb ? db : fwl),
            };
        }

        // ---------------------------------------------------------------- copy / delete

        /// <summary>
        /// Copies a world into another save folder's worlds_local, in whatever format it is
        /// already in, leaving the source completely untouched. The biome data cache travels
        /// with it when one exists (the game regenerates it otherwise, which costs a long
        /// first boot). Returns the copied world's new folder.
        /// <para>
        /// A world of the same name already at the destination stops the copy. Copying onto it
        /// would leave one folder holding files from two unrelated save histories, and whichever
        /// generation happened to end up with a whole set of files would then load as the world.
        /// </para>
        /// </summary>
        /// <param name="overwrite">
        /// True to replace the world already at the destination. The replacement is staged in a
        /// sibling folder (a chunked world) or under sibling temp names (a legacy pair) and
        /// swapped in only once every file it needs has landed, so a copy that fails part way
        /// through leaves the world that was there untouched rather than half gone. Nothing at
        /// the destination is removed before the whole replacement is on disk beside it.
        /// </param>
        public static string CopyWorld(WorldInfo world, string destSaveFolder, bool overwrite = false)
        {
            if (world == null) throw new ArgumentNullException(nameof(world));
            if (string.IsNullOrWhiteSpace(destSaveFolder))
                throw new ArgumentException("A destination save folder is required.", nameof(destSaveFolder));

            // A dedicated server reads its worlds from worlds_local, so land the copy there.
            var destWorldsDir = System.IO.Path.Combine(destSaveFolder, WorldSubfolders[0]);
            Directory.CreateDirectory(destWorldsDir);

            // Copying a world onto itself is the one call that can only destroy: with overwrite
            // the source is what gets cleared to make room for the source.
            if (SameDirectory(SourceWorldsDir(world), destWorldsDir))
            {
                throw new InvalidOperationException(
                    $"'{world.Name}' is already in that save folder. A world cannot be copied over itself, "
                    + "so nothing was copied.");
            }

            var taken = FindWorldFilesIn(destWorldsDir, world.Name);
            if (taken != null && !overwrite)
            {
                throw new InvalidOperationException(
                    $"A world called '{world.Name}' is already in that save folder. Copying onto it would "
                    + "mix the two saves together, so nothing was copied.");
            }

            // A directory of that name is the name taken as well, even when it holds no finished
            // save: a copy landing beside it leaves two spellings of one world and whichever the
            // game read first would be the one it opened. Only a caller that asked to replace
            // what is there may clear it.
            var occupied = System.IO.Path.Combine(destWorldsDir, world.Name);
            if (!overwrite && DirectoryHasAnything(occupied))
            {
                throw new InvalidOperationException(
                    $"A folder called '{world.Name}' is already in that save folder. Copying onto it would "
                    + "mix the two saves together, so nothing was copied.");
            }

            string destFolder;
            if (world.Format == WorldFormat.Chunked)
            {
                destFolder = System.IO.Path.Combine(destWorldsDir, world.Name);

                if (Directory.Exists(destFolder) && FindGeneration(destFolder).Exists)
                {
                    // A real world is there. Swap the whole directory, never merge into it.
                    ReplaceDirectoryContents(world.Folder, destFolder, recoveryHint: DestinationUnchanged);
                }
                else
                {
                    TryDeleteDirectory(destFolder);
                    CopyDirectory(world.Folder, destFolder);
                }

                // A legacy pair of the same name would be shadowed by the directory and would
                // come back as a world of its own the moment the directory went away.
                if (overwrite) DeleteLegacyPair(destWorldsDir, world.Name, includeOldSiblings: true);
            }
            else
            {
                destFolder = destWorldsDir;
                CopyLegacyPairIn(world, destWorldsDir, overwrite);
            }

            CopyBiomeCache(world.SaveFolder, destSaveFolder, BiomeCacheKey(world));
            return destFolder;
        }

        /// <summary>
        /// Copies one world beside itself under a NEW name, in either save format, and
        /// rewrites the name stored inside the copy's own header so the copy is a real
        /// rename rather than the same world wearing two names at once.
        /// <para>
        /// The source is never touched, and neither are its backup layers: they belong to
        /// the world that is still there. The copy lands in the same worlds folder the
        /// source sits in, so it shows up in the world list beside it.
        /// </para>
        /// <para>
        /// Nothing takes the new name until the whole copy is finished. Every file lands
        /// under a temporary name first, the header is rewritten on THAT, and only then is
        /// the finished copy moved into place. A header that cannot be read stops the copy
        /// before the move, and a move that cannot land takes back whatever it had already
        /// put down, so a refusal leaves nothing behind: a world whose folder name and stored
        /// name disagreed would be LISTED under the folder it sits in and key its biome cache
        /// on the name inside its header, and half a world is not a world at all.
        /// </para>
        /// </summary>
        /// <returns>The folder the copy now lives in.</returns>
        public static string CopyWorldAs(WorldInfo world, string targetName) =>
            CopyWorldAs(world, targetName, null);

        /// <summary>
        /// The same copy, landing in ANOTHER save folder. This is what a duplicated server
        /// needs: the new realm has its own save folder, and the world that goes into it is
        /// the source realm's world under whatever name the new realm calls its own.
        /// <para>
        /// Everything about it is the copy above: the whole tree or the pair of files, the
        /// header rewritten so the copy names itself, the staging thrown away on any refusal,
        /// and the source untouched. The two differences are where it lands and what counts
        /// as the name being taken, which is the DESTINATION folder rather than the source's.
        /// Keeping the same name is allowed here, because a world of that name in a save
        /// folder of its own is not the same world twice.
        /// </para>
        /// </summary>
        /// <param name="destSaveFolder">
        /// The save folder to copy into, or null to copy beside the source the way
        /// <see cref="CopyWorldAs(WorldInfo, string)"/> does.
        /// </param>
        public static string CopyWorldAs(WorldInfo world, string targetName, string destSaveFolder)
        {
            if (world == null) throw new ArgumentNullException(nameof(world));

            // Trimmed here rather than trusted to arrive trimmed, because the rule below
            // judges the trimmed name and the name that lands has to be the one that was
            // judged. The RPC trims before it calls, so this changes nothing on the way a
            // host actually reaches it; it is the guarantee for anything else that ever
            // calls this.
            targetName = (targetName ?? string.Empty).Trim();

            if (WorldNameProblem(targetName) != null)
                throw new HostFacingException("worlds.copyBadTargetRef",
                    "That is not a name a world can be saved under.",
                    ("target", targetName));

            // Where the source really is, which is what the copy reads from either way.
            var sourceWorldsDir = SourceWorldsDir(world);
            if (string.IsNullOrWhiteSpace(sourceWorldsDir) || !Directory.Exists(sourceWorldsDir))
                throw new HostFacingException("worlds.copyFailed",
                    $"'{world.Name}' is not in a worlds folder any more, so nothing was copied.",
                    ("world", world.Name));

            var intoAnotherFolder = !string.IsNullOrWhiteSpace(destSaveFolder);
            string worldsDir;
            string checkedSaveFolder;

            if (intoAnotherFolder)
            {
                // A dedicated server reads its worlds from worlds_local, which is where a copy
                // into another save folder lands, exactly as CopyWorld puts one there.
                EnsureSaveFolderLayout(destSaveFolder);
                worldsDir = System.IO.Path.Combine(destSaveFolder, WorldSubfolders[0]);
                Directory.CreateDirectory(worldsDir);
                checkedSaveFolder = destSaveFolder;

                // Copying a world onto itself is the one call that can only destroy.
                if (SameDirectory(sourceWorldsDir, worldsDir) &&
                    string.Equals(world.Name, targetName, StringComparison.OrdinalIgnoreCase))
                {
                    throw new HostFacingException("worlds.copyTargetExists",
                        $"'{world.Name}' is already in that save folder under that name, so nothing was copied.",
                        ("target", targetName));
                }
            }
            else
            {
                worldsDir = sourceWorldsDir;
                checkedSaveFolder = world.SaveFolder;

                // Beside itself, the source's own name is the one name that cannot be used.
                if (string.Equals(world.Name, targetName, StringComparison.OrdinalIgnoreCase))
                {
                    throw new HostFacingException("worlds.copyTargetExists",
                        $"A world called '{targetName}' is already in that save folder, so nothing was copied.",
                        ("target", targetName));
                }
            }

            // Every way the name can already be spoken for, including the ones the world
            // list refuses to return: a backup shaped folder holds its name just as hard as
            // a world does, and a copy landing on one would mix two saves together.
            if (FindWorldFilesOnDisk(checkedSaveFolder, targetName) != null
                || DirectoryHasAnything(System.IO.Path.Combine(worldsDir, targetName)))
            {
                throw new HostFacingException("worlds.copyTargetExists",
                    $"A world called '{targetName}' is already in that save folder, so nothing was copied.",
                    ("target", targetName));
            }

            // Asked before a byte moves. A header that will not parse is a header that
            // cannot be rewritten, and a copy that kept the source's name inside it is the
            // thing this whole method exists to avoid.
            //
            // This is the early no, not the last word. The rewrite holds a header to a
            // stricter bound than this reader does (see FwlNameRewriter), so a header can
            // pass here and be refused there, and that refusal is answered with the same
            // sentence from inside the staging, with the staging thrown away.
            if (FwlReader.TryRead(world.MetaPath) == null)
                throw new HostFacingException("worlds.copyUnreadable",
                    $"The world file for '{world.Name}' could not be read, so nothing was copied.",
                    ("world", world.Name));

            var cacheKey = BiomeCacheKey(world);

            var landed = world.Format == WorldFormat.Chunked
                ? CopyChunkedWorldAs(world, worldsDir, targetName)
                : CopyLegacyWorldAs(world, worldsDir, targetName);

            // The game keys the cache on the name inside the header, which is the new one
            // now, so the copy gets its own cache rather than reading the source's. Into
            // another save folder it is the destination's cache folder that gets it, and the
            // source's is left exactly as it was.
            if (intoAnotherFolder) CopyBiomeCacheInto(world.SaveFolder, cacheKey, destSaveFolder, targetName);
            else CopyBiomeCacheAs(world.SaveFolder, cacheKey, targetName);
            return landed;
        }

        /// <summary>
        /// A world's biome data cache copied into ANOTHER save folder under another name,
        /// which is the shape a duplicated realm needs. Best effort like its two siblings:
        /// the game rebuilds the cache, and a copy must never fail over one.
        /// </summary>
        public static void CopyBiomeCacheInto(
            string sourceSaveFolder, string sourceWorldName, string destSaveFolder, string targetWorldName)
        {
            try
            {
                var source = BiomeCachePath(sourceSaveFolder, sourceWorldName);
                var destination = BiomeCachePath(destSaveFolder, targetWorldName);
                if (source == null || destination == null || !File.Exists(source)) return;
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(destination));
                File.Copy(source, destination, overwrite: true);
            }
            catch { /* the game regenerates the cache; never fail a copy over it */ }
        }

        /// <summary>
        /// A 1.0 world directory copied under a new name: the whole tree lands in a sibling
        /// folder, every "_main.{N}.fwl2" in it is rewritten, and the finished tree is
        /// renamed into place in one move.
        /// </summary>
        private static string CopyChunkedWorldAs(WorldInfo world, string worldsDir, string targetName)
        {
            var destination = System.IO.Path.Combine(worldsDir, targetName);
            var staged = destination + CopyStagingMarker
                         + DateTime.Now.ToString(BackupStampFormat, CultureInfo.InvariantCulture);

            TryDeleteDirectory(staged);

            try
            {
                CopyDirectory(world.Folder, staged);

                // The whole tree, not only the top of it. A "_main.{N}.fwl2" carried
                // across in a subfolder and left unrewritten would still be naming the
                // world it was copied from, and the count below would not notice: the
                // top level files alone are enough to satisfy it.
                var rewritten = 0;
                foreach (var meta in Directory.EnumerateFiles(staged, "*", SearchOption.AllDirectories))
                {
                    var match = MainFwl2.Match(System.IO.Path.GetFileName(meta));
                    if (!match.Success) continue;

                    if (FwlNameRewriter.TryRewriteWorldName(meta, targetName))
                    {
                        rewritten++;
                        continue;
                    }

                    // It would not rewrite. Whether that is a reason to refuse the whole copy
                    // depends on whether the game would ever read this generation, and the .ok
                    // marker beside it is the answer: the game writes it LAST and deletes the
                    // generation before it once it is down, so a generation with no .ok is one
                    // the game was in the middle of writing and will discard itself. The world
                    // this copy is of read fine at the precheck; refusing the copy over a torn
                    // extra generation loses the host their committed world for a file nothing
                    // will ever open. It is carried across as it is and left uncounted, so a
                    // tree with nothing BUT torn generations still ends at the guard below.
                    if (!File.Exists(NeighbourOk(meta, match.Groups[1].Value))) continue;

                    throw new HostFacingException("worlds.copyUnreadable",
                        $"The world file for '{world.Name}' could not be read, so nothing was copied.",
                        ("world", world.Name));
                }

                if (rewritten == 0)
                    throw new HostFacingException("worlds.copyUnreadable",
                        $"The world file for '{world.Name}' could not be read, so nothing was copied.",
                        ("world", world.Name));
            }
            catch (Exception e)
            {
                TryDeleteDirectory(staged);
                if (e is HostFacingException) throw;
                throw new HostFacingException("worlds.copyFailed",
                    $"'{world.Name}' could not be copied, so nothing in the worlds folder was changed. "
                    + e.Message, ("world", world.Name));
            }

            try
            {
                Directory.Move(staged, destination);
            }
            catch (Exception e)
            {
                TryDeleteDirectory(staged);
                throw new HostFacingException("worlds.copyFailed",
                    $"'{world.Name}' could not be copied, so nothing in the worlds folder was changed. "
                    + e.Message, ("world", world.Name));
            }

            return destination;
        }

        /// <summary>
        /// A pre-1.0 pair copied under a new name. Only the live pair travels: the ".old"
        /// siblings are the previous save of the world that is staying, and a fresh copy has
        /// no previous save of its own yet.
        /// </summary>
        private static string CopyLegacyWorldAs(WorldInfo world, string worldsDir, string targetName)
        {
            var suffix = CopyStagingMarker + DateTime.Now.ToString(BackupStampFormat, CultureInfo.InvariantCulture);
            var staged = new List<(string Temp, string Final)>();

            try
            {
                foreach (var ext in new[] { ".fwl", ".db" })
                {
                    var source = System.IO.Path.Combine(world.Folder, world.Name + ext);
                    if (!File.Exists(source)) continue;

                    var final = System.IO.Path.Combine(worldsDir, targetName + ext);
                    var temp = final + suffix;
                    staged.Add((temp, final));

                    File.Copy(source, temp, overwrite: true);
                    if (!File.Exists(temp) || SafeFileSize(temp) != SafeFileSize(source))
                        throw new IOException($"'{targetName + ext}' did not land whole at the destination.");
                }

                var meta = staged.FirstOrDefault(
                    s => s.Final.EndsWith(".fwl", StringComparison.OrdinalIgnoreCase));
                if (meta.Temp == null || !FwlNameRewriter.TryRewriteWorldName(meta.Temp, targetName))
                    throw new HostFacingException("worlds.copyUnreadable",
                        $"The world file for '{world.Name}' could not be read, so nothing was copied.",
                        ("world", world.Name));
            }
            catch (Exception e)
            {
                foreach (var (temp, _) in staged) TryDeleteFile(temp);
                if (e is HostFacingException) throw;
                throw new HostFacingException("worlds.copyFailed",
                    $"'{world.Name}' could not be copied, so nothing in the worlds folder was changed. "
                    + e.Message, ("world", world.Name));
            }

            // Every file is on disk beside the name it is about to take, and the copy's own
            // header already names the copy. Only now does anything take the new name.
            //
            // Without overwrite, which is what the 1.0 path's Directory.Move has always done:
            // the name was free when it was asked for, and if something took it in the gap
            // between that question and this move, the copy fails closed rather than writing
            // over a world that arrived while the copy was being made.
            //
            // And a move that throws part way through used to leave whatever had already
            // landed standing. A ".fwl" with no ".db" beside it is half a world wearing a
            // name of its own, and the world list reads it as a world: a host would be left
            // looking at a save that cannot be opened and was never asked for. What landed is
            // taken back out before the refusal is spoken, and the refusal is one the page
            // has a sentence for rather than whatever the filesystem threw.
            var landed = new List<string>();
            try
            {
                foreach (var (temp, final) in staged)
                {
                    File.Move(temp, final);
                    landed.Add(final);
                }
            }
            catch (Exception e)
            {
                foreach (var file in landed) TryDeleteFile(file);
                throw new HostFacingException("worlds.copyFailed",
                    $"'{world.Name}' could not be copied, so nothing in the worlds folder was changed. "
                    + e.Message, ("world", world.Name));
            }
            finally
            {
                foreach (var (temp, _) in staged) TryDeleteFile(temp);
            }

            return worldsDir;
        }

        /// <summary>
        /// Copies a legacy world's files into a worlds folder the staged way: every file lands
        /// beside its final name first and is checked, what was there is only taken once the
        /// whole set is on disk, and the temp names are swapped in last. Deleting first and
        /// copying second is what leaves a destination world gone and half a replacement in
        /// its place when the second file cannot be written.
        /// </summary>
        private static void CopyLegacyPairIn(WorldInfo world, string destWorldsDir, bool overwrite)
        {
            var suffix = CopyStagingMarker + DateTime.Now.ToString(BackupStampFormat, CultureInfo.InvariantCulture);
            var staged = new List<(string Temp, string Final)>();

            try
            {
                foreach (var ext in new[] { ".fwl", ".db", ".fwl.old", ".db.old" })
                {
                    var src = System.IO.Path.Combine(world.Folder, world.Name + ext);
                    if (!File.Exists(src)) continue;

                    var final = System.IO.Path.Combine(destWorldsDir, world.Name + ext);
                    var temp = final + suffix;
                    staged.Add((temp, final));

                    File.Copy(src, temp, overwrite: true);
                    if (!File.Exists(temp) || SafeFileSize(temp) != SafeFileSize(src))
                    {
                        throw new IOException(
                            $"'{world.Name + ext}' did not land whole at the destination.");
                    }
                }
            }
            catch (Exception e)
            {
                foreach (var (temp, _) in staged) TryDeleteFile(temp);
                throw new InvalidOperationException(
                    $"'{world.Name}' could not be copied, so nothing in the destination folder was changed. "
                    + e.Message, e);
            }

            // Every file is on disk beside the name it is about to take. Only now is it safe to
            // take what was there.
            if (overwrite)
            {
                TryDeleteDirectory(System.IO.Path.Combine(destWorldsDir, world.Name));
                DeleteLegacyPair(destWorldsDir, world.Name, includeOldSiblings: true);
            }

            try
            {
                foreach (var (temp, final) in staged) File.Move(temp, final, overwrite: true);
            }
            finally
            {
                // A rename inside one folder rarely fails, but if one does the rest of the
                // staged files must not be left sitting in the worlds folder under a name
                // nothing will ever read again.
                foreach (var (temp, _) in staged) TryDeleteFile(temp);
            }
        }

        /// <summary>The worlds folder a world's files sit directly in, whichever format it is.</summary>
        private static string SourceWorldsDir(WorldInfo world)
        {
            if (world == null || string.IsNullOrWhiteSpace(world.Folder)) return null;
            if (world.Format != WorldFormat.Chunked) return world.Folder;
            return System.IO.Path.GetDirectoryName(System.IO.Path.TrimEndingDirectorySeparator(world.Folder));
        }

        /// <summary>True when two paths name the same directory. False on any doubt.</summary>
        private static bool SameDirectory(string left, string right)
        {
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
            try
            {
                return string.Equals(
                    System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(left)),
                    System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(right)),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        /// <summary>True when a directory is there and holds at least one file or folder.</summary>
        private static bool DirectoryHasAnything(string dir)
        {
            try
            {
                return !string.IsNullOrWhiteSpace(dir)
                       && Directory.Exists(dir)
                       && Directory.EnumerateFileSystemEntries(dir).Any();
            }
            catch { return false; }
        }

        /// <summary>Removes a file if it is there. Never throws.</summary>
        private static void TryDeleteFile(string path)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) File.Delete(path);
            }
            catch { /* best effort */ }
        }

        /// <summary>What a failed staged swap tells the host when there is no snapshot to name.</summary>
        private const string DestinationUnchanged = "Nothing in the destination folder was changed.";

        /// <summary>
        /// Removes a legacy "{world}.fwl/.db" pair from a worlds folder and returns what went.
        /// Nothing is removed when the pair is not there.
        /// </summary>
        /// <param name="includeOldSiblings">
        /// True to take the "{world}.fwl.old"/".db.old" pair as well. Those are a backup LAYER, so
        /// only a caller that is replacing or removing the whole world says yes.
        /// </param>
        private static List<string> DeleteLegacyPair(string worldsDir, string worldName, bool includeOldSiblings)
        {
            var removed = new List<string>();
            if (string.IsNullOrWhiteSpace(worldsDir) || string.IsNullOrWhiteSpace(worldName)) return removed;
            if (worldName.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0) return removed;

            var extensions = includeOldSiblings
                ? new[] { ".fwl", ".db", ".fwl.old", ".db.old" }
                : new[] { ".fwl", ".db" };

            foreach (var ext in extensions)
            {
                var path = System.IO.Path.Combine(worldsDir, worldName + ext);
                try
                {
                    if (!File.Exists(path)) continue;
                    File.Delete(path);
                    removed.Add(System.IO.Path.GetFileName(path));
                }
                catch { /* a locked leftover must not abort what is already done */ }
            }
            return removed;
        }

        /// <summary>
        /// The name of a world's pre-update snapshot layer: "{world}_backup_preupdate-{stamp}".
        /// Legacy layers add ".fwl"/".db"; a chunked layer is a directory by exactly this name.
        /// </summary>
        public static string PreUpdateLayerName(string worldName, DateTime whenLocal)
            => worldName + "_backup_" + PreUpdateInfix + whenLocal.ToString(BackupStampFormat, CultureInfo.InvariantCulture);

        /// <summary>
        /// Copies ONE world aside as a pre-update snapshot and returns the bytes written.
        /// A legacy world becomes "{world}_backup_preupdate-{stamp}.fwl/.db"; a 1.0 world
        /// becomes a sibling directory of the same name holding the committed generation.
        ///
        /// A chunked world whose newest generation was never committed (no .ok marker) is
        /// REFUSED rather than half-copied: there is no finished save in it to preserve.
        /// Throws on any copy failure so the caller can abort the launch it was protecting.
        /// </summary>
        public static long SnapshotWorldPreUpdate(WorldInfo world, DateTime whenLocal)
        {
            if (world == null) throw new ArgumentNullException(nameof(world));

            var layerName = PreUpdateLayerName(world.Name, whenLocal);

            if (world.Format == WorldFormat.Chunked)
            {
                if (!world.IsCommitted || world.SaveNumber == null)
                {
                    throw new InvalidOperationException(
                        $"'{world.Name}' has no finished save on disk yet, so there is nothing to copy aside.");
                }

                // The worlds folder is the world directory's parent for a chunked world.
                var parent = System.IO.Path.GetDirectoryName(world.Folder)
                    ?? throw new InvalidOperationException($"Could not locate the worlds folder for '{world.Name}'.");

                var destDir = System.IO.Path.Combine(parent, layerName);

                // The ".ok" marker is written last and deleted when the next generation commits,
                // so it is the one file that says "this save is still the finished one". Read it
                // before and after: if it moved, a save landed while we were copying and the
                // layer is a mixture of two generations rather than a world.
                var okPath = System.IO.Path.Combine(
                    world.Folder,
                    "_main." + world.SaveNumber.Value.ToString(CultureInfo.InvariantCulture) + ".ok");
                var okBefore = (Size: SafeFileSize(okPath), Written: SafeLastWriteUtc(okPath));

                CopyCommittedGeneration(world.Folder, destDir, world.SaveNumber);

                var okAfter = (Size: SafeFileSize(okPath), Written: SafeLastWriteUtc(okPath));
                if (!File.Exists(okPath) || okBefore != okAfter)
                {
                    TryDeleteDirectory(destDir);
                    throw new IOException(
                        $"'{world.Name}' was saved while it was being copied aside, so the copy is not a whole world.");
                }

                return DirectorySize(destDir);
            }

            long bytes = 0;
            var fwl = System.IO.Path.Combine(world.Folder, world.Name + ".fwl");
            var db = System.IO.Path.Combine(world.Folder, world.Name + ".db");

            if (!File.Exists(fwl))
            {
                throw new InvalidOperationException($"'{world.Name}.fwl' is missing, so the world cannot be copied aside.");
            }

            var destFwl = System.IO.Path.Combine(world.Folder, layerName + ".fwl");
            File.Copy(fwl, destFwl, overwrite: true);
            bytes += SafeFileSize(destFwl);

            if (File.Exists(db))
            {
                var destDb = System.IO.Path.Combine(world.Folder, layerName + ".db");
                File.Copy(db, destDb, overwrite: true);
                bytes += SafeFileSize(destDb);
            }

            return bytes;
        }

        /// <summary>
        /// Copies a chunked world directory, keeping only the committed generation's
        /// "_main.{N}.*" files and only the chunk files that generation's ".chunks" index
        /// actually names. A half-written newer generation is left behind, because restoring it
        /// would hand the game a save with no .ok marker, and so is any chunk file an in-flight
        /// save has already written for the generation after this one.
        /// </summary>
        private static void CopyCommittedGeneration(string sourceDir, string destinationDir, int? committedNumber)
        {
            // The index is the committed save's own list of what belongs to it. Without it there
            // is no way to tell this generation's chunk files from the next one's, and a copy
            // that mixes the two is not a world, so this is a failure rather than a fallback.
            HashSet<string> wanted = null;
            if (committedNumber != null)
            {
                wanted = Atlas.ChunkedWorldReader.TryReadChunkFileNames(sourceDir, committedNumber.Value);
                if (wanted == null)
                {
                    throw new IOException(
                        "The finished save's chunk index could not be read, so there is no way to tell "
                        + "which chunk files belong to it.");
                }
            }

            Directory.CreateDirectory(destinationDir);

            foreach (var file in Directory.GetFiles(sourceDir))
            {
                var name = System.IO.Path.GetFileName(file);
                if (committedNumber != null && IsOtherGeneration(name, committedNumber.Value)) continue;

                if (wanted != null
                    && name.EndsWith(".chunk", StringComparison.OrdinalIgnoreCase)
                    && !wanted.Contains(name))
                {
                    continue;
                }

                File.Copy(file, System.IO.Path.Combine(destinationDir, name), overwrite: true);
            }

            foreach (var dir in Directory.GetDirectories(sourceDir))
                CopyDirectory(dir, System.IO.Path.Combine(destinationDir, System.IO.Path.GetFileName(dir)));
        }

        // "_main.<N>.<anything>" belonging to a generation other than the committed one.
        private static readonly Regex MainGeneration =
            new(@"^_main\.(\d+)\.", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static bool IsOtherGeneration(string fileName, int committedNumber)
        {
            var m = MainGeneration.Match(fileName);
            return m.Success
                && int.TryParse(m.Groups[1].Value, out var number)
                && number != committedNumber;
        }

        /// <summary>What one pre-update snapshot pass did.</summary>
        public sealed class WorldSnapshotResult
        {
            /// <summary>Layer names actually written, in world order.</summary>
            public List<string> Copied { get; } = new();

            /// <summary>Worlds that were deliberately not copied, with the reason.</summary>
            public List<string> Skipped { get; } = new();

            /// <summary>Total bytes written across every copied layer.</summary>
            public long Bytes { get; set; }

            /// <summary>Null when the pass finished; the first failure otherwise.</summary>
            public string Error { get; set; }

            public bool Ok => Error == null;
        }

        /// <summary>
        /// Copies every world under a save folder aside as a pre-update snapshot. Stops at the
        /// first hard failure and reports it in <see cref="WorldSnapshotResult.Error"/>, so a
        /// caller can refuse to start a server whose worlds are not safely copied.
        /// <para>
        /// A pass that protected none of the worlds it COULD have protected is a failure too.
        /// The three cases are not the same thing and the caller has to be able to tell them
        /// apart: an empty save folder is a clean pass with nothing to do; a world with no
        /// finished save on disk yet (created and never saved, or force killed inside its first
        /// save) is named in <see cref="WorldSnapshotResult.Skipped"/> and is not counted, since
        /// there is nothing in it to lose; a world that DOES have a finished save and could not
        /// be copied is a hard failure that has to stop the launch it was protecting.
        /// </para>
        /// <para>
        /// A folder shaped like the game's rolling save, "{world}_{stamp}", is not a world and
        /// is not copied, but it is named in <see cref="WorldSnapshotResult.Skipped"/> so it
        /// does not fall out of the world list and the snapshot list at the same time.
        /// </para>
        /// </summary>
        /// <param name="refuse">
        /// Optional veto asked about every world before it is copied. Returning a reason is a
        /// HARD failure, not a skip: several profiles can share one save folder, and a world
        /// another server is saving into cannot be copied at all - the copy would pair a
        /// finished index with chunk files the in-flight save has already replaced, which reads
        /// as a whole world and is not one.
        /// </param>
        public static WorldSnapshotResult SnapshotAllPreUpdate(
            string saveFolder,
            DateTime? whenLocal = null,
            Func<WorldInfo, string> refuse = null)
        {
            var result = new WorldSnapshotResult();
            var stamp = whenLocal ?? DateTime.Now;

            if (string.IsNullOrWhiteSpace(saveFolder))
            {
                result.Error = "No save folder is configured for this server.";
                return result;
            }

            IReadOnlyList<WorldInfo> worlds;
            try
            {
                worlds = Enumerate(saveFolder);
            }
            catch (Exception e)
            {
                result.Error = "Could not read the save folder: " + e.Message;
                return result;
            }

            // Worlds that actually had something to protect. A world with no finished save on
            // disk is not a failed copy, it is a world with nothing in it to copy, and counting
            // it as a failure refuses a launch that was never at risk.
            var protectable = 0;
            string onlyProtectable = null;

            foreach (var world in worlds)
            {
                if (!HasFinishedSaveToCopy(world))
                {
                    result.Skipped.Add(
                        world.Name + " (there is no finished save in it yet, so there is nothing to copy aside.)");
                    continue;
                }

                protectable++;
                onlyProtectable ??= world.Name;

                if (refuse != null)
                {
                    string veto;
                    try
                    {
                        veto = refuse(world);
                    }
                    catch (Exception e)
                    {
                        result.Error = $"Could not check whether '{world.Name}' is in use: {e.Message}";
                        return result;
                    }

                    if (!string.IsNullOrWhiteSpace(veto))
                    {
                        result.Error = $"'{world.Name}' cannot be copied aside: {veto}";
                        return result;
                    }
                }

                try
                {
                    result.Bytes += SnapshotWorldPreUpdate(world, stamp);
                    result.Copied.Add(PreUpdateLayerName(world.Name, stamp));
                }
                catch (Exception e)
                {
                    // This world DID have a finished save in it, so a refusal here is a world
                    // that was worth protecting and is not protected. That stops the pass.
                    result.Error = $"Could not copy '{world.Name}' aside: {e.Message}";
                    return result;
                }
            }

            // Belt and braces on the loop above: worlds that were worth protecting were there
            // and not one of them was copied. Reporting that as a finished snapshot is how a
            // launch gets waved through with zero bytes behind it.
            if (protectable > 0 && result.Copied.Count == 0)
            {
                result.Error = protectable == 1
                    ? $"'{onlyProtectable}' is the only world in this save folder with a finished save and it could not be copied aside, so there is nothing to go back to."
                    : $"None of the {protectable} worlds in this save folder with a finished save could be copied aside, so there is nothing to go back to.";
            }

            NameRollingSaves(saveFolder, result);
            return result;
        }

        /// <summary>
        /// True when a world has a finished save on disk that a snapshot could copy: a
        /// committed generation for a 1.0 world, the ".fwl" for a legacy pair. A world without
        /// one holds nothing that an upgrade could take away.
        /// </summary>
        private static bool HasFinishedSaveToCopy(WorldInfo world)
        {
            if (world == null) return false;
            if (world.Format == WorldFormat.Chunked) return world.IsCommitted && world.SaveNumber != null;
            try { return !string.IsNullOrEmpty(world.MetaPath) && File.Exists(world.MetaPath); }
            catch { return false; }
        }

        /// <summary>
        /// Names every rolling save under the save folder in the pass's skipped list. The game
        /// files "{world}_{stamp}" under the trimmed name and never opens it as a world, so it
        /// is not in the world list and cannot be copied as one; saying so is what keeps it
        /// from disappearing out of both lists at once.
        /// </summary>
        private static void NameRollingSaves(string saveFolder, WorldSnapshotResult result)
        {
            foreach (var sub in WorldSubfolders)
            {
                var dir = WorldsDir(saveFolder, sub);
                if (dir == null || !Directory.Exists(dir)) continue;

                try
                {
                    foreach (var child in Directory.EnumerateDirectories(dir))
                    {
                        var name = System.IO.Path.GetFileName(child);
                        if (IsRollingSaveName(name, out var owner)) Note(name, owner);
                    }

                    foreach (var file in Directory.EnumerateFiles(dir))
                    {
                        var name = System.IO.Path.GetFileName(file);
                        // One entry per layer, keyed on the ".fwl" the way the Barrow keys it.
                        if (!string.Equals(System.IO.Path.GetExtension(name), ".fwl", StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (IsRollingSaveName(System.IO.Path.GetFileNameWithoutExtension(name), out var owner))
                            Note(name, owner);
                    }
                }
                catch { /* an unreadable folder is not a snapshot failure */ }
            }

            void Note(string name, string owner)
                => result.Skipped.Add(
                    name + " (the game files this under '" + owner + "' as a rolling save, so it is left where it is.)");
        }

        /// <summary>Copies "{world}_biomedatacache.bin" between save folders when the source has one.</summary>
        public static void CopyBiomeCache(string sourceSaveFolder, string destSaveFolder, string worldName)
        {
            try
            {
                var src = BiomeCachePath(sourceSaveFolder, worldName);
                if (src == null || !File.Exists(src)) return;
                var destDir = System.IO.Path.Combine(destSaveFolder, CacheFolderName);
                Directory.CreateDirectory(destDir);
                File.Copy(src, System.IO.Path.Combine(destDir, System.IO.Path.GetFileName(src)), overwrite: true);
            }
            catch { /* the game regenerates the cache; never fail a copy over it */ }
        }

        /// <summary>
        /// Copies a world's biome data cache under ANOTHER name inside one save folder, which
        /// is what a world copied under a new name needs: the game builds the cache file name
        /// from the name stored in the header, and the copy's header names the copy.
        /// </summary>
        public static void CopyBiomeCacheAs(string saveFolder, string sourceWorldName, string targetWorldName)
        {
            try
            {
                var source = BiomeCachePath(saveFolder, sourceWorldName);
                var destination = BiomeCachePath(saveFolder, targetWorldName);
                if (source == null || destination == null || !File.Exists(source)) return;
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(destination));
                File.Copy(source, destination, overwrite: true);
            }
            catch { /* the game regenerates the cache; never fail a copy over it */ }
        }

        /// <summary>Path of a world's biome data cache under a save folder (never null for valid input).</summary>
        public static string BiomeCachePath(string saveFolder, string worldName)
        {
            if (string.IsNullOrWhiteSpace(saveFolder) || string.IsNullOrWhiteSpace(worldName)) return null;
            return System.IO.Path.Combine(saveFolder, CacheFolderName, worldName + BiomeCacheSuffix);
        }

        /// <summary>
        /// The name the game keys a world's biome data cache on: the world name stored INSIDE the
        /// .fwl/.fwl2, which is what the game carries in World.m_name and what it builds
        /// "{name}_biomedatacache.bin" from. That is only the same as the folder or file name
        /// until a copy or a rename puts the world under a different one, and the payload bytes
        /// (and so the name in them) travel with every copy. The on-disk name is the fallback for
        /// a world whose metadata cannot be read, and for one whose stored name would not make a
        /// file name.
        /// </summary>
        public static string BiomeCacheKey(WorldInfo world)
        {
            if (world == null) return null;

            var stored = FwlReader.TryRead(world.MetaPath)?.WorldName;
            if (string.IsNullOrWhiteSpace(stored)) return world.Name;
            if (stored.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0) return world.Name;
            return stored;
        }

        /// <summary>
        /// Makes sure a save folder has the sibling folders the game writes into, so a freshly
        /// provisioned isolated save folder is shaped like one the game made itself.
        /// </summary>
        public static void EnsureSaveFolderLayout(string saveFolder)
        {
            if (string.IsNullOrWhiteSpace(saveFolder)) return;
            try
            {
                Directory.CreateDirectory(System.IO.Path.Combine(saveFolder, WorldSubfolders[0]));
                Directory.CreateDirectory(System.IO.Path.Combine(saveFolder, CacheFolderName));
            }
            catch { /* best effort */ }
        }

        /// <summary>
        /// Deletes a world and everything the game would delete with it: the world directory
        /// (or the legacy file pair), its backup layers when asked, and the biome data cache.
        /// Returns the names of what was actually removed.
        /// <para>
        /// The sweep covers BOTH worlds subfolders, not just the one the world was found in.
        /// A same-named spelling next door is hidden by the dedupe in <see cref="Enumerate"/>
        /// for exactly as long as the one being deleted is there, so leaving it behind is how a
        /// world nobody could see comes back as a live world of the same name on the very next
        /// read: a stale save the host never asked to keep, and one that a server would then be
        /// launched onto.
        /// </para>
        /// </summary>
        public static IReadOnlyList<string> DeleteWorld(string saveFolder, string worldName, bool includeBackups = true)
        {
            var deleted = new List<string>();
            var world = Find(saveFolder, worldName);
            if (world == null) return deleted;

            // Read before anything is removed: the stored name lives in the .fwl we are about to
            // delete, and it is the only name the game's cache file is ever built from.
            var cacheKey = BiomeCacheKey(world);

            if (world.Format == WorldFormat.Chunked)
            {
                Directory.Delete(world.Folder, recursive: true);
                deleted.Add(System.IO.Path.GetFileName(world.Folder));
            }
            else
            {
                foreach (var ext in new[] { ".fwl", ".db", ".fwl.old", ".db.old" })
                {
                    var path = System.IO.Path.Combine(world.Folder, world.Name + ext);
                    if (!File.Exists(path)) continue;
                    File.Delete(path);
                    deleted.Add(System.IO.Path.GetFileName(path));
                }
            }

            // Every other spelling of this name, in BOTH worlds subfolders. The dedupe in
            // Enumerate hides a same-named directory or legacy pair next door for exactly as
            // long as the one just deleted was there, so this is the only pass that can reach it.
            foreach (var sub in WorldSubfolders)
            {
                var worldsDir = WorldsDir(saveFolder, sub);
                if (worldsDir == null || !Directory.Exists(worldsDir)) continue;

                var shadowed = System.IO.Path.Combine(worldsDir, world.Name);
                if (Directory.Exists(shadowed) && FindGeneration(shadowed).Exists)
                {
                    try
                    {
                        Directory.Delete(shadowed, recursive: true);
                        deleted.Add(System.IO.Path.GetFileName(shadowed));
                    }
                    catch { /* a locked leftover must not abort what is already done */ }
                }

                // Only the live pair here: the ".old" siblings are a backup layer, and whether
                // layers go is what includeBackups decides just below.
                deleted.AddRange(DeleteLegacyPair(worldsDir, world.Name, includeOldSiblings: false));

                if (!includeBackups) continue;

                foreach (var layer in EnumerateBackups(saveFolder, sub, world.Name))
                {
                    try
                    {
                        deleted.AddRange(DeleteBackup(layer));
                    }
                    catch { /* a locked layer must not abort the delete */ }
                }
            }

            // The game deletes "{world}_biomedatacache.bin" with the world; a stale 2048x2048
            // grid left behind would be reused by a later world of the same name.
            try
            {
                var cache = BiomeCachePath(saveFolder, cacheKey);
                if (cache != null && File.Exists(cache))
                {
                    File.Delete(cache);
                    deleted.Add(System.IO.Path.GetFileName(cache));
                }
            }
            catch { /* best effort */ }

            return deleted;
        }

        /// <summary>Deletes one backup layer (a directory, or a .fwl plus its paired .db).</summary>
        public static IReadOnlyList<string> DeleteBackup(WorldBackupInfo layer)
        {
            var deleted = new List<string>();
            if (layer == null) return deleted;

            if (layer.IsDirectory)
            {
                if (Directory.Exists(layer.Path))
                {
                    Directory.Delete(layer.Path, recursive: true);
                    deleted.Add(layer.Name);
                }
                return deleted;
            }

            if (File.Exists(layer.Path))
            {
                File.Delete(layer.Path);
                deleted.Add(System.IO.Path.GetFileName(layer.Path));
            }
            if (!string.IsNullOrEmpty(layer.DbPath) && File.Exists(layer.DbPath))
            {
                File.Delete(layer.DbPath);
                deleted.Add(System.IO.Path.GetFileName(layer.DbPath));
            }
            return deleted;
        }

        // ---------------------------------------------------------------- filesystem helpers

        /// <summary>Recursive copy that creates the destination tree as it goes.</summary>
        public static void CopyDirectory(string sourceDir, string destinationDir)
        {
            Directory.CreateDirectory(destinationDir);

            foreach (var file in Directory.GetFiles(sourceDir))
                File.Copy(file, System.IO.Path.Combine(destinationDir, System.IO.Path.GetFileName(file)), overwrite: true);

            foreach (var dir in Directory.GetDirectories(sourceDir))
                CopyDirectory(dir, System.IO.Path.Combine(destinationDir, System.IO.Path.GetFileName(dir)));
        }

        /// <summary>
        /// Replaces a live world directory's contents with another directory's, keeping the
        /// live directory itself in place. A restored generation never sits next to stale chunk
        /// files from the newer save.
        /// <para>
        /// The copy lands in a sibling directory first and is only swapped in once it holds a
        /// committed generation. Emptying the live world first and copying into it means any
        /// failure part way through - a locked file, a full disk, a cloud-sync handle - leaves
        /// the world half gone and unloadable with nothing to put it back.
        /// </para>
        /// </summary>
        /// <param name="safetyLayerName">
        /// The snapshot the caller took before it asked for this, named in the error so the host
        /// is told what to unearth if the swap could not be finished.
        /// </param>
        /// <param name="recoveryHint">
        /// The way back to put in the error instead of the snapshot sentence, for a caller that
        /// took no snapshot because it was not replacing anything of the host's to begin with.
        /// </param>
        public static void ReplaceDirectoryContents(
            string sourceDir, string liveDir, string safetyLayerName = null, string recoveryHint = null)
        {
            if (string.IsNullOrWhiteSpace(liveDir)) throw new ArgumentException("liveDir is required", nameof(liveDir));

            var parent = System.IO.Path.GetDirectoryName(
                System.IO.Path.TrimEndingDirectorySeparator(liveDir));
            if (string.IsNullOrWhiteSpace(parent))
                throw new InvalidOperationException("The live world has no parent folder to stage the restore in.");

            var stamp = DateTime.Now.ToString(BackupStampFormat);
            var leaf = System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(liveDir));
            var staged = System.IO.Path.Combine(parent, leaf + ".restoring-" + stamp);
            var scratch = System.IO.Path.Combine(parent, leaf + ".replaced-" + stamp);

            var safety = string.IsNullOrWhiteSpace(safetyLayerName)
                ? "the safety copy taken before the restore"
                : "the safety copy \"" + safetyLayerName + "\"";

            var custom = !string.IsNullOrWhiteSpace(recoveryHint);
            var wayBack = custom ? recoveryHint : "Unearth " + safety + " if it does not load.";

            TryDeleteDirectory(staged);
            TryDeleteDirectory(scratch);

            try
            {
                CopyDirectory(sourceDir, staged);

                // Prove the copy is a world before anything irreversible happens to the live one.
                if (!FindGeneration(staged).IsCommitted)
                {
                    throw new InvalidOperationException(
                        "The copied world has no finished save in it, so the live world was left as it was. "
                        + wayBack);
                }
            }
            catch (Exception e)
            {
                TryDeleteDirectory(staged);

                if (e is InvalidOperationException) throw;

                throw new InvalidOperationException(
                    "The world could not be copied, so it was left as it was. " + wayBack, e);
            }

            var livePresent = Directory.Exists(liveDir);

            try
            {
                if (livePresent) Directory.Move(liveDir, scratch);
                Directory.Move(staged, liveDir);
            }
            catch (Exception e)
            {
                // Put the live world back if it was already moved out of the way.
                if (livePresent && !Directory.Exists(liveDir) && Directory.Exists(scratch))
                {
                    try { Directory.Move(scratch, liveDir); }
                    catch
                    {
                        throw new InvalidOperationException(
                            "The world could not be swapped in and the original could not be put back. "
                            + "It is at \"" + scratch + "\"."
                            + (custom ? string.Empty : " Unearth " + safety + " if that folder is gone."), e);
                    }
                }

                TryDeleteDirectory(staged);
                throw new InvalidOperationException(
                    "The world could not be replaced, so it was left as it was. " + wayBack, e);
            }

            TryDeleteDirectory(scratch);
        }

        /// <summary>Removes a directory tree if it is there. Never throws.</summary>
        private static void TryDeleteDirectory(string dir)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            }
            catch { /* best effort */ }
        }

        /// <summary>Total bytes of a directory tree. Zero when unreadable.</summary>
        public static long DirectorySize(string dir)
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return 0;
            long total = 0;
            try
            {
                foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                    total += SafeFileSize(file);
            }
            catch { /* partial total beats none */ }
            return total;
        }

        /// <summary>
        /// In-game day from a world database header: int32 worldVersion then a double netTime
        /// in seconds, identical in the legacy .db and the 1.0 .db2. One day is 1800 seconds.
        /// Null on any doubt, because a missing day beats a wrong one.
        /// </summary>
        public static long? TryReadWorldDay(string dbPath)
        {
            if (string.IsNullOrWhiteSpace(dbPath)) return null;
            try
            {
                if (!File.Exists(dbPath)) return null;
                using var stream = new FileStream(dbPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                if (stream.Length < 12) return null;
                using var reader = new BinaryReader(stream);
                var version = reader.ReadInt32();
                if (version <= 0 || version >= 100) return null;
                var netTime = reader.ReadDouble();
                if (double.IsNaN(netTime) || netTime < 0 || netTime >= 4e9) return null;
                return (long)(netTime / 1800.0);
            }
            catch { return null; }
        }

        private static string WorldsDir(string saveFolder, string sub)
        {
            if (string.IsNullOrWhiteSpace(saveFolder) || string.IsNullOrWhiteSpace(sub)) return null;
            return System.IO.Path.Combine(saveFolder, sub);
        }

        private static long SafeFileSize(string path)
        {
            try { return new FileInfo(path).Length; } catch { return 0; }
        }

        private static DateTime SafeLastWriteUtc(string path)
        {
            try
            {
                if (path == null) return DateTime.MinValue;
                if (Directory.Exists(path)) return Directory.GetLastWriteTimeUtc(path);
                return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
            }
            catch { return DateTime.MinValue; }
        }
    }
}
