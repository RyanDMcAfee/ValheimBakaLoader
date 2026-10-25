using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ValheimBakaLoader.Tools
{
    /// <summary>One character on this PC, as the test copy dialog lists it.</summary>
    public sealed class CharacterEntry
    {
        /// <summary>
        /// characters_local or characters, the folder under the Valheim save folder, or
        /// "cloud:" and a Steam account id for a character in that account's Steam Cloud folder.
        /// </summary>
        public string Folder { get; init; }

        /// <summary>
        /// "local" for a character the game keeps in its own save folder, "cloud" for one Steam
        /// keeps in Steam Cloud, so the dialog can say which is which.
        /// </summary>
        public string Source { get; init; }

        /// <summary>The file's own name, "ragnar.fch".</summary>
        public string File { get; init; }

        /// <summary>The name inside the file, or null when the file could not be read.</summary>
        public string Name { get; init; }

        /// <summary>The name the copy would carry, or null when the file could not be read.</summary>
        public string CopyName { get; init; }

        /// <summary>The file the copy would be written to, or null when the file could not be read.</summary>
        public string CopyFile { get; init; }

        /// <summary>A file of that name is already there, so the copy would be refused.</summary>
        public bool CopyExists { get; init; }

        /// <summary>Why the file could not be read (a CharacterFile problem id), or null.</summary>
        public string Problem { get; init; }
    }

    /// <summary>A character copy that landed.</summary>
    public sealed class CharacterCopyResult
    {
        /// <summary>The folder under the Valheim save folder the copy was written into.</summary>
        public string Folder { get; init; }
        public string File { get; init; }
        public string Name { get; init; }
        public string SourceName { get; init; }
        public string Path { get; init; }
    }

    /// <summary>
    /// The character half of a test copy. Lists the characters this PC's game keeps and
    /// writes a renamed copy of one, so the host plays a test realm with a copy and the main
    /// character never sets foot in it.
    /// <para>
    /// The characters are the game client's, not the server's, and they live in three kinds of
    /// place. characters_local under the client's save folder holds the ones saved on this PC
    /// only, and characters beside it is the older folder an earlier game kept. A character kept
    /// in Steam Cloud is not under the save folder at all: Steam keeps its copy on this PC under
    /// its own install, in userdata\&lt;account&gt;\892970\remote\characters, one folder for
    /// every Steam account that has played here. A host who plays on Steam Cloud had every
    /// character in that last place, and the dialog said there were none.
    /// </para>
    /// <para>
    /// All of them are read and nothing under any of them is ever changed. A copy of a local
    /// character is written beside it; a copy of a Steam Cloud character is written into
    /// characters_local, never into Steam's folder, so Steam is never handed a file it did not
    /// make and the copy is a plain local character the game lists like any other. The copy is a
    /// new file and only ever a new file: it is opened with CreateNew, so a file that is already
    /// there is refused rather than replaced, even when it appears between the check and the
    /// write.
    /// </para>
    /// </summary>
    public static class CharacterCopy
    {
        /// <summary>What a copy's name ends in. The same suffix a test realm and its world carry.</summary>
        public const string Suffix = "_bakaT";

        /// <summary>The two folders the game keeps characters in, under its save folder.</summary>
        public static readonly IReadOnlyList<string> Folders = new[] { "characters_local", "characters" };

        /// <summary>Where a character the game keeps in its own save folder is listed from.</summary>
        public const string LocalSource = "local";

        /// <summary>Where a character Steam keeps in Steam Cloud is listed from.</summary>
        public const string CloudSource = "cloud";

        /// <summary>What a Steam Cloud folder's name starts with: "cloud:" and the Steam account id.</summary>
        public const string CloudFolderPrefix = "cloud:";

        /// <summary>The folder under the save folder a copy of a Steam Cloud character is written into.</summary>
        public const string CloudCopyFolder = "characters_local";

        /// <summary>Every character file in the two save folders, sorted by name. The overload adds Steam Cloud.</summary>
        public static IReadOnlyList<CharacterEntry> List(string saveRoot) => List(saveRoot, null);

        /// <summary>
        /// Every character file in the two save folders and in each Steam Cloud folder under
        /// <paramref name="steamRoot"/>, sorted by name. Backups the game keeps beside a character
        /// (name_backup_auto-..., .old, .new) are not characters and are left out. A null Steam
        /// folder lists the save folder only.
        /// </summary>
        public static IReadOnlyList<CharacterEntry> List(string saveRoot, string steamRoot)
        {
            var found = new List<CharacterEntry>();
            if (string.IsNullOrWhiteSpace(saveRoot)) return found;

            foreach (var folder in Folders)
            {
                var dir = Path.Combine(saveRoot, folder);
                ReadFolder(found, dir, folder, LocalSource, dir);
            }

            // A copy of a Steam Cloud character lands in characters_local, so that is where
            // "already there" is asked.
            foreach (var (account, dir) in CloudFolders(steamRoot))
                ReadFolder(found, dir, CloudFolderPrefix + account, CloudSource, Path.Combine(saveRoot, CloudCopyFolder));

            return found
                .OrderBy(c => c.Name ?? c.File, StringComparer.OrdinalIgnoreCase)
                .ThenBy(c => c.Folder, StringComparer.Ordinal)
                .ToList();
        }

        /// <summary>
        /// Every Steam account on this PC that has a Steam Cloud character folder for Valheim, by
        /// account id, with that folder. Several accounts can have played on one PC and each one
        /// that has the folder is listed; an account without it never saved a character here.
        /// Never throws.
        /// </summary>
        public static IReadOnlyList<(string Account, string Dir)> CloudFolders(string steamRoot)
        {
            var found = new List<(string Account, string Dir)>();
            if (string.IsNullOrWhiteSpace(steamRoot)) return found;

            var userdata = Path.Combine(steamRoot, "userdata");
            if (!Directory.Exists(userdata) || IsLink(userdata)) return found;

            List<string> accounts;
            try { accounts = Directory.EnumerateDirectories(userdata).ToList(); }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { return found; }

            foreach (var accountDir in accounts)
            {
                var account = Path.GetFileName(accountDir);
                if (!IsAccountId(account)) continue;
                var dir = CloudDir(steamRoot, account);
                if (Directory.Exists(dir) && !IsLink(dir)) found.Add((account, dir));
            }

            return found.OrderBy(f => f.Account, StringComparer.Ordinal).ToList();
        }

        /// <summary>
        /// Steam's own folder on this PC: the one the Steam client names in the registry (the key
        /// the server finder already reads), else the folder Steam installs to when it is not told
        /// otherwise. Null when neither is there. Read only, and never throws.
        /// </summary>
        public static string FindSteamRoot()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
                var named = (key?.GetValue("SteamPath") as string)?.Replace('/', Path.DirectorySeparatorChar);
                if (!string.IsNullOrWhiteSpace(named) && Directory.Exists(named)) return named;
            }
            catch (Exception) { /* no Steam key, or no right to read it: the usual folder is next */ }

            try
            {
                var usual = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam");
                return Directory.Exists(usual) ? usual : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>One account's Steam Cloud character folder for Valheim.</summary>
        private static string CloudDir(string steamRoot, string account) =>
            Path.Combine(steamRoot, "userdata", account, Properties.Resources.ValheimSteamAppId, "remote", "characters");

        /// <summary>A Steam account folder's name, which is its account id: digits only.</summary>
        private static bool IsAccountId(string name) =>
            !string.IsNullOrEmpty(name) && name.Length <= 20 && name.All(c => c >= '0' && c <= '9');

        /// <summary>The account a "cloud:" folder names, or null when the folder is not one.</summary>
        private static string CloudAccountOf(string folder)
        {
            if (folder == null || !folder.StartsWith(CloudFolderPrefix, StringComparison.Ordinal)) return null;
            var account = folder.Substring(CloudFolderPrefix.Length);
            return IsAccountId(account) ? account : null;
        }

        /// <summary>
        /// Adds every character file in <paramref name="dir"/> to the list under the folder name
        /// the page hands back, asking whether its copy is already in <paramref name="copyDir"/>.
        /// </summary>
        private static void ReadFolder(List<CharacterEntry> found, string dir, string folder, string source, string copyDir)
        {
            if (!Directory.Exists(dir) || IsLink(dir)) return;

            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(dir, "*.fch").ToList(); }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { return; }

            foreach (var path in files)
            {
                var file = Path.GetFileName(path);
                if (!IsCharacterFileName(file)) continue;

                string problem = null, name = null, copyName = null, copyFile = null;
                var exists = false;
                try
                {
                    if (IsLink(path)) problem = CharacterFile.NotACharacter;
                    else if (new FileInfo(path).Length > CharacterFile.MaxBytes) problem = CharacterFile.NotACharacter;
                    else if (CharacterFile.TryRead(System.IO.File.ReadAllBytes(path), out var info, out var why))
                    {
                        name = info.Name;
                        copyName = info.Name + Suffix;
                        copyFile = CharacterFile.FileNameFor(copyName);
                        exists = PathTaken(Path.Combine(copyDir, copyFile));
                    }
                    else problem = why;
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                    problem = CharacterFile.NotACharacter;
                }

                found.Add(new CharacterEntry
                {
                    Folder = folder,
                    Source = source,
                    File = file,
                    Name = name,
                    CopyName = copyName,
                    CopyFile = copyFile,
                    CopyExists = exists,
                    Problem = problem,
                });
            }
        }

        /// <summary>A copy of a character in one of the two save folders. The overload adds Steam Cloud.</summary>
        public static CharacterCopyResult Copy(string saveRoot, string folder, string file) => Copy(saveRoot, null, folder, file);

        /// <summary>
        /// Writes a copy of <paramref name="file"/> in <paramref name="folder"/> with the name
        /// <c>name_bakaT</c>, under the file name the game would give that name: beside it for a
        /// character in the save folder, and into characters_local for a character in a Steam
        /// Cloud folder under <paramref name="steamRoot"/>. Refuses, by name and before anything
        /// is written, a folder that is none of those, a file name with a path in it, a file that
        /// is not there, one that cannot be read as a character, and a copy that is already there.
        /// </summary>
        public static CharacterCopyResult Copy(string saveRoot, string steamRoot, string folder, string file)
        {
            folder = (folder ?? string.Empty).Trim();
            file = (file ?? string.Empty).Trim();

            var cloudAccount = string.IsNullOrWhiteSpace(steamRoot) ? null : CloudAccountOf(folder);
            if (string.IsNullOrWhiteSpace(saveRoot)
                || (!Folders.Contains(folder, StringComparer.Ordinal) && cloudAccount == null)
                || !IsCharacterFileName(file)
                || !string.Equals(Path.GetFileName(file), file, StringComparison.Ordinal))
                throw new HostFacingException("characters.copy.badRef",
                    "That is not a character file BakaLoader can copy.", ("file", file));

            // A Steam Cloud character is read where Steam keeps it and its copy goes into the
            // save folder's characters_local; any other character is copied beside itself.
            var dir = cloudAccount != null ? CloudDir(steamRoot, cloudAccount) : Path.Combine(saveRoot, folder);
            var copyFolder = cloudAccount != null ? CloudCopyFolder : folder;
            var copyDir = Path.Combine(saveRoot, copyFolder);
            var source = Path.Combine(dir, file);
            if (!System.IO.File.Exists(source) || IsLink(dir) || IsLink(source))
                throw new HostFacingException("characters.copy.missing",
                    $"There is no character file '{file}' on this PC any more, so nothing was copied.", ("file", file));

            byte[] bytes;
            try
            {
                if (new FileInfo(source).Length > CharacterFile.MaxBytes)
                    throw new HostFacingException("characters.copy.unreadable",
                        $"'{file}' could not be read as a Valheim character, so nothing was copied.", ("file", file));
                bytes = System.IO.File.ReadAllBytes(source);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                throw new HostFacingException("characters.copy.unreadable",
                    $"'{file}' could not be read as a Valheim character, so nothing was copied.", ("file", file));
            }

            if (!CharacterFile.TryRead(bytes, out var info, out var problem))
            {
                if (problem == CharacterFile.UnknownVersion)
                    throw new HostFacingException("characters.copy.unknownVersion",
                        $"'{file}' was saved by a version of Valheim that BakaLoader does not know, so it was not copied.", ("file", file));
                throw new HostFacingException("characters.copy.unreadable",
                    $"'{file}' could not be read as a Valheim character, so nothing was copied.", ("file", file));
            }

            var copyName = info.Name + Suffix;
            var copyFile = CharacterFile.FileNameFor(copyName);
            var target = Path.Combine(copyDir, copyFile);
            if (PathTaken(target))
                throw new HostFacingException("characters.copy.exists",
                    $"There is already a character file '{copyFile}', so '{info.Name}' was not copied. BakaLoader never writes over a character.",
                    ("file", copyFile), ("name", info.Name));

            var renamed = CharacterFile.Rename(info, copyName);

            // characters_local is not there on a PC that has only ever saved to Steam Cloud. It is
            // made here, after every refusal, so a refused copy leaves the save folder as it found
            // it; and a folder that turns out to be a link is not written through.
            try
            {
                if (IsLink(copyDir)) throw new IOException("the folder is a link");
                Directory.CreateDirectory(copyDir);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                throw new HostFacingException("characters.copy.failed",
                    $"The copy of '{info.Name}' could not be written: {e.Message}", ("name", info.Name), ("detail", e.Message));
            }

            // CreateNew is the never-overwrite: a file that turned up since the check above
            // makes the open throw rather than be replaced. Only a file this call opened is
            // ever taken away again.
            FileStream stream;
            try
            {
                stream = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            }
            catch (IOException) when (PathTaken(target))
            {
                throw new HostFacingException("characters.copy.exists",
                    $"There is already a character file '{copyFile}', so '{info.Name}' was not copied. BakaLoader never writes over a character.",
                    ("file", copyFile), ("name", info.Name));
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                throw new HostFacingException("characters.copy.failed",
                    $"The copy of '{info.Name}' could not be written: {e.Message}", ("name", info.Name), ("detail", e.Message));
            }

            try
            {
                using (stream)
                {
                    stream.Write(renamed, 0, renamed.Length);
                    stream.Flush(true);
                }
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                TryDeleteOurs(target);
                throw new HostFacingException("characters.copy.failed",
                    $"The copy of '{info.Name}' could not be written: {e.Message}", ("name", info.Name), ("detail", e.Message));
            }

            // Read back with the same reader: a copy that does not read as the new name is not a copy.
            if (!CharacterFile.TryRead(System.IO.File.ReadAllBytes(target), out var back, out _)
                || !string.Equals(back.Name, copyName, StringComparison.Ordinal))
            {
                TryDeleteOurs(target);
                throw new HostFacingException("characters.copy.failed",
                    $"The copy of '{info.Name}' could not be written: it did not read back.", ("name", info.Name), ("detail", "read back"));
            }

            return new CharacterCopyResult
            {
                Folder = copyFolder,
                File = copyFile,
                Name = copyName,
                SourceName = info.Name,
                Path = target,
            };
        }

        /// <summary>A character's own file: ends in .fch and is not one of the game's backups of one.</summary>
        internal static bool IsCharacterFileName(string file)
        {
            if (string.IsNullOrWhiteSpace(file)) return false;
            if (file.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;
            if (file.Contains("..", StringComparison.Ordinal)) return false;
            if (!file.EndsWith(".fch", StringComparison.OrdinalIgnoreCase)) return false;
            if (file.Length <= ".fch".Length) return false;
            return file.IndexOf("_backup_", StringComparison.OrdinalIgnoreCase) < 0;
        }

        private static bool PathTaken(string path) => System.IO.File.Exists(path) || Directory.Exists(path);

        private static bool IsLink(string path)
        {
            try { return (System.IO.File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
            catch { return false; }
        }

        /// <summary>Takes away the file this copy opened with CreateNew. Nobody else's file can be at that path.</summary>
        private static void TryDeleteOurs(string path)
        {
            try { if (System.IO.File.Exists(path)) System.IO.File.Delete(path); }
            catch { /* best effort */ }
        }
    }
}
