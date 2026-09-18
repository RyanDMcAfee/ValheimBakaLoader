using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ValheimBakaLoader.Tests.Tools;
using Xunit;

namespace ValheimBakaLoader.Tests.Forms
{
    /// <summary>
    /// The last of the app.js wording to move into the catalog: the realm dialogs, the
    /// Log settings dialog, the Network card, the world-delete and Barrow dialogs, the
    /// Statistics reset, the Settings hall's own lines, the Herald and Waystone wizards,
    /// the Map hall's save note, the empty-state table and the world-difficulty dials.
    /// <para>
    /// These read the SOURCE, the way every gate in this suite does, and they read it
    /// from both ends: the page has to ask for the id, and the catalog has to answer it
    /// with the words that were there before. An assertion on only one of the two would
    /// pass on a dialog that draws a dotted name, or on an entry nothing reads.
    /// </para>
    /// </summary>
    public class WebUiDialogCopyTests
    {
        private static string AppJs() => AppSourceTree.Web("app.js");
        private static string Html() => AppSourceTree.Web("index.html");

        private static Dictionary<string, JsonElement> Catalog()
        {
            using var document = JsonDocument.Parse(AppSourceTree.Web("i18n/en.json"));
            var map = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var entry in document.RootElement.GetProperty("keys").EnumerateObject())
                map[entry.Name] = entry.Value.Clone();
            return map;
        }

        private static string Field(JsonElement entry, string name) =>
            entry.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() : null;

        private static readonly Regex TCall =
            new(@"(?<![A-Za-z0-9_$])T\(""([a-z][A-Za-z0-9_.]*)""\)", RegexOptions.Compiled);

        /// <summary>Every TT("literal") still in the page, by the English it carries.</summary>
        private static List<string> BridgedLiterals()
        {
            var source = AppJs();
            var found = new List<string>();
            foreach (Match match in Regex.Matches(source, @"(?<![A-Za-z0-9_$])TT\(""((?:[^""\\\n]|\\.)*)""\)"))
                found.Add(match.Groups[1].Value);
            return found;
        }

        // ------------------------------------------------- A. the dialogs ask by id

        /// <summary>
        /// One row per dialog this pass keyed: a sentence only that dialog says, the id
        /// it now asks for, and the English that id has to answer with. A dialog that
        /// went back to spelling its own words fails on the id; a catalog edit that
        /// reworded one of them fails on the English.
        /// </summary>
        public static IEnumerable<object[]> Sentences() => new[]
        {
            new object[] { "realm.delete.title", "Delete realm" },
            new object[] { "realm.restore.orphans.head", "Past worlds on disk" },
            new object[] { "realm.new.title", "Found a new realm" },
            new object[] { "realm.new.status.provisioning", "provisioning a separate install (this can take a moment)…" },
            new object[] { "saga.vellum.modal.title", "Log settings" },
            new object[] { "saga.vellum.folder.label", "Logs folder" },
            new object[] { "saga.divider.earlier", "earlier this session" },
            new object[] { "hearth.net.game_version", "Game version" },
            new object[] { "hearth.net.players.empty", "no vikings connected" },
            new object[] { "world.delete.title", "Delete this world?" },
            new object[] { "world.delete.warning", "There is no undo, and nothing else on this machine holds this world." },
            new object[] { "barrow.title", "Backups" },
            new object[] { "barrow.layer.restore.chip", "RESTORE" },
            new object[] { "barrow.layer.block.running", "Stop the server first" },
            new object[] { "skald.reset.confirm.title", "Reset the statistics?" },
            new object[] { "world.seed.not_created", "not created yet · seed set on first launch" },
            new object[] { "world.editbar.editing", "Editing" },
            new object[] { "herald.wiz.intro.title", "Summon the Herald" },
            new object[] { "herald.wiz.publish", "Publish the post" },
            new object[] { "waystone.wiz.intro.title", "Raise a Waystone" },
            new object[] { "waystone.wiz.check.stat.match", "the name answers with this server's IP, perfect" },
            new object[] { "atlas.save.none", "No save file yet. The clock starts with the first launch." },
            new object[] { "atlas.wx.anchor.live", "Anchored to the last world save." },
            new object[] { "setup.wiz.done.note", "Everything can be changed any time in the <strong>WORLD</strong> hall. Name your server, pick a world, and sail forth." },
        };

        [Theory]
        [MemberData(nameof(Sentences))]
        public void The_dialog_asks_for_the_id_and_the_catalog_answers_with_the_words(string id, string lore)
        {
            Assert.Contains("T(\"" + id + "\")", AppJs());
            var catalog = Catalog();
            Assert.True(catalog.ContainsKey(id), "the catalog lost " + id);
            Assert.Equal(lore, Field(catalog[id], "lore"));
        }

        /// <summary>
        /// The words a whole run-time family owns are asked for by app.js and by nothing
        /// else, the same rule the update and condition families already keep: an element
        /// the walker fills and a dialog overwrites is a sentence with two owners.
        /// </summary>
        [Theory]
        // realm 63 and waystone.wiz 46 after the last slice: the realm dialogs' own
        // buttons and toasts, and the four Waystone sentences that used to be built from
        // fragments either side of an address.
        [InlineData("realm.", 63)]
        [InlineData("barrow.", 54)]
        [InlineData("waystone.wiz.", 46)]
        [InlineData("world.wg.", 65)]
        public void Every_dialog_key_is_asked_for_by_app_js_and_by_nothing_else(string prefix, int howMany)
        {
            var source = AppJs();
            var html = Html();
            var mine = Catalog().Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal))
                                     .OrderBy(k => k, StringComparer.Ordinal).ToList();

            Assert.Equal(howMany, mine.Count);
            foreach (var id in mine)
            {
                Assert.True(source.Contains("\"" + id + "\"", StringComparison.Ordinal),
                            "app.js never asks for " + id);
                Assert.DoesNotContain("data-i18n=\"" + id + "\"", html);
                Assert.DoesNotContain("data-i18n-title=\"" + id + "\"", html);
                Assert.DoesNotContain("data-i18n-placeholder=\"" + id + "\"", html);
                Assert.DoesNotContain("data-i18n-aria=\"" + id + "\"", html);
            }
        }

        // ------------------------------------------------- B. the empty-state table

        /// <summary>
        /// emptyState() words nothing itself any more. It used to run its title, reason
        /// and button label through TT(), which made the table of empty states a table of
        /// English that no gate could see and no translator could reach. Every caller now
        /// asks the catalog first and hands the words in.
        /// </summary>
        [Fact]
        public void The_empty_state_shape_words_nothing_itself()
        {
            var body = Between(AppJs(), "function emptyState(o){", "\n}");

            Assert.DoesNotContain("TT(", body);
            Assert.Contains("${esc(o.title||T(\"common.empty.title\"))}", body);
            Assert.Contains("${esc(o.reason)}", body);
            Assert.Contains("${esc(a.label)}", body);
            Assert.Equal("Nothing here yet", Field(Catalog()["common.empty.title"], "lore"));
        }

        /// <summary>
        /// And every call site hands in catalog words. There is no exception left: the
        /// mod search's reason counts the rows it is hiding, and the Mods slice gave it a
        /// named slot and a plural category either side of the number, so it comes out of
        /// the catalog like the rest. Pinned by count so a new empty state written in
        /// English, or one that goes back to the bridge, fails here.
        /// </summary>
        [Fact]
        public void Every_empty_state_hands_in_words_the_catalog_owns()
        {
            var source = AppJs();
            var calls = Regex.Matches(source, @"emptyState\(\{(?:[^{}]|\{[^{}]*\})*\}\)");

            Assert.Equal(17, calls.Count);
            var composed = 0;
            foreach (Match call in calls)
            {
                var text = call.Value;
                foreach (var field in new[] { "title:", "reason:", "label:" })
                {
                    var at = text.IndexOf(field, StringComparison.Ordinal);
                    if (at < 0) continue;
                    var rest = text.Substring(at + field.Length);
                    if (rest.StartsWith("TT(", StringComparison.Ordinal)) { composed++; continue; }
                    Assert.True(rest.StartsWith("T(\"", StringComparison.Ordinal),
                                "an empty state still spells its own " + field + " " + rest.Substring(0, Math.Min(60, rest.Length)));
                }
            }

            Assert.Equal(0, composed);
            Assert.Contains("reason:T(\"mods.empty.no_match.reason\",{count:mods.length})", source);
        }

        // ------------------------------------------------- C. the world-difficulty dials

        /// <summary>
        /// The dial table holds catalog ids now. Its `label` is the one field that keeps
        /// its English beside the id, because one composed sentence still reads it: the
        /// first-run wizard's summary line. The forge dialog's help button used to be the
        /// second, and now names the same id the settings page does through `ariaId`.
        /// Keeping both halves is only safe while something holds them together, and that
        /// is the gate rule below this test.
        /// </summary>
        [Fact]
        public void The_world_dials_read_their_wording_out_of_the_catalog()
        {
            var source = AppJs();
            var catalog = Catalog();

            foreach (var dial in new[] { "combat", "deathpenalty", "resources", "raids", "portals" })
            {
                Assert.Contains("labelId:\"world.wg." + dial + ".label\"", source);
                Assert.Contains("introId:\"world.wg." + dial + ".intro\"", source);
                Assert.True(catalog.ContainsKey("world.wg." + dial + ".intro"),
                            "the catalog lost the " + dial + " dial's own sentence");
            }

            Assert.Equal("Combat", Field(catalog["world.wg.combat.label"], "lore"));
            Assert.Equal("Death penalty", Field(catalog["world.wg.deathpenalty.label"], "lore"));
            Assert.Equal("Hardcore, items and skills lost",
                         Field(catalog["world.wg.deathpenalty.hardcore.label"], "lore"));
            Assert.Equal("BakaLoader applies these settings every time the server starts, and clears any "
                         + "leftover difficulty keys first. A difficulty change made with the in-game "
                         + "console does not survive a restart.",
                         Field(catalog["world.wg.own_note"], "lore"));

            // the panel, the dropdown and the line under a dial all read the ids
            Assert.Contains("${esc(T(h.labelId))}", source);
            Assert.Contains("${esc(T(h.introId))}", source);
            Assert.Contains("${esc(T(o.labelId))}", source);
            Assert.Contains("${esc(T(o.explainId))}", source);
            Assert.Contains("return o?T(o.explainId):\"\";", source);
        }

        /// <summary>
        /// The keys the game itself sets are NOT wording. They are what a host reads off
        /// their own world-modifier menu to check a BakaLoader world against it, so they
        /// stay verbatim, are not in the catalog, and are not asked for by id.
        /// </summary>
        [Fact]
        public void The_console_keys_under_each_dial_are_never_translated()
        {
            var source = AppJs();
            var catalog = Catalog();

            Assert.Contains("effects:\"playerdamage 125, enemydamage 50, enemyspeedsize 90\"", source);
            Assert.Contains("effects:\"deathdeleteunequipped, skillreductionrate 150\"", source);
            Assert.Contains("effects:\"no keys set\"", source);
            Assert.DoesNotContain("effectsId:", source);
            Assert.DoesNotContain("${esc(T(o.effects", source);

            foreach (var entry in catalog.Values)
            {
                var lore = Field(entry, "lore");
                if (lore == null) continue;
                Assert.DoesNotContain("resourcerate", lore);
                Assert.DoesNotContain("eventrate", lore);
                Assert.DoesNotContain("playerdamage", lore);
            }
        }

        // ------------------------------------------------- D. what is deliberately left

        /// <summary>
        /// The bridge is gone, and so is the last sentence that went through it. TT() was
        /// the migration's seam - the catalog first, the old regex swap underneath - and
        /// L2b deleted both arms. The count stays as the gate: a new English sentence
        /// written straight into app.js and wrapped in TT() would land here, and there is
        /// no TT() left for it to be wrapped in.
        /// <para>
        /// The one sentence that was left, "no reply from the server", sat inside a
        /// logLine body. The log stays English and verbatim by decision, so it is a plain
        /// literal there now rather than a call into a lookup that would never have
        /// changed it.
        /// </para>
        /// </summary>
        [Fact]
        public void Nothing_goes_through_the_bridge_because_there_is_no_bridge()
        {
            var left = BridgedLiterals();
            Assert.Empty(left);

            var js = AppJs();
            Assert.DoesNotContain("function TT(", js);
            Assert.DoesNotContain("function plainify(", js);
            Assert.DoesNotContain("const TERM_PAIRS=", js);
            // and the log line kept its English, as a literal rather than a lookup
            Assert.Contains("(said||\"no reply from the server\")", js);

            // Nothing this pass keyed is still spelled out beside its id. The dashboard
            // slice took the rest of the Hearth, the sidebar, the condition bar and the
            // two update dialogs off this list with it.
            foreach (var sentence in new[]
            {
                "Delete realm", "Log settings", "Reset the statistics?", "Summon the Herald",
                "Raise a Waystone", "Backups", "Layers", "World", "Cancel", "Name", "Password",
                " online", "ready", "is ready", "last ran Valheim ", "a build it has not recorded",
                "Steam is downloading: ", "no reason was given", "The update did not finish: ",
                "The server could not write the world to disk", " is available.",
                // and the Mods slice, which took the row pills, the bulk bar, the row menu,
                "Failed", "unknown", "Updating", "Updating ", "Updating mods", "Updated",
                // the Update all and remove flows, and both install paths with it.
                "failed", "already up to date", "Fetching from Hexium", "Installed from Hexium",
                "Hexium install did not go ahead", "Hexium page did not open · ",
                // "unknown error" went with them: every fallback that said it now asks
                // for common.error.unknown, which is a whole sentence with an entry.
                "unknown error",
                "Thunderstore page did not open · ",
                // and the roster and Configs slice, which took the last two lists of
                // fragments off the bridge: the two words either side of a pair of numbers,
                // the word that joined a list in English order, and the four that were glued
                // to a player, a column or a stack count.
                "showing", "of", " and ", "Actions for ", "position", "deaths",
                "spawn failed", "server running & player online?", "level", "quality",
                // and the Settings, World and Barrow slice, which took the last three
                // fragments this list vouched for with it: the two either side of a world
                // name in the delete prompt, and the clause naming which files go with a
                // layer. Each is now inside a whole sentence with a named slot, so the
                // delete prompt is two sentences and the layer prompt three rather than
                // one sentence with a noun phrase swapped into the middle.
                " for good:", "its .fwl and .db pair", "its whole world folder",
                "and its paired .db", "and everything inside it",
                "will be deleted from disk. This cannot be undone.",
                "Nothing else on this machine holds this world.",
                "This deletes", "World deleted", "Layer deleted", "Layer unearthed",
                "World brought back", "safety copy laid down", "folder",
                "is running this world right now. Stop the server first.",
                "There is no live world for this layer any more.",
                "The live realm is copied to a fresh safety layer first, then",
                "will claim game port ", "Password rule", "ᛟ seed copied · ",
                // and the last slice, which took the Map, the Log's own chrome, Discord,
                // Statistics, the palette's refusals and every toast and dialog literal
                // left anywhere in the page. These four were the ones this list vouched
                // for a moment ago; each is inside a whole sentence with a named slot now.
                "preview only", "alight", "the webhook answers", "back to the raw IP",
                "Statistics reset", "old journal kept as", "unique souls", "visits",
                "chronicled since ", "counted on this machine only, nothing leaves it",
                "The Waystone stands", "The Waystone falls", "a well-formed name",
                "the name does not resolve yet", "give DNS a few minutes, then Check again",
                "Teach DNS that", "lives at this server's public IP:", "BakaLoader asks DNS for",
                "and compares the answer with this server's public IP.",
                "the name only replaces the IP, so friends still need the port",
                ", and port-forwarding stays exactly as it is today.",
                "Start held · ", "Could not start the server · ", "see the log for what stopped it",
                "ᚦ Server crashed · consult the saga", "The save could not be read: ",
                "⌂ Realm restored · ", "↺ Realm restored · ", "ᛒ Helm turned · ", "helm turned · ",
                "session · ", "files · ", " · cannot be undone", "world", "worlds",
            })
            {
                Assert.DoesNotContain(sentence, left);
            }
        }

        /// <summary>
        /// The Log settings dialog's file-name note got the slots it was waiting for. The
        /// catalog's markup rule reads &lt;realm&gt; as a tag and marking the entry allowsHtml
        /// would be a lie on a value that goes through esc(), so the sentence names two
        /// slots and the angle brackets are put round the words at the call site, where
        /// they are punctuation rather than wording. The host reads what they read before.
        /// </summary>
        [Fact]
        public void The_note_with_angle_brackets_carries_slots_instead()
        {
            var catalog = Catalog();
            var note = Field(catalog["saga.vellum.serverlog.note"], "lore");

            Assert.StartsWith("each server session writes its own scroll: ServerLogs-{server}-{start}.txt",
                              note, StringComparison.Ordinal);
            Assert.DoesNotContain("<", note);
            Assert.False(catalog["saga.vellum.serverlog.note"].TryGetProperty("allowsHtml", out _),
                         "the note goes through esc(), so it may not claim markup");
            Assert.Equal("realm", Field(catalog["saga.vellum.serverlog.realm"], "lore"));
            Assert.Equal("start time", Field(catalog["saga.vellum.serverlog.start"], "lore"));

            // and the brackets are the call site's, round the two words the catalog owns
            var js = AppJs();
            Assert.Contains("{server:\"<\"+T(\"saga.vellum.serverlog.realm\")+\">\"", js);
            Assert.Contains("start:\"<\"+T(\"saga.vellum.serverlog.start\")+\">\"", js);
            Assert.DoesNotContain(BridgedLiterals(),
                                  left => left.Contains("ServerLogs-", StringComparison.Ordinal));
        }

        /// <summary>
        /// allowsHtml is on the entries whose call site writes them into the page without
        /// esc(), and on no others. The wizards are the only surfaces that do that, and
        /// they do it because their steps carry &lt;strong&gt; and &lt;span class="mono"&gt;.
        /// </summary>
        [Fact]
        public void Only_the_wizard_steps_are_allowed_to_carry_markup()
        {
            var catalog = Catalog();
            var html = catalog.Where(pair => pair.Value.TryGetProperty("allowsHtml", out var flag)
                                             && flag.ValueKind == JsonValueKind.True)
                              .Select(pair => pair.Key)
                              .OrderBy(k => k, StringComparer.Ordinal).ToList();

            Assert.NotEmpty(html);
            foreach (var id in html)
            {
                Assert.True(id.StartsWith("herald.wiz.", StringComparison.Ordinal)
                            || id.StartsWith("waystone.wiz.", StringComparison.Ordinal)
                            || id.StartsWith("setup.wiz.", StringComparison.Ordinal),
                            id + " says allowsHtml but is not a wizard step");
                // Either shape of the call, and neither of them escaped: a wizard step that
                // names a slot is written in exactly as one that does not, which is the
                // point of the flag. What may never appear is esc() round the lookup, so
                // that is asserted rather than inferred from the shape.
                var js = AppJs();
                Assert.True(js.Contains("${T(\"" + id + "\")}", StringComparison.Ordinal)
                            || js.Contains("+T(\"" + id + "\")+", StringComparison.Ordinal)
                            || js.Contains("${T(\"" + id + "\",", StringComparison.Ordinal),
                            id + " says allowsHtml but nothing writes it into the page");
                Assert.DoesNotContain("esc(T(\"" + id + "\"", js);
            }
        }

        // ------------------------------------------- E. a dialog follows the language

        /// <summary>
        /// The arguments that are not copy, and why. The rename prompt's placeholder is
        /// the realm's CURRENT NAME: data the host typed, which reads the same in every
        /// language and has nothing to ask the catalog for. Anything else that wants on
        /// this list is a sentence, and a sentence belongs in a thunk.
        /// </summary>
        private static readonly HashSet<string> NotCopy = new(StringComparer.Ordinal)
        {
            "promptModal(2): name",
        };

        /// <summary>
        /// Every dialog is handed its words as something that can be ASKED AGAIN.
        /// <para>
        /// confirmModal and promptModal both keep the arrow that built them and run it
        /// again when the language changes, and that redraw puts the same arguments back
        /// through worded(). An argument that was a STRING when the call was made is the
        /// string it was: the dialog on screen keeps the wording it opened with while
        /// every other surface switches, and nothing reports it, because from the
        /// dialog's own side nothing went wrong. So the shape is the rule.
        /// </para>
        /// <para>
        /// A bare T("id") in the argument list is refused for the same reason a literal
        /// is: it is worded at the moment of the call, once. What passes is an arrow, or
        /// a name the file declares as one, which is how the two dialogs that build a
        /// body out of several sentences hand theirs over.
        /// </para>
        /// </summary>
        [Fact]
        public void Every_dialog_is_handed_its_words_as_something_that_can_be_asked_again()
        {
            var js = AppJs();
            var code = CodeOnly(js);
            var frozen = new List<string>();
            var sites = 0;

            foreach (var (name, howMany) in new[] { ("confirmModal", 3), ("promptModal", 2) })
            {
                var call = name + "(";
                for (var at = code.IndexOf(call, StringComparison.Ordinal); at >= 0;
                         at = code.IndexOf(call, at + 1, StringComparison.Ordinal))
                {
                    // the declaration itself is not a call site
                    var before = at - "function ".Length;
                    if (before >= 0 && string.CompareOrdinal(js, before, "function ", 0, "function ".Length) == 0)
                        continue;

                    sites++;
                    var line = js.Take(at).Count(c => c == '\n') + 1;
                    var arguments = Arguments(js, code, at + call.Length, howMany);
                    for (var which = 0; which < Math.Min(howMany, arguments.Count); which++)
                    {
                        var argument = arguments[which].Trim();
                        if (NotCopy.Contains(name + "(" + (which + 1) + "): " + argument)) continue;
                        if (Regex.IsMatch(argument, @"^(?:async\s*)?\(\s*\)\s*=>")) continue;
                        if (Regex.IsMatch(argument, @"^[A-Za-z_$][A-Za-z0-9_$]*$")
                            && DeclaredAsAThunk(js, argument)) continue;

                        frozen.Add($"app.js:{line} {name} argument {which + 1} is "
                                   + Shorten(argument) + ", which is worded once and never again");
                    }
                }
            }

            // Written down so a scanner that quietly stops finding calls fails too. A new
            // dialog moves this number and reads the rule above on its way past.
            Assert.Equal(17, sites);
            Assert.True(frozen.Count == 0,
                "a dialog would keep its wording through a language switch:\n  "
                + string.Join("\n  ", frozen));
        }

        private static string Shorten(string argument) =>
            argument.Length <= 60 ? argument : argument.Substring(0, 57) + "...";

        /// <summary>A name app.js declares as an arrow that takes nothing, or as a function.</summary>
        private static bool DeclaredAsAThunk(string js, string name) =>
            Regex.IsMatch(js, @"\b(?:const|let|var)\s+" + Regex.Escape(name) + @"\s*=\s*(?:async\s*)?\(\s*\)\s*=>")
            || Regex.IsMatch(js, @"\bfunction\s+" + Regex.Escape(name) + @"\s*\(");

        /// <summary>
        /// The first few arguments of the call whose bracket is at <paramref name="from"/>,
        /// split on the commas that are the call's own. The masked copy is what the split
        /// reads, so a comma inside a string, a template or a comment is not a comma here;
        /// the text handed back is the real one.
        /// </summary>
        private static List<string> Arguments(string js, string code, int from, int howMany)
        {
            var found = new List<string>();
            var depth = 0;
            var start = from;
            for (var i = from; i < code.Length && found.Count < howMany; i++)
            {
                var c = code[i];
                if (c == '(' || c == '[' || c == '{') depth++;
                else if (c == ')' || c == ']' || c == '}')
                {
                    if (depth == 0) { found.Add(js.Substring(start, i - start)); break; }
                    depth--;
                }
                else if (c == ',' && depth == 0)
                {
                    found.Add(js.Substring(start, i - start));
                    start = i + 1;
                }
            }
            return found;
        }

        /// <summary>
        /// The source with every string, template, regular expression and comment blanked
        /// out, the same length and the same line breaks, so a bracket inside a sentence
        /// never closes a call and the word confirmModal inside a comment is never read as
        /// one. The regular expression arm earns its keep on one line: the escaper's
        /// character class holds both kinds of quote, and without it every quote after it
        /// pairs up with the wrong one.
        /// </summary>
        private static string CodeOnly(string js)
        {
            var made = js.ToCharArray();
            void Wipe(int at) { if (at < made.Length) made[at] = js[at] == '\n' ? '\n' : ' '; }

            const string StartsRegex = "(,=:[!&|?{};+-*%~^<>\n";
            var templates = new Stack<int>();       // the brace depth each ${ } opened at
            var braces = 0;
            var inTemplate = false;
            var previous = '\0';
            var i = 0;

            while (i < js.Length)
            {
                var c = js[i];

                if (inTemplate)
                {
                    if (c == '\\') { Wipe(i); Wipe(i + 1); i += 2; continue; }
                    if (c == '`') { Wipe(i); inTemplate = false; i++; continue; }
                    if (c == '$' && i + 1 < js.Length && js[i + 1] == '{')
                    {
                        Wipe(i); Wipe(i + 1);
                        templates.Push(braces);
                        inTemplate = false;
                        i += 2;
                        continue;
                    }
                    Wipe(i);
                    i++;
                    continue;
                }

                if (c == '/' && i + 1 < js.Length && js[i + 1] == '/')
                {
                    while (i < js.Length && js[i] != '\n') { Wipe(i); i++; }
                    continue;
                }
                if (c == '/' && i + 1 < js.Length && js[i + 1] == '*')
                {
                    while (i + 1 < js.Length && !(js[i] == '*' && js[i + 1] == '/')) { Wipe(i); i++; }
                    Wipe(i); Wipe(i + 1);
                    i += 2;
                    continue;
                }
                if (c == '/' && (previous == '\0' || StartsRegex.IndexOf(previous) >= 0))
                {
                    Wipe(i); i++;
                    while (i < js.Length && js[i] != '/' && js[i] != '\n')
                    {
                        if (js[i] == '\\') { Wipe(i); Wipe(i + 1); i += 2; continue; }
                        Wipe(i); i++;
                    }
                    if (i < js.Length && js[i] == '/') { Wipe(i); i++; }
                    previous = 'x';
                    continue;
                }
                if (c == '"' || c == '\'')
                {
                    Wipe(i); i++;
                    while (i < js.Length && js[i] != c)
                    {
                        if (js[i] == '\\') { Wipe(i); Wipe(i + 1); i += 2; continue; }
                        Wipe(i); i++;
                    }
                    if (i < js.Length) { Wipe(i); i++; }
                    previous = 'x';
                    continue;
                }
                if (c == '`') { Wipe(i); inTemplate = true; previous = 'x'; i++; continue; }

                if (c == '{') braces++;
                else if (c == '}')
                {
                    if (templates.Count > 0 && braces == templates.Peek())
                    {
                        templates.Pop();
                        Wipe(i);
                        inTemplate = true;
                        i++;
                        continue;
                    }
                    braces--;
                }
                if (!char.IsWhiteSpace(c) || c == '\n') previous = c;
                i++;
            }
            return new string(made);
        }

        private static string Between(string source, string open, string shut)
        {
            var from = source.IndexOf(open, StringComparison.Ordinal);
            Assert.True(from >= 0, "the source no longer holds " + open);
            var to = source.IndexOf(shut, from + open.Length, StringComparison.Ordinal);
            Assert.True(to > from, "the source no longer closes " + open);
            return source.Substring(from, to - from);
        }
    }
}
