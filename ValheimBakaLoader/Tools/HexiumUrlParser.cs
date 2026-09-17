using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace ValheimBakaLoader.Tools
{
    /// <summary>
    /// A Hexium package reference parsed from a pasted link: owner, package name,
    /// and an optional pinned version. A null <see cref="Version"/> means "whichever
    /// version BakaLoader works out is the highest".
    /// </summary>
    public class HexiumModReference
    {
        public string Owner { get; init; }

        public string Name { get; init; }

        public string Version { get; init; }

        /// <summary>The folder this package installs into, "Owner-Name".</summary>
        public string FolderName => $"{Owner}-{Name}";
    }

    /// <summary>
    /// Reads the link shapes Hexium hands out for a package:
    ///
    ///   https://valheim.hexium.gg/mods/{owner}/{name}            (package page)
    ///   https://valheim.hexium.gg/mods/{owner}/{name}/{version}  (a named version)
    ///   https://valheim.hexium.gg/mods/{owner}/{name}/v/{version}
    ///
    /// Any name under hexium.gg is accepted as the host, because the site is split by
    /// game across sub-domains. Nothing outside hexium.gg parses here at all.
    /// <para>
    /// Owners on Hexium are not limited the way Thunderstore teams are: they carry
    /// hyphens and dots (bruceirons-team, Kurios.ZeuS), so the owner rule is wider
    /// than the Thunderstore parser's. Package names are letters, digits and
    /// underscores, as they are on both sites.
    /// </para>
    /// <para>
    /// A CDN address (cdn.hexium.gg/upload/...) is deliberately turned down: those
    /// addresses are opaque numbers with no owner or name in them, so there is
    /// nothing in one to look a package up by, and BakaLoader will not download a
    /// zip it cannot name a package for.
    /// </para>
    /// </summary>
    public static class HexiumUrlParser
    {
        // Hexium owners: letters, digits, underscore, hyphen and dot.
        private static readonly Regex OwnerPattern = new(@"^[A-Za-z0-9_.\-]{1,128}$", RegexOptions.Compiled);

        // Package names: letters, digits and underscores, as on Thunderstore.
        private static readonly Regex NamePattern = new(@"^\w{1,128}$", RegexOptions.Compiled);

        // A version number, with the pre-release suffix Hexium packages sometimes carry.
        private static readonly Regex VersionPattern =
            new(@"^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.\-]{1,64})?$", RegexOptions.Compiled);

        /// <summary>
        /// True when a pasted address points at hexium.gg at all, whatever shape the
        /// rest of it is. Used to decide which parser a pasted link belongs to before
        /// either one has judged it.
        /// </summary>
        public static bool LooksLikeHexiumLink(string input)
        {
            var host = ReadHost(input);
            return host != null && HexiumClient.IsHexiumHost(host);
        }

        public static bool TryParse(string input, out HexiumModReference reference, out string error)
        {
            reference = null;
            error = null;

            var text = (input ?? "").Trim();
            if (text.Length == 0)
            {
                error = "Paste a Hexium link first.";
                return false;
            }

            // Query strings and fragments carry nothing we read.
            var cut = text.IndexOfAny(new[] { '?', '#' });
            if (cut >= 0) text = text.Substring(0, cut);

            var schemeSplit = text.Split(new[] { "://" }, 2, StringSplitOptions.None);
            if (schemeSplit.Length != 2)
            {
                error = "That does not look like a link (it is missing https://).";
                return false;
            }

            var scheme = schemeSplit[0].ToLowerInvariant();
            if (scheme != "http" && scheme != "https")
            {
                error = $"Unsupported link type \"{scheme}://\". Paste the mod's page address on Hexium.";
                return false;
            }

            var tokens = schemeSplit[1]
                .Split('/')
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .ToArray();

            if (tokens.Length == 0 || !HexiumClient.IsHexiumHost(tokens[0]))
            {
                error = "That is not a hexium.gg link.";
                return false;
            }

            var path = tokens.Skip(1).ToArray();

            if (path.Length >= 1 && path[0].Equals("upload", StringComparison.OrdinalIgnoreCase))
            {
                error = "That is a file address, not a mod page. Paste the mod's page address on Hexium.";
                return false;
            }

            // https://{sub}.hexium.gg/mods/{owner}/{name}[/{version} | /v/{version}]
            if (path.Length >= 3 && path[0].Equals("mods", StringComparison.OrdinalIgnoreCase))
            {
                string version = null;
                if (path.Length >= 5 && path[3].Equals("v", StringComparison.OrdinalIgnoreCase))
                {
                    version = path[4];
                }
                else if (path.Length >= 4 && VersionPattern.IsMatch(path[3]))
                {
                    version = path[3];
                }

                return Build(path[1], path[2], version, out reference, out error);
            }

            error = "Could not find an owner and a mod in that link. Paste the mod's page address on Hexium.";
            return false;
        }

        private static bool Build(string owner, string name, string version,
            out HexiumModReference reference, out string error)
        {
            reference = null;
            error = null;

            if (!IsOwnerSegment(owner) || !IsNameSegment(name))
            {
                error = $"\"{owner}/{name}\" does not look like a Hexium owner and mod pair.";
                return false;
            }

            if (!string.IsNullOrWhiteSpace(version) && !VersionPattern.IsMatch(version.Trim()))
            {
                error = $"\"{version}\" does not look like a version number (something like 2.0.11).";
                return false;
            }

            reference = new HexiumModReference
            {
                Owner = owner.Trim(),
                Name = name.Trim(),
                Version = string.IsNullOrWhiteSpace(version) ? null : version.Trim(),
            };
            return true;
        }

        /// <summary>
        /// True when a value is usable as the owner half of a Hexium address. A run
        /// of nothing but dots is refused: it passes the character rule and then
        /// walks the path somewhere else once a browser works the address out.
        /// </summary>
        public static bool IsOwnerSegment(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;

            var trimmed = value.Trim();
            if (trimmed.Trim('.').Length == 0) return false;

            return OwnerPattern.IsMatch(trimmed);
        }

        /// <summary>True when a value is usable as the package-name half of an address.</summary>
        public static bool IsNameSegment(string value) =>
            !string.IsNullOrWhiteSpace(value) && NamePattern.IsMatch(value.Trim());

        /// <summary>True when a value has the shape of a version number.</summary>
        public static bool IsVersionSegment(string value) =>
            !string.IsNullOrWhiteSpace(value) && VersionPattern.IsMatch(value.Trim());

        /// <summary>
        /// The address of a package's own page on Hexium, or null when either half of
        /// the identity is missing or is not a plain segment. BakaLoader builds this
        /// itself and never takes one from the page, so opening it can only ever land
        /// on Hexium.
        /// </summary>
        public static string PageUrl(string owner, string name)
        {
            if (!IsOwnerSegment(owner) || !IsNameSegment(name)) return null;

            return "https://valheim.hexium.gg/mods/" + owner.Trim() + "/" + name.Trim();
        }

        /// <summary>
        /// True when an address is one BakaLoader will fetch a package from: https, and
        /// a host that is hexium.gg itself or a name under it at a label boundary.
        /// Everything else is turned down, "nothexium.gg" and "hexium.gg.example.com"
        /// included. Checked on the address the index gave AND again on wherever a
        /// redirect leads, so a hop off the site stops the download rather than
        /// following it.
        /// </summary>
        public static bool IsDownloadAddress(string url, out Uri uri)
        {
            uri = null;
            if (string.IsNullOrWhiteSpace(url)) return false;
            if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var parsed)) return false;
            if (parsed.Scheme != Uri.UriSchemeHttps) return false;
            if (!HexiumClient.IsHexiumHost(parsed.Host)) return false;

            uri = parsed;
            return true;
        }

        /// <summary>The host of a pasted address, or null when there is not one to read.</summary>
        private static string ReadHost(string input)
        {
            var text = (input ?? "").Trim();
            if (text.Length == 0) return null;

            var cut = text.IndexOfAny(new[] { '?', '#' });
            if (cut >= 0) text = text.Substring(0, cut);

            var schemeSplit = text.Split(new[] { "://" }, 2, StringSplitOptions.None);
            if (schemeSplit.Length != 2) return null;

            var rest = schemeSplit[1];
            var slash = rest.IndexOf('/');
            var host = slash >= 0 ? rest.Substring(0, slash) : rest;

            // Drop any user-info and port so "user@hexium.gg:443" reads as the host.
            var at = host.LastIndexOf('@');
            if (at >= 0) host = host.Substring(at + 1);

            var colon = host.IndexOf(':');
            if (colon >= 0) host = host.Substring(0, colon);

            return host.Length == 0 ? null : host;
        }
    }
}
