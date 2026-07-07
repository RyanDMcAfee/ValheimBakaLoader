using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace ValheimBakaLoader.Tools
{
    /// <summary>
    /// A Thunderstore package reference parsed from a user-pasted link:
    /// owner (namespace/team), package name, and an optional pinned version.
    /// A null <see cref="Version"/> means "install the latest".
    /// </summary>
    public class ThunderstoreModReference
    {
        public string Owner { get; init; }
        public string Name { get; init; }
        public string Version { get; init; }

        public string FolderName => $"{Owner}-{Name}";
    }

    /// <summary>
    /// Parses every link shape Thunderstore hands out for a package into a
    /// single {owner, name, version?} reference:
    ///
    ///   https://thunderstore.io/c/{community}/p/{owner}/{name}/           (page, latest)
    ///   https://thunderstore.io/c/{community}/p/{owner}/{name}/versions   (versions page, latest)
    ///   https://thunderstore.io/c/{community}/p/{owner}/{name}/v/{ver}/   (version page, pinned)
    ///   https://old.thunderstore.io/package/{owner}/{name}/               (legacy page, latest)
    ///   https://old.thunderstore.io/package/{owner}/{name}/versions/      (legacy versions, latest)
    ///   https://old.thunderstore.io/package/{owner}/{name}/{ver}/         (legacy version page, pinned)
    ///   https://{any}.thunderstore.io/package/download/{owner}/{name}/{ver}/  (direct download, pinned)
    ///   ror2mm://v1/install/thunderstore.io/{owner}/{name}/{ver}/         (mod-manager protocol, pinned)
    ///
    /// Trailing slashes, surrounding whitespace, query strings, and fragments
    /// are all tolerated. Case of the host/scheme is ignored; owner/name keep
    /// their original casing (Thunderstore folder names are case-sensitive-ish
    /// on disk but the API lookup is case-insensitive).
    /// </summary>
    public static class ThunderstoreUrlParser
    {
        // Thunderstore team + package names: letters, digits, underscores.
        private static readonly Regex NamePattern = new(@"^\w+$", RegexOptions.Compiled);

        // Package versions are strict semver-shaped triplets (e.g. 5.4.2333).
        private static readonly Regex VersionPattern = new(@"^\d+\.\d+\.\d+$", RegexOptions.Compiled);

        public static bool TryParse(string input, out ThunderstoreModReference reference, out string error)
        {
            reference = null;
            error = null;

            var text = (input ?? "").Trim();
            if (text.Length == 0)
            {
                error = "Paste a Thunderstore link first.";
                return false;
            }

            // Strip query string / fragment.
            var cut = text.IndexOfAny(new[] { '?', '#' });
            if (cut >= 0) text = text.Substring(0, cut);

            // Split "scheme://rest" - accepts https, http, and ror2mm.
            var schemeSplit = text.Split(new[] { "://" }, 2, StringSplitOptions.None);
            if (schemeSplit.Length != 2)
            {
                error = "That doesn't look like a link (missing https:// or ror2mm://).";
                return false;
            }

            var scheme = schemeSplit[0].ToLowerInvariant();
            var tokens = schemeSplit[1]
                .Split('/')
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .ToArray();

            if (scheme == "ror2mm")
            {
                // ror2mm://v1/install/thunderstore.io/{owner}/{name}/{version}/
                if (tokens.Length >= 5
                    && tokens[0].Equals("v1", StringComparison.OrdinalIgnoreCase)
                    && tokens[1].Equals("install", StringComparison.OrdinalIgnoreCase)
                    && tokens[2].EndsWith("thunderstore.io", StringComparison.OrdinalIgnoreCase))
                {
                    return Build(tokens[3], tokens[4], tokens.Length >= 6 ? tokens[5] : null,
                        out reference, out error);
                }

                error = "Unrecognized ror2mm link - expected ror2mm://v1/install/thunderstore.io/{owner}/{mod}/{version}/.";
                return false;
            }

            if (scheme != "http" && scheme != "https")
            {
                error = $"Unsupported link type \"{scheme}://\" - paste a thunderstore.io or ror2mm:// link.";
                return false;
            }

            if (tokens.Length == 0 || !tokens[0].ToLowerInvariant().EndsWith("thunderstore.io"))
            {
                error = "Not a thunderstore.io link.";
                return false;
            }

            var path = tokens.Skip(1).ToArray();

            // https://thunderstore.io/c/{community}/p/{owner}/{name}[/versions | /v/{version}]
            if (path.Length >= 5
                && path[0].Equals("c", StringComparison.OrdinalIgnoreCase)
                && path[2].Equals("p", StringComparison.OrdinalIgnoreCase))
            {
                string version = null;
                if (path.Length >= 7 && path[5].Equals("v", StringComparison.OrdinalIgnoreCase))
                    version = path[6];
                return Build(path[3], path[4], version, out reference, out error);
            }

            if (path.Length >= 3 && path[0].Equals("package", StringComparison.OrdinalIgnoreCase))
            {
                // https://{domain}/package/download/{owner}/{name}/{version}/
                if (path[1].Equals("download", StringComparison.OrdinalIgnoreCase))
                {
                    if (path.Length >= 5)
                        return Build(path[2], path[3], path[4], out reference, out error);

                    error = "Download links need owner, mod name, and version - e.g. /package/download/denikson/BepInExPack_Valheim/5.4.2333/.";
                    return false;
                }

                // https://old.thunderstore.io/package/{owner}/{name}[/versions | /{version}]
                if (path.Length >= 3)
                {
                    string version = null;
                    if (path.Length >= 4
                        && !path[3].Equals("versions", StringComparison.OrdinalIgnoreCase)
                        && VersionPattern.IsMatch(path[3]))
                    {
                        version = path[3];
                    }
                    return Build(path[1], path[2], version, out reference, out error);
                }
            }

            error = "Couldn't find an owner/mod in that link - paste the mod's Thunderstore page or download link.";
            return false;
        }

        private static bool Build(string owner, string name, string version,
            out ThunderstoreModReference reference, out string error)
        {
            reference = null;
            error = null;

            if (string.IsNullOrWhiteSpace(owner) || !NamePattern.IsMatch(owner)
                || string.IsNullOrWhiteSpace(name) || !NamePattern.IsMatch(name))
            {
                error = $"\"{owner}/{name}\" doesn't look like a valid Thunderstore owner/mod pair.";
                return false;
            }

            if (!string.IsNullOrWhiteSpace(version) && !VersionPattern.IsMatch(version))
            {
                error = $"\"{version}\" doesn't look like a Thunderstore version (expected e.g. 5.4.2333).";
                return false;
            }

            reference = new ThunderstoreModReference
            {
                Owner = owner,
                Name = name,
                Version = string.IsNullOrWhiteSpace(version) ? null : version,
            };
            return true;
        }
    }
}
