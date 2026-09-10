using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
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
    /// server's save-data folder. Valheim hot-reloads these files every ten seconds, so edits
    /// take effect on a running server with no restart. Comment lines (starting with <c>//</c>)
    /// and blank lines are preserved when rewriting.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Valheim 1.0 changed how a line is matched. The server now parses the line into a
    /// platform id and runs it through Splatform's display filter before comparing, and for
    /// Steam that filter rewrites the id to <c>V_&lt;steamid64&gt;</c>. The rewritten value
    /// overwrites the earlier bare and <c>Steam_</c> comparisons, so on 1.0 a bare
    /// <c>76561198...</c> line and a <c>Steam_76561198...</c> line no longer match anything:
    /// admins silently lose admin, bans stop applying, and a populated permittedlist locks
    /// everyone out. Pre-1.0 servers match the bare form and do not know about <c>V_</c>.
    /// </para>
    /// <para>
    /// So for a Steam id this service keeps BOTH lines on disk, the bare id and the
    /// <c>V_</c> form. That reads correctly on either server version and costs one extra line.
    /// A console id (<c>Xbox_</c>, <c>PlayStation_</c>, <c>Nintendo_</c>, <c>GameCenter_</c>)
    /// gets the same treatment: the raw line for a pre-1.0 server plus the 1.0 line, which is
    /// the display prefix and the user id multiplied by the constant Splatform applies before
    /// the comparison. An id whose user part is not a number, such as a PlayFab id, is passed
    /// through untouched because the server does not rewrite it either.
    /// </para>
    /// <para>
    /// Whether a line is already effective on disk is decided the way the server decides it:
    /// the raw text of the line, with no trimming and no inline comment handling, because
    /// SyncedList.Load stores the line as written and the lookup is a whole string compare.
    /// Comparisons are ordinal and case sensitive, the same as the game's own list lookup, so
    /// this service never reports a player as listed when the server would disagree.
    /// </para>
    /// </remarks>
    public class PlayerListService
    {
        /// <summary>Splatform's 1.0 display prefix for Steam accounts.</summary>
        private const string SteamDisplayPrefix = "V_";

        /// <summary>The pre-1.0 spelling of a Steam platform id.</summary>
        private const string SteamLegacyPrefix = "Steam_";

        /// <summary>
        /// The multiplier Splatform.FilterPlatformUserID applies to a console user id before the
        /// server compares it. Steam ids keep their number and only gain the <c>V_</c> prefix.
        /// </summary>
        private const ulong ConsoleIdMultiplier = 11400714819323198485UL;

        /// <summary>
        /// The console platforms whose user id the server multiplies, mapped from the spelling a
        /// log line and a pre-1.0 list file use to the display prefix 1.0 writes.
        /// </summary>
        private static readonly KeyValuePair<string, string>[] ConsolePrefixes =
        {
            new("Xbox_", "X_"),
            new("PlayStation_", "S_"),
            new("Nintendo_", "N_"),
            new("GameCenter_", "A_"),
        };

        /// <summary>
        /// Files already run through <see cref="UpgradeLegacyEntries"/> in this process, keyed by
        /// full path. Keeps the lazy upgrade to one pass per file per run.
        /// </summary>
        private static readonly ConcurrentDictionary<string, byte> UpgradedFiles =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// One gate per list file, keyed by full path, so a read then rewrite in this process
        /// cannot interleave with another one and lose a line. Monitor is re-entrant, so the
        /// lazy upgrade can run inside a caller that already holds the same gate.
        /// </summary>
        private static readonly ConcurrentDictionary<string, object> FileGates =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>UTF-8 with no byte order mark, which is what the game writes and reads.</summary>
        private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

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

        /// <summary>The full path of a list file, or the path as given when it cannot be resolved.</summary>
        private static string GateKey(string path)
        {
            try { return Path.GetFullPath(path); }
            catch { return path; }
        }

        /// <summary>The gate that serialises every read then rewrite of one list file.</summary>
        private static object GateFor(string path) =>
            FileGates.GetOrAdd(GateKey(path), _ => new object());

        /// <summary>
        /// Every on-disk spelling that stands for the given id.
        /// <list type="bullet">
        /// <item>plain digits (a steamid64) gives the bare id and its <c>V_</c> twin;</item>
        /// <item><c>V_&lt;digits&gt;</c> and <c>Steam_&lt;digits&gt;</c> give the same pair;</item>
        /// <item><c>Xbox_</c>, <c>PlayStation_</c>, <c>Nintendo_</c> and <c>GameCenter_</c>
        /// followed by a number give the raw line and the 1.0 line the server looks up, which
        /// is the display prefix and the id multiplied by
        /// <see cref="ConsoleIdMultiplier"/>;</item>
        /// <item>anything else, such as an already filtered <c>X_</c> id or a <c>PlayFab_</c>
        /// id, is kept verbatim because the server does not rewrite it either.</item>
        /// </list>
        /// Pure and side effect free, so it can be unit tested and reused for display.
        /// </summary>
        public static IReadOnlyList<string> NormalizeForms(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return Array.Empty<string>();

            var trimmed = id.Trim();

            if (IsSteamId(trimmed)) return SteamForms(trimmed);

            // Prefixes are matched exactly, because the server's own list lookup is ordinal and
            // case sensitive: a "v_" or "steam_" line is not the same player to the server, so
            // it must not be the same player here either.
            var digits = WithoutPrefix(trimmed, SteamDisplayPrefix);
            if (digits != null && IsSteamId(digits)) return SteamForms(digits);

            digits = WithoutPrefix(trimmed, SteamLegacyPrefix);
            if (digits != null && IsSteamId(digits)) return SteamForms(digits);

            var console = ConsoleForms(trimmed);
            if (console != null) return console;

            return new[] { trimmed };
        }

        /// <summary>
        /// The pair for a console id: the line as written plus the line the 1.0 server looks up.
        /// Returns null when the id is not a console platform with a numeric user id, which is
        /// the case the server passes through unchanged.
        /// </summary>
        private static string[] ConsoleForms(string id)
        {
            foreach (var platform in ConsolePrefixes)
            {
                var userId = WithoutPrefix(id, platform.Key);
                if (userId == null) continue;

                // The same overload the game uses, so an id it rewrites is one we rewrite.
                if (!ulong.TryParse(userId, out var value)) return null;

                var filtered = unchecked(value * ConsoleIdMultiplier);

                // The game treats a zero user id as no id at all, so there is nothing to look up.
                if (filtered == 0) return new[] { id };

                var display = platform.Value + filtered.ToString(CultureInfo.InvariantCulture);
                return display == id ? new[] { id } : new[] { id, display };
            }

            return null;
        }

        /// <summary>
        /// Whether the server would treat the id as listed right now: true when one of the id's
        /// spellings is on a line the server can see, false when it is not, and null when the
        /// file could not be read, so the caller can say "unknown" instead of offering the wrong
        /// verb. A line only counts when its raw text matches, because that is all the server
        /// compares.
        /// </summary>
        public bool? IsListed(string saveFolder, PlayerListType list, string id)
        {
            if (string.IsNullOrWhiteSpace(saveFolder) || string.IsNullOrWhiteSpace(id)) return false;

            EnsureUpgraded(saveFolder, list);

            var forms = FormSet(id);
            if (forms.Count == 0) return false;

            var path = PathFor(saveFolder, list);

            try
            {
                if (!File.Exists(path)) return false;

                lock (GateFor(path))
                {
                    return ReadListFile(path).Lines.Any(line => forms.Contains(ServerEntry(line)));
                }
            }
            catch (Exception e)
            {
                Logger.Warning("Could not read {file}: {message}", FileNameFor(list), e.Message);
                return null;
            }
        }

        /// <summary>
        /// Adds every missing spelling of the id (for a Steam id that is the bare number and the
        /// <c>V_</c> form). Existing lines, their order, blank lines and comments are untouched
        /// and new lines are appended. Returns true if a write occurred and false when every
        /// spelling was already there. A write that fails throws, so the caller never reports a
        /// change that did not happen.
        /// </summary>
        public bool AddToList(string saveFolder, PlayerListType list, string id)
        {
            if (string.IsNullOrWhiteSpace(saveFolder) || string.IsNullOrWhiteSpace(id)) return false;
            id = id.Trim();

            EnsureUpgraded(saveFolder, list);

            var forms = NormalizeForms(id);
            if (forms.Count == 0) return false;

            var path = PathFor(saveFolder, list);

            try
            {
                lock (GateFor(path))
                {
                    var file = ReadListFile(path);
                    var present = ActiveEntries(file.Lines);

                    // Exact spelling, not a fuzzy match: the whole point is that the V_ line is
                    // physically on disk, so a "Steam_" line does not satisfy the V_ form.
                    var missing = forms.Where(form => !present.Contains(form)).ToList();
                    if (missing.Count == 0) return false; // already listed in every form

                    file.Lines.AddRange(missing);
                    WriteListFile(path, file);
                }

                Logger.Information("Added {id} to {file}", id, FileNameFor(list));
                return true;
            }
            catch (Exception e)
            {
                // A lost write must not look like "nothing needed doing", so this goes back to
                // the caller as a failure rather than as false.
                Logger.Error(e, "Could not add {id} to {file}", id, FileNameFor(list));
                throw;
            }
        }

        /// <summary>
        /// Removes every active entry that stands for the id, in any of its spellings, and
        /// nothing else. This is the one place the lenient reading is used, because the question
        /// is "which player is this line about" rather than "does the server see it". Returns
        /// true if a write occurred; a write that fails throws.
        /// </summary>
        public bool RemoveFromList(string saveFolder, PlayerListType list, string id)
        {
            if (string.IsNullOrWhiteSpace(saveFolder) || string.IsNullOrWhiteSpace(id)) return false;
            id = id.Trim();

            EnsureUpgraded(saveFolder, list);

            var forms = FormSet(id);
            if (forms.Count == 0) return false;

            var path = PathFor(saveFolder, list);

            try
            {
                if (!File.Exists(path)) return false;

                lock (GateFor(path))
                {
                    var file = ReadListFile(path);
                    var kept = file.Lines
                        .Where(line => !Matches(NormalizeEntry(line), forms))
                        .ToList();

                    if (kept.Count == file.Lines.Count) return false; // nothing removed

                    file.Lines = kept;
                    WriteListFile(path, file);
                }

                Logger.Information("Removed {id} from {file}", id, FileNameFor(list));
                return true;
            }
            catch (Exception e)
            {
                Logger.Error(e, "Could not remove {id} from {file}", id, FileNameFor(list));
                throw;
            }
        }

        /// <summary>
        /// Gives every pre-1.0 Steam line in the file its 1.0 twin: a bare <c>76561198...</c> or
        /// <c>Steam_76561198...</c> line gains the matching <c>V_</c> line (and a <c>Steam_</c>
        /// line also gains the bare form). Nothing is ever removed or reordered, console ids are
        /// left alone, and a file that is already complete is not rewritten, so this is safe to
        /// run on every launch. Returns the number of lines added.
        /// </summary>
        public int UpgradeLegacyEntries(string saveFolder, PlayerListType list)
        {
            if (string.IsNullOrWhiteSpace(saveFolder)) return 0;

            var path = PathFor(saveFolder, list);

            try
            {
                if (!File.Exists(path)) return 0;

                int count;

                lock (GateFor(path))
                {
                    var file = ReadListFile(path);
                    var present = ActiveEntries(file.Lines);
                    var added = new List<string>();

                    foreach (var line in file.Lines)
                    {
                        // The raw line, because a line the server cannot see is not an entry to
                        // give a twin to: it never granted anything and must not start now.
                        var entry = ServerEntry(line);
                        if (entry.Length == 0) continue;

                        var forms = NormalizeForms(entry);
                        if (forms.Count < 2) continue; // ids the server keeps as written stay put

                        foreach (var form in forms)
                        {
                            if (present.Add(form)) added.Add(form);
                        }
                    }

                    if (added.Count == 0) return 0;

                    file.Lines.AddRange(added);
                    WriteListFile(path, file);
                    count = added.Count;
                }

                Logger.Information("{file}: added the 1.0 id form for {count} entries",
                    FileNameFor(list), count);
                return count;
            }
            catch (Exception e)
            {
                Logger.Warning("Could not upgrade {file}: {message}", FileNameFor(list), e.Message);
                return 0;
            }
        }

        /// <summary>
        /// Runs <see cref="UpgradeLegacyEntries"/> over all three lists in a save folder.
        /// Returns the total number of lines added.
        /// </summary>
        public int UpgradeAll(string saveFolder)
        {
            if (string.IsNullOrWhiteSpace(saveFolder)) return 0;

            return UpgradeLegacyEntries(saveFolder, PlayerListType.Admin)
                + UpgradeLegacyEntries(saveFolder, PlayerListType.Banned)
                + UpgradeLegacyEntries(saveFolder, PlayerListType.Permitted);
        }

        /// <summary>
        /// Upgrades a list file the first time this process touches it, so an existing install
        /// is fixed up without the caller having to know about it. Cheap after the first call.
        /// </summary>
        private void EnsureUpgraded(string saveFolder, PlayerListType list)
        {
            string path;
            string key;
            try
            {
                path = PathFor(saveFolder, list);
                key = Path.GetFullPath(path);
            }
            catch
            {
                return; // an unusable path is the caller's problem, not the upgrade's
            }

            if (UpgradedFiles.ContainsKey(key)) return;

            // Check, run and mark under the file's own gate so two threads cannot both decide
            // they are the one that has to run the pass.
            lock (GateFor(path))
            {
                if (UpgradedFiles.ContainsKey(key)) return;

                // A file that does not exist yet has nothing to upgrade, and is deliberately not
                // marked as done so a list created later still gets its pass.
                if (!File.Exists(key)) return;

                UpgradeLegacyEntries(saveFolder, list);
                UpgradedFiles.TryAdd(key, 0);
            }
        }

        /// <summary>
        /// The distinct lines the server can see, exactly as it sees them. Anything the server
        /// would skip, and any padding or trailing comment the server would keep as part of the
        /// line, is preserved so "already present" means present for the server too.
        /// </summary>
        private static HashSet<string> ActiveEntries(IEnumerable<string> lines)
        {
            var entries = new HashSet<string>(StringComparer.Ordinal);

            foreach (var line in lines)
            {
                var entry = ServerEntry(line);
                if (entry.Length > 0) entries.Add(entry);
            }

            return entries;
        }

        /// <summary>The spellings of an id as a lookup set.</summary>
        private static HashSet<string> FormSet(string id) =>
            new(NormalizeForms(id), StringComparer.Ordinal);

        /// <summary>
        /// True when a file entry stands for the same player as the given form set. The entry is
        /// itself normalised first, so a legacy <c>Steam_</c> line is recognised as the same
        /// player as the bare id and the <c>V_</c> id.
        /// </summary>
        private static bool Matches(string entry, HashSet<string> forms)
        {
            if (entry.Length == 0) return false;

            foreach (var form in NormalizeForms(entry))
            {
                if (forms.Contains(form)) return true;
            }

            return false;
        }

        private static string[] SteamForms(string steamId) =>
            new[] { steamId, SteamDisplayPrefix + steamId };

        /// <summary>A steamid64 is plain ASCII digits. Length is not checked so test ids still work.</summary>
        private static bool IsSteamId(string value)
        {
            if (value.Length == 0) return false;

            foreach (var c in value)
            {
                if (c < '0' || c > '9') return false;
            }

            return true;
        }

        private static string WithoutPrefix(string value, string prefix) =>
            value.StartsWith(prefix, StringComparison.Ordinal)
                ? value.Substring(prefix.Length)
                : null;

        /// <summary>
        /// The line as the server holds it. SyncedList.Load keeps the raw text and skips only an
        /// empty line and a line that literally starts with <c>//</c>, and the lookup is a whole
        /// string compare, so there is no trimming and no inline comment handling here. Returns
        /// an empty string for a line the server drops.
        /// </summary>
        private static string ServerEntry(string line)
        {
            if (line == null || line.Length == 0) return string.Empty;

            return line.StartsWith("//", StringComparison.Ordinal) ? string.Empty : line;
        }

        /// <summary>
        /// Strips a trailing <c>//comment</c> and surrounding whitespace from a list line,
        /// returning the bare id (or an empty string for blank/comment-only lines). This is the
        /// lenient reading, used only to work out which player a line is about when removing.
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

        /// <summary>A list file split into lines, remembering how it was laid out on disk.</summary>
        private sealed class ListFile
        {
            public List<string> Lines = new();

            /// <summary>The line ending the file already uses, so a Linux-written file stays LF.</summary>
            public string NewLine = Environment.NewLine;

            /// <summary>Whether the file ended with a line break, which is how the game writes it.</summary>
            public bool TrailingNewLine = true;
        }

        private static ListFile ReadListFile(string path)
        {
            if (!File.Exists(path)) return new ListFile();

            // ReadAllText detects and strips a byte order mark if one is there; we always write
            // back without one because a mark on line one breaks the game's match on that line.
            var text = File.ReadAllText(path);
            var file = new ListFile
            {
                NewLine = DetectNewLine(text),
                TrailingNewLine = text.Length == 0 || text.EndsWith("\n") || text.EndsWith("\r"),
            };

            var lines = text.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None).ToList();

            // Splitting "a\nb\n" yields a trailing empty element that is not a real line.
            if (lines.Count > 0 && file.TrailingNewLine) lines.RemoveAt(lines.Count - 1);

            file.Lines = lines;
            return file;
        }

        /// <summary>
        /// Writes the file whole or not at all. The text goes to a sibling temp file first and
        /// the temp file is then renamed over the target, so the server's ten second reload
        /// always opens a complete file. Rewriting in place lets that reload clear its list and
        /// then read a truncated file, which drops every admin, lifts every ban and switches a
        /// whitelist off until the next write.
        /// </summary>
        private static void WriteListFile(string path, ListFile file)
        {
            var text = string.Join(file.NewLine, file.Lines);
            if (file.TrailingNewLine && file.Lines.Count > 0) text += file.NewLine;

            // Same directory, so the rename stays on one volume and is a directory entry swap.
            var temp = path + ".vbl-" + Guid.NewGuid().ToString("N") + ".tmp";

            try
            {
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(stream, Utf8NoBom))
                {
                    writer.Write(text);
                    writer.Flush();
                    stream.Flush(flushToDisk: true);
                }

                MoveOverWithRetry(temp, path);
            }
            catch
            {
                // Never leave a stray adminlist.txt.vbl-....tmp behind.
                try { if (File.Exists(temp)) File.Delete(temp); } catch { /* best effort */ }
                throw;
            }
        }

        /// <summary>
        /// Renames the temp file over the target, retrying briefly because the game opens these
        /// files for its own save with no sharing at all and can hold the name for a moment.
        /// </summary>
        private static void MoveOverWithRetry(string temp, string path)
        {
            const int attempts = 5;

            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    File.Move(temp, path, overwrite: true);
                    return;
                }
                catch (IOException) when (attempt < attempts) { }
                catch (UnauthorizedAccessException) when (attempt < attempts) { }

                Thread.Sleep(50);
            }
        }

        private static string DetectNewLine(string text)
        {
            var lf = text.IndexOf('\n');
            if (lf > 0 && text[lf - 1] == '\r') return "\r\n";
            if (lf >= 0) return "\n";
            return text.IndexOf('\r') >= 0 ? "\r" : Environment.NewLine;
        }
    }
}
