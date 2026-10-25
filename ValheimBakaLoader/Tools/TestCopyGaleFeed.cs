using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ValheimBakaLoader.Tools.Models;

namespace ValheimBakaLoader.Tools
{
    /// <summary>
    /// A test copy read as if it were a Gale profile, so the realm it was copied from can take
    /// back the mods and configs the host proved on it (merge-back). Nothing here goes near the
    /// network: the "profile" is the test copy's own folders on this PC.
    /// <para>
    /// The mod list is the test copy's plugins (and patchers) as the Mods hall reads them, one
    /// line per mod folder: its name, its version from manifest.json or, with no manifest, from
    /// the note BakaLoader leaves when it installs a mod, and a hash of every file in the
    /// folder. The hash is what tells a mod rebuilt under the same version number from the one
    /// the source has (<see cref="GaleDiffKind.Changed"/>); the files' names are in it too, so
    /// a mod switched off on the copy (its .dll renamed to .dll.old) differs from the same mod
    /// switched on. Every line is ticked: the copy has no ticks of its own, and a switched off
    /// mod is still a mod the copy carries.
    /// </para>
    /// <para>
    /// The configs are the test copy's BepInEx/config, read into memory in the shape the
    /// profile zip gives (<see cref="GaleProfileConfigs.ReadFolder"/>), less the files
    /// BakaLoader writes per realm (<see cref="PerRealmConfigs"/>). The world never travels.
    /// </para>
    /// <para>
    /// Like the network client, nothing here throws: a copy that is gone, or one that would
    /// not read, is an outcome with one sentence for the status line.
    /// </para>
    /// </summary>
    public sealed class TestCopyGaleFeed : IGaleSyncClient
    {
        /// <summary>The status line when the test copy the realm follows is not a realm any more.</summary>
        public const string GoneSentence = "mods.testcopy.check.gone";

        /// <summary>The status line when the realm named is no longer a test copy of this one (its source was deleted, or it never was).</summary>
        public const string NotACopySentence = "mods.testcopy.check.not_a_copy";

        /// <summary>The status line when the test copy runs on this realm's own install, so there is nothing to take back.</summary>
        public const string SharesInstallSentence = "mods.testcopy.check.shares_install";

        /// <summary>The status line when the test copy's mod folder could not be read.</summary>
        public const string UnreadableSentence = "mods.testcopy.check.unreadable";

        /// <summary>The configs part, when the test copy holds more config files than a profile may.</summary>
        public const string ConfigsTooManySentence = "mods.testcopy.configs.too_many";

        /// <summary>The configs part, when the test copy's config files come to more than a profile may.</summary>
        public const string ConfigsTooLargeSentence = "mods.testcopy.configs.too_large";

        /// <summary>The configs part, when the test copy's config folder could not be read.</summary>
        public const string ConfigsUnreadableSentence = "mods.testcopy.configs.unreadable";

        /// <summary>What the fingerprint standing in for a profile's updatedAt starts with.</summary>
        public const string StampPrefix = "testcopy-";

        /// <summary>
        /// The config files BakaLoader writes for each realm on its own (the companion plugins'
        /// port, password and player count). They are this realm's and never another's, so a
        /// test copy never hands them back.
        /// </summary>
        public static readonly IReadOnlyList<string> PerRealmConfigs = new[]
        {
            MaxPlayersInstaller.ConfigFileName,
            MaxPlayersInstaller.LegacyConfigFileName,
            CommanderInstaller.ConfigFileName,
        };

        private readonly string PluginsDir;
        private readonly string ConfigDir;
        private readonly IModScanner Scanner;

        /// <summary>The test copy's profile name.</summary>
        public string CopyName { get; }

        /// <summary>The sentence every read answers with when this feed was made refused, or null.</summary>
        public string Refusal { get; }

        /// <summary>A feed reading the test copy's plugins folder (and its patchers beside it) and its config folder.</summary>
        public TestCopyGaleFeed(string copyName, string pluginsDir, string configDir, IModScanner scanner)
        {
            CopyName = copyName;
            PluginsDir = pluginsDir;
            ConfigDir = configDir;
            Scanner = scanner;
        }

        private TestCopyGaleFeed(string copyName, string refusal)
        {
            CopyName = copyName;
            Refusal = refusal;
        }

        /// <summary>A feed that reads nothing and answers every ask with <paramref name="sentenceId"/>.</summary>
        public static TestCopyGaleFeed Refused(string copyName, string sentenceId) => new(copyName, sentenceId);

        /// <summary>The test copy's mods as a profile's meta. The id is not used: the feed is the copy it was made for.</summary>
        public Task<GaleCheckResult> GetMetaAsync(string profileId, CancellationToken cancellationToken = default) =>
            Task.FromResult(ReadMeta());

        /// <summary>
        /// The test copy's configs as a profile zip in a temp file, for a caller that only takes
        /// a zip. The configs part reads <see cref="ReadConfigs"/> straight off and never writes one.
        /// </summary>
        public Task<GaleZipResult> GetProfileZipAsync(string profileId, CancellationToken cancellationToken = default)
        {
            var read = ReadConfigs();
            if (!read.Ok)
                return Task.FromResult(new GaleZipResult { Outcome = GaleCheckOutcome.Malformed, SentenceId = read.Refusal });

            var path = Path.Combine(Path.GetTempPath(), "vbl-testcopy-configs-" + Guid.NewGuid().ToString("N") + ".zip");
            try
            {
                using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
                {
                    foreach (var file in read.Files)
                    {
                        var entry = zip.CreateEntry("config/" + file.Rel);
                        using var stream = entry.Open();
                        stream.Write(file.Bytes, 0, file.Bytes.Length);
                    }
                }
                return Task.FromResult(new GaleZipResult { Outcome = GaleCheckOutcome.Ok, ZipPath = path });
            }
            catch (Exception)
            {
                using (new GaleZipResult { ZipPath = path }) { /* disposing it takes the half written file away */ }
                return Task.FromResult(new GaleZipResult { Outcome = GaleCheckOutcome.Malformed, SentenceId = ConfigsUnreadableSentence });
            }
        }

        /// <summary>The test copy's mods as a profile's meta, or the outcome that says why not. Never throws.</summary>
        public GaleCheckResult ReadMeta()
        {
            if (Refusal != null)
                return new GaleCheckResult { Outcome = GaleCheckOutcome.NotFound, SentenceId = Refusal };

            List<InstalledMod> scanned;
            try
            {
                scanned = string.IsNullOrWhiteSpace(PluginsDir) || !Directory.Exists(PluginsDir) || Scanner == null
                    ? new List<InstalledMod>()
                    : Scanner.ScanPlugins(PluginsDir) ?? new List<InstalledMod>();
            }
            catch (Exception)
            {
                return new GaleCheckResult { Outcome = GaleCheckOutcome.Malformed, SentenceId = UnreadableSentence };
            }

            var lines = scanned
                .Where(m => m != null)
                .Select(m => new GaleProfileMod
                {
                    Name = NameOf(m),
                    Enabled = true,
                    Version = VersionOf(m),
                    ContentHash = FolderHash(m),
                })
                .ToList();

            return new GaleCheckResult
            {
                Outcome = GaleCheckOutcome.Ok,
                Meta = new GaleProfileMeta
                {
                    Mods = lines,
                    UpdatedAt = Fingerprint(lines),
                    Community = "valheim",
                    LocalFeed = true,
                },
            };
        }

        /// <summary>
        /// The test copy's config files, in memory, in the shape a profile zip reads to. A refusal
        /// is worded for a test copy rather than for a zip. Never throws.
        /// </summary>
        public GaleZipRead ReadConfigs()
        {
            if (Refusal != null) return new GaleZipRead { Refusal = Refusal };

            var read = GaleProfileConfigs.ReadFolder(ConfigDir, PerRealmConfigs);
            if (read.Ok) return read;
            return new GaleZipRead
            {
                Refusal = read.Refusal == GaleProfileConfigs.TooManySentence ? ConfigsTooManySentence
                    : read.Refusal == GaleProfileConfigs.TooLargeSentence ? ConfigsTooLargeSentence
                    : ConfigsUnreadableSentence,
            };
        }

        /// <summary>
        /// The name a mod is compared by: the manifest's "Namespace-Name" as the folder spells it,
        /// or the bare name for a folder with no author. The same rule the Gale check reads the
        /// realm's own mods by, so the two sides meet.
        /// </summary>
        public static string NameOf(InstalledMod mod) =>
            mod == null ? null : string.IsNullOrWhiteSpace(mod.Author) ? mod.ModName : mod.FullName;

        /// <summary>
        /// The version a mod folder says it is: manifest.json's, else the version in the note
        /// BakaLoader left when it installed the folder, else "unknown".
        /// </summary>
        public static string VersionOf(InstalledMod mod)
        {
            var version = mod?.InstalledVersion?.Trim();
            if (SemVer.IsReadable(version)) return version;

            var note = ModSourceMarkerFile.Read(mod?.PluginDirectory) ?? ModSourceMarkerFile.Read(mod?.PatcherDirectory);
            var noted = note?.Version?.Trim();
            if (SemVer.IsReadable(noted)) return noted;

            return string.IsNullOrWhiteSpace(version) ? "unknown" : version;
        }

        /// <summary>
        /// One text for the whole list, standing in for a profile's updatedAt: it moves when a
        /// mod is added, taken away, given another version or rebuilt on the test copy, and only
        /// then, so a dismissal holds until the copy really changes.
        /// </summary>
        public static string Fingerprint(IEnumerable<GaleProfileMod> lines)
        {
            var text = new StringBuilder();
            foreach (var line in (lines ?? Enumerable.Empty<GaleProfileMod>())
                         .Where(l => l?.Name != null)
                         .OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(l => l.Name, StringComparer.Ordinal))
                text.Append(line.Name.ToLowerInvariant()).Append('|').Append(line.Version).Append('|').Append(line.ContentHash).Append('\n');

            using var sha = SHA256.Create();
            return StampPrefix + Hex(sha.ComputeHash(Encoding.UTF8.GetBytes(text.ToString()))).Substring(0, 32);
        }

        // Each file's own hash, by path, held while its length and its last write stay the same,
        // so a second check of an unchanged realm reads no file again.
        private static readonly ConcurrentDictionary<string, (long Length, DateTime WrittenUtc, string Hash)> FileHashes =
            new(StringComparer.OrdinalIgnoreCase);

        private const int MaxHeldFileHashes = 50_000;

        /// <summary>
        /// A hash of every file in a mod's folders (its plugins folder and its patchers folder),
        /// by each file's path inside the folder and its contents. The note BakaLoader leaves in
        /// an installed folder is left out, because it says when the folder was installed, which
        /// differs between two installs of the same files. A file or folder that is a link is not
        /// followed. Null when the mod has no folder, or a file in it could not be read: a mod
        /// whose files cannot be read is compared by its version only.
        /// </summary>
        public static string FolderHash(InstalledMod mod)
        {
            if (mod == null || (mod.PluginDirectory == null && mod.PatcherDirectory == null)) return null;
            try
            {
                var files = new List<(string Rel, string Full)>();
                Collect(mod.PluginDirectory, "plugins/", files);
                Collect(mod.PatcherDirectory, "patchers/", files);
                files.Sort((a, b) => string.CompareOrdinal(a.Rel, b.Rel));

                using var sha = SHA256.Create();
                foreach (var (rel, full) in files)
                {
                    var part = Encoding.UTF8.GetBytes(rel + "\n" + FileHash(full) + "\n");
                    sha.TransformBlock(part, 0, part.Length, null, 0);
                }
                sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                return Hex(sha.Hash);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                return null;
            }
        }

        private static void Collect(string root, string prefix, List<(string Rel, string Full)> files)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root) || IsLink(root)) return;
            Walk(new DirectoryInfo(root), prefix, files, top: true);
        }

        private static void Walk(DirectoryInfo dir, string prefix, List<(string Rel, string Full)> files, bool top)
        {
            foreach (var entry in dir.GetFileSystemInfos())
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                if (entry is DirectoryInfo sub)
                {
                    Walk(sub, prefix + sub.Name.ToLowerInvariant() + "/", files, top: false);
                    continue;
                }
                if (top && string.Equals(entry.Name, ModSourceMarkerFile.FileName, StringComparison.OrdinalIgnoreCase)) continue;
                files.Add((prefix + entry.Name.ToLowerInvariant(), entry.FullName));
            }
        }

        private static string FileHash(string path)
        {
            var info = new FileInfo(path);
            var length = info.Length;
            var written = info.LastWriteTimeUtc;
            if (FileHashes.TryGetValue(path, out var held) && held.Length == length && held.WrittenUtc == written)
                return held.Hash;

            string hash;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var sha = SHA256.Create())
                hash = Hex(sha.ComputeHash(stream));

            if (FileHashes.Count >= MaxHeldFileHashes) FileHashes.Clear();
            FileHashes[path] = (length, written, hash);
            return hash;
        }

        private static bool IsLink(string path)
        {
            try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
            catch (Exception) { return false; }
        }

        private static string Hex(byte[] bytes)
        {
            var text = new StringBuilder(bytes.Length * 2);
            foreach (var b in bytes) text.Append(b.ToString("x2"));
            return text.ToString();
        }
    }
}
