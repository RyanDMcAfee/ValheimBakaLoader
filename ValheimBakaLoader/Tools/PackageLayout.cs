using System;
using System.Collections.Generic;

namespace ValheimBakaLoader.Tools
{
    /// <summary>Which BepInEx folder a file from a package lands in.</summary>
    public enum PackageFileKind
    {
        /// <summary>Nothing to place: a directory entry, an empty name, or a path that walks up.</summary>
        Ignored,
        Plugins,
        Patchers,
        Monomod,
        Core,
        Config,
    }

    /// <summary>
    /// Where one file out of a Thunderstore package belongs under BepInEx, by the rule r2modman
    /// and Gale share.
    /// <para>
    /// Before 1.2.15 the whole zip was unpacked as it came into <c>plugins/Author-Name/</c>. A
    /// package that ships <c>plugins/X.dll</c> landed as <c>plugins/Author-Name/plugins/X.dll</c>,
    /// which BepInEx still loads because it walks plugins recursively, but a <c>patchers/</c>
    /// folder never reached <c>BepInEx/patchers</c> (so a patcher package did nothing at all) and
    /// a <c>config/</c> folder never reached <c>BepInEx/config</c> (so the mod wrote its defaults
    /// over the settings the author shipped). A host holding the server against a Gale profile
    /// saw every folder differ.
    /// </para>
    /// <para>
    /// Ported from Gale's <c>SubdirInstaller::map_file</c> with the BepInEx table
    /// (<c>plugins</c>, <c>patchers</c>, <c>monomod</c> or any <c>*.mm.dll</c>, <c>core</c>,
    /// <c>config</c>; plugins is the default). The first path component that names one of
    /// those, ignoring case, decides; everything before it is dropped and everything after it
    /// is kept. A file with no such component goes into the package's plugins folder by its
    /// file name alone. That is Gale's flatten, and it is why
    /// <c>Assets/Translations/English/t.json</c> lands as <c>plugins/Author-Name/t.json</c>.
    /// The package's own manifest.json, icon.png, README.md and CHANGELOG.md are root files
    /// with no trigger, so the same default puts them at the root of the plugins folder, which
    /// is where the Mods page reads the version from.
    /// </para>
    /// Pure: no disk, no clock.
    /// </summary>
    public static class PackageLayout
    {
        /// <summary>
        /// The trigger names in Gale's order. The order matters only for a component that could
        /// match two rows, which in practice is a folder named like a monomod file.
        /// </summary>
        private static readonly (string Name, PackageFileKind Kind)[] Triggers =
        {
            ("plugins", PackageFileKind.Plugins),
            ("patchers", PackageFileKind.Patchers),
            ("monomod", PackageFileKind.Monomod),
            ("core", PackageFileKind.Core),
            ("config", PackageFileKind.Config),
        };

        /// <summary>The suffix that sends a file to monomod wherever it sits, matched as Gale matches it (ordinal).</summary>
        private const string MonomodSuffix = ".mm.dll";

        /// <summary>
        /// The root files a package carries about itself. r2modman keeps them in the package's
        /// plugins folder and so does BakaLoader: manifest.json is where a scan reads the version.
        /// </summary>
        public static readonly IReadOnlyList<string> PackageMetadataFiles = new[]
        {
            "manifest.json", "icon.png", "README.md", "CHANGELOG.md",
        };

        /// <summary>The folder under BepInEx a kind lands in, or null for <see cref="PackageFileKind.Ignored"/>.</summary>
        public static string FolderOf(PackageFileKind kind) => kind switch
        {
            PackageFileKind.Plugins => "plugins",
            PackageFileKind.Patchers => "patchers",
            PackageFileKind.Monomod => "monomod",
            PackageFileKind.Core => "core",
            PackageFileKind.Config => "config",
            _ => null,
        };

        /// <summary>
        /// The destination of one package entry, relative to the BepInEx folder and written with
        /// forward slashes (<c>plugins/Author-Name/X.dll</c>, <c>config/a.cfg</c>), and the kind
        /// of folder it lands in. A directory entry, an empty path, or a path with a <c>..</c> in
        /// it answers <c>(null, Ignored)</c>: the archive was already unpacked into a temporary
        /// folder by an extractor that refuses an entry escaping it, and nothing that reaches
        /// here should walk upward either.
        /// </summary>
        public static (string Destination, PackageFileKind Kind) Map(string entryPath, string packageFolderName)
        {
            if (string.IsNullOrWhiteSpace(entryPath) || string.IsNullOrWhiteSpace(packageFolderName))
                return (null, PackageFileKind.Ignored);

            var normalized = entryPath.Replace('\\', '/');
            if (normalized.EndsWith("/", StringComparison.Ordinal))
                return (null, PackageFileKind.Ignored);

            var parts = new List<string>();
            foreach (var part in normalized.Split('/'))
            {
                if (part.Length == 0 || part == ".") continue;
                if (part == "..") return (null, PackageFileKind.Ignored);
                parts.Add(part);
            }

            if (parts.Count == 0) return (null, PackageFileKind.Ignored);

            for (var i = 0; i < parts.Count; i++)
            {
                var kind = TriggerOf(parts[i]);
                if (kind == PackageFileKind.Ignored) continue;

                // Gale's "defaulted" case: the trigger was the last component, which only a
                // *.mm.dll file or a file literally named like a trigger can be. The file then
                // goes in by its own name.
                var after = parts.GetRange(i + 1, parts.Count - (i + 1));
                var rest = after.Count > 0 ? string.Join('/', after) : parts[i];

                return (Join(kind, packageFolderName, rest), kind);
            }

            // No trigger anywhere: the default subdir (plugins), flattened to the file name.
            return (Join(PackageFileKind.Plugins, packageFolderName, parts[parts.Count - 1]), PackageFileKind.Plugins);
        }

        /// <summary>
        /// True when an entry is one of the package's own root files (manifest.json and the
        /// rest of <see cref="PackageMetadataFiles"/>, sitting at the top of the archive).
        /// </summary>
        public static bool IsRootMetadata(string entryPath)
        {
            if (string.IsNullOrWhiteSpace(entryPath)) return false;

            var normalized = entryPath.Replace('\\', '/').TrimStart('/');
            if (normalized.IndexOf('/') >= 0) return false;

            foreach (var name in PackageMetadataFiles)
                if (string.Equals(name, normalized, StringComparison.OrdinalIgnoreCase)) return true;

            return false;
        }

        private static PackageFileKind TriggerOf(string component)
        {
            foreach (var (name, kind) in Triggers)
            {
                if (string.Equals(name, component, StringComparison.OrdinalIgnoreCase)) return kind;

                if (kind == PackageFileKind.Monomod && component.EndsWith(MonomodSuffix, StringComparison.Ordinal))
                    return kind;
            }

            return PackageFileKind.Ignored;
        }

        private static string Join(PackageFileKind kind, string packageFolderName, string rest)
        {
            // config is Gale's one untracked subdir: shared by every mod, no package folder.
            return kind == PackageFileKind.Config
                ? FolderOf(kind) + "/" + rest
                : FolderOf(kind) + "/" + packageFolderName + "/" + rest;
        }
    }
}
