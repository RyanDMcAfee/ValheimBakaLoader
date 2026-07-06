using System;

namespace ValheimBakaLoader.Tools
{
    /// <summary>
    /// Minimal, dependency-free semantic-version comparison helper. Tolerant of a
    /// leading 'v', missing components ("1.2" vs "1.2.0"), and extra components.
    /// Pre-release/build metadata (anything after a '-' or '+') is ignored for
    /// the purposes of "is this a newer release?" comparisons.
    /// </summary>
    public static class SemVer
    {
        /// <summary>
        /// Compares two version strings numerically component-by-component.
        /// Returns -1 if <paramref name="a"/> is older, 1 if newer, 0 if equal.
        /// Unparseable/empty versions sort as lowest.
        /// </summary>
        public static int Compare(string a, string b)
        {
            var pa = Parse(a);
            var pb = Parse(b);

            var length = Math.Max(pa.Length, pb.Length);
            for (var i = 0; i < length; i++)
            {
                var va = i < pa.Length ? pa[i] : 0;
                var vb = i < pb.Length ? pb[i] : 0;

                if (va != vb)
                {
                    return va < vb ? -1 : 1;
                }
            }

            return 0;
        }

        /// <summary>
        /// Returns true when <paramref name="latest"/> is a strictly higher
        /// version than <paramref name="installed"/>.
        /// </summary>
        public static bool IsNewer(string latest, string installed)
        {
            if (string.IsNullOrWhiteSpace(latest)) return false;
            if (string.IsNullOrWhiteSpace(installed)) return false;

            return Compare(latest, installed) > 0;
        }

        /// <summary>
        /// Parses a version string into its numeric components, stripping a leading
        /// 'v' and discarding any pre-release/build metadata. Non-numeric or empty
        /// input yields an empty array (which sorts as the lowest version).
        /// </summary>
        private static int[] Parse(string version)
        {
            if (string.IsNullOrWhiteSpace(version))
            {
                return Array.Empty<int>();
            }

            var trimmed = version.Trim();

            if (trimmed.Length > 0 && (trimmed[0] == 'v' || trimmed[0] == 'V'))
            {
                trimmed = trimmed.Substring(1);
            }

            // Drop pre-release ("-rc1") and build metadata ("+abc") suffixes.
            var dashIndex = trimmed.IndexOf('-');
            if (dashIndex >= 0) trimmed = trimmed.Substring(0, dashIndex);

            var plusIndex = trimmed.IndexOf('+');
            if (plusIndex >= 0) trimmed = trimmed.Substring(0, plusIndex);

            if (trimmed.Length == 0)
            {
                return Array.Empty<int>();
            }

            var parts = trimmed.Split('.');
            var result = new int[parts.Length];

            for (var i = 0; i < parts.Length; i++)
            {
                // Treat non-numeric or negative components as 0.
                result[i] = int.TryParse(parts[i], out var value) && value > 0 ? value : 0;
            }

            return result;
        }
    }
}
