using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ValheimBakaLoader.Tools
{
    /// <summary>One character on this PC, as the test copy dialog lists it.</summary>
    public sealed class CharacterEntry
    {
        /// <summary>characters_local or characters, the folder under the Valheim save folder.</summary>
        public string Folder { get; init; }

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
        public string Folder { get; init; }
        public string File { get; init; }
        public string Name { get; init; }
        public string SourceName { get; init; }
        public string Path { get; init; }
    }

    /// <summary>
    /// The character half of a test copy. Lists the characters this PC's game keeps and
    /// writes a renamed copy of one beside it, so the host plays a test realm with a copy and
    /// the main character never sets foot in it.
    /// <para>
    /// The characters are the game client's, not the server's: SaveSystem.GetCharacterFolderPath
    /// puts them under the client's save folder, in characters_local, or in characters for the
    /// ones kept in Steam Cloud. Both are read and nothing under them is ever changed. The copy
    /// is a new file and only ever a new file: it is opened with CreateNew, so a file that is
    /// already there is refused rather than replaced, even when it appears between the check and
    /// the write.
    /// </para>
    /// </summary>
    public static class CharacterCopy
    {
        /// <summary>What a copy's name ends in. The same suffix a test realm and its world carry.</summary>
        public const string Suffix = "_bakaT";

        /// <summary>The two folders the game keeps characters in, under its save folder.</summary>
        public static readonly IReadOnlyList<string> Folders = new[] { "characters_local", "characters" };

        /// <summary>
        /// Every character file in the two folders, sorted by name. Backups the game keeps
        /// beside a character (name_backup_auto-..., .old, .new) are not characters and are left out.
        /// </summary>
        public static IReadOnlyList<CharacterEntry> List(string saveRoot)
        {
            var found = new List<CharacterEntry>();
            if (string.IsNullOrWhiteSpace(saveRoot)) return found;

            foreach (var folder in Folders)
            {
                var dir = Path.Combine(saveRoot, folder);
                if (!Directory.Exists(dir) || IsLink(dir)) continue;

                IEnumerable<string> files;
                try { files = Directory.EnumerateFiles(dir, "*.fch").ToList(); }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { continue; }

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
                            exists = PathTaken(Path.Combine(dir, copyFile));
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
                        File = file,
                        Name = name,
                        CopyName = copyName,
                        CopyFile = copyFile,
                        CopyExists = exists,
                        Problem = problem,
                    });
                }
            }

            return found
                .OrderBy(c => c.Name ?? c.File, StringComparer.OrdinalIgnoreCase)
                .ThenBy(c => c.Folder, StringComparer.Ordinal)
                .ToList();
        }

        /// <summary>
        /// Writes a copy of <paramref name="file"/> in <paramref name="folder"/> with the name
        /// <c>name_bakaT</c>, beside it, under the file name the game would give that name.
        /// Refuses, by name and before anything is written, a folder that is not one of the two,
        /// a file name with a path in it, a file that is not there, one that cannot be read as a
        /// character, and a copy that is already there.
        /// </summary>
        public static CharacterCopyResult Copy(string saveRoot, string folder, string file)
        {
            folder = (folder ?? string.Empty).Trim();
            file = (file ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(saveRoot)
                || !Folders.Contains(folder, StringComparer.Ordinal)
                || !IsCharacterFileName(file)
                || !string.Equals(Path.GetFileName(file), file, StringComparison.Ordinal))
                throw new HostFacingException("characters.copy.badRef",
                    "That is not a character file BakaLoader can copy.", ("file", file));

            var dir = Path.Combine(saveRoot, folder);
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
                        $"'{file}' was saved by a version of Valheim BakaLoader does not know, so it was not copied.", ("file", file));
                throw new HostFacingException("characters.copy.unreadable",
                    $"'{file}' could not be read as a Valheim character, so nothing was copied.", ("file", file));
            }

            var copyName = info.Name + Suffix;
            var copyFile = CharacterFile.FileNameFor(copyName);
            var target = Path.Combine(dir, copyFile);
            if (PathTaken(target))
                throw new HostFacingException("characters.copy.exists",
                    $"There is already a character file '{copyFile}', so '{info.Name}' was not copied. BakaLoader never writes over a character.",
                    ("file", copyFile), ("name", info.Name));

            var renamed = CharacterFile.Rename(info, copyName);

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
                Folder = folder,
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
