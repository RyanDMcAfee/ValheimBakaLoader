using System;
using System.IO;
using System.Linq;

namespace ValheimBakaLoader.Tools
{
    /// <summary>
    /// The folder a realm with its own worlds keeps them in: <c>&lt;base&gt;/servers/&lt;name&gt;</c>,
    /// made on disk and shaped like a save folder the game made itself.
    /// <para>
    /// WHY THIS IS NOT INSIDE THE BRIDGE ANY MORE. It is the line issue 17 was about. The
    /// base folder came out of the app wide preferences, which on every fresh install hold
    /// the literal string <c>%USERPROFILE%\AppData\LocalLow\IronGate\Valheim</c>, and
    /// <c>Path.Combine</c> plus <c>Directory.CreateDirectory</c> were handed it raw. A raw
    /// <c>%USERPROFILE%</c> is a FOLDER NAME, not a place: Windows anchored the whole thing
    /// at the working directory and made
    /// <c>&lt;install&gt;\%USERPROFILE%\AppData\LocalLow\IronGate\Valheim\servers\&lt;name&gt;</c>,
    /// a real directory with a percent sign in its name, and then the raw string was stored as
    /// the new realm's save folder so every later reader disagreed with every other one.
    /// </para>
    /// <para>
    /// Two rules, and both of them are the fix. The base is RESOLVED before anything is joined
    /// onto it, and what comes back is the resolved path, which is what gets stored: an
    /// isolated realm's folder is a concrete place, so it is written down as one. Stored paths
    /// a HOST typed keep their variables (see <see cref="PathCheck.Resolve"/>); this one was
    /// never typed by anybody.
    /// </para>
    /// </summary>
    public static class IsolatedSaveFolder
    {
        /// <summary>The folder every per-realm save folder sits in, under the base folder.</summary>
        public const string ServersFolderName = "servers";

        /// <summary>
        /// Valheim's own default save folder for this user, with no variable left in it. The
        /// fallback for an install whose app wide save folder has somehow been blanked.
        /// </summary>
        public static string ValheimDefault() => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "AppData", "LocalLow", "IronGate", "Valheim");

        /// <summary>
        /// The path a new realm's folder WOULD get, without touching the disk to make it: the
        /// resolved base, <c>servers</c>, and a name a file system will take, with a number
        /// added while the candidate is a folder that already holds something.
        /// <para>
        /// <paramref name="occupied"/> is the one question that reaches a disk, handed in so
        /// the numbering can be driven as a table.
        /// </para>
        /// <para>
        /// REFUSES rather than running out. The ceiling is here because the question is asked
        /// of a disk and a predicate that answers yes to everything would spin forever, but
        /// falling out of the loop at the ceiling was worse than the spin it was guarding
        /// against: it handed back <c>&lt;name&gt;-999</c>, a path the predicate had just said
        /// is occupied, and <see cref="Create"/> then called CreateDirectory on it and laid a
        /// new realm's save folder over somebody else's worlds. So the ceiling is a refusal in
        /// words, which is the honest answer: nothing under this folder can be made without a
        /// name, and the host is the one who can free one up.
        /// </para>
        /// </summary>
        public static string Plan(string baseSave, string profileName, Func<string, bool> occupied)
        {
            var resolved = PathCheck.Resolve(baseSave);
            if (resolved.Length == 0) resolved = ValheimDefault();

            var parent = Path.Combine(resolved, ServersFolderName);
            var safe = InstallIsolationService.MakeSafeName(profileName);
            var candidate = Path.Combine(parent, safe);

            var n = 2;
            while (occupied != null && occupied(candidate))
            {
                if (n >= NameCeiling)
                    throw new HostFacingException("paths.isolated.noFreeName",
                        "There is already a folder called '" + safe + "' under " + parent
                        + ", and every numbered name up to " + NameCeiling.ToString(
                            System.Globalization.CultureInfo.InvariantCulture)
                        + " is taken too, so no save folder was made. Give this server a different"
                        + " name, or tidy up the folders under that one.",
                        ("name", safe), ("folder", parent),
                        ("ceiling", NameCeiling.ToString(System.Globalization.CultureInfo.InvariantCulture)));

                candidate = Path.Combine(parent, safe + "-" + n++.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            return candidate;
        }

        /// <summary>
        /// How far the numbering goes before <see cref="Plan"/> refuses. Past any real install
        /// by a wide margin: it is the guard against a predicate that never says no, not a
        /// limit anybody is meant to reach.
        /// </summary>
        public const int NameCeiling = 1000;

        /// <summary>
        /// <see cref="Plan"/>, then made on disk and given the layout the game expects.
        /// Returns the resolved path, which is what the realm stores.
        /// </summary>
        public static string Create(string baseSave, string profileName)
        {
            var candidate = Plan(baseSave, profileName,
                path => Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any());

            Directory.CreateDirectory(candidate);

            // Shape it like a save folder the game made itself: worlds_local plus the cache/
            // sibling it drops "{world}_biomedatacache.bin" into on every world load.
            WorldStore.EnsureSaveFolderLayout(candidate);
            return candidate;
        }
    }
}
