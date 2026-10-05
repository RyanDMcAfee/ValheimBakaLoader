using System;
using System.Collections.Generic;
using System.IO;

namespace ValheimBakaLoader.Tools
{
    /// <summary>
    /// What the disk said about the two places a stored save folder could be, gathered in one
    /// place so the rule that reads it never touches a disk itself. The same split
    /// <see cref="PathCheck"/> uses, for the same reason: every case a host can land in is a
    /// row of a table rather than a folder somebody had to arrange on a build machine.
    /// </summary>
    public readonly struct StrayFolderFacts
    {
        /// <summary>The stored string still carries an environment variable to fill in.</summary>
        public bool StoredCarriesVariable { get; init; }

        /// <summary>There is a folder at the resolved path, which is where the app looks now.</summary>
        public bool ResolvedExists { get; init; }

        /// <summary>
        /// And that folder holds worlds. Asked separately from whether it is THERE, because the
        /// two answers want different words: worlds in both folders is a decision about worlds,
        /// while an empty folder where the app reads is a server that would come up on a brand
        /// new world. See <see cref="StraySaveFolder.HoldsWorlds"/> for what counts.
        /// </summary>
        public bool ResolvedHoldsWorlds { get; init; }

        /// <summary>
        /// There is a folder at the path the raw string resolves to against the install folder:
        /// a real directory literally named for the variable, holding the worlds.
        /// </summary>
        public bool StrayExists { get; init; }

        /// <summary>
        /// The stray folder holds WORLDS, so there is something to move. Not "holds anything at
        /// all": an empty directory chain is what a move or a by-hand repair leaves behind, and
        /// counting that as contents is what raised a permanent row about a folder with no
        /// worlds in it.
        /// </summary>
        public bool StrayHoldsWorlds { get; init; }
    }

    /// <summary>
    /// One realm's answer: the word for what is on disk, and every path that word is about.
    /// </summary>
    public readonly struct StrayFolderAnswer
    {
        /// <summary>One of the five shapes on <see cref="StraySaveFolder"/>.</summary>
        public string Shape { get; init; }

        /// <summary>The stored string, as the host wrote it.</summary>
        public string Stored { get; init; }

        /// <summary>Where the app reads now: the stored string through the boundary.</summary>
        public string Resolved { get; init; }

        /// <summary>Where an older build put it, or null when there is no second place.</summary>
        public string Stray { get; init; }

        /// <summary>
        /// EVERY folder an older build could have put it in that really holds worlds, in the
        /// order they were asked about. Normally one. Two when the build that made the mess
        /// ran from one working directory and the build before it ran from another, which is
        /// what a shortcut with a "Start in" of its own and a service-style launch do to the
        /// same install. The bridge asks about both folders, and naming only the first of two
        /// would be telling a host their worlds are in one place while leaving a second tree
        /// of them unmentioned on the same disk.
        /// </summary>
        public IReadOnlyList<string> Strays { get; init; }

        /// <summary>
        /// <see cref="Strays"/> with the null case filled in, so no reader has to: an answer
        /// built before this field existed, or the no-op answer, both come back as the list
        /// they mean rather than as null.
        /// </summary>
        public IReadOnlyList<string> StrayFolders
            => Strays ?? (string.IsNullOrEmpty(Stray) ? Array.Empty<string>() : new[] { Stray });

        /// <summary>
        /// The install folder the stray path was anchored at, which is also the folder a prune
        /// after a move is never allowed to walk above. Null with <see cref="Stray"/>.
        /// </summary>
        public string Install { get; init; }
    }

    /// <summary>
    /// The one shape issue 17 left on disk, and what to say about it.
    /// <para>
    /// A tester duplicated a realm on 1.2.6. The new realm's save folder was built by joining
    /// <c>servers/&lt;name&gt;</c> onto the stored app wide folder, which is the shipped default
    /// <c>%USERPROFILE%\AppData\LocalLow\IronGate\Valheim</c> and is a RELATIVE path until
    /// something expands it. Nothing did, so Windows anchored it at the working directory and
    /// made a folder called <c>%USERPROFILE%</c> inside the BakaLoader install, with the copied
    /// world inside that. 1.2.9 expands on every read, which means the app now looks in LocalLow
    /// and finds nothing: the worlds are still sitting in the install folder where the old build
    /// put them.
    /// </para>
    /// <para>
    /// So the fix needs a second half, and this is it. The folder is NEVER moved on its own.
    /// This answers which of five things is true, the page says it in one sentence naming every
    /// folder it is about, and the move happens when the host presses the button and not before.
    /// Hosts who already moved the folder by hand, like the tester did, land on
    /// <see cref="ShapeNone"/> and are told nothing at all.
    /// </para>
    /// <para>
    /// TWO REALMS READ ONE STORED STRING, and that is why this asks about WORLDS rather than
    /// about entries. A duplicated realm stores <c>&lt;app wide&gt;\servers\&lt;name&gt;</c> and
    /// the realm it came from stores nothing of its own, so it falls back to the app wide string,
    /// which is the PARENT of that. Moving the duplicate's folder leaves
    /// <c>%USERPROFILE%\AppData\LocalLow\IronGate\Valheim\servers</c> empty inside the install,
    /// and the sibling realm still asks about it. A row raised over that is false, permanent,
    /// and nothing the host can act on, and it sits above every other row that realm has. Both
    /// halves of this file answer it: the move takes the empty chain it made with it
    /// (<see cref="PruneEmptyFoldersAbove"/>), and a directory holding no worlds is not a save
    /// folder to offer (<see cref="HoldsWorlds"/>).
    /// </para>
    /// </summary>
    public static class StraySaveFolder
    {
        /// <summary>Nothing to say: no variable in the stored path, or the worlds are where the app looks.</summary>
        public const string ShapeNone = "none";

        /// <summary>
        /// The worlds are in a folder named for the variable inside the install, and the place
        /// the app reads now is not there at all. This is the shape the tester hit, and the only
        /// one that offers a move.
        /// </summary>
        public const string ShapeStray = "stray";

        /// <summary>
        /// Both folders hold worlds. Nothing is moved and nothing is guessed: merging two save
        /// folders is a decision about worlds, and the host is told both paths so they can make
        /// it with Explorer open.
        /// </summary>
        public const string ShapeBoth = "both";

        /// <summary>
        /// The worlds are in the folder named for the variable, and the folder the app reads IS
        /// there but holds no worlds, which is what a fresh save folder looks like. No move,
        /// because a move into a folder that already exists is a merge; but it is said out loud
        /// and it is said as an error, because this is the one shape where a start comes up on a
        /// brand new world with the real one sitting on the same disk.
        /// </summary>
        public const string ShapeDestinationEmpty = "destination_empty";

        /// <summary>
        /// MORE THAN ONE old folder holds worlds for this one stored string. Nothing is moved
        /// and every folder is named: a move would take one of them to the destination and
        /// leave the other where it is, which is a half repair reported as a whole one.
        /// <para>
        /// Reachable because a relative path is anchored at the WORKING DIRECTORY, and one
        /// install can have had more than one: a shortcut carrying a "Start in" of its own,
        /// and a service-style launch, leave their messes in different folders. The old build
        /// made its folder wherever it happened to be running from each time.
        /// </para>
        /// </summary>
        public const string ShapeMany = "many";

        /// <summary>
        /// Whether a stored path still has a variable in it that expansion will fill. Asked of
        /// the STORED string rather than of the resolved one, because the resolved one never
        /// has: a pair of percent marks around a name the environment knows.
        /// </summary>
        public static bool CarriesVariable(string stored)
        {
            var trimmed = (stored ?? "").Trim();
            if (trimmed.Length == 0) return false;

            // The test is the one that matters: does filling the variables in CHANGE the string.
            // A path with a stray percent in a folder name is not carrying a variable, and a
            // variable this machine has never heard of is not one either: expansion leaves both
            // exactly as they are, and neither can be the shape below.
            var expanded = PathCheck.Expand(trimmed);
            return !string.Equals(expanded, trimmed, StringComparison.Ordinal);
        }

        /// <summary>
        /// The path the OLD build would have made from this stored string: the raw text, still
        /// carrying its variable, anchored at the folder BakaLoader runs from. Null when the
        /// stored string is absolute to begin with, because then there was never a second place.
        /// </summary>
        public static string StrayPathFor(string stored, string installFolder)
        {
            var trimmed = (stored ?? "").Trim();
            if (trimmed.Length == 0 || string.IsNullOrWhiteSpace(installFolder)) return null;
            if (!CarriesVariable(trimmed)) return null;

            try
            {
                // Path.Combine would hand back the second argument whole if it were rooted, and
                // a raw "%USERPROFILE%\..." is not rooted, which is the entire bug. Combined and
                // then made full, so what comes back is one place and can be printed.
                var joined = Path.Combine(installFolder, trimmed);
                return Path.IsPathRooted(trimmed) ? null : Path.GetFullPath(joined);
            }
            catch { return null; }
        }

        /// <summary>
        /// Whether a folder holds SAVE DATA, which is the only thing worth telling a host about
        /// and the only thing worth offering to move.
        /// <para>
        /// Three ways to be one, and they are the three ways the game and this app write them:
        /// a <c>worlds_local</c> or <c>worlds</c> folder with anything in it, a world file
        /// sitting directly in the folder (a host may point a save folder at one), or a
        /// <c>servers</c> folder holding a realm folder that is one of those. The last is the
        /// one that matters here, because the app wide folder's whole content is per-realm
        /// folders under <c>servers</c>.
        /// </para>
        /// <para>
        /// WHAT THIS DELIBERATELY DOES NOT COUNT: a directory, however deep, with no world in
        /// it. That is what a move leaves above the folder it moved, what the by-hand repair in
        /// the wiki leaves if the host does the move and not the delete, and what an isolated
        /// realm that has never been started looks like. "Anything at all inside it" counted
        /// every one of those as a save folder.
        /// </para>
        /// </summary>
        public static bool HoldsWorlds(string folder) => HoldsWorlds(folder, 1);

        private static bool HoldsWorlds(string folder, int serversDepth)
        {
            if (string.IsNullOrWhiteSpace(folder)) return false;
            try
            {
                if (!Directory.Exists(folder)) return false;

                // A world the game wrote, straight in this folder. A save folder proper keeps
                // them one level down, but a host who typed the worlds folder itself into the
                // box has a save folder as far as every reader here is concerned.
                foreach (var file in Directory.EnumerateFiles(folder))
                    if (IsWorldFileName(Path.GetFileName(file))) return true;

                // The ordinary shape: worlds_local or worlds, with something in it. Anything at
                // all, because a world is a pair of files in one layout and a directory in the
                // other, and a backup layer with no live world beside it is still save data.
                foreach (var sub in WorldStore.WorldSubfolders)
                {
                    var dir = Path.Combine(folder, sub);
                    if (!Directory.Exists(dir)) continue;
                    if (AnythingIn(dir)) return true;
                }

                // And the app wide folder's shape: servers/<realm>, each of which is a save
                // folder of its own. One level, so a chain of empty directories cannot answer
                // yes by being deep.
                if (serversDepth > 0)
                {
                    var servers = Path.Combine(folder, IsolatedSaveFolder.ServersFolderName);
                    if (Directory.Exists(servers))
                        foreach (var realm in Directory.EnumerateDirectories(servers))
                            if (HoldsWorlds(realm, serversDepth - 1)) return true;
                }

                return false;
            }
            catch
            {
                // A drive that is not answering must not be read as "there are worlds here":
                // this runs on every load, and a false yes is a row the host cannot act on.
                return false;
            }
        }

        /// <summary>Whether anything at all is in a directory, with the drive allowed to say no.</summary>
        private static bool AnythingIn(string dir)
        {
            try
            {
                using var walk = Directory.EnumerateFileSystemEntries(dir).GetEnumerator();
                return walk.MoveNext();
            }
            catch { return false; }
        }

        /// <summary>
        /// Whether a file name is a world the game wrote: the pre-1.0 <c>.fwl</c> and <c>.db</c>
        /// pair, the 1.0 <c>.fwl2</c> and <c>.db2</c>, and the <c>.old</c> copy the game keeps
        /// of any of them.
        /// </summary>
        private static bool IsWorldFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;

            var trimmed = name;
            if (trimmed.EndsWith(".old", StringComparison.OrdinalIgnoreCase))
                trimmed = trimmed.Substring(0, trimmed.Length - 4);

            var extension = Path.GetExtension(trimmed);
            foreach (var world in new[] { ".fwl", ".fwl2", ".db", ".db2" })
                if (string.Equals(extension, world, StringComparison.OrdinalIgnoreCase)) return true;

            return false;
        }

        /// <summary>
        /// The one word for a row of facts. Pure: hand it a row and it answers the same way on
        /// every machine.
        /// </summary>
        public static string Judge(StrayFolderFacts facts)
        {
            // No variable means the old build could not have anchored it anywhere else, so there
            // is no second folder and nothing to say.
            if (!facts.StoredCarriesVariable) return ShapeNone;

            // A folder at the install root with no worlds in it is a leftover, not a save folder.
            // Offering to move it would be offering to move nothing, and SAYING anything about it
            // would be a row with no action and no truth in it.
            if (!facts.StrayExists || !facts.StrayHoldsWorlds) return ShapeNone;

            // Something is at the destination. Which sentence depends on whether it holds worlds:
            // two sets of worlds is a decision about worlds, and an empty folder where the app
            // reads is a start that would generate a new one.
            if (facts.ResolvedExists)
                return facts.ResolvedHoldsWorlds ? ShapeBoth : ShapeDestinationEmpty;

            return ShapeStray;
        }

        /// <summary>
        /// The facts, off the real disk. Every question is wrapped, because this runs on every
        /// load and a drive that is not answering must not be able to stop one.
        /// </summary>
        public static StrayFolderFacts Look(string stored, string installFolder)
        {
            var carries = CarriesVariable(stored);
            if (!carries) return new StrayFolderFacts();

            var resolved = PathCheck.Resolve(stored);
            var stray = StrayPathFor(stored, installFolder);

            bool resolvedThere = false, strayThere = false;
            try { resolvedThere = resolved.Length > 0 && Directory.Exists(resolved); } catch { }
            try { strayThere = stray != null && Directory.Exists(stray); } catch { }

            return new StrayFolderFacts
            {
                StoredCarriesVariable = true,
                ResolvedExists = resolvedThere,
                ResolvedHoldsWorlds = resolvedThere && HoldsWorlds(resolved),
                StrayExists = strayThere,
                StrayHoldsWorlds = strayThere && HoldsWorlds(stray),
            };
        }

        /// <summary>
        /// Which stored string a realm's row is about: its own if it named one, and otherwise the
        /// app wide one, which is the fallback every realm without a folder of its own shares.
        /// <para>
        /// Named rather than written out at the call site because it is the layer the whole
        /// sibling-realm case lives in: two realms, one string, and a move under one of them.
        /// </para>
        /// </summary>
        public static string StoredFor(string ownValue, string appWideValue)
            => !string.IsNullOrWhiteSpace(ownValue) ? ownValue : (appWideValue ?? "");

        /// <summary>
        /// The folders worth asking about, from the ones a caller can name: blanks dropped,
        /// each one made full, and the same folder never asked twice. A build started from a
        /// shortcut and one started as a service have different working directories, so the
        /// caller hands in both rather than guessing which it was.
        /// </summary>
        public static IEnumerable<string> FoldersToAsk(IEnumerable<string> candidates)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (candidates == null) yield break;

            foreach (var candidate in candidates)
            {
                if (string.IsNullOrWhiteSpace(candidate)) continue;
                string full;
                try { full = Path.GetFullPath(candidate); } catch { continue; }
                if (seen.Add(full)) yield return full;
            }
        }

        /// <summary>
        /// One realm's whole answer, asked of EVERY folder an older build could have anchored a
        /// relative path at. The first folder that has something to say is the one a move would
        /// act on, and the install folder that answered travels with it, because a move needs to
        /// know where it is allowed to tidy up to.
        /// <para>
        /// EVERY folder, not the first one. This used to stop at the first candidate with
        /// something to say, which on an install where both the program's own folder and the
        /// working directory hold a stray tree with worlds in it named one of the two and said
        /// nothing at all about the other. The host read a sentence about one folder, pressed
        /// the button, and a second tree of worlds stayed on the disk unmentioned. So the walk
        /// finishes, every folder that holds worlds is carried on the answer, and more than one
        /// of them is <see cref="ShapeMany"/>: no move, both named, because moving one and
        /// leaving the other is a half repair reported as a whole one.
        /// </para>
        /// </summary>
        public static StrayFolderAnswer For(string stored, IEnumerable<string> installFolders)
        {
            var resolved = PathCheck.Resolve(stored);
            var found = new List<(string Shape, string Stray, string Install)>();

            if (installFolders != null)
            {
                foreach (var root in installFolders)
                {
                    string shape;
                    try { shape = Judge(Look(stored, root)); }
                    catch { continue; }

                    if (string.Equals(shape, ShapeNone, StringComparison.Ordinal)) continue;

                    string stray;
                    try { stray = StrayPathFor(stored, root); }
                    catch { continue; }

                    found.Add((shape, stray, root));
                }
            }

            if (found.Count == 0)
                return new StrayFolderAnswer
                {
                    Shape = ShapeNone,
                    Stored = stored ?? "",
                    Resolved = resolved,
                    Stray = null,
                    Strays = Array.Empty<string>(),
                    Install = null,
                };

            var strays = new List<string>(found.Count);
            foreach (var one in found) strays.Add(one.Stray);

            return new StrayFolderAnswer
            {
                // One folder keeps the word the disk gave it. More than one is its own word,
                // whatever those folders said on their own: the decision the host is being
                // asked for is no longer "move this", it is "there are two of these".
                Shape = found.Count == 1 ? found[0].Shape : ShapeMany,
                Stored = stored ?? "",
                Resolved = resolved,
                Stray = found[0].Stray,
                Strays = strays,
                Install = found[0].Install,
            };
        }

        /// <summary>
        /// The name of one FACT about a stray save folder, so a row the host has waved away can
        /// be recognised when the same fact is read again.
        /// <para>
        /// WHY THIS EXISTS, and it is the same reason
        /// <see cref="BepInExNoticeKey"/> exists. Two of the three shapes are raised from facts
        /// BakaLoader cannot change: worlds in both folders, and a destination that is there
        /// holding nothing, are states only folder surgery in Explorer ends. The page asks on
        /// the first frame, on every realm switch and after every Save Config, so a dismissal
        /// that only cleared the bar meant the same sentence came back on the next boot, the
        /// next switch and the next save, for ever, over a decision the host had already made.
        /// </para>
        /// <para>
        /// A fact is named by WHAT IT IS: the shape, every folder it is about, and the folder
        /// the app reads. Nothing else, and nothing from the clock. The moment any of those
        /// changes the key changes with it and the row is news again, which is the whole point:
        /// a host who moves one of two trees by hand is told about what is left.
        /// </para>
        /// </summary>
        public static string KeyFor(StrayFolderAnswer answer)
        {
            var shape = answer.Shape;
            if (string.IsNullOrEmpty(shape) || string.Equals(shape, ShapeNone, StringComparison.Ordinal))
                return null;

            // The two separators are characters a Windows path cannot hold, so two different
            // facts cannot split into one key and silence a row the host has never read.
            return string.Join("|", shape, string.Join(";", answer.StrayFolders), answer.Resolved ?? "");
        }

        /// <summary>
        /// Whether the host has already waved away a row about this exact fact. Both sides null
        /// safe: an install that has waved away nothing holds no keys, and an answer that names
        /// no fact is never "seen".
        /// </summary>
        public static bool Seen(string key, IEnumerable<string> seenKeys)
        {
            if (string.IsNullOrEmpty(key) || seenKeys == null) return false;

            foreach (var held in seenKeys)
                if (string.Equals(held, key, StringComparison.Ordinal)) return true;

            return false;
        }

        /// <summary>How many waved-away facts an install remembers. See <see cref="Remember"/>.</summary>
        public const int KeysKept = 24;

        /// <summary>
        /// The list of waved-away facts with one more in it, newest last, no repeats, and never
        /// longer than <see cref="KeysKept"/>.
        /// <para>
        /// A LIST rather than the one field the BepInEx notice keeps, because this fact is per
        /// REALM where that one is per install: an isolated realm's folders are its own, so a
        /// host with four realms in two of these shapes would have had each dismissal undo the
        /// one before it. Bounded because nothing else ever takes an entry out: the ceiling is
        /// well past any install's realm count, and the oldest entry falling off the front is a
        /// row said once more rather than anything lost.
        /// </para>
        /// </summary>
        public static List<string> Remember(IEnumerable<string> seenKeys, string key, int keep = KeysKept)
        {
            var kept = new List<string>();
            if (seenKeys != null)
                foreach (var held in seenKeys)
                    if (!string.IsNullOrEmpty(held) && !string.Equals(held, key, StringComparison.Ordinal))
                        kept.Add(held);

            if (!string.IsNullOrEmpty(key)) kept.Add(key);

            var ceiling = keep > 0 ? keep : KeysKept;
            if (kept.Count > ceiling) kept.RemoveRange(0, kept.Count - ceiling);

            return kept;
        }

        /// <summary>
        /// Moves the stray folder to where the app reads now, and refuses rather than merging.
        /// <para>
        /// Three refusals, all of them before anything is touched: no stray folder to move, a
        /// folder already at the destination, and no destination at all. A move that cannot be
        /// a move is never half done.
        /// </para>
        /// <para>
        /// ACROSS DRIVES IS THE ORDINARY CASE HERE, not the odd one, and that is why this is
        /// not one call to <c>Directory.Move</c>. The stray folder is inside the BakaLoader
        /// install and the destination is under the user profile, which on the machine this was
        /// reported from were <c>H:</c> and <c>C:</c>. <c>Directory.Move</c> refuses two
        /// different roots outright, so a one-line version of this would have failed for the
        /// very host it was written for. Same root is still a rename, because a rename is
        /// instant and cannot half happen; different roots copy, verify the count of files that
        /// landed, and only then delete the source, so a copy that fails part way leaves the
        /// worlds where they were and takes the half-written destination away with it.
        /// </para>
        /// <para>
        /// AND IT TAKES THE SKELETON WITH IT. What is moved is a LEAF, usually
        /// <c>servers/&lt;realm&gt;</c>, and what is left behind is the chain of directories
        /// that leaf was under, which on the tester's shape is
        /// <c>%USERPROFILE%\AppData\LocalLow\IronGate\Valheim\servers</c> with nothing in it.
        /// Another realm reads the top of that chain, so leaving it there is leaving a row
        /// standing for good over a folder holding no worlds. <paramref name="installFolder"/>
        /// is where the prune stops, and it is handed in rather than worked out from the paths:
        /// how far up this is allowed to tidy is the caller's fact, not something to infer.
        /// </para>
        /// </summary>
        public static void Move(string stray, string destination, string installFolder)
        {
            if (string.IsNullOrWhiteSpace(stray) || !Directory.Exists(stray))
                throw new DirectoryNotFoundException(
                    "There is no folder to move at " + (stray ?? "(unset)") + ".");

            if (string.IsNullOrWhiteSpace(destination))
                throw new ArgumentException("There is nowhere to move the folder to.", nameof(destination));

            if (Directory.Exists(destination))
                throw new InvalidOperationException(
                    "There is already a folder at " + destination + ", so nothing was moved.");

            var parent = Path.GetDirectoryName(destination);
            if (!string.IsNullOrWhiteSpace(parent)) Directory.CreateDirectory(parent);

            if (SameRoot(stray, destination)) Directory.Move(stray, destination);
            else CopyAcrossVolumes(stray, destination);

            PruneEmptyFoldersAbove(stray, installFolder);
        }

        /// <summary>
        /// The across-drives arm of <see cref="Move"/>, named on its own so it can be driven
        /// directly: the mechanics are the same whichever volumes are involved, and a test
        /// cannot conjure a second volume.
        /// <para>
        /// Copy, count what landed, then delete. The count is the gate, because
        /// <c>File.Copy</c> raising is not the only way a copy can come up short: a cloud-sync
        /// handle or an antivirus can take a file out from under the enumeration. A copy that
        /// does not match leaves the worlds exactly where they were and takes the half-written
        /// destination away with it, so the row simply offers the move again.
        /// </para>
        /// </summary>
        public static void CopyAcrossVolumes(string stray, string destination)
        {
            var expected = Directory.GetFiles(stray, "*", SearchOption.AllDirectories).Length;
            try
            {
                WorldStore.CopyDirectory(stray, destination);

                var landed = Directory.GetFiles(destination, "*", SearchOption.AllDirectories).Length;
                if (landed != expected)
                    throw new IOException(
                        "Only " + landed.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + " of " + expected.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + " files reached " + destination + ", so nothing was deleted.");
            }
            catch
            {
                // The destination was not there when this started, so taking it away leaves the
                // disk as it was. The worlds have not been touched: they are still in the stray
                // folder, and the row will offer the move again.
                try { if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true); }
                catch { /* best effort: the throw below is what the host reads */ }
                throw;
            }

            Directory.Delete(stray, recursive: true);
        }

        /// <summary>
        /// Takes away the empty directories a moved leaf was sitting in, upward, and stops at
        /// the first one that still holds something.
        /// <para>
        /// Two rules keep this from being able to take anything a host wants. It never goes to
        /// <paramref name="boundary"/> or above it, which is the install folder the stray path
        /// was anchored at, so the folders BakaLoader itself lives in are out of reach whatever
        /// is passed in. And every delete is NON RECURSIVE: a directory that has gained a file
        /// since the move raises instead of being deleted with the file in it, and that raise
        /// stops the walk. So the worst this can do is less than it was asked to.
        /// </para>
        /// </summary>
        public static void PruneEmptyFoldersAbove(string leaf, string boundary)
        {
            if (string.IsNullOrWhiteSpace(leaf) || string.IsNullOrWhiteSpace(boundary)) return;

            string stop, at;
            try
            {
                stop = Settled(boundary);
                at = Path.GetDirectoryName(Settled(leaf));
            }
            catch { return; }

            while (!string.IsNullOrEmpty(at))
            {
                string here;
                try { here = Settled(at); }
                catch { return; }

                // The install folder itself, anything beside it, and anything above it: not ours.
                if (!IsStrictlyUnder(here, stop)) return;

                try
                {
                    if (Directory.Exists(here)) Directory.Delete(here);
                }
                catch
                {
                    // Still holds something, or the drive said no. Either way this is as far up
                    // as the tidy goes, and nothing above it would be empty anyway.
                    return;
                }

                at = Path.GetDirectoryName(here);
            }
        }

        /// <summary>One full path with no trailing separator, so two of them can be compared.</summary>
        private static string Settled(string path)
            => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

        /// <summary>
        /// Whether one path is INSIDE another, with equal paths answering no: this is the
        /// question "may the prune take this", and the boundary itself is never takeable.
        /// </summary>
        private static bool IsStrictlyUnder(string child, string parent)
        {
            if (string.IsNullOrEmpty(child) || string.IsNullOrEmpty(parent)) return false;
            if (child.Length <= parent.Length) return false;
            if (!child.StartsWith(parent, StringComparison.OrdinalIgnoreCase)) return false;

            var next = child[parent.Length];
            return next == Path.DirectorySeparatorChar || next == Path.AltDirectorySeparatorChar;
        }

        /// <summary>
        /// Whether two paths are on one volume, which is the question
        /// <see cref="Directory.Move(string, string)"/> answers by throwing.
        /// </summary>
        private static bool SameRoot(string a, string b)
        {
            try
            {
                return string.Equals(
                    Path.GetPathRoot(Path.GetFullPath(a)),
                    Path.GetPathRoot(Path.GetFullPath(b)),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }
    }
}
