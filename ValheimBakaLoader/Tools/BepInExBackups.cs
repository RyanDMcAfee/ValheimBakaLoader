using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ValheimBakaLoader.Tools
{
    /// <summary>One folder of files a write replaced, and where the two halves of it are.</summary>
    public sealed class BepInExBackup
    {
        /// <summary>The stamp the folder is named after, "20260920-143000".</summary>
        public string Stamp { get; init; }

        /// <summary>The folder itself.</summary>
        public string Folder { get; init; }

        /// <summary>The core that was replaced, or null when this backup has none.</summary>
        public string CoreFolder { get; init; }

        /// <summary>The root loader files that were replaced, or null when there are none.</summary>
        public string RootFolder { get; init; }

        /// <summary>
        /// True for a backup written by 1.1.x, which kept only the core and kept it one level
        /// deeper. Those are still read and still pruned; nothing writes one any more.
        /// </summary>
        public bool Legacy { get; init; }

        /// <summary>
        /// True for the backup taken on the write that brought an install BakaLoader did NOT
        /// make into its care. It is worth more than the three after it: every other backup
        /// holds a loader BakaLoader itself wrote, and this one holds the host's own. It is
        /// never pruned.
        /// </summary>
        public bool Adoption { get; init; }

        /// <summary>The core version this backup holds, when the mark inside it recorded one.</summary>
        public string CoreVersion { get; init; }
    }

    /// <summary>
    /// The small file that marks the adoption backup, so "the BepInEx you had before
    /// BakaLoader" can be found again however many writes come after it.
    /// <para>
    /// A mark inside the folder rather than a list kept elsewhere: the folder is the thing a
    /// host copies, moves and looks at in Explorer, and a record of it anywhere else is one
    /// that goes stale the moment they do.
    /// </para>
    /// </summary>
    public static class BepInExAdoptionMark
    {
        public const string FileName = ".bakaloader-adoption.json";

        /// <summary>Writes the mark. Best effort: a mark that will not write is not a failed write.</summary>
        public static void Write(string backupFolder, DateTime takenUtc, string coreVersion)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(backupFolder)) return;
                Directory.CreateDirectory(backupFolder);

                File.WriteAllText(Path.Combine(backupFolder, FileName),
                    "{\"takenUtc\":\"" + takenUtc.ToString("o") + "\",\"coreVersion\":"
                    + (string.IsNullOrWhiteSpace(coreVersion) ? "null" : "\"" + coreVersion.Trim() + "\"")
                    + "}");
            }
            catch { /* best effort */ }
        }

        /// <summary>True when this folder is the adoption backup.</summary>
        public static bool On(string backupFolder)
        {
            try
            {
                return !string.IsNullOrWhiteSpace(backupFolder)
                    && File.Exists(Path.Combine(backupFolder, FileName));
            }
            catch { return false; }
        }

        /// <summary>The core version the mark recorded, or null when it said none.</summary>
        public static string CoreVersionIn(string backupFolder)
        {
            try
            {
                var path = Path.Combine(backupFolder ?? string.Empty, FileName);
                if (!File.Exists(path)) return null;

                var text = File.ReadAllText(path);
                var at = text.IndexOf("\"coreVersion\":\"", StringComparison.Ordinal);
                if (at < 0) return null;

                at += "\"coreVersion\":\"".Length;
                var end = text.IndexOf('"', at);
                return end <= at ? null : text.Substring(at, end - at);
            }
            catch { return null; }
        }
    }

    /// <summary>
    /// Where the files a write replaced are kept, and how many of them are worth keeping.
    /// <para>
    /// The folder lives under <c>BepInEx</c> rather than beside the server, so no mod scan and
    /// no pack ever reads it, and it is named with a leading dot so it sorts out of the way.
    /// One folder per write, stamped with the local time the write began, holding the whole of
    /// the core that was there (<c>core</c>) and every loose loader file that was overwritten
    /// or dropped (<c>root</c>).
    /// </para>
    /// <para>
    /// 1.1.x wrote <c>core\&lt;stamp&gt;</c> instead, with no root half at all. Those folders
    /// are still read here, because a host upgrading has them on disk and the one thing worse
    /// than an old layout is a restore that cannot see the backup it needs.
    /// </para>
    /// </summary>
    public static class BepInExBackups
    {
        /// <summary>The folder every backup sits in, under BepInEx.</summary>
        public const string DirName = BepInExService.BackupDirName;

        /// <summary>The name of the half holding the core that was replaced.</summary>
        public const string CoreHalf = "core";

        /// <summary>The name of the half holding the loose loader files that were replaced.</summary>
        public const string RootHalf = "root";

        /// <summary>The backup folder for one install, whether or not it exists yet.</summary>
        public static string RootOf(string baseDir)
        {
            if (string.IsNullOrWhiteSpace(baseDir)) return null;

            try { return Path.Combine(baseDir, "BepInEx", DirName); }
            catch { return null; }
        }

        /// <summary>
        /// Every backup an install holds, newest first, both layouts together. The stamps sort
        /// as text because the format they are written in sorts that way on purpose.
        /// </summary>
        public static IReadOnlyList<BepInExBackup> All(string baseDir)
        {
            var root = RootOf(baseDir);
            if (root == null || !Directory.Exists(root)) return Array.Empty<BepInExBackup>();

            var found = new List<BepInExBackup>();

            try
            {
                foreach (var folder in Directory.GetDirectories(root))
                {
                    var name = Path.GetFileName(folder);

                    // The old layout's container, which is not a backup itself: its children are.
                    if (string.Equals(name, CoreHalf, StringComparison.OrdinalIgnoreCase))
                    {
                        foreach (var old in Directory.GetDirectories(folder))
                            found.Add(new BepInExBackup
                            {
                                Stamp = Path.GetFileName(old),
                                Folder = old,
                                CoreFolder = old,
                                RootFolder = null,
                                Legacy = true,
                            });
                        continue;
                    }

                    var core = Path.Combine(folder, CoreHalf);
                    var loose = Path.Combine(folder, RootHalf);

                    found.Add(new BepInExBackup
                    {
                        Stamp = name,
                        Folder = folder,
                        CoreFolder = Directory.Exists(core) ? core : null,
                        RootFolder = Directory.Exists(loose) ? loose : null,
                        Adoption = BepInExAdoptionMark.On(folder),
                        CoreVersion = BepInExAdoptionMark.CoreVersionIn(folder),
                    });
                }
            }
            catch
            {
                // A backup folder that will not list is a backup folder with nothing in it,
                // as far as everything above here is concerned.
            }

            return found
                .OrderByDescending(b => b.Stamp, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// The newest backup that could actually be put back, which means one that holds a
        /// core. A root-only folder is a real backup of real files and is still pruned with
        /// the rest, but it is not something to restore an install from.
        /// </summary>
        public static BepInExBackup Newest(string baseDir)
            => All(baseDir).FirstOrDefault(b => b.CoreFolder != null
                && Directory.EnumerateFileSystemEntries(b.CoreFolder).Any());

        /// <summary>One named backup, or null when there is no such stamp.</summary>
        public static BepInExBackup Find(string baseDir, string stamp)
        {
            if (string.IsNullOrWhiteSpace(stamp)) return Newest(baseDir);

            return All(baseDir)
                .FirstOrDefault(b => string.Equals(b.Stamp, stamp.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// A fresh, empty folder for the write that is about to begin, and the stamp naming it.
        /// The folder is made now so the sentinel can name it before a single byte moves.
        /// </summary>
        public static BepInExBackup Begin(string baseDir, DateTime localNow)
        {
            var root = RootOf(baseDir)
                ?? throw new ArgumentException("A backup needs an install root.", nameof(baseDir));

            Directory.CreateDirectory(root);

            var stamp = localNow.ToString("yyyyMMdd-HHmmss");
            var folder = Path.Combine(root, stamp);

            var n = 2;
            while (Directory.Exists(folder))
            {
                stamp = localNow.ToString("yyyyMMdd-HHmmss") + "-" + n++;
                folder = Path.Combine(root, stamp);
            }

            Directory.CreateDirectory(folder);

            return new BepInExBackup
            {
                Stamp = stamp,
                Folder = folder,
                CoreFolder = Path.Combine(folder, CoreHalf),
                RootFolder = Path.Combine(folder, RootHalf),
            };
        }

        /// <summary>
        /// The backup holding the loader this host had before BakaLoader, when there is one.
        /// <para>
        /// The OLDEST marked one, not the newest. There is normally only ever one: after the
        /// first adoption the install carries a note and no later write is an adoption. A drift
        /// takes that note away, though, and the write that takes the install back is marked as
        /// an adoption too. What the host means by "my own BepInEx" is the one from before
        /// BakaLoader ever wrote here, which is the first of them.
        /// </para>
        /// </summary>
        public static BepInExBackup Adoption(string baseDir)
            => All(baseDir).LastOrDefault(b => b.Adoption && b.CoreFolder != null);

        /// <summary>
        /// Keeps the newest <paramref name="keep"/> backups and deletes the rest, counting both
        /// layouts as one list. The one just written is never a candidate, whatever its name
        /// sorts as, and neither is the ADOPTION backup: keeping three drops the oldest first,
        /// and the oldest is the only one that ever holds a loader BakaLoader did not write.
        /// After four ordinary writes the host's own original core was gone for good, which is
        /// the one copy they can never fetch again. Best effort throughout: a folder that will
        /// not delete is a tidiness problem and never a reason to fail a write that has already
        /// succeeded.
        /// </summary>
        public static void Prune(string baseDir, string keepStamp, int keep)
        {
            try
            {
                // ONE of them is protected, and it is the first adoption rather than every
                // adoption: a drift takes the note away and the write that takes the install
                // back is an adoption too, so protecting all of them would let a pair of tools
                // writing over each other fill the folder for ever. Any later one is an
                // ordinary backup and rolls over with the rest.
                var ownStamp = Adoption(baseDir)?.Stamp;

                var old = All(baseDir)
                    .Where(b => !string.Equals(b.Stamp, keepStamp, StringComparison.OrdinalIgnoreCase))
                    .Where(b => ownStamp == null
                        || !string.Equals(b.Stamp, ownStamp, StringComparison.OrdinalIgnoreCase))
                    .Skip(Math.Max(0, keep - 1))
                    .ToList();

                foreach (var backup in old)
                {
                    try { Directory.Delete(backup.Folder, recursive: true); }
                    catch { /* a backup that will not go is not worth failing a write over */ }
                }
            }
            catch { /* best effort */ }
        }
    }

    /// <summary>
    /// The mark a write leaves while it is in the middle of moving folders about, so a write
    /// that was interrupted can be finished the other way round next time.
    /// <para>
    /// The swap has one moment where the install has no core at all: the old one has been
    /// renamed into the backup and the new one has not been renamed in yet. It is a single
    /// rename wide, but a machine losing power inside it leaves a server that starts with no
    /// mods and nothing on disk saying why. So the file is written first, holds the stamp of
    /// the backup the old core went into, and is deleted once the note is written.
    /// </para>
    /// <para>
    /// It is held OPEN, with no sharing, for as long as the write runs. That is what tells a
    /// status read a second later apart from a status read after a crash: a sentinel nobody is
    /// holding is a write that did not finish, and one that will not open is a write that is
    /// still going.
    /// </para>
    /// </summary>
    /// <summary>
    /// A write mark somebody is holding.
    /// <para>
    /// Disposing it is the ordinary end of a write: the file goes and the install reads as
    /// settled. <see cref="Abandon"/> is the other end, for a write whose files are all in
    /// but whose NOTE would not write: the mark stays on disk, which is what tells every
    /// later read not to judge this install's files against a note that no longer describes
    /// them. Without it the next read would compare BakaLoader's own fresh loader with the
    /// old note, call it somebody else's work, and set the note aside, which is a one way
    /// door. The heal takes the mark off once the note can be put right.
    /// </para>
    /// </summary>
    public interface IBepInExWriteMark : IDisposable
    {
        /// <summary>Lets the file go without deleting it, so the mark stands.</summary>
        void Abandon();
    }

    public static class BepInExWriteSentinel
    {
        /// <summary>The file itself, under BepInEx beside the backups.</summary>
        public const string FileName = ".bakaloader-bepinex-writing";

        /// <summary>Where the sentinel for one install would be.</summary>
        public static string PathOf(string baseDir)
        {
            if (string.IsNullOrWhiteSpace(baseDir)) return null;

            try { return Path.Combine(baseDir, "BepInEx", FileName); }
            catch { return null; }
        }

        /// <summary>
        /// Writes the sentinel and keeps hold of it. Disposing it deletes the file, so the
        /// ordinary end of a write leaves nothing behind and every other end leaves the mark.
        /// </summary>
        public static IBepInExWriteMark Hold(string baseDir, string stamp)
        {
            var path = PathOf(baseDir)
                ?? throw new ArgumentException("A sentinel needs an install root.", nameof(baseDir));

            Directory.CreateDirectory(Path.GetDirectoryName(path));

            var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            var bytes = System.Text.Encoding.UTF8.GetBytes(stamp ?? string.Empty);
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush(flushToDisk: true);

            return new Holder(stream, path);
        }

        /// <summary>
        /// True when the mark is there AT ALL: a write running right now, or one that stopped
        /// and has not been put back yet.
        /// <para>
        /// This is what the drift check asks before it compares a note against the files.
        /// Either state means the loader files on disk are mid-move, and comparing them then
        /// would read BakaLoader's own half-done write as somebody else having taken the
        /// install over, drop ownership on the strength of it, and leave a healthy install
        /// permanently Outside. <see cref="StaleStamp"/> cannot answer this question: it says
        /// null both when there is no mark and when a write is holding one, which are the two
        /// opposite answers here.
        /// </para>
        /// </summary>
        public static bool Present(string baseDir)
        {
            var path = PathOf(baseDir);
            if (path == null) return false;

            try { return File.Exists(path); }
            catch { return false; }
        }

        /// <summary>
        /// The stamp of a write that did not finish, or null when there is nothing to finish:
        /// no sentinel at all, or one another write is holding open right now.
        /// </summary>
        public static string StaleStamp(string baseDir)
        {
            var path = PathOf(baseDir);
            if (path == null) return null;

            try
            {
                if (!File.Exists(path)) return null;

                // Opened with no sharing on purpose. A write still running holds the file with
                // no sharing of its own, so this throws and the answer is "nothing to finish".
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
                using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
                return reader.ReadToEnd().Trim();
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Removes the mark. Best effort: a file that will not go is not a failure.</summary>
        public static void Clear(string baseDir)
        {
            var path = PathOf(baseDir);
            if (path == null) return;

            try { if (File.Exists(path)) File.Delete(path); }
            catch { /* best effort */ }
        }

        private sealed class Holder : IBepInExWriteMark
        {
            private readonly FileStream Stream;
            private readonly string Path;

            public Holder(FileStream stream, string path)
            {
                Stream = stream;
                Path = path;
            }

            public void Dispose()
            {
                try { Stream.Dispose(); } catch { /* best effort */ }
                try { if (File.Exists(Path)) File.Delete(Path); } catch { /* best effort */ }
            }

            public void Abandon()
            {
                try { Stream.Dispose(); } catch { /* best effort */ }
            }
        }
    }
}
