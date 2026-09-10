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

        /// <summary>Backup-shaped but none of the above (a bare "{world}_{stamp}.fwl", say).</summary>
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
    ///  * backups are never worlds. Legacy backups are "{world}_backup_*.fwl",
    ///    "{world}.fwl.old" and bare "{world}_{stamp}.fwl"; chunked backups are sibling
    ///    DIRECTORIES named "{world}_backup_[auto-|restore-|cloud-]{stamp}".
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

        // A save stamp is yyyyMMddHHmmss (14 chars, pre-1.0) or yyyyMMdd-HHmmss (15 chars, 1.0).
        private const string StampPattern = @"(?<stamp>\d{8}-?\d{6})";

        private static readonly Regex BackupNamed = new(
            @"^(?<world>.+?)_backup_(?<infix>auto-|restore-|cloud-|preupdate-)?" + StampPattern + "$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>The infix BakaLoader stamps onto a pre-update snapshot.</summary>
        public const string PreUpdateInfix = "preupdate-";

        /// <summary>The timestamp shape Valheim 1.0 uses for its own backup names.</summary>
        public const string BackupStampFormat = "yyyyMMdd-HHmmss";

        // A bare timestamped snapshot with no "_backup_" marker, e.g. "Midgard_20220620-101500".
        private static readonly Regex BackupStamped = new(
            @"^(?<world>.+?)_" + StampPattern + "$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

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

        /// <summary>Names of every world under the save folder, deduped, worlds_local first.</summary>
        public static List<string> GetWorldNames(string saveFolder)
            => Enumerate(saveFolder).Select(w => w.Name).ToList();

        // ---------------------------------------------------------------- backups

        /// <summary>
        /// Every backup layer of one world inside a single worlds subfolder: legacy .fwl pairs,
        /// the ".fwl.old" pair, and chunked backup directories. Newest first.
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
                    if (!isOldPair && !string.Equals(System.IO.Path.GetExtension(fileName), ".fwl", StringComparison.OrdinalIgnoreCase))
                        continue;

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
                        LastWriteUtc = SafeLastWriteUtc(hasDb ? dbPath : file),
                        Stamp = stamp,
                    });
                }
            }
            catch { /* ignore */ }

            return results.OrderByDescending(b => b.LastWriteUtc).ToList();
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
        /// Resolves a backup reference from the UI to a real layer of that world, or null.
        /// A reference can ONLY ever name a layer the world actually owns: the name is matched
        /// against the layers found on disk, and the resolved path is re-checked to sit directly
        /// inside that worlds folder. There is no path built from caller-supplied text, so the
        /// live world (file pair or 1.0 directory) can never be reached through here.
        /// </summary>
        public static WorldBackupInfo ResolveBackupLayer(string saveFolder, string sub, string worldName, string name)
        {
            if (!IsSafeReferenceToken(worldName) || !IsSafeReferenceToken(name)) return null;
            if (sub != WorldSubfolders[0] && sub != WorldSubfolders[1]) return null;
            if (string.IsNullOrWhiteSpace(saveFolder)) return null;

            var dir = WorldsDir(saveFolder, sub);
            if (dir == null || !Directory.Exists(dir)) return null;

            var layer = EnumerateBackups(saveFolder, sub, worldName)
                .FirstOrDefault(b => string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase));
            if (layer == null) return null;

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

        /// <summary>
        /// True when a DIRECTORY sitting in worlds_local/worlds is a backup rather than a world.
        /// </summary>
        public static bool IsBackupDirectoryName(string directoryName)
            => TryParseBackupName(directoryName, out _, out _, out _);

        /// <summary>
        /// True when a FILE sitting in worlds_local/worlds is a backup rather than a live world
        /// file: "{world}_backup_*.fwl/.db", "{world}.fwl.old"/".db.old", or a bare
        /// "{world}_{stamp}.fwl/.db" left behind by the 2022 worlds_local migration.
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
        /// Splits a backup-shaped name (no extension) into the world it belongs to, its kind,
        /// and its timestamp. False when the name is not backup-shaped at all.
        /// </summary>
        public static bool TryParseBackupName(string name, out string worldName, out WorldBackupKind kind, out DateTime? stamp)
        {
            worldName = null;
            kind = WorldBackupKind.Other;
            stamp = null;
            if (string.IsNullOrWhiteSpace(name)) return false;

            // "_backup_" shapes first: a bare-stamp match would otherwise swallow them
            // and hand back a world name ending in "_backup".
            var m = BackupNamed.Match(name);
            if (m.Success)
            {
                worldName = m.Groups["world"].Value;
                if (worldName.Length == 0) return false;
                var infix = m.Groups["infix"].Value.ToLowerInvariant();
                kind = infix switch
                {
                    "auto-" => WorldBackupKind.Auto,
                    "restore-" => WorldBackupKind.Restore,
                    "cloud-" => WorldBackupKind.Cloud,
                    PreUpdateInfix => WorldBackupKind.PreUpdate,
                    // No infix: the untouched originals the game renames aside when it
                    // converts a pre-1.0 world to the directory format.
                    _ => WorldBackupKind.Legacy,
                };
                stamp = ParseStamp(m.Groups["stamp"].Value);
                return true;
            }

            m = BackupStamped.Match(name);
            if (m.Success)
            {
                worldName = m.Groups["world"].Value;
                if (worldName.Length == 0) return false;
                kind = WorldBackupKind.Other;
                stamp = ParseStamp(m.Groups["stamp"].Value);
                return true;
            }

            return false;
        }

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
        /// </summary>
        public static string CopyWorld(WorldInfo world, string destSaveFolder)
        {
            if (world == null) throw new ArgumentNullException(nameof(world));
            if (string.IsNullOrWhiteSpace(destSaveFolder))
                throw new ArgumentException("A destination save folder is required.", nameof(destSaveFolder));

            // A dedicated server reads its worlds from worlds_local, so land the copy there.
            var destWorldsDir = System.IO.Path.Combine(destSaveFolder, WorldSubfolders[0]);
            Directory.CreateDirectory(destWorldsDir);

            string destFolder;
            if (world.Format == WorldFormat.Chunked)
            {
                destFolder = System.IO.Path.Combine(destWorldsDir, world.Name);
                CopyDirectory(world.Folder, destFolder);
            }
            else
            {
                destFolder = destWorldsDir;
                foreach (var ext in new[] { ".fwl", ".db", ".fwl.old", ".db.old" })
                {
                    var src = System.IO.Path.Combine(world.Folder, world.Name + ext);
                    if (!File.Exists(src)) continue;
                    File.Copy(src, System.IO.Path.Combine(destWorldsDir, world.Name + ext), overwrite: true);
                }
            }

            CopyBiomeCache(world.SaveFolder, destSaveFolder, world.Name);
            return destFolder;
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

            foreach (var world in worlds)
            {
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
                catch (InvalidOperationException refused)
                {
                    // Nothing to copy for this world; that is not a reason to block the start.
                    result.Skipped.Add(world.Name + " (" + refused.Message + ")");
                }
                catch (Exception e)
                {
                    result.Error = $"Could not copy '{world.Name}' aside: {e.Message}";
                    return result;
                }
            }

            return result;
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

        /// <summary>Path of a world's biome data cache under a save folder (never null for valid input).</summary>
        public static string BiomeCachePath(string saveFolder, string worldName)
        {
            if (string.IsNullOrWhiteSpace(saveFolder) || string.IsNullOrWhiteSpace(worldName)) return null;
            return System.IO.Path.Combine(saveFolder, CacheFolderName, worldName + BiomeCacheSuffix);
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
        /// </summary>
        public static IReadOnlyList<string> DeleteWorld(string saveFolder, string worldName, bool includeBackups = true)
        {
            var deleted = new List<string>();
            var world = Find(saveFolder, worldName);
            if (world == null) return deleted;

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

            if (includeBackups)
            {
                foreach (var layer in EnumerateBackups(saveFolder, world.Sub, world.Name))
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
                var cache = BiomeCachePath(saveFolder, world.Name);
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
        public static void ReplaceDirectoryContents(string sourceDir, string liveDir, string safetyLayerName = null)
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
                        + "Unearth " + safety + " if it does not load.");
                }
            }
            catch (Exception e)
            {
                TryDeleteDirectory(staged);

                if (e is InvalidOperationException) throw;

                throw new InvalidOperationException(
                    "The world could not be copied, so it was left as it was. Unearth " + safety
                    + " if it does not load.", e);
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
                            + "It is at \"" + scratch + "\". Unearth " + safety + " if that folder is gone.", e);
                    }
                }

                TryDeleteDirectory(staged);
                throw new InvalidOperationException(
                    "The world could not be replaced, so it was left as it was. Unearth " + safety
                    + " if it does not load.", e);
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

        private static DateTime? ParseStamp(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var digits = raw.Replace("-", "");
            if (digits.Length != 14) return null;
            return DateTime.TryParseExact(digits, "yyyyMMddHHmmss",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var parsed)
                ? parsed
                : null;
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
