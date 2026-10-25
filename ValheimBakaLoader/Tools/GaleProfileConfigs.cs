using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using ValheimBakaLoader.Forms;

namespace ValheimBakaLoader.Tools
{
    /// <summary>One config file a Gale profile carries, as the zip held it.</summary>
    public sealed class GaleProfileConfigFile
    {
        /// <summary>The path under BepInEx/config, with forward slashes, as the Configs hall writes it.</summary>
        public string Rel { get; init; }

        /// <summary>The profile's bytes, exactly. These and nothing else are what an apply writes.</summary>
        public byte[] Bytes { get; init; }
    }

    /// <summary>What reading a profile zip came to.</summary>
    public sealed class GaleZipRead
    {
        /// <summary>The catalog id of the reason the whole zip was turned down, or null when it was read.</summary>
        public string Refusal { get; init; }

        public bool Ok => Refusal == null;

        /// <summary>The config files the zip carries, in Configs hall order.</summary>
        public IReadOnlyList<GaleProfileConfigFile> Files { get; init; } = Array.Empty<GaleProfileConfigFile>();

        /// <summary>
        /// Files under the config folder that were left out one by one: a kind the Configs hall
        /// does not show, a name Windows cannot hold, one over <see cref="GaleProfileConfigs.MaxEntryBytes"/>,
        /// or the same path twice.
        /// </summary>
        public int Skipped { get; init; }

        /// <summary>True when the zip had the export.r2x a Gale profile carries beside its configs.</summary>
        public bool HasManifest { get; init; }
    }

    public enum GaleConfigState
    {
        /// <summary>The realm has no file at that path.</summary>
        Added,

        /// <summary>The realm has the file and its text is not the profile's.</summary>
        Changed,

        /// <summary>The realm's text is the profile's, line endings and a trailing newline aside.</summary>
        Same,
    }

    /// <summary>One line of a preview: ' ' context, '+' a line the profile brings, '-' a line it takes out, '@' a hunk head.</summary>
    public sealed class GaleDiffLine
    {
        public char Kind { get; init; }
        public string Text { get; init; }
    }

    /// <summary>One profile file held against the realm's copy.</summary>
    public sealed class GaleConfigRow
    {
        public string Rel { get; init; }

        /// <summary>The same file as a path from the server folder, "BepInEx/config/...".</summary>
        public string RealmPath => GaleProfileConfigs.RealmPrefix + Rel;

        public GaleConfigState State { get; init; }

        /// <summary>The preview, at most <see cref="GaleProfileConfigs.MaxPreviewLines"/> lines. Empty for a file that is the same.</summary>
        public IReadOnlyList<GaleDiffLine> Preview { get; init; } = Array.Empty<GaleDiffLine>();

        /// <summary>How many preview lines were cut off past the cap.</summary>
        public int More { get; init; }
    }

    public sealed class GaleConfigComparison
    {
        public IReadOnlyList<GaleConfigRow> Rows { get; init; } = Array.Empty<GaleConfigRow>();

        /// <summary>Files left out because their path on this realm runs through a link or out of the folder.</summary>
        public int Skipped { get; init; }

        public int Added => Rows.Count(r => r.State == GaleConfigState.Added);
        public int Changed => Rows.Count(r => r.State == GaleConfigState.Changed);
        public int Same => Rows.Count(r => r.State == GaleConfigState.Same);
    }

    /// <summary>
    /// The config half of a Gale profile: reading the files out of the profile's zip, and
    /// holding each one against the realm's own copy under BepInEx/config.
    /// <para>
    /// Gale keeps a BepInEx profile's configs in the profile's own "config" folder, which is
    /// the game's BepInEx/config (gale's mod_loader.rs: Subdir "config" to "BepInEx/config"),
    /// so a zip entry "config/x.cfg" is the realm's "BepInEx/config/x.cfg". An entry under
    /// "BepInEx/config/" is taken the same way, in case an export ever writes the long form.
    /// Everything else in the zip is passed over, export.r2x included: the mod list is the
    /// meta's business, read elsewhere.
    /// </para>
    /// <para>
    /// The zip is somebody else's file, so it is walked entry by entry rather than handed to
    /// an extract: a rooted name, a ".." anywhere, or an entry that is a link turns the whole
    /// zip down, because a profile that carries one is not a profile to take anything from.
    /// Nothing here writes to the disk; the apply is the bridge's, through ConfigFiles.
    /// </para>
    /// </summary>
    public static class GaleProfileConfigs
    {
        public const string RealmPrefix = "BepInEx/config/";

        /// <summary>A config file is a few kilobytes. Two megabytes is a file nobody edits by hand.</summary>
        public const int MaxEntryBytes = 2 * 1024 * 1024;

        /// <summary>A big profile has a few hundred files. Past this the zip is not a profile.</summary>
        public const int MaxEntries = 2000;

        /// <summary>The whole of what is held in memory for one profile, unpacked.</summary>
        public const long MaxTotalBytes = 50L * 1024 * 1024;

        /// <summary>The longest preview one file gets; the rest is counted in a sentence.</summary>
        public const int MaxPreviewLines = 400;

        /// <summary>Lines of context either side of a change.</summary>
        public const int Context = 3;

        /// <summary>
        /// Above this many cells the line match is not worked out and the preview says the old
        /// text goes and the new comes, which is true, just less helpful.
        /// </summary>
        public const long MaxDiffCells = 4_000_000;

        public const string NotZipSentence = "mods.gale.configs.refused.not_zip";
        public const string TooManySentence = "mods.gale.configs.refused.too_many";
        public const string UnsafeSentence = "mods.gale.configs.refused.unsafe";
        public const string TooLargeSentence = "mods.gale.configs.refused.too_large";

        private static readonly string[] Prefixes = { "config/", "BepInEx/config/" };

        /// <summary>Reads a profile zip off the disk. Never throws.</summary>
        public static GaleZipRead ReadZip(string zipPath)
        {
            try
            {
                using var stream = File.OpenRead(zipPath);
                return ReadZip(stream);
            }
            catch (Exception)
            {
                return new GaleZipRead { Refusal = NotZipSentence };
            }
        }

        /// <summary>Reads a profile zip from a stream. Never throws.</summary>
        public static GaleZipRead ReadZip(Stream zip)
        {
            try
            {
                using var archive = new ZipArchive(zip, ZipArchiveMode.Read, leaveOpen: true);
                return Walk(archive);
            }
            catch (InvalidDataException)
            {
                return new GaleZipRead { Refusal = NotZipSentence };
            }
            catch (Exception)
            {
                return new GaleZipRead { Refusal = NotZipSentence };
            }
        }

        private static GaleZipRead Walk(ZipArchive archive)
        {
            var entries = archive.Entries;
            if (entries.Count > MaxEntries) return new GaleZipRead { Refusal = TooManySentence };

            var files = new List<GaleProfileConfigFile>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var skipped = 0;
            var manifest = false;
            long total = 0;

            foreach (var entry in entries)
            {
                var name = (entry.FullName ?? "").Replace('\\', '/');
                if (IsHostile(name) || IsLink(entry)) return new GaleZipRead { Refusal = UnsafeSentence };

                if (string.Equals(name, "export.r2x", StringComparison.OrdinalIgnoreCase))
                {
                    manifest = true;
                    continue;
                }

                var rel = UnderConfig(name);
                // Outside the config folder, or the folder itself: not ours to read.
                if (rel == null) continue;
                // A folder entry. Its files, if any, come as entries of their own.
                if (rel.Length == 0 || name.EndsWith("/", StringComparison.Ordinal)) continue;

                if (!IsWritableName(rel) || !ConfigFiles.IsAllowedExtension(rel) || entry.Length > MaxEntryBytes
                    || !seen.Add(rel))
                {
                    skipped++;
                    continue;
                }

                var bytes = ReadEntry(entry);
                if (bytes == null)
                {
                    // Longer than it said it was. Treated like any other oversize file.
                    skipped++;
                    continue;
                }

                total += bytes.Length;
                if (total > MaxTotalBytes) return new GaleZipRead { Refusal = TooLargeSentence };

                files.Add(new GaleProfileConfigFile { Rel = rel, Bytes = bytes });
            }

            files.Sort((a, b) => ConfigFiles.ComparePaths(a.Rel, b.Rel));
            return new GaleZipRead { Files = files, Skipped = skipped, HasManifest = manifest };
        }

        /// <summary>
        /// The config files under a realm's own BepInEx/config, read into memory in the same shape
        /// <see cref="ReadZip(Stream)"/> gives a profile's zip, for a feed that is a folder on this
        /// PC (a test copy) rather than a download. The same limits hold: the same kinds of file,
        /// the same size per file, the same count and the same total, and a file or folder that
        /// is a link is never followed (ConfigFiles.List). A file named in
        /// <paramref name="leaveOut"/> is passed over, for the settings BakaLoader writes per realm
        /// (a port, a player count), which must never travel from one realm to another. A folder
        /// that is not there is an empty listing. Never throws.
        /// </summary>
        public static GaleZipRead ReadFolder(string configDir, IEnumerable<string> leaveOut = null)
        {
            try
            {
                var rels = ConfigFiles.List(configDir);
                if (rels.Count > MaxEntries) return new GaleZipRead { Refusal = TooManySentence };

                var skip = new HashSet<string>(leaveOut ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
                var files = new List<GaleProfileConfigFile>();
                var skipped = 0;
                long total = 0;

                foreach (var rel in rels)
                {
                    if (skip.Contains(rel)) continue;
                    if (!IsWritableName(rel))
                    {
                        skipped++;
                        continue;
                    }

                    byte[] bytes;
                    try
                    {
                        var full = Path.Combine(new[] { configDir }.Concat(rel.Split('/')).ToArray());
                        var info = new FileInfo(full);
                        if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || info.Length > MaxEntryBytes)
                        {
                            skipped++;
                            continue;
                        }
                        bytes = File.ReadAllBytes(full);
                    }
                    catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                    {
                        skipped++;
                        continue;
                    }

                    if (bytes.Length > MaxEntryBytes)
                    {
                        skipped++;
                        continue;
                    }

                    total += bytes.Length;
                    if (total > MaxTotalBytes) return new GaleZipRead { Refusal = TooLargeSentence };
                    files.Add(new GaleProfileConfigFile { Rel = rel, Bytes = bytes });
                }

                files.Sort((a, b) => ConfigFiles.ComparePaths(a.Rel, b.Rel));
                return new GaleZipRead { Files = files, Skipped = skipped, HasManifest = false };
            }
            catch (Exception)
            {
                // A folder that went away under the walk, or one the walk may not read.
                return new GaleZipRead { Refusal = UnsafeSentence };
            }
        }

        /// <summary>A name that points somewhere a profile has no business: rooted, a drive or stream, or a step up.</summary>
        internal static bool IsHostile(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if (name.StartsWith("/", StringComparison.Ordinal) || name.Contains(':')) return true;
            try { if (Path.IsPathRooted(name)) return true; }
            catch (ArgumentException) { return true; }
            return name.Split('/').Any(s => s == "..");
        }

        /// <summary>
        /// True for an entry written as a link: a unix symlink in the upper half of the external
        /// attributes (mode S_IFLNK), or a Windows reparse point in the lower half.
        /// </summary>
        internal static bool IsLink(ZipArchiveEntry entry)
        {
            var attrs = entry.ExternalAttributes;
            var unixMode = (attrs >> 16) & 0xF000;
            if (unixMode == 0xA000) return true;
            return (attrs & (int)FileAttributes.ReparsePoint) != 0;
        }

        /// <summary>The path under the config folder, "" for the folder itself, or null when the entry is not under it.</summary>
        private static string UnderConfig(string name)
        {
            foreach (var prefix in Prefixes)
            {
                if (string.Equals(name, prefix.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)) return "";
                if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return name.Substring(prefix.Length);
            }
            return null;
        }

        /// <summary>The same rules ConfigFiles.Resolve holds a page's path to, so every file read here can be written there.</summary>
        private static bool IsWritableName(string rel)
        {
            var parts = rel.Split('/');
            return parts.All(s => s.Length > 0 && s != "." && s != ".." && s.Trim() == s && s.TrimEnd('.') == s
                && s.IndexOfAny(Path.GetInvalidFileNameChars()) < 0);
        }

        /// <summary>The entry's bytes, or null when it runs past <see cref="MaxEntryBytes"/> whatever its header said.</summary>
        private static byte[] ReadEntry(ZipArchiveEntry entry)
        {
            using var source = entry.Open();
            using var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            int read;
            while ((read = source.Read(chunk, 0, chunk.Length)) > 0)
            {
                if (buffer.Length + read > MaxEntryBytes) return null;
                buffer.Write(chunk, 0, read);
            }
            return buffer.ToArray();
        }

        /// <summary>
        /// Every profile file held against the realm's copy under <paramref name="configDir"/>.
        /// A file whose path on this realm runs through a link is left out and counted, so the
        /// host is never shown a file the apply would refuse to write.
        /// </summary>
        public static GaleConfigComparison Compare(IEnumerable<GaleProfileConfigFile> files, string configDir)
        {
            var rows = new List<GaleConfigRow>();
            var skipped = 0;
            foreach (var file in files ?? Enumerable.Empty<GaleProfileConfigFile>())
            {
                if (file?.Rel == null) continue;
                byte[] realm = null;
                if (!string.IsNullOrWhiteSpace(configDir))
                {
                    try
                    {
                        var full = ConfigFiles.ResolveForCreate(configDir, file.Rel, out var exists);
                        if (exists) realm = File.ReadAllBytes(full);
                    }
                    catch (Exception)
                    {
                        skipped++;
                        continue;
                    }
                }
                rows.Add(Row(file.Rel, realm, file.Bytes));
            }
            return new GaleConfigComparison { Rows = rows, Skipped = skipped };
        }

        /// <summary>One file's state and preview. <paramref name="realm"/> is null when the realm has no such file.</summary>
        public static GaleConfigRow Row(string rel, byte[] realm, byte[] profile)
        {
            var profileLines = Lines(profile);
            if (realm == null)
            {
                var added = profileLines.Select(l => new GaleDiffLine { Kind = '+', Text = l }).ToList();
                return Capped(rel, GaleConfigState.Added, added);
            }

            var realmLines = Lines(realm);
            if (realmLines.SequenceEqual(profileLines, StringComparer.Ordinal))
                return new GaleConfigRow { Rel = rel, State = GaleConfigState.Same };

            return Capped(rel, GaleConfigState.Changed, Unified(realmLines, profileLines));
        }

        /// <summary>True when the two texts are the same once line endings and a trailing newline are put aside.</summary>
        public static bool SameText(byte[] a, byte[] b) =>
            Lines(a).SequenceEqual(Lines(b), StringComparer.Ordinal);

        private static GaleConfigRow Capped(string rel, GaleConfigState state, List<GaleDiffLine> lines)
        {
            var more = Math.Max(0, lines.Count - MaxPreviewLines);
            return new GaleConfigRow
            {
                Rel = rel,
                State = state,
                Preview = more > 0 ? lines.Take(MaxPreviewLines).ToList() : lines,
                More = more,
            };
        }

        /// <summary>
        /// The text as lines: a byte order mark dropped, CRLF and a lone CR read as LF, and
        /// trailing newlines put aside. BepInEx writes its .cfg files with CRLF and a newline
        /// at the end, so a profile made on another machine reads as the same file it is.
        /// </summary>
        public static List<string> Lines(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return new List<string>();
            var start = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
            var text = Encoding.UTF8.GetString(bytes, start, bytes.Length - start)
                .Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd('\n');
            return text.Length == 0 ? new List<string>() : text.Split('\n').ToList();
        }

        /// <summary>
        /// A unified line diff from the realm's text (a) to the profile's (b): hunk heads in the
        /// "@@ -a,n +b,m @@" shape, three lines of context, "-" for a line that goes and "+" for
        /// one that comes.
        /// </summary>
        public static List<GaleDiffLine> Unified(IReadOnlyList<string> a, IReadOnlyList<string> b)
        {
            var ops = Ops(a, b);
            var output = new List<GaleDiffLine>();
            var changes = Enumerable.Range(0, ops.Count).Where(i => ops[i].Kind != ' ').ToList();
            if (changes.Count == 0) return output;

            var k = 0;
            while (k < changes.Count)
            {
                var from = Math.Max(0, changes[k] - Context);
                var last = changes[k];
                while (k + 1 < changes.Count && changes[k + 1] - last <= 2 * Context)
                {
                    k++;
                    last = changes[k];
                }
                var to = Math.Min(ops.Count - 1, last + Context);

                // Where the hunk starts on each side: the line the first op of it stands on.
                var aStart = ops[from].ALine;
                var bStart = ops[from].BLine;
                var aCount = 0;
                var bCount = 0;
                for (var i = from; i <= to; i++)
                {
                    if (ops[i].Kind != '+') aCount++;
                    if (ops[i].Kind != '-') bCount++;
                }
                // An empty side names the line before it, the way diff -u does.
                var aFrom = aCount == 0 ? aStart - 1 : aStart;
                var bFrom = bCount == 0 ? bStart - 1 : bStart;
                output.Add(new GaleDiffLine { Kind = '@', Text = $"@@ -{aFrom},{aCount} +{bFrom},{bCount} @@" });
                for (var i = from; i <= to; i++) output.Add(new GaleDiffLine { Kind = ops[i].Kind, Text = ops[i].Text });
                k++;
            }
            return output;
        }

        private readonly struct Op
        {
            public Op(char kind, string text, int aLine, int bLine)
            {
                Kind = kind;
                Text = text;
                ALine = aLine;
                BLine = bLine;
            }

            public char Kind { get; }
            public string Text { get; }

            /// <summary>The 1-based line on the realm's side this op stands at (the next line, for an add).</summary>
            public int ALine { get; }

            /// <summary>The 1-based line on the profile's side this op stands at (the next line, for a removal).</summary>
            public int BLine { get; }
        }

        /// <summary>
        /// The edit from a to b, line by line. The lines the two share at the top and the bottom
        /// are taken off first, which is nearly all of a config file, and the middle is matched
        /// by longest common run when it is small enough to be worth it.
        /// </summary>
        private static List<Op> Ops(IReadOnlyList<string> a, IReadOnlyList<string> b)
        {
            var ops = new List<Op>(a.Count + b.Count);
            var pre = 0;
            while (pre < a.Count && pre < b.Count && a[pre] == b[pre]) pre++;
            var suf = 0;
            while (suf < a.Count - pre && suf < b.Count - pre && a[a.Count - 1 - suf] == b[b.Count - 1 - suf]) suf++;

            int ai = 0, bi = 0;
            void Same() { ops.Add(new Op(' ', a[ai], ai + 1, bi + 1)); ai++; bi++; }
            void Gone() { ops.Add(new Op('-', a[ai], ai + 1, bi + 1)); ai++; }
            void Came() { ops.Add(new Op('+', b[bi], ai + 1, bi + 1)); bi++; }

            for (var i = 0; i < pre; i++) Same();

            var ma = a.Count - pre - suf;
            var mb = b.Count - pre - suf;
            if ((long)ma * mb <= MaxDiffCells && ma > 0 && mb > 0)
            {
                // Longest common run, table filled from the bottom right.
                var table = new int[ma + 1, mb + 1];
                for (var i = ma - 1; i >= 0; i--)
                    for (var j = mb - 1; j >= 0; j--)
                        table[i, j] = a[pre + i] == b[pre + j]
                            ? table[i + 1, j + 1] + 1
                            : Math.Max(table[i + 1, j], table[i, j + 1]);

                int x = 0, y = 0;
                while (x < ma && y < mb)
                {
                    if (a[pre + x] == b[pre + y]) { Same(); x++; y++; }
                    else if (table[x + 1, y] >= table[x, y + 1]) { Gone(); x++; }
                    else { Came(); y++; }
                }
                while (x < ma) { Gone(); x++; }
                while (y < mb) { Came(); y++; }
            }
            else
            {
                for (var i = 0; i < ma; i++) Gone();
                for (var j = 0; j < mb; j++) Came();
            }

            for (var i = 0; i < suf; i++) Same();
            return ops;
        }
    }
}
