using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ValheimBakaLoader.Tools.Logging;

namespace ValheimBakaLoader.Tools
{
    public enum PlayerListType
    {
        Admin,
        Banned,
        Permitted,
    }

    /// <summary>
    /// Reads and writes Valheim's one-platform-id-per-line access lists
    /// (<c>adminlist.txt</c>, <c>bannedlist.txt</c>, <c>permittedlist.txt</c>) that live in the
    /// server's save-data folder. Valheim hot-reloads these files, so edits take effect on a
    /// running server with no restart. Comment lines (starting with <c>//</c>) and blank lines
    /// are preserved when rewriting.
    /// </summary>
    public class PlayerListService
    {
        private readonly IApplicationLogger Logger;

        public PlayerListService(IApplicationLogger logger)
        {
            Logger = logger;
        }

        private static string FileNameFor(PlayerListType list) => list switch
        {
            PlayerListType.Admin => "adminlist.txt",
            PlayerListType.Banned => "bannedlist.txt",
            PlayerListType.Permitted => "permittedlist.txt",
            _ => throw new ArgumentOutOfRangeException(nameof(list)),
        };

        private static string PathFor(string saveFolder, PlayerListType list) =>
            Path.Combine(saveFolder, FileNameFor(list));

        /// <summary>Returns true if the given id has an active (non-comment) entry in the list.</summary>
        public bool IsListed(string saveFolder, PlayerListType list, string id)
        {
            if (string.IsNullOrWhiteSpace(saveFolder) || string.IsNullOrWhiteSpace(id)) return false;

            try
            {
                var path = PathFor(saveFolder, list);
                if (!File.Exists(path)) return false;

                return File.ReadAllLines(path)
                    .Select(NormalizeEntry)
                    .Any(entry => string.Equals(entry, id.Trim(), StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception e)
            {
                Logger.Warning("Could not read {file}: {message}", FileNameFor(list), e.Message);
                return false;
            }
        }

        /// <summary>Adds the id to the list if not already present. Returns true if a write occurred.</summary>
        public bool AddToList(string saveFolder, PlayerListType list, string id)
        {
            if (string.IsNullOrWhiteSpace(saveFolder) || string.IsNullOrWhiteSpace(id)) return false;
            id = id.Trim();

            try
            {
                var path = PathFor(saveFolder, list);
                var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : new List<string>();

                if (lines.Any(l => string.Equals(NormalizeEntry(l), id, StringComparison.OrdinalIgnoreCase)))
                {
                    return false; // already listed
                }

                lines.Add(id);
                File.WriteAllLines(path, lines);
                Logger.Information("Added {id} to {file}", id, FileNameFor(list));
                return true;
            }
            catch (Exception e)
            {
                Logger.Error(e, "Could not add {id} to {file}", id, FileNameFor(list));
                return false;
            }
        }

        /// <summary>Removes every active entry matching the id. Returns true if a write occurred.</summary>
        public bool RemoveFromList(string saveFolder, PlayerListType list, string id)
        {
            if (string.IsNullOrWhiteSpace(saveFolder) || string.IsNullOrWhiteSpace(id)) return false;
            id = id.Trim();

            try
            {
                var path = PathFor(saveFolder, list);
                if (!File.Exists(path)) return false;

                var lines = File.ReadAllLines(path).ToList();
                var kept = lines
                    .Where(l => !string.Equals(NormalizeEntry(l), id, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (kept.Count == lines.Count) return false; // nothing removed

                File.WriteAllLines(path, kept);
                Logger.Information("Removed {id} from {file}", id, FileNameFor(list));
                return true;
            }
            catch (Exception e)
            {
                Logger.Error(e, "Could not remove {id} from {file}", id, FileNameFor(list));
                return false;
            }
        }

        /// <summary>
        /// Strips a trailing <c>//comment</c> and surrounding whitespace from a list line,
        /// returning the bare id (or an empty string for blank/comment-only lines).
        /// </summary>
        private static string NormalizeEntry(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return string.Empty;

            var trimmed = line.Trim();
            if (trimmed.StartsWith("//")) return string.Empty;

            var commentIndex = trimmed.IndexOf("//", StringComparison.Ordinal);
            if (commentIndex >= 0) trimmed = trimmed.Substring(0, commentIndex).Trim();

            return trimmed;
        }
    }
}
