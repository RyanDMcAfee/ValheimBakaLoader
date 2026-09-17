using System;

namespace ValheimBakaLoader.Tools
{
    /// <summary>
    /// Dependency-free version comparison following semantic versioning 2.0
    /// precedence, with the tolerances a mod index needs.
    /// <para>
    /// Tolerated: a leading 'v', missing components ("1.2" reads as 1.2.0), and a
    /// fourth numeric component, which is kept and used as a tie-break so 1.0.0.1
    /// sorts above 1.0.0. Build metadata (everything after a '+') carries no
    /// precedence and is dropped.
    /// </para>
    /// <para>
    /// Pre-release versions rank BELOW their own release, so 2.0.13-beta.1 is older
    /// than 2.0.13. Pre-release identifiers are compared left to right: all-numeric
    /// identifiers compare as numbers (beta.2 before beta.10) and rank below
    /// alphanumeric ones, which compare by ordinal text; when everything else is
    /// equal, the version with more identifiers is the higher one.
    /// </para>
    /// <para>
    /// A version that cannot be read at all sorts lowest and never counts as newer,
    /// so a folder with no readable version is never offered a download on the
    /// strength of a version nobody could parse. Nothing here throws.
    /// </para>
    /// </summary>
    public static class SemVer
    {
        /// <summary>
        /// Compares two version strings. Returns -1 when <paramref name="a"/> is
        /// lower, 1 when it is higher, and 0 when the two rank the same.
        /// Unreadable versions sort lowest, and two unreadable ones rank the same.
        /// </summary>
        public static int Compare(string a, string b)
        {
            var pa = Parse(a);
            var pb = Parse(b);

            if (!pa.Readable && !pb.Readable) return 0;
            if (!pa.Readable) return -1;
            if (!pb.Readable) return 1;

            var length = Math.Max(pa.Core.Length, pb.Core.Length);
            for (var i = 0; i < length; i++)
            {
                var va = i < pa.Core.Length ? pa.Core[i] : 0L;
                var vb = i < pb.Core.Length ? pb.Core[i] : 0L;

                if (va != vb) return va < vb ? -1 : 1;
            }

            return ComparePreRelease(pa.PreRelease, pb.PreRelease);
        }

        /// <summary>
        /// Returns true when <paramref name="latest"/> ranks strictly above
        /// <paramref name="installed"/>. A blank or unreadable
        /// <paramref name="latest"/> is never newer.
        /// </summary>
        public static bool IsNewer(string latest, string installed)
        {
            if (string.IsNullOrWhiteSpace(latest)) return false;
            if (string.IsNullOrWhiteSpace(installed)) return false;

            // An offer to replace what is installed has to rest on a version that
            // was actually read. Garbage sorts lowest, and lowest is never newer.
            if (!Parse(latest).Readable) return false;

            return Compare(latest, installed) > 0;
        }

        /// <summary>True when a version carries a pre-release suffix (1.2.3-beta.1).</summary>
        public static bool IsPreRelease(string version)
        {
            var parsed = Parse(version);
            return parsed.Readable && parsed.PreRelease.Length > 0;
        }

        /// <summary>True when a version could be read at all.</summary>
        public static bool IsReadable(string version) => Parse(version).Readable;

        /// <summary>
        /// Compares only the numeric core of two versions, ignoring any pre-release
        /// suffix, so 2.0.13-beta.1 and 2.0.13 answer 0. Used where the question is
        /// "which release line is this", not "which build is newer".
        /// </summary>
        public static int CompareCore(string a, string b)
        {
            var pa = Parse(a);
            var pb = Parse(b);

            if (!pa.Readable && !pb.Readable) return 0;
            if (!pa.Readable) return -1;
            if (!pb.Readable) return 1;

            var length = Math.Max(pa.Core.Length, pb.Core.Length);
            for (var i = 0; i < length; i++)
            {
                var va = i < pa.Core.Length ? pa.Core[i] : 0L;
                var vb = i < pb.Core.Length ? pb.Core[i] : 0L;

                if (va != vb) return va < vb ? -1 : 1;
            }

            return 0;
        }

        /// <summary>
        /// Semantic-versioning pre-release precedence. An absent suffix ranks above
        /// a present one; otherwise identifiers are weighed one at a time.
        /// </summary>
        private static int ComparePreRelease(string[] a, string[] b)
        {
            if (a.Length == 0 && b.Length == 0) return 0;
            if (a.Length == 0) return 1;   // a release outranks its own pre-release
            if (b.Length == 0) return -1;

            var length = Math.Min(a.Length, b.Length);
            for (var i = 0; i < length; i++)
            {
                var result = CompareIdentifier(a[i], b[i]);
                if (result != 0) return result;
            }

            // Everything shared matched, so the longer list of identifiers wins.
            if (a.Length == b.Length) return 0;
            return a.Length < b.Length ? -1 : 1;
        }

        /// <summary>
        /// One pre-release identifier against another: numbers compare as numbers
        /// and rank below text, text compares by ordinal.
        /// </summary>
        private static int CompareIdentifier(string a, string b)
        {
            var aNumeric = TryReadNumber(a, out var aValue);
            var bNumeric = TryReadNumber(b, out var bValue);

            if (aNumeric && bNumeric)
            {
                if (aValue == bValue) return 0;
                return aValue < bValue ? -1 : 1;
            }

            if (aNumeric) return -1;  // numeric identifiers rank below alphanumeric
            if (bNumeric) return 1;

            var text = string.CompareOrdinal(a, b);
            return text == 0 ? 0 : (text < 0 ? -1 : 1);
        }

        /// <summary>
        /// Reads an all-digit identifier as a number. Anything else, or a run of
        /// digits too long to hold, is treated as text.
        /// </summary>
        private static bool TryReadNumber(string value, out long number)
        {
            number = 0;
            if (string.IsNullOrEmpty(value)) return false;

            foreach (var c in value)
            {
                if (c < '0' || c > '9') return false;
            }

            return long.TryParse(value, out number);
        }

        private readonly struct Parsed
        {
            public Parsed(bool readable, long[] core, string[] preRelease)
            {
                Readable = readable;
                Core = core;
                PreRelease = preRelease;
            }

            public bool Readable { get; }

            public long[] Core { get; }

            public string[] PreRelease { get; }
        }

        private static readonly long[] NoCore = Array.Empty<long>();
        private static readonly string[] NoPreRelease = Array.Empty<string>();
        private static readonly Parsed Unreadable = new(false, NoCore, NoPreRelease);

        /// <summary>
        /// Splits a version into its numeric core and its pre-release identifiers.
        /// A version whose first component is not a number is unreadable; a later
        /// component that is not a number counts as 0, which keeps older tolerant
        /// behaviour for shapes like "1.2.x".
        /// </summary>
        private static Parsed Parse(string version)
        {
            if (string.IsNullOrWhiteSpace(version)) return Unreadable;

            var trimmed = version.Trim();

            if (trimmed.Length > 0 && (trimmed[0] == 'v' || trimmed[0] == 'V'))
            {
                trimmed = trimmed.Substring(1);
            }

            // Build metadata carries no precedence at all.
            var plusIndex = trimmed.IndexOf('+');
            if (plusIndex >= 0) trimmed = trimmed.Substring(0, plusIndex);

            var preRelease = NoPreRelease;
            var dashIndex = trimmed.IndexOf('-');
            if (dashIndex >= 0)
            {
                var suffix = trimmed.Substring(dashIndex + 1);
                trimmed = trimmed.Substring(0, dashIndex);

                if (suffix.Length > 0)
                {
                    preRelease = suffix.Split('.');
                }
            }

            if (trimmed.Length == 0) return Unreadable;

            var parts = trimmed.Split('.');
            if (!long.TryParse(parts[0], out var first) || first < 0) return Unreadable;

            var core = new long[parts.Length];
            core[0] = first;

            for (var i = 1; i < parts.Length; i++)
            {
                core[i] = long.TryParse(parts[i], out var value) && value > 0 ? value : 0L;
            }

            return new Parsed(true, core, preRelease);
        }
    }
}
