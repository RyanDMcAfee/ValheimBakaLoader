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
    /// the comparison. The already filtered spelling (<c>X_</c>, <c>S_</c>, <c>N_</c>,
    /// <c>A_</c>), which is what a 1.0 server shows in its own log lines, gives the same two
    /// lines: the multiplier is odd, so the multiplication can be undone exactly and the raw id
    /// it came from is recovered rather than lost. An id whose user part is not a number, such
    /// as a PlayFab id, is passed through untouched because the server does not rewrite it
    /// either.
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
        /// The multiplier's inverse modulo 2^64. The multiplier is odd, so multiplying by it is a
        /// one to one map over the whole 64 bit range and it can be undone exactly: multiplying a
        /// filtered id by this gives back the raw user id it was made from
        /// (11400714819323198485 * 17428512612931826493 = 1 in 64 bit arithmetic). That is what
        /// lets a display form an operator copied off a 1.0 screen keep its pre-1.0 twin.
        /// </summary>
        private const ulong ConsoleIdMultiplierInverse = 17428512612931826493UL;

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

        /// <summary>
        /// UTF-8 with no byte order mark, which is the encoding the game's own writer produces:
        /// SyncedList.Save writes through a plain <c>StreamWriter</c>, whose default encoder emits
        /// no mark. The game reads through a plain <c>StreamReader</c>, which detects and strips a
        /// mark if one is there, so a marked file still loads for it. BakaLoader simply does not
        /// add one, and a file it rewrites keeps the byte shape the game would have written.
        /// </summary>
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
        /// <item>the already filtered <c>X_</c>, <c>S_</c>, <c>N_</c> and <c>A_</c> forms give
        /// that same pair, because the multiplication can be undone exactly;</item>
        /// <item>anything else, such as a <c>PlayFab_</c> id, is kept verbatim because the
        /// server does not rewrite it either.</item>
        /// </list>
        /// An id that cannot be stored as one line the server would read back as itself, such as
        /// one carrying a line break or one the game files under comments, gives no forms at all,
        /// so nothing downstream can write it. A value the game WOULD read back as itself is
        /// always given its forms, even one starting with <c>#</c> that this service will not
        /// write: an entry already sitting in the file has to be addressable to be removed.
        /// Pure and side effect free, so it can be unit tested and reused for display.
        /// </summary>
        public static IReadOnlyList<string> NormalizeForms(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return Array.Empty<string>();

            var trimmed = id.Trim();

            // Surrounding whitespace, a stray line break at either end included, is trimmed off
            // above. What is left has to survive a round trip through the file as one entry, so a
            // value carrying a line break in the middle, or reading as a comment, gives nothing at
            // all rather than a line that splits in two or that the server never looks at.
            if (!IsStorableForm(trimmed)) return Array.Empty<string>();

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
        /// The pair for a console id: the raw line a pre-1.0 server looks up plus the line a 1.0
        /// server looks up. Either spelling can be the one the operator has in hand, so both are
        /// accepted and both give the same pair. A raw id is multiplied to get its display twin;
        /// a display id is divided by the same constant, which is exact because the multiplier
        /// is odd (see <see cref="ConsoleIdMultiplierInverse"/>). Returns null when the id is not
        /// a console platform with a numeric user id, which is the case the server passes through
        /// unchanged.
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

            foreach (var platform in ConsolePrefixes)
            {
                // The other direction: the id a 1.0 server shows in a kick line or a connect
                // line, which is the form an operator most often has to hand. Undoing the
                // multiplication gives the raw id it was made from, so this entry gets the same
                // two lines a raw id gets and keeps working if the profile is ever rolled back
                // to a pre-1.0 build.
                var filteredText = WithoutPrefix(id, platform.Value);
                if (filteredText == null) continue;

                if (!ulong.TryParse(filteredText, out var filtered)) return null;
                if (filtered == 0) return new[] { id };

                var value = unchecked(filtered * ConsoleIdMultiplierInverse);
                var raw = platform.Key + value.ToString(CultureInfo.InvariantCulture);
                return raw == id ? new[] { id } : new[] { raw, id };
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
        /// and new lines are appended. Returns true if a write occurred and false when nothing was
        /// written, which is either because every spelling was already there or because the id
        /// cannot be stored as one line the server would read back as itself. A write that fails
        /// throws, so the caller never reports a change that did not happen.
        /// </summary>
        public bool AddToList(string saveFolder, PlayerListType list, string id)
        {
            if (string.IsNullOrWhiteSpace(saveFolder) || string.IsNullOrWhiteSpace(id)) return false;
            id = id.Trim();

            EnsureUpgraded(saveFolder, list);

            var forms = NormalizeForms(id);

            // Two refusals in one: a value the file cannot hold as one entry gives no forms at
            // all, and a value the game could read back but BakaLoader will not author (a leading
            // comment marker) is turned away here rather than in NormalizeForms, so the read and
            // remove paths can still address a line like that if one is already on disk.
            if (forms.Count == 0 || !forms.All(IsWritableForm))
            {
                // A blank id was turned away above. The id is left out of the message on purpose:
                // the thing wrong with it may well be the line break it is carrying.
                Logger.Warning("Ignored an id that cannot be written to {file} as one entry",
                    FileNameFor(list));
                return false;
            }

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
            if (forms.Count == 0)
            {
                // Said out loud for the same reason the add path says it: a silent false reads as
                // "there was nothing to remove", and an operator looking at the id on their screen
                // would have no way to tell the difference.
                Logger.Warning("Ignored a request to remove an id from {file} that no line can hold as one entry",
                    FileNameFor(list));
                return false;
            }

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
        public int UpgradeLegacyEntries(string saveFolder, PlayerListType list) =>
            TryUpgradeLegacyEntries(saveFolder, list, out var added) ? added : 0;

        /// <summary>
        /// The same pass with its outcome kept apart from its result. True means the pass actually
        /// ran, with <paramref name="added"/> holding how many lines it added, which is zero when
        /// the file was already complete. False means it could not run at all, because the file
        /// could not be read or the rewrite failed, and nothing was changed. Anything that caches
        /// "this file is done" must only cache on true: a failure and a file that needed nothing
        /// both add zero lines, and treating them the same is how a locked file loses its upgrade
        /// for the rest of the run.
        /// </summary>
        private bool TryUpgradeLegacyEntries(string saveFolder, PlayerListType list, out int added)
        {
            added = 0;

            if (string.IsNullOrWhiteSpace(saveFolder)) return false;

            var path = PathFor(saveFolder, list);

            try
            {
                if (!File.Exists(path)) return true;

                int count;

                lock (GateFor(path))
                {
                    var file = ReadListFile(path);
                    var present = ActiveEntries(file.Lines);
                    var missing = new List<string>();

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
                            if (present.Add(form)) missing.Add(form);
                        }
                    }

                    if (missing.Count == 0) return true;

                    file.Lines.AddRange(missing);
                    WriteListFile(path, file);
                    count = missing.Count;
                }

                Logger.Information("{file}: added the 1.0 id form for {count} entries",
                    FileNameFor(list), count);
                added = count;
                return true;
            }
            catch (Exception e)
            {
                Logger.Warning("Could not upgrade {file}: {message}", FileNameFor(list), e.Message);
                return false;
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
        /// is fixed up without the caller having to know about it. Cheap after the first call that
        /// succeeds; a pass that could not run is not remembered, so it is tried again.
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

                // Only a pass that actually ran counts as done. A file that was locked or that
                // could not be rewritten stays unmarked, so the next call tries again instead of
                // one moment of bad luck costing the upgrade for the rest of the run.
                if (TryUpgradeLegacyEntries(saveFolder, list, out _)) UpgradedFiles.TryAdd(key, 0);
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
        /// Whether the game would read this text back from the file as one entry equal to the text
        /// itself. SyncedList.Load reads line by line, drops an empty line, files a line starting
        /// with <c>//</c> under comments and keeps everything else exactly as written, so a value
        /// holding a line break would arrive as two entries and a value that reads as a comment
        /// would never be an entry at all.
        /// </summary>
        private static bool IsStorableForm(string form)
        {
            if (string.IsNullOrEmpty(form)) return false;
            if (form.IndexOf('\n') >= 0 || form.IndexOf('\r') >= 0) return false;

            // Both the compare this class uses and the culture sensitive one the game's own reader
            // uses, because a value that either of them calls a comment must not be written.
            return !form.StartsWith("//", StringComparison.Ordinal)
                && !form.StartsWith("//", StringComparison.CurrentCulture);
        }

        /// <summary>
        /// Whether this service is willing to WRITE the form, which is a narrower question than
        /// whether the game can read it back. A leading <c>#</c> is not a comment to the game, so
        /// a <c>#</c> line already in a file is a live entry that has to stay readable and
        /// removable, but it is the comment marker every other tool that edits these files uses
        /// and it is never part of a platform id, so BakaLoader will not add one. Refusing it in
        /// <see cref="NormalizeForms"/> instead is what left such a line unmatched and undeletable
        /// from the interface: the only way to clear it was to edit the file by hand.
        /// </summary>
        private static bool IsWritableForm(string form) =>
            IsStorableForm(form) && form[0] != '#';

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

            /// <summary>
            /// The line ending the file mostly uses, so a Linux written file stays LF. Every line
            /// is written with this one, so a file that arrived mixed leaves consistent.
            /// </summary>
            public string NewLine = Environment.NewLine;

            /// <summary>Whether the file ended with a line break, which is how the game writes it.</summary>
            public bool TrailingNewLine = true;
        }

        private static ListFile ReadListFile(string path)
        {
            if (!File.Exists(path)) return new ListFile();

            // ReadAllText detects and strips a byte order mark if one is there, and the game's own
            // reader does the same, so a marked file is not broken for either of us. The rewrite
            // still leaves the mark off, because that is what the game's writer produces.
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

        /// <summary>
        /// The line ending the file mostly uses, so a file written on Linux stays on LF and one
        /// written on Windows stays on CRLF. A file that mixes the two is counted rather than
        /// judged by whichever break happens to come first, because one stray break at the top
        /// would otherwise flip every other line in the file. The winner is then used for the
        /// whole rewrite, so what lands on disk is never mixed. A tie goes to CRLF, which is what
        /// the game writes on Windows, and a file with no break at all keeps the local default.
        /// </summary>
        private static string DetectNewLine(string text)
        {
            var crlf = 0;
            var lf = 0;
            var cr = 0;

            for (var i = 0; i < text.Length; i++)
            {
                if (text[i] == '\r')
                {
                    if (i + 1 < text.Length && text[i + 1] == '\n')
                    {
                        crlf++;
                        i++; // the LF belongs to this break, do not count it a second time
                    }
                    else
                    {
                        cr++;
                    }
                }
                else if (text[i] == '\n')
                {
                    lf++;
                }
            }

            if (crlf == 0 && lf == 0 && cr == 0) return Environment.NewLine;
            if (crlf >= lf && crlf >= cr) return "\r\n";

            return lf >= cr ? "\n" : "\r";
        }
    }
}
