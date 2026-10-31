using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using ValheimBakaLoader.Tools.Models;

namespace ValheimBakaLoader.Tools
{
    /// <summary>What a package zip on this PC turned out to be, once it was read and passed.</summary>
    public sealed class LocalPackageFacts
    {
        public string Owner { get; init; }
        public string Name { get; init; }
        public string Version { get; init; }
        public string[] Dependencies { get; init; } = Array.Empty<string>();

        /// <summary>The zip's own file name and nothing above it: the folder it sat in is the host's business.</summary>
        public string FileName { get; init; }

        /// <summary>The size of the copy that was read, which is the copy that gets installed.</summary>
        public long Bytes { get; init; }

        /// <summary>"installed" when the owner came from the copy already on this server, "fileName" when from the zip's name.</summary>
        public string OwnerFrom { get; init; }

        public string FolderName => $"{Owner}-{Name}";
    }

    /// <summary>Why a zip was turned down: an id the page has a sentence for, the sentence's values, and the English.</summary>
    public sealed class LocalPackageRefusal
    {
        public string Reason { get; init; }
        public Dictionary<string, object> Params { get; init; } = new();
        public string Sentence { get; init; }
    }

    /// <summary>One of the two, never both.</summary>
    public sealed class LocalPackageReading
    {
        public LocalPackageFacts Facts { get; init; }
        public LocalPackageRefusal Refusal { get; init; }
    }

    /// <summary>
    /// Reads a Thunderstore or Hexium package zip the host has on this PC and says whether it can be
    /// installed, before a single file of the server's is touched.
    /// <para>
    /// The zip is copied to a private temporary file first and everything after that reads ONLY the
    /// copy, so the bytes that were checked are the bytes that are installed, a file on a share is
    /// read across the network once, and a browser still writing the original cannot change what
    /// lands half way through. The whole read runs off the calling thread with a clock on it,
    /// because a share that has stopped answering can hold File.Exists for minutes and the caller
    /// is the window.
    /// </para>
    /// <para>
    /// The order is the order a host would want to be told things in: is there a file, is it a zip,
    /// is it a sane size, can it be read, is it a zip inside, does anything in it point outside its
    /// folder, does it carry a manifest where Thunderstore puts one, and does that manifest say who
    /// and what it is. Every entry is looked at before anything is extracted, because the extractor's
    /// own refusal of an escaping entry would come back worded as a damaged download.
    /// </para>
    /// </summary>
    public static class LocalPackageReader
    {
        /// <summary>
        /// BakaLoader's own limit on how many files one zip may unpack to. Real Valheim packages hold
        /// a few hundred at the most; a zip with a hundred thousand tiny entries is a way to fill a
        /// folder, not a mod.
        /// </summary>
        public const int MaxEntries = 20000;

        /// <summary>
        /// BakaLoader's own limit on what one zip may unpack to, read off the sizes the zip itself
        /// declares. Two gigabytes is several times the largest real package, and a small zip that
        /// declares more is a zip bomb. An entry that lies about its size is caught by the extractor,
        /// which stops at the declared length.
        /// </summary>
        public const long MaxUnpackedBytes = 2L * 1024 * 1024 * 1024;

        /// <summary>The same cap a downloaded package is held to (ModUpdateService.MaxModDownloadBytes).</summary>
        public const long DefaultMaxZipBytes = 600L * 1024 * 1024;

        /// <summary>The most of a manifest.json that is read. A real one is well under a kilobyte.</summary>
        public const int MaxManifestBytes = 64 * 1024;

        /// <summary>How long the whole read may take before the host is told the file did not answer.</summary>
        public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(120);

        private const string ManifestName = "manifest.json";

        // The manifest name has to be a Thunderstore name: the folder is Owner-Name and the scan
        // splits it on the first hyphen, so a hyphen in either half would read back as another mod.
        private static readonly Regex NamePattern = new(@"^\w+$", RegexOptions.CultureInvariant);

        // Owner-Name-Version.zip, the shape Thunderstore and the mod managers name a package file.
        private static readonly Regex FileNameShape = new(@"^(?<o>\w+)-(?<n>\w+)-(?<v>\d+\.\d+\.\d+[^\\/]*)\.zip$",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        /// <summary>
        /// Reads <paramref name="path"/> into <paramref name="tempCopyPath"/> and checks the copy.
        /// <paramref name="installed"/> is what the server already has, which is where an owner comes
        /// from when the manifest does not say (a manifest never does). The caller deletes the copy.
        /// <paramref name="openSource"/> is for a test that needs a file that never answers.
        /// </summary>
        public static async Task<LocalPackageReading> InspectAsync(string path, string tempCopyPath,
            IReadOnlyList<InstalledMod> installed, long maxZipBytes = DefaultMaxZipBytes,
            TimeSpan? timeout = null, CancellationToken stop = default, Func<string, Stream> openSource = null)
        {
            var bound = timeout ?? DefaultTimeout;
            // Not disposed on the slow road: the read is still running when this returns, and it
            // reads the token to know it has to clean its copy up and stop.
            var cancel = CancellationTokenSource.CreateLinkedTokenSource(stop);
            var work = Task.Run(() => Inspect(path, tempCopyPath, installed, maxZipBytes, openSource, cancel.Token));
            if (await Task.WhenAny(work, Task.Delay(bound)).ConfigureAwait(false) == work)
            {
                cancel.Dispose();
                return await work.ConfigureAwait(false);
            }

            cancel.Cancel();
            var file = SafeFileName(path);
            var detail = $"no answer within {(int)bound.TotalSeconds} seconds";
            return Refuse("local.unreadable", $"{file} could not be read: {detail}.", ("file", file), ("detail", detail));
        }

        internal static LocalPackageReading Inspect(string path, string tempCopyPath, IReadOnlyList<InstalledMod> installed,
            long maxZipBytes, Func<string, Stream> openSource, CancellationToken stop)
        {
            var file = SafeFileName(path);

            bool isFile;
            try { isFile = File.Exists(path); } catch { isFile = false; }
            if (!isFile)
            {
                bool isFolder;
                try { isFolder = Directory.Exists(path); } catch { isFolder = false; }
                return isFolder
                    ? Refuse("local.isFolder", $"{path} is a folder. Pick the .zip file itself.", ("path", path))
                    : Refuse("local.missing", $"There is no file at {path}.", ("path", path));
            }

            if (!file.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                return Refuse("local.notZip", $"{file} is not a .zip file.", ("file", file));

            long length;
            try { length = new FileInfo(path).Length; }
            catch (Exception e) { return Unreadable(file, e.Message); }

            if (length == 0)
                return Refuse("local.empty", $"{file} is empty.", ("file", file));
            if (maxZipBytes > 0 && length > maxZipBytes)
                return TooLarge(file, length, maxZipBytes);

            // The copy. Opened for reading with others allowed to read only, so a program still
            // WRITING the file (a browser part way through a download) answers locked rather than
            // handing over half a zip.
            long copied = 0;
            var tooLarge = false;
            try
            {
                using var source = (openSource ?? OpenForCopy)(path);
                using var target = new FileStream(tempCopyPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                var buffer = new byte[81920];
                int read;
                while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                {
                    stop.ThrowIfCancellationRequested();
                    copied += read;
                    if (maxZipBytes > 0 && copied > maxZipBytes) { tooLarge = true; break; }
                    target.Write(buffer, 0, read);
                }
                stop.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException)
            {
                TryDelete(tempCopyPath);
                return Unreadable(file, "it stopped answering");
            }
            catch (IOException e) when (IsSharingViolation(e))
            {
                TryDelete(tempCopyPath);
                return Refuse("local.locked", $"Another program has {file} open.", ("file", file));
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                TryDelete(tempCopyPath);
                return Unreadable(file, e.Message);
            }

            if (tooLarge)
            {
                // It grew while it was read.
                TryDelete(tempCopyPath);
                return TooLarge(file, copied, maxZipBytes);
            }
            if (copied == 0)
                return Refuse("local.empty", $"{file} is empty.", ("file", file));

            try
            {
                using var zip = ZipFile.OpenRead(tempCopyPath);
                return ReadArchive(zip, file, copied, installed);
            }
            catch (InvalidDataException)
            {
                return NotArchive(file);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                return Unreadable(file, e.Message);
            }
        }

        private static LocalPackageReading ReadArchive(ZipArchive zip, string file, long bytes, IReadOnlyList<InstalledMod> installed)
        {
            var entries = zip.Entries;
            if (entries.Count > MaxEntries)
                return Refuse("local.tooManyEntries", $"{file} holds {entries.Count} files, more than the {MaxEntries} BakaLoader will unpack from one zip.",
                    ("file", file), ("count", entries.Count), ("limit", MaxEntries));

            long declared = 0;
            foreach (var entry in entries)
            {
                declared += Math.Max(0, entry.Length);
                if (declared > MaxUnpackedBytes)
                    return Refuse("local.unpackedTooLarge", $"{file} would unpack to more than {Megabytes(MaxUnpackedBytes)} MB.",
                        ("file", file), ("limit", Megabytes(MaxUnpackedBytes)));
            }

            foreach (var entry in entries)
                if (IsUnsafeEntry(entry.FullName))
                    return Refuse("local.unsafeEntry", $"{file} holds a path that points outside the mod's folder ({entry.FullName}).",
                        ("file", file), ("entry", entry.FullName));

            var files = entries.Where(e => !IsIgnored(e.FullName) && !IsDirectoryEntry(e.FullName)).ToList();

            var manifest = files.FirstOrDefault(e => string.Equals(Normalize(e.FullName), ManifestName, StringComparison.OrdinalIgnoreCase));
            if (manifest == null)
            {
                var nested = files
                    .Select(e => Normalize(e.FullName))
                    .Where(n => n.EndsWith("/" + ManifestName, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(n => n.Count(c => c == '/'))
                    .FirstOrDefault();
                if (nested != null)
                {
                    var folder = nested.Substring(0, nested.Length - ManifestName.Length - 1);
                    return Refuse("local.manifestInFolder", $"{file} keeps manifest.json inside a folder ({folder}).",
                        ("file", file), ("folder", folder));
                }
                return Refuse("local.noManifest", $"{file} has no manifest.json at the top of the zip.", ("file", file));
            }

            ManifestFields fields;
            try
            {
                if (manifest.Length > MaxManifestBytes)
                    return BadManifest(file, "it is larger than 64 KB");
                using var stream = manifest.Open();
                using var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
                var buffer = new char[MaxManifestBytes];
                var count = reader.ReadBlock(buffer, 0, buffer.Length);
                fields = JsonConvert.DeserializeObject<ManifestFields>(new string(buffer, 0, count));
                if (fields == null) return BadManifest(file, "it is empty");
            }
            catch (JsonException e)
            {
                return BadManifest(file, e.Message);
            }
            catch (InvalidDataException)
            {
                return NotArchive(file);
            }

            var name = fields.Name?.Trim();
            var version = fields.VersionNumber?.Trim();
            if (string.IsNullOrEmpty(name) || !NamePattern.IsMatch(name)
                || string.IsNullOrEmpty(version) || !SemVer.IsReadable(version))
                return Refuse("local.manifestFields", $"The manifest.json in {file} needs a name and a version_number.", ("file", file));

            if (!files.Any(e => Normalize(e.FullName).EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
                return Refuse("local.noDll", $"{file} holds no .dll file, so there is nothing for BepInEx to load.", ("file", file));

            // The loader and the parts BakaLoader looks after itself are never a mod from a zip.
            if (IsReservedName(name))
                return Reserved(name);

            // Who publishes it. Never guessed: a folder under an owner nobody named is a second
            // folder for the same plugin, BepInEx loads both copies, and a Gale follow reads the
            // two as different mods. An installed folder with no owner in its name is a hand copy
            // nothing can be matched to, so it is not asked.
            var same = (installed ?? Array.Empty<InstalledMod>())
                .Where(m => m != null && !string.IsNullOrWhiteSpace(m.Author) && NamePattern.IsMatch(m.Author)
                            && string.Equals(m.ModName, name, StringComparison.OrdinalIgnoreCase))
                .GroupBy(m => m.Author, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();

            var shape = FileNameShape.Match(file);
            var fileOwner = shape.Success && string.Equals(shape.Groups["n"].Value, name, StringComparison.OrdinalIgnoreCase)
                ? shape.Groups["o"].Value
                : null;

            string owner, finalName, ownerFrom;
            // Two installed owners share the name: the zip's own file name may say which one it
            // updates. Anything short of that is a guess, and is refused.
            var named = same.Count > 1 && fileOwner != null
                ? same.FirstOrDefault(m => string.Equals(m.Author, fileOwner, StringComparison.OrdinalIgnoreCase))
                : null;
            if (same.Count > 1 && named == null)
            {
                var owners = string.Join(", ", same.Select(m => m.Author));
                return Refuse("local.ownerAmbiguous", $"More than one installed mod is called {name} ({owners}).",
                    ("name", name), ("owners", owners));
            }
            if (same.Count >= 1)
            {
                var have = named ?? same[0];
                if (fileOwner != null && !string.Equals(fileOwner, have.Author, StringComparison.OrdinalIgnoreCase))
                    return Refuse("local.ownerConflict", $"{name} is installed from {have.Author}, and this zip's name says {fileOwner}.",
                        ("name", name), ("installed", have.Author), ("owner", fileOwner));
                // The installed folder's own spelling, so a case-only difference is the same folder.
                owner = have.Author;
                finalName = have.ModName;
                ownerFrom = "installed";
            }
            else if (fileOwner != null)
            {
                owner = fileOwner;
                finalName = name;
                ownerFrom = "fileName";
            }
            else
            {
                return Refuse("local.noOwner", $"BakaLoader cannot tell who publishes {name}. Rename the zip to Owner-Name-Version.zip.",
                    ("name", name));
            }

            var folderName = $"{owner}-{finalName}";
            if (IsReservedFolder(folderName))
                return Reserved(folderName);

            return new LocalPackageReading
            {
                Facts = new LocalPackageFacts
                {
                    Owner = owner,
                    Name = finalName,
                    Version = version,
                    Dependencies = ModUpdateService.CleanDependencyList(fields.Dependencies),
                    FileName = file,
                    Bytes = bytes,
                    OwnerFrom = ownerFrom,
                },
            };
        }

        /// <summary>
        /// True for an entry that would land outside the folder it is unpacked into, or that names a
        /// stream rather than a file: a rooted path, a drive letter, any colon (x.dll:ads is an NTFS
        /// alternate stream) and any .. step, with either slash.
        /// </summary>
        internal static bool IsUnsafeEntry(string entryName)
        {
            if (string.IsNullOrEmpty(entryName)) return false;
            var n = entryName.Replace('\\', '/');
            if (n.StartsWith("/", StringComparison.Ordinal)) return true;
            if (n.IndexOf(':') >= 0) return true;
            if (Path.IsPathRooted(entryName)) return true;
            return n.Split('/').Any(part => part == "..");
        }

        /// <summary>
        /// Unpacks the checked copy into <paramref name="destination"/>, every entry held to the
        /// length the zip declares for it. ZipFile.ExtractToDirectory trusts that length: an entry
        /// that says it is a kilobyte and inflates to gigabytes would be written out whole. Here the
        /// copy stops one byte past the declared size and throws, the caller's finally removes the
        /// folder, and nothing has been placed. Each destination is checked to sit inside the folder
        /// again, though the entry scan has already turned down every entry that could leave it,
        /// and the two kinds of Mac leftover are not unpacked at all.
        /// </summary>
        internal static void ExtractTo(string zipPath, string destination)
        {
            var root = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(root);
            long total = 0;
            using var zip = ZipFile.OpenRead(zipPath);
            foreach (var entry in zip.Entries)
            {
                if (IsUnsafeEntry(entry.FullName))
                    throw new InvalidDataException($"The zip holds a path that points outside the mod's folder ({entry.FullName}).");
                if (IsIgnored(entry.FullName)) continue;

                var target = Path.GetFullPath(Path.Combine(root, Normalize(entry.FullName)));
                if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"The zip holds a path that points outside the mod's folder ({entry.FullName}).");
                if (IsDirectoryEntry(entry.FullName)) { Directory.CreateDirectory(target); continue; }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using var from = entry.Open();
                using var to = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);
                var buffer = new byte[81920];
                long written = 0;
                int read;
                while ((read = from.Read(buffer, 0, buffer.Length)) > 0)
                {
                    written += read;
                    total += read;
                    if (written > entry.Length || total > MaxUnpackedBytes)
                        throw new InvalidDataException($"{entry.FullName} in the zip is larger than the zip says it is, so it was not unpacked.");
                    to.Write(buffer, 0, read);
                }
            }
        }

        /// <summary>The two kinds of entry a Mac's zip carries that are not part of anything.</summary>
        internal static bool IsIgnored(string entryName)
        {
            var parts = Normalize(entryName).Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return true;
            return string.Equals(parts[0], "__MACOSX", StringComparison.OrdinalIgnoreCase)
                || string.Equals(parts[parts.Length - 1], ".DS_Store", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsDirectoryEntry(string entryName) => Normalize(entryName).EndsWith("/", StringComparison.Ordinal);

        private static string Normalize(string entryName) => (entryName ?? "").Replace('\\', '/').TrimStart('/');

        private static bool IsReservedName(string name) =>
            ModScanner.IsCompanionFolder(name)
            || string.Equals(name, BepInExService.DefaultPackageName, StringComparison.OrdinalIgnoreCase);

        private static bool IsReservedFolder(string folderName) =>
            ModScanner.IsCompanionFolder(folderName)
            || ModScanner.IsPluginsFrameworkFolder(folderName)
            || ModScanner.IsProtectedPatcher(folderName)
            || string.Equals(folderName, BepInExService.DefaultPackage, StringComparison.OrdinalIgnoreCase);

        private static Stream OpenForCopy(string path) =>
            new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan);

        private static bool IsSharingViolation(IOException e)
        {
            var code = e.HResult & 0xFFFF;
            return code == 32 || code == 33;
        }

        /// <summary>The file name part of a path the host pasted, without throwing on one that is malformed.</summary>
        internal static string SafeFileName(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            var cut = path.TrimEnd('\\', '/');
            var at = Math.Max(cut.LastIndexOf('\\'), cut.LastIndexOf('/'));
            return at >= 0 ? cut.Substring(at + 1) : cut;
        }

        private static long Megabytes(long bytes) => Math.Max(1, (bytes + (1024 * 1024) - 1) / (1024 * 1024));

        private static LocalPackageReading TooLarge(string file, long bytes, long cap) =>
            Refuse("local.tooLarge", $"{file} is {Megabytes(bytes)} MB, more than the {Megabytes(cap)} MB BakaLoader will install from one zip.",
                ("file", file), ("size", Megabytes(bytes)), ("limit", Megabytes(cap)));

        private static LocalPackageReading NotArchive(string file) =>
            Refuse("local.notArchive", $"{file} ends in .zip but is not a readable zip archive.", ("file", file));

        private static LocalPackageReading Unreadable(string file, string detail) =>
            Refuse("local.unreadable", $"{file} could not be read: {detail}", ("file", file), ("detail", detail));

        private static LocalPackageReading BadManifest(string file, string detail) =>
            Refuse("local.badManifest", $"The manifest.json in {file} could not be read: {detail}", ("file", file), ("detail", detail));

        private static LocalPackageReading Reserved(string mod) =>
            Refuse("local.reserved", $"{mod} is the loader or a part BakaLoader looks after itself.", ("mod", mod));

        private static LocalPackageReading Refuse(string reason, string sentence, params (string Key, object Value)[] values) => new()
        {
            Refusal = new LocalPackageRefusal
            {
                Reason = reason,
                Sentence = sentence,
                Params = values.ToDictionary(v => v.Key, v => v.Value),
            },
        };

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { /* a temp file; the caller tries again */ }
        }

        private sealed class ManifestFields
        {
            [JsonProperty("name")] public string Name { get; set; }
            [JsonProperty("version_number")] public string VersionNumber { get; set; }
            [JsonProperty("dependencies")] public string[] Dependencies { get; set; }
        }
    }
}
