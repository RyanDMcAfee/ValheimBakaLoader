using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ValheimBakaLoader.Tools
{
    /// <summary>What a note claims against what the install actually holds.</summary>
    public sealed class BepInExFileCheck
    {
        /// <summary>Files the note lists that are still there but no longer hold what it recorded.</summary>
        public IReadOnlyList<string> Changed { get; init; } = Array.Empty<string>();

        /// <summary>Files the note lists that are not on disk at all.</summary>
        public IReadOnlyList<string> Missing { get; init; } = Array.Empty<string>();

        /// <summary>True when somebody else wrote over the loader BakaLoader put here.</summary>
        public bool Drifted => Changed.Count > 0;
    }

    /// <summary>
    /// Whether the loader on disk is still the one BakaLoader wrote, and whether the pack it
    /// is about to fetch would change anything at all.
    /// <para>
    /// Both questions are asked over the same set of files, and the set is smaller than the
    /// note's own list on purpose. <c>BepInEx.cfg</c> and <c>doorstop_config.ini</c> are the
    /// host's files: BakaLoader writes the first one once and never again, and keeps the second
    /// one, so a host editing either is ordinary use and not somebody else taking the install
    /// over. Debug symbols and documentation beside an assembly (<c>.pdb</c>, <c>.xml</c>) are
    /// not what loads, and packs have added and dropped them between builds without the loader
    /// changing at all.
    /// </para>
    /// <para>
    /// What is left is the loader's identity: everything under <c>BepInEx\core</c> that is not
    /// a symbol or a doc file, <c>winhttp.dll</c>, and <c>.doorstop_version</c>. Those are the
    /// files that decide what actually runs, so those are the files worth comparing.
    /// </para>
    /// </summary>
    public static class BepInExIntegrity
    {
        /// <summary>Endings that ride along beside an assembly and never load anything.</summary>
        private static readonly string[] Ignored = { ".pdb", ".xml" };

        /// <summary>
        /// True when a path relative to the install root is one of the files that decide what
        /// the loader actually runs. Forward or backward slashes both read, because the note
        /// writes forward ones and the filesystem hands back backward ones.
        /// </summary>
        public static bool IsLoaderIdentity(string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath)) return false;

            var path = relativePath.Replace('\\', '/').TrimStart('/');

            if (Ignored.Any(ending => path.EndsWith(ending, StringComparison.OrdinalIgnoreCase))) return false;

            if (path.StartsWith("BepInEx/core/", StringComparison.OrdinalIgnoreCase)) return true;

            return string.Equals(path, "winhttp.dll", StringComparison.OrdinalIgnoreCase)
                || string.Equals(path, BepInExDoorstop.VersionFileName, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// The note read back against the disk. A file that is GONE and a file that CHANGED are
        /// two different things and are answered separately: one is an install to repair, the
        /// other is an install somebody else now owns.
        /// </summary>
        public static BepInExFileCheck Against(string baseDir, BepInExMarker marker)
        {
            if (string.IsNullOrWhiteSpace(baseDir) || marker?.Files == null || marker.Files.Count == 0)
                return new BepInExFileCheck();

            var changed = new List<string>();
            var missing = new List<string>();

            foreach (var entry in marker.Files)
            {
                if (entry == null || string.IsNullOrWhiteSpace(entry.Path)) continue;
                if (!IsLoaderIdentity(entry.Path)) continue;
                if (string.IsNullOrWhiteSpace(entry.Sha256)) continue;

                string full;
                try { full = Path.Combine(baseDir, entry.Path.Replace('/', Path.DirectorySeparatorChar)); }
                catch { continue; }

                if (!File.Exists(full))
                {
                    missing.Add(entry.Path);
                    continue;
                }

                var actual = BepInExService.Sha256Of(full);

                // A file that will not read is not evidence of anything: an antivirus holding
                // one open for a moment must not be read as another tool taking the install.
                if (actual == null) continue;

                if (!string.Equals(actual, entry.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
                    changed.Add(entry.Path);
            }

            return new BepInExFileCheck { Changed = changed, Missing = missing };
        }

        /// <summary>
        /// True when writing this pack would not change one byte the loader reads: the same
        /// core files with the same contents, the same <c>winhttp.dll</c> and the same
        /// <c>.doorstop_version</c>. The host's <c>doorstop_config.ini</c> is not part of the
        /// question, because it is kept either way.
        /// <para>
        /// The set is compared in BOTH directions. A core file on disk that the pack does not
        /// ship still loads, so an install carrying one is not the same install as the pack.
        /// </para>
        /// </summary>
        public static bool PackMatchesDisk(string loaderRoot, string baseDir)
        {
            if (string.IsNullOrWhiteSpace(loaderRoot) || string.IsNullOrWhiteSpace(baseDir)) return false;

            try
            {
                var packCore = Path.Combine(loaderRoot, "BepInEx", "core");
                var diskCore = Path.Combine(baseDir, "BepInEx", "core");
                if (!Directory.Exists(packCore) || !Directory.Exists(diskCore)) return false;

                var fromPack = CoreFiles(packCore);
                var fromDisk = CoreFiles(diskCore);

                if (fromPack.Count != fromDisk.Count) return false;

                foreach (var (relative, file) in fromPack)
                {
                    if (!fromDisk.TryGetValue(relative, out var onDisk)) return false;
                    if (!SameBytes(file, onDisk)) return false;
                }

                foreach (var loose in new[] { "winhttp.dll", BepInExDoorstop.VersionFileName })
                {
                    var packFile = Path.Combine(loaderRoot, loose);
                    var diskFile = Path.Combine(baseDir, loose);

                    // A file the pack does not ship is not a difference; one it ships and the
                    // install does not have is.
                    if (!File.Exists(packFile)) continue;
                    if (!File.Exists(diskFile)) return false;
                    if (!SameBytes(packFile, diskFile)) return false;
                }

                return true;
            }
            catch
            {
                // Anything that cannot be read cannot be called identical.
                return false;
            }
        }

        /// <summary>
        /// Every file under a core folder that decides what loads, keyed by its path inside
        /// that folder with forward slashes.
        /// </summary>
        private static Dictionary<string, string> CoreFiles(string coreDir)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var file in Directory.GetFiles(coreDir, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(coreDir, file).Replace('\\', '/');
                if (Ignored.Any(ending => relative.EndsWith(ending, StringComparison.OrdinalIgnoreCase))) continue;
                map[relative] = file;
            }

            return map;
        }

        private static bool SameBytes(string a, string b)
        {
            var one = BepInExService.Sha256Of(a);
            var two = BepInExService.Sha256Of(b);
            return one != null && two != null && string.Equals(one, two, StringComparison.OrdinalIgnoreCase);
        }
    }
}
