using System;
using System.IO;
using System.Linq;

namespace ValheimBakaLoader.Tools
{
    /// <summary>
    /// The host's <c>doorstop_config.ini</c>, read rather than assumed.
    /// <para>
    /// This file is what actually decides which assembly the game loads at start, and it is
    /// the one loader file that is genuinely the host's: r2modman, Gale and Thunderstore Mod
    /// Manager all keep their mod set in a profile folder of their own and point
    /// <c>target_assembly</c> at it. Writing the pack's own copy over that silently swaps a
    /// host's whole mod set for an empty one, with nothing on screen saying so. So the file is
    /// read before anything is written, and what it says decides whether BakaLoader touches
    /// this install at all.
    /// </para>
    /// <para>
    /// Two generations are in the wild. Doorstop 4 writes <c>[General]</c> with
    /// <c>target_assembly</c>; Doorstop 3 writes <c>[UnityDoorstop]</c> with
    /// <c>targetAssembly</c>. Both spellings are read, because an install that has been
    /// sitting on a server for two years is very often still on the old one.
    /// </para>
    /// </summary>
    public static class BepInExDoorstop
    {
        /// <summary>The loader file itself, at the root of the install.</summary>
        public const string FileName = "doorstop_config.ini";

        /// <summary>The file the pack ships naming which Doorstop generation it is.</summary>
        public const string VersionFileName = ".doorstop_version";

        /// <summary>Doorstop 4's section header.</summary>
        public const string ModernSection = "General";

        /// <summary>Doorstop 3's section header.</summary>
        public const string LegacySection = "UnityDoorstop";

        /// <summary>The assembly a correct BepInEx install beside the server points at.</summary>
        public const string ExpectedTargetRelative = @"BepInEx\core\BepInEx.Preloader.dll";

        /// <summary>
        /// The raw text of the target assembly setting, exactly as the file spells it, or null
        /// when there is no such file or no such key. Raw, because the point of showing it to a
        /// host is to show them what their own file says.
        /// </summary>
        public static string TargetOf(string installRoot)
        {
            var path = PathOf(installRoot);
            if (path == null) return null;

            try
            {
                if (!File.Exists(path)) return null;
                return TargetInText(File.ReadAllText(path));
            }
            catch
            {
                // A file that will not read says nothing, and saying nothing is not an accusation.
                return null;
            }
        }

        /// <summary>The same read, from text that is already in hand.</summary>
        public static string TargetInText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;

            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#' || line[0] == ';' || line[0] == '[') continue;

                var equals = line.IndexOf('=');
                if (equals <= 0) continue;

                var key = line.Substring(0, equals).Trim();
                if (!string.Equals(key, "target_assembly", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(key, "targetAssembly", StringComparison.OrdinalIgnoreCase))
                    continue;

                var value = line.Substring(equals + 1).Trim();
                return value.Length == 0 ? null : value;
            }

            return null;
        }

        /// <summary>
        /// The section header the file uses, <see cref="ModernSection"/> or
        /// <see cref="LegacySection"/>, or null when it has neither. The FIRST header wins,
        /// because that is the one the loader itself reads.
        /// </summary>
        public static string SectionInText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;

            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length < 3 || line[0] != '[') continue;

                var close = line.IndexOf(']');
                if (close <= 1) continue;

                var name = line.Substring(1, close - 1).Trim();
                if (string.Equals(name, ModernSection, StringComparison.OrdinalIgnoreCase)) return ModernSection;
                if (string.Equals(name, LegacySection, StringComparison.OrdinalIgnoreCase)) return LegacySection;
            }

            return null;
        }

        /// <summary>The section header of the file at an install root, or null.</summary>
        public static string SectionOf(string installRoot)
        {
            var path = PathOf(installRoot);
            if (path == null) return null;

            try
            {
                return File.Exists(path) ? SectionInText(File.ReadAllText(path)) : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// True when the target assembly points somewhere OTHER than
        /// <c>&lt;install&gt;\BepInEx\core\BepInEx.Preloader.dll</c>, which is what a mod
        /// manager's profile folder looks like. A relative path resolves against the install
        /// root, which is where the game's working directory is. A missing file, an unreadable
        /// one, or one with no target at all is NOT driven elsewhere: silence is not evidence,
        /// and a plain install that has never had a doorstop config is the commonest case of
        /// all.
        /// </summary>
        public static bool DrivenElsewhere(string installRoot, string target)
        {
            if (string.IsNullOrWhiteSpace(installRoot) || string.IsNullOrWhiteSpace(target)) return false;

            try
            {
                var expected = Path.GetFullPath(Path.Combine(installRoot, ExpectedTargetRelative));
                var actual = Path.GetFullPath(Path.Combine(installRoot, target.Trim().Trim('"')));

                return !string.Equals(
                    expected.TrimEnd(Path.DirectorySeparatorChar),
                    actual.TrimEnd(Path.DirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                // A target that will not even resolve as a path is not something to write over.
                return true;
            }
        }

        /// <summary>
        /// True when the host's config cannot drive the loader the pack is about to put in, so
        /// the pack's own copy has to go in and the host's goes to the backup. Two things say
        /// so and nothing else does: the Doorstop major the pack ships differs from the one on
        /// disk, or the section header differs. Everything else about that file is the host's
        /// own tuning and is kept.
        /// </summary>
        public static bool GenerationChanged(
            string packDoorstopVersion, string diskDoorstopVersion,
            string packSection, string diskSection)
        {
            var packMajor = MajorOf(packDoorstopVersion);
            var diskMajor = MajorOf(diskDoorstopVersion);
            if (packMajor != null && diskMajor != null && packMajor != diskMajor) return true;

            if (!string.IsNullOrWhiteSpace(packSection) && !string.IsNullOrWhiteSpace(diskSection)
                && !string.Equals(packSection, diskSection, StringComparison.OrdinalIgnoreCase))
                return true;

            return false;
        }

        /// <summary>
        /// The leading numeric component of a <c>.doorstop_version</c> file's contents, or null
        /// when there is not one. "4.4.0" reads as "4"; anything unreadable reads as nothing,
        /// and nothing never counts as a difference.
        /// </summary>
        public static string MajorOf(string doorstopVersion)
        {
            if (string.IsNullOrWhiteSpace(doorstopVersion)) return null;

            var text = doorstopVersion.Trim().TrimStart('v', 'V');
            var digits = new string(text.TakeWhile(char.IsDigit).ToArray());
            return digits.Length == 0 ? null : digits;
        }

        /// <summary>The contents of a <c>.doorstop_version</c> file, or null.</summary>
        public static string VersionTextIn(string folder)
        {
            if (string.IsNullOrWhiteSpace(folder)) return null;

            try
            {
                var path = Path.Combine(folder, VersionFileName);
                return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
            }
            catch
            {
                return null;
            }
        }

        private static string PathOf(string installRoot)
        {
            if (string.IsNullOrWhiteSpace(installRoot)) return null;

            try { return Path.Combine(installRoot, FileName); }
            catch { return null; }
        }
    }
}
