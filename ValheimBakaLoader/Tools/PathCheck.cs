using System;
using System.IO;

namespace ValheimBakaLoader.Tools
{
    /// <summary>
    /// What the disk said about one path, gathered in one place so the rule that reads it
    /// never has to touch a disk itself.
    /// <para>
    /// The split is the whole point. <see cref="PathCheck.Look"/> asks the filesystem the
    /// four or five questions there are to ask, and <see cref="PathCheck.Judge"/> turns the
    /// answers into the one sentence a host reads. Judge is pure, so every case a host can
    /// land in is a row in a table rather than a folder somebody had to create on a build
    /// machine, and the cases that are awkward to create on purpose (a folder that exists
    /// and refuses a write) are the ones that get tested rather than the ones that do not.
    /// </para>
    /// </summary>
    public readonly struct PathFacts
    {
        /// <summary>The path names a file that is there.</summary>
        public bool FileExists { get; init; }

        /// <summary>The path names a folder that is there.</summary>
        public bool DirectoryExists { get; init; }

        /// <summary>
        /// The path typed in the FOLDER box names a file that is there. Asked because the
        /// answer without it read as the opposite of what was on disk: a path with a file
        /// at it is not a folder and is not missing either, and the rule fell through to
        /// "the folder is not there yet, it is created the first time the server starts",
        /// which is a promise Windows cannot keep while a file of that name is sitting in
        /// the way.
        /// </summary>
        public bool PathIsFile { get; init; }

        /// <summary>
        /// The mirror, for the EXECUTABLE box: the path names a folder that is there.
        /// Same defect the other way round. A host who pastes the install folder rather
        /// than the program inside it was told the path "names another file", and a host
        /// whose folder happened to be called valheim_server.exe was told there was no
        /// file at a path with something plainly at it.
        /// </summary>
        public bool PathIsDirectory { get; init; }

        /// <summary>A <c>valheim_server_Data</c> folder sits beside the file.</summary>
        public bool SiblingDataFolder { get; init; }

        /// <summary>The folder one level up is there, so a missing folder could be created.</summary>
        public bool ParentExists { get; init; }

        /// <summary>The folder took a write. False for a folder that is not there yet.</summary>
        public bool Writable { get; init; }

        /// <summary>
        /// Windows will not take this path: a name in it uses a mark that is not allowed in
        /// a file name, or is one of the names Windows keeps for a device. Nothing else in
        /// the struct means anything when this is true.
        /// <para>
        /// Set by the written rule in <see cref="PathCheck.WindowsWillNotTake"/> rather than
        /// by a caught exception. On .NET 6 <c>File.Exists</c> and <c>Directory.Exists</c>
        /// never throw and <c>Path.GetFullPath</c> hands back <c>K:\a&lt;b&gt;c</c> without
        /// a word, so a path written that way used to come back as "there is no file at
        /// this path", which sends the reader looking for the wrong thing. The catches in
        /// <see cref="PathCheck.Look"/> stay behind the rule as a backstop.
        /// </para>
        /// </summary>
        public bool Unreadable { get; init; }
    }

    /// <summary>
    /// The answer <c>paths.check</c> hands the page: the path with its environment
    /// variables filled in, three plain facts, and at most one problem to say out loud.
    /// </summary>
    public sealed class PathCheckAnswer
    {
        /// <summary>The path with %VARIABLES% filled in, which is what the app will open.</summary>
        public string Expanded { get; init; } = "";

        /// <summary>The file or the folder is on disk right now.</summary>
        public bool Exists { get; init; }

        /// <summary>It is the thing it is supposed to be, not merely something that exists.</summary>
        public bool LooksRight { get; init; }

        /// <summary>A folder that took a write. Always false for the server executable.</summary>
        public bool Writable { get; init; }

        /// <summary>
        /// The catalog id of the one sentence to show under the field, or null when there is
        /// nothing to say. Never a refusal: the page shows it and carries on.
        /// </summary>
        public string ProblemId { get; init; }
    }

    /// <summary>
    /// The rules behind the line under each of the two Directories fields.
    /// <para>
    /// Nothing here ever refuses anything. A host typing a path is mid thought, and a
    /// server that will not start is something the start itself says; what this produces is
    /// one plain sentence under the box saying what is there and what is not, so the answer
    /// arrives while the path can still be fixed rather than at the next launch.
    /// </para>
    /// </summary>
    public static class PathCheck
    {
        /// <summary>The one file name a Valheim dedicated server is ever called.</summary>
        public const string ServerExeName = "valheim_server.exe";

        /// <summary>The folder the game's own managed assemblies sit in, beside the exe.</summary>
        public const string ServerDataFolderName = "valheim_server_Data";

        /// <summary>The two kinds of path this hall knows. Closed, like every other keyword
        /// the page is allowed to send.</summary>
        public const string KindExe = "exe";

        /// <inheritdoc cref="KindExe"/>
        public const string KindDir = "dir";

        /// <summary>The path was set for this one server.</summary>
        public const string SourceProfile = "profile";

        /// <summary>The path came from the app wide setting every server falls back to.</summary>
        public const string SourceDefault = "default";

        // The problem sentences, by id. Each one is a catalog entry under paths.problem, and
        // the id is the identity: the English beside it can be rewritten any day.
        public const string ProblemExeWrongName = "paths.problem.exe_wrong_name";
        public const string ProblemExeMissing = "paths.problem.exe_missing";
        public const string ProblemExeNoDataFolder = "paths.problem.exe_no_data_folder";
        public const string ProblemExeIsFolder = "paths.problem.exe_is_folder";
        public const string ProblemDirReadOnly = "paths.problem.dir_read_only";
        public const string ProblemDirWillBeCreated = "paths.problem.dir_will_be_created";
        public const string ProblemDirMissingParent = "paths.problem.dir_missing_parent";
        public const string ProblemDirIsFile = "paths.problem.dir_is_file";
        public const string ProblemUnreadable = "paths.problem.unreadable";
        public const string ProblemTimedOut = "paths.problem.timed_out";

        /// <summary>
        /// The answer for a path the disk did not come back about in time.
        /// <para>
        /// Every other answer here is the disk's. This one is the app's, and it exists
        /// because the questions in <see cref="Look"/> have no upper bound on how long
        /// they take: a share on a host that is not answering, or a drive that has spun
        /// down, can hold a single <c>Directory.Exists</c> for seconds. The caller waits
        /// its budget and then says so, which is a sentence under the box rather than a
        /// window that has stopped painting.
        /// </para>
        /// </summary>
        public static PathCheckAnswer CouldNotBeChecked(string expanded)
            => new PathCheckAnswer { Expanded = expanded ?? "", ProblemId = ProblemTimedOut };

        /// <summary>
        /// A path with its %VARIABLES% filled in, trimmed, and never a throw: a half typed
        /// path is the normal state of the box this reads, so a malformed one comes back as
        /// itself rather than as an exception nobody asked for.
        /// <para>
        /// A pair of double quotes around the whole thing comes off first. Explorer's own
        /// "Copy as path" wraps what it puts on the clipboard in quotes, which makes it the
        /// commonest way a path arrives in one of these boxes, and it arrived unusable: the
        /// quotes are part of the string, so the name no longer ended in
        /// <c>valheim_server.exe</c> and the line under the box said the file was the wrong
        /// one. The file name was right all along. A quote anywhere else is left where it
        /// is, and is one of the marks Windows will not take.
        /// </para>
        /// </summary>
        public static string Expand(string raw)
        {
            var trimmed = (raw ?? "").Trim();
            // Named rather than written twice as a literal: two of those on one line puts a
            // double quote on either side of an arithmetic minus, and the copy gate reads
            // the stretch between them as a sentence with a dash standing in it.
            const char quote = '"';
            if (trimmed.Length >= 2 && trimmed[0] == quote && trimmed[^1] == quote)
                trimmed = trimmed.Substring(1, trimmed.Length - 2).Trim();
            if (trimmed.Length == 0) return "";
            try { return Environment.ExpandEnvironmentVariables(trimmed); }
            catch { return trimmed; }
        }

        /// <summary>
        /// The path BakaLoader will really use for one server, and where it came from.
        /// A profile that names its own wins; anything else falls back to the app wide
        /// setting, which is where the first time setup writes its answers.
        /// </summary>
        public static (string Path, string Source) Effective(string profileValue, string appValue)
            => string.IsNullOrWhiteSpace(profileValue)
                ? (Expand(appValue), SourceDefault)
                : (Expand(profileValue), SourceProfile);

        /// <summary>
        /// The one sentence for a path, from facts somebody else gathered. Pure: hand it a
        /// row of a table and it answers the same way every time, on any machine.
        /// </summary>
        /// <param name="kind"><see cref="KindExe"/> or <see cref="KindDir"/>.</param>
        /// <param name="expanded">The path with its variables already filled in.</param>
        /// <param name="facts">What the disk said, or a row of a table.</param>
        public static PathCheckAnswer Judge(string kind, string expanded, PathFacts facts)
        {
            var path = expanded ?? "";

            // Nothing typed is not a problem: an empty box means "use the default", and the
            // note under the boxes says so once rather than under each of them.
            if (path.Trim().Length == 0)
                return new PathCheckAnswer { Expanded = "" };

            if (facts.Unreadable)
                return new PathCheckAnswer { Expanded = path, ProblemId = ProblemUnreadable };

            if (string.Equals(kind, KindExe, StringComparison.Ordinal))
            {
                // A folder first of all, because every sentence below this one is about a
                // file and would be describing something that is not one. Pasting the
                // install folder rather than the program inside it is the ordinary way to
                // get here, and being told the path "names another file" sends the reader
                // looking at the name they typed instead of at what it points to.
                if (facts.PathIsDirectory)
                    return new PathCheckAnswer
                    {
                        Expanded = path,
                        Exists = true,
                        ProblemId = ProblemExeIsFolder,
                    };

                // The name next, because a host who picked the game's own launcher rather
                // than the server's reads that before being told the file is missing: it is
                // there, it is simply not the file a dedicated server is started from.
                if (!path.EndsWith(ServerExeName, StringComparison.OrdinalIgnoreCase))
                    return new PathCheckAnswer
                    {
                        Expanded = path,
                        Exists = facts.FileExists,
                        ProblemId = ProblemExeWrongName,
                    };

                if (!facts.FileExists)
                    return new PathCheckAnswer { Expanded = path, ProblemId = ProblemExeMissing };

                // The exe on its own is half an install. The managed assemblies beside it are
                // what a launch actually loads, and a copied exe with no folder beside it
                // starts and dies with nothing on screen to explain why.
                if (!facts.SiblingDataFolder)
                    return new PathCheckAnswer
                    {
                        Expanded = path,
                        Exists = true,
                        ProblemId = ProblemExeNoDataFolder,
                    };

                return new PathCheckAnswer { Expanded = path, Exists = true, LooksRight = true };
            }

            if (!string.Equals(kind, KindDir, StringComparison.Ordinal))
                throw new ArgumentException($"Unknown path kind: {kind}", nameof(kind));

            if (facts.DirectoryExists)
                return facts.Writable
                    ? new PathCheckAnswer
                    {
                        Expanded = path, Exists = true, LooksRight = true, Writable = true,
                    }
                    : new PathCheckAnswer
                    {
                        Expanded = path, Exists = true, ProblemId = ProblemDirReadOnly,
                    };

            // A file sitting where the folder should be. Asked before "not there yet",
            // because it is not there yet and never will be: the server creates its save
            // folder on first start, and it cannot create one over a file of that name. The
            // answer without this arm was the cheerful one, which is the whole defect.
            if (facts.PathIsFile)
                return new PathCheckAnswer
                {
                    Expanded = path, Exists = true, ProblemId = ProblemDirIsFile,
                };

            // Not there yet is fine as long as the folder above it is: the server makes its
            // own save folder on first start. Saying so is worth a line, because a host who
            // has just typed a path wants to know it was understood.
            return facts.ParentExists
                ? new PathCheckAnswer
                {
                    Expanded = path, LooksRight = true, ProblemId = ProblemDirWillBeCreated,
                }
                : new PathCheckAnswer { Expanded = path, ProblemId = ProblemDirMissingParent };
        }

        /// <summary>
        /// The names Windows keeps for devices. A folder or a file called any of these
        /// cannot be created, whatever extension is put after it.
        /// </summary>
        private static readonly string[] ReservedNames =
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        };

        /// <summary>
        /// Whether Windows will refuse this path on sight: a mark in one of the names that
        /// is not allowed in a file name, or a name it keeps for a device.
        /// <para>
        /// Written out rather than left to a caught exception, because there is no exception
        /// to catch. .NET 6 stopped validating these: <c>Path.GetFullPath</c> answers
        /// <c>K:\a&lt;b&gt;c</c> with <c>K:\a&lt;b&gt;c</c>, <c>File.Exists</c> answers
        /// false, and the reader was told the file was missing rather than that the path
        /// could not be one. Driven against the real disk before this was written: every
        /// one of these rows reached the disk and came back as a missing file or a missing
        /// parent.
        /// </para>
        /// </summary>
        public static bool WindowsWillNotTake(string path)
        {
            var rest = path ?? "";

            // The two prefixes that are allowed to hold a mark from the list below: the
            // long path escape, and a drive letter's own colon.
            if (rest.StartsWith(@"\\?\", StringComparison.Ordinal)
                || rest.StartsWith(@"\\.\", StringComparison.Ordinal))
                rest = rest.Substring(4);
            if (rest.Length >= 2 && rest[1] == ':' && char.IsLetter(rest[0]))
                rest = rest.Substring(2);

            foreach (var c in rest)
                if (c < ' ' || c == '<' || c == '>' || c == '"' || c == '|'
                    || c == '?' || c == '*' || c == ':')
                    return true;

            foreach (var part in rest.Split('\\', '/'))
            {
                // A device name keeps its meaning whatever is put after the dot, so NUL.txt
                // is as impossible a file as NUL is.
                var dot = part.IndexOf('.');
                var bare = (dot >= 0 ? part.Substring(0, dot) : part).Trim();
                if (bare.Length == 0) continue;
                foreach (var reserved in ReservedNames)
                    if (string.Equals(bare, reserved, StringComparison.OrdinalIgnoreCase))
                        return true;
            }

            return false;
        }

        /// <summary>
        /// The facts, off the real disk. Every question is wrapped: an unreadable path is a
        /// fact of its own rather than a throw, because this is fed by a text box.
        /// <para>
        /// The written rule runs first and is what actually produces an unreadable answer.
        /// The catches at the bottom are a backstop for whatever a future framework does
        /// still throw, not the rule: on .NET 6 none of them fires for a path a host can
        /// type.
        /// </para>
        /// </summary>
        public static PathFacts Look(string kind, string expanded)
        {
            var path = (expanded ?? "").Trim();
            if (path.Length == 0) return new PathFacts();
            if (WindowsWillNotTake(path)) return new PathFacts { Unreadable = true };

            try
            {
                if (string.Equals(kind, KindExe, StringComparison.Ordinal))
                {
                    var fileExists = File.Exists(path);
                    var folder = Path.GetDirectoryName(Path.GetFullPath(path));
                    var sibling = !string.IsNullOrEmpty(folder)
                        && Directory.Exists(Path.Combine(folder, ServerDataFolderName));
                    return new PathFacts
                    {
                        FileExists = fileExists,
                        // Asked whatever the file answered: the two are never both true, and
                        // the rule needs to tell "there is nothing here" from "there is a
                        // folder here". Never asked when the file IS there, because that
                        // answer is already settled and this is a second trip to the disk.
                        PathIsDirectory = !fileExists && Directory.Exists(path),
                        SiblingDataFolder = sibling,
                    };
                }

                var full = Path.GetFullPath(path);
                var here = Directory.Exists(full);
                var parent = Path.GetDirectoryName(full);
                return new PathFacts
                {
                    DirectoryExists = here,
                    // The same question the other way round, and skipped for a folder that
                    // is there for the same reason.
                    PathIsFile = !here && File.Exists(full),
                    ParentExists = !string.IsNullOrEmpty(parent) && Directory.Exists(parent),
                    Writable = here && TakesAWrite(full),
                };
            }
            catch (ArgumentException) { return new PathFacts { Unreadable = true }; }
            catch (NotSupportedException) { return new PathFacts { Unreadable = true }; }
            catch (PathTooLongException) { return new PathFacts { Unreadable = true }; }
            catch (IOException) { return new PathFacts { Unreadable = true }; }
            catch (UnauthorizedAccessException) { return new PathFacts { Unreadable = true }; }
        }

        /// <summary>
        /// Whether a folder that is there will take a write, asked the only way Windows
        /// answers honestly: by writing.
        /// <para>
        /// SAY IT PLAINLY, because the call that reaches this is shaped like a read and is
        /// not one: the check CREATES AND DELETES A TEMPORARY FILE IN THE FOLDER THE HOST
        /// HAS TYPED, once each time the typing pauses. The file deletes itself when the
        /// handle closes, so nothing is left behind even if the app is killed between the
        /// two lines. Anyone reading <c>paths.check</c> as a read-only question about a
        /// path is reading it wrong, and so is anyone who reaches for this from a code
        /// path where writing into a folder somebody named would be a surprise.
        /// </para>
        /// <para>
        /// This runs on a worker rather than on the window's own thread, because a write
        /// into a folder on a share that is not answering has no upper bound on how long
        /// it takes. <see cref="Look"/> is called through a Task.Run with a budget over
        /// it; what that budget runs out on is this line.
        /// </para>
        /// <para>
        /// The rest of the reasoning, which has not changed: it CREATES A REAL FILE, once
        /// each time the typing pauses. It is named for BakaLoader so a reader who catches
        /// it in a folder listing knows what it is, it carries no content, and
        /// <c>DeleteOnClose</c> hands its removal to Windows rather than to a later line of
        /// ours. An end to end run over a temporary folder leaves zero entries behind, and
        /// the test above this one asserts exactly that. There is no read-only way to get
        /// the same answer: a permission set can allow everything a read can see and still
        /// refuse the write, which is the case worth warning about in the first place.
        /// </para>
        /// </summary>
        private static bool TakesAWrite(string folder)
        {
            var probe = Path.Combine(folder, ".bakaloader-write-probe-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using var handle = new FileStream(
                    probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1,
                    FileOptions.DeleteOnClose);
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// The whole answer for one path: expand it, look at the disk, judge what was found.
        /// </summary>
        public static PathCheckAnswer Check(string kind, string raw)
        {
            var expanded = Expand(raw);
            return Judge(kind, expanded, Look(kind, expanded));
        }
    }
}
