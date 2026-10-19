using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ValheimBakaLoader.Forms
{
    /// <summary>
    /// The files the Configs hall may list, open and save, all of them under one realm's
    /// BepInEx/config folder.
    /// <para>
    /// Until 1.2.15 the hall read the top level of that folder and only the .cfg files in it.
    /// Mods keep more than that there now: a folder of their own, and .yaml, .json and .md files
    /// beside or inside it (issue 20). So the list walks the folder down and hands back paths
    /// relative to it, with forward slashes whatever Windows writes, and every read and write
    /// comes back through <see cref="Resolve"/>, which is the one door a page-supplied path
    /// goes through on its way to the disk.
    /// </para>
    /// <para>
    /// Pure file-system work with no window and no realm in it, so the rules can be driven
    /// from a test over a temp folder.
    /// </para>
    /// </summary>
    public static class ConfigFiles
    {
        /// <summary>
        /// The kinds of file the hall shows. Exactly the list the issue named, and no more: a
        /// .txt or a .dll under config is something a mod keeps for itself, not a setting the
        /// host is meant to edit.
        /// </summary>
        public static readonly IReadOnlyList<string> Extensions = new[] { ".cfg", ".yaml", ".yml", ".json", ".md" };

        /// <summary>
        /// How far down the walk goes. Deep enough for a mod's folder with a folder inside it
        /// and one more inside that, which is the deepest anything seen in the wild goes, and
        /// shallow enough that a folder looping back on itself cannot keep the list busy.
        /// </summary>
        public const int MaxDepth = 6;

        public static bool IsAllowedExtension(string path) =>
            Extensions.Any(e => (path ?? "").EndsWith(e, StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// Every listable file under <paramref name="configDir"/>, as a relative path with
        /// forward slashes, sorted by path. A folder or file that is a link (a junction or a
        /// symbolic link) is not followed or listed: what it points at is outside the folder
        /// the hall is about, or a second copy of something already on the list.
        /// </summary>
        public static List<string> List(string configDir)
        {
            var found = new List<string>();
            if (string.IsNullOrWhiteSpace(configDir) || !Directory.Exists(configDir)) return found;
            Walk(new DirectoryInfo(configDir), "", 0, found);
            found.Sort(ComparePaths);
            return found;
        }

        /// <summary>
        /// Folders first within a folder would hide the loose files under a long run of
        /// prefixes, so the order is plain path order, case ignored, with "/" sorting before
        /// any letter so a folder's own files stay together.
        /// </summary>
        public static int ComparePaths(string a, string b) =>
            string.Compare((a ?? "").Replace('/', '\u0001'), (b ?? "").Replace('/', '\u0001'),
                StringComparison.OrdinalIgnoreCase);

        private static void Walk(DirectoryInfo dir, string prefix, int depth, List<string> found)
        {
            FileSystemInfo[] entries;
            try { entries = dir.GetFileSystemInfos(); }
            catch (IOException) { return; }
            catch (UnauthorizedAccessException) { return; }

            foreach (var entry in entries)
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                if (entry is DirectoryInfo sub)
                {
                    if (depth + 1 < MaxDepth) Walk(sub, prefix + sub.Name + "/", depth + 1, found);
                    continue;
                }
                if (IsAllowedExtension(entry.Name)) found.Add(prefix + entry.Name);
            }
        }

        /// <summary>
        /// The full path a page-supplied relative path stands for, or an exception saying why
        /// it does not stand for one. The rules, every one of them checked here rather than
        /// trusted to the page:
        /// <list type="bullet">
        /// <item>not empty, not rooted (no drive, no leading slash, no UNC share);</item>
        /// <item>no ".." or "." segment and no empty one, whichever slash separates them;</item>
        /// <item>an extension from <see cref="Extensions"/>;</item>
        /// <item>still under the config folder once Windows has worked the path out;</item>
        /// <item>no folder or file on the way down that is a link, so a junction cannot carry
        /// a read or a write somewhere else;</item>
        /// <item>and, for a read and for a write alike, a file that already exists. A save
        /// never creates a file: the hall only edits what a mod has written.</item>
        /// </list>
        /// </summary>
        public static string Resolve(string configDir, string relative)
        {
            if (string.IsNullOrWhiteSpace(relative)) throw new ArgumentException("file is required");
            if (string.IsNullOrWhiteSpace(configDir))
                throw new InvalidOperationException("Server exe path is not configured");

            var rel = relative.Trim();
            if (rel.StartsWith("/") || rel.StartsWith("\\") || rel.Contains(':') || Path.IsPathRooted(rel))
                throw new ArgumentException($"Invalid config file name '{relative}'");

            var parts = rel.Split('/', '\\');
            if (parts.Any(s => s.Length == 0 || s == "." || s == ".." || s.Trim() != s || s.TrimEnd('.') != s))
                throw new ArgumentException($"Invalid config file name '{relative}'");
            if (parts.Any(s => s.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
                throw new ArgumentException($"Invalid config file name '{relative}'");
            if (!IsAllowedExtension(parts[parts.Length - 1]))
                throw new ArgumentException($"Invalid config file name '{relative}'");

            var root = Path.GetFullPath(configDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var full = Path.GetFullPath(Path.Combine(new[] { root }.Concat(parts).ToArray()));
            if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"Invalid config file name '{relative}'");

            // Each step below the config folder, the file included. The config folder itself
            // may be a junction (an isolated install can share it), and that one is the folder
            // the hall is about, so it is not held against the path.
            var step = root;
            foreach (var part in parts)
            {
                step = Path.Combine(step, part);
                FileAttributes attrs;
                try { attrs = File.GetAttributes(step); }
                catch (FileNotFoundException) { throw new FileNotFoundException($"No config file '{relative}'"); }
                catch (DirectoryNotFoundException) { throw new FileNotFoundException($"No config file '{relative}'"); }
                if ((attrs & FileAttributes.ReparsePoint) != 0)
                    throw new ArgumentException($"Invalid config file name '{relative}'");
            }

            if (!File.Exists(full)) throw new FileNotFoundException($"No config file '{relative}'");
            return full;
        }

        /// <summary>
        /// The config files that belong to one mod, picked out of <paramref name="files"/> (the
        /// relative paths <see cref="List"/> answers). A file belongs when its own name, with
        /// case, extension and every "_", "-" and space taken out, contains one of the mod's
        /// names said the same way: the mod's name, its "author.name" pair, or the name its
        /// manifest gives. BepInEx names a config after the plugin's GUID, so
        /// "com.someModAuthor.ModName.ExtraSettings.cfg" carries the name inside it. And a file
        /// whose FIRST folder is named after the mod's own package folder belongs whatever it is
        /// called, because that folder is the mod's.
        /// <para>
        /// A name shorter than three letters matches nothing on its own: "Ab" is inside half the
        /// files on any server.
        /// </para>
        /// </summary>
        public static List<string> MatchForMod(IEnumerable<string> files, string modName, string author,
            string manifestName, IEnumerable<string> packageFolders)
        {
            var names = new List<string>();
            void Add(string value)
            {
                var n = Squash(value);
                if (n.Length >= 3 && !names.Contains(n)) names.Add(n);
            }
            Add(modName);
            Add(manifestName);
            if (!string.IsNullOrWhiteSpace(author) && !string.IsNullOrWhiteSpace(modName))
                Add(author + "." + modName);

            var folders = (packageFolders ?? Enumerable.Empty<string>())
                .Where(f => !string.IsNullOrWhiteSpace(f))
                .Select(f => f.Trim())
                .ToList();

            var matched = new List<string>();
            foreach (var file in files ?? Enumerable.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(file)) continue;
                var parts = file.Split('/', '\\');
                var leaf = parts[parts.Length - 1];
                var stem = Path.GetFileNameWithoutExtension(leaf);
                var squashed = Squash(stem);
                var byName = names.Any(n => squashed.Contains(n));
                var byFolder = parts.Length > 1
                    && folders.Any(f => string.Equals(f, parts[0], StringComparison.OrdinalIgnoreCase));
                if (byName || byFolder) matched.Add(file);
            }
            matched.Sort(ComparePaths);
            return matched;
        }

        /// <summary>Lower case, with "_", "-" and spaces taken out. Dots stay, so the author
        /// and the name of an "author.name" pair cannot run together into some other word.</summary>
        public static string Squash(string value) =>
            new string((value ?? "").ToLowerInvariant().Where(c => c != '_' && c != '-' && c != ' ').ToArray());
    }
}
