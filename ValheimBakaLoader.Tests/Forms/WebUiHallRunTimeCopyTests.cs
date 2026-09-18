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
    /// The run-time half of three halls: the mod table with its pills, tags, marks, row menu
    /// and the two dialogs that fetch from the second site; the roster with its dash tooltips
    /// and its row menu's refusals; and the config list with its two empty lines, its find
    /// count and the question it asks over unsaved work. Their words used to be English
    /// literals inside app.js and are now catalog ids.
    /// <para>
    /// WebUiHallCopyTests holds the STATIC half of the same three halls, and
    /// WebUiStaticCopyTests the general rule for the whole page. This file is what a general
    /// gate cannot be: the inventory of which run-time sentences are keyed, which are
    /// deliberately still composed in code and why, and the three decisions inside those keys
    /// that nothing else would notice going wrong - the plain register of a sentence the
    /// terminology switch rewords, the one sentence that deliberately has none, and the ids
    /// the search haystack shares with the row it has to find.
    /// </para>
    /// <para>
    /// And the defect this slice had to close before any of it was safe: renderMods,
    /// renderPlayers and renderCfgList all paint while app.js is still being evaluated, the
    /// preview feeding them real rows on the way past. The catalog is fetched, so a T() call
    /// on that road answers with its own id. All three are in repaintBootCopy now, and the
    /// last four tests below drop each one from it and insist the first-frame gate says so.
    /// </para>
    /// </summary>
    public class WebUiHallRunTimeCopyTests : IDisposable
    {
        private static string AppJs() => AppSourceTree.Web("app.js");

        private static string Html() => AppSourceTree.Web("index.html");

        private static string FirstFrameGate() =>
            RepoScript.At("scripts", "i18n", "check_first_frame.py");

        private readonly List<string> _scratch = new();

        public void Dispose()
        {
            foreach (var folder in _scratch)
            {
                try { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
                catch (IOException) { /* a scanner still holding a temp folder */ }
            }
        }

        /// <summary>The shipped app.js with one edit, written somewhere throwaway. The edit
        /// has to bite: a fixture that quietly failed to change anything would make the
        /// assertion that follows it meaningless.</summary>
        private string Fixture(string find, string replace)
        {
            var source = AppJs();
            Assert.Contains(find, source);
            var folder = Path.Combine(Path.GetTempPath(), "vbl-hallcopy-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            _scratch.Add(folder);
            var path = Path.Combine(folder, "app.js");
            File.WriteAllText(path, source.Replace(find, replace), new UTF8Encoding(false));
            return path;
        }

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
                ? value.GetString()
                : null;

        /// <summary>One CLDR category of a plural field, or null when it is not there.</summary>
        private static string Plural(JsonElement entry, string name, string category) =>
            entry.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object
            && value.TryGetProperty(category, out var picked) && picked.ValueKind == JsonValueKind.String
                ? picked.GetString()
                : null;

        private static string Between(string source, string from, string to)
        {
            var start = source.IndexOf(from, StringComparison.Ordinal);
            Assert.True(start >= 0, "app.js no longer has " + from);
            var end = source.IndexOf(to, start, StringComparison.Ordinal);
            Assert.True(end > start, from + " no longer ends at " + to);
            return source.Substring(start, end - start);
        }

        private static readonly Regex AskedById =
            new(@"(?<![A-Za-z0-9_$])T\(""([a-z][a-z0-9_.]*)""", RegexOptions.Compiled);

        /// <summary>
        /// The other way a surface asks for an id: a table that holds ids rather than
        /// English, in a property whose name ends in Id. The roster's Status pill and its
        /// two player lists are rendered in a loop off such a table, so their ids never
        /// appear inside a T("...") call the regex above can see. This is the same shape
        /// check_catalog.py counts as "asked for", so the gate and this file agree on what
        /// an orphan is.
        /// </summary>
        private static readonly Regex AskedByTable =
            new(@"(?<![A-Za-z0-9_$])[A-Za-z0-9_$]*Id\s*:\s*""([a-z][a-z0-9_.]*)""", RegexOptions.Compiled);

        private static List<string> IdsAskedIn(string block) =>
            AskedById.Matches(block).Select(m => m.Groups[1].Value)
                .Concat(AskedByTable.Matches(block).Select(m => m.Groups[1].Value))
                .Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();

        // ------------------------------------------------- A. the inventory of the surfaces

        /// <summary>
        /// Every prefix this slice owns, with the surface it belongs to. A key under one of
        /// these is run-time copy: app.js asks for it by id, and the walker must never also
        /// write it, because an element the walker fills and app.js overwrites is a sentence
        /// with two owners and the loser is whichever ran second.
        /// </summary>
        public static IEnumerable<object[]> Prefixes() => new[]
        {
            new object[] { "mods.tag.", "the chips beside a mod's name" },
            new object[] { "mods.status.", "the Status pill" },
            new object[] { "mods.possibly_outdated.", "the Possibly outdated cell" },
            new object[] { "mods.row.", "the transient pill a bulk update writes on a row" },
            new object[] { "mods.mark.", "the mark hanging off the Latest cell" },
            new object[] { "mods.progress.", "the bulk update bar" },
            new object[] { "mods.showing.", "what Update all says while a search narrows the table" },
            new object[] { "mods.menu.", "the row menu" },
            new object[] { "mods.hexium.consent.", "the dialog that asks before fetching from the second site" },
            new object[] { "mods.swap.", "the dialog that hands a mod back to Thunderstore" },
            new object[] { "vikings.row.", "the roster's cells and their tooltips" },
            new object[] { "vikings.menu.", "the roster row menu's labels and its refusals" },
            new object[] { "vikings.cols.", "the note naming the columns the window is too narrow for" },
            new object[] { "vikings.status.", "the word in the Status pill" },
            new object[] { "vikings.list.", "the admin, whitelist and ban lists this menu writes" },
            new object[] { "vikings.spawn.", "the spawn-item dialog and what it says afterwards" },
            new object[] { "vikings.caps.install.", "the missing-mods banner's button and its answers" },
            new object[] { "runes.dirty.", "the question the Configs hall asks over unsaved work" },
            new object[] { "runes.save.", "the Save button, both sides of its confirm" },
        };

        [Theory]
        [MemberData(nameof(Prefixes))]
        public void Every_run_time_key_is_asked_for_by_app_js_and_never_also_by_the_walker(
            string prefix, string surface)
        {
            var catalog = Catalog();
            var asked = new HashSet<string>(IdsAskedIn(AppJs()), StringComparer.Ordinal);
            var walked = new HashSet<string>(
                Regex.Matches(Html(), @"data-i18n(?:-title|-placeholder|-aria)?=""([^""]+)""")
                    .Select(m => m.Groups[1].Value), StringComparer.Ordinal);

            var mine = catalog.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList();
            Assert.True(mine.Count > 0, "the catalog has nothing under " + prefix + " (" + surface + ")");

            foreach (var id in mine)
            {
                Assert.True(asked.Contains(id), id + " is in the catalog and nothing asks for it");
                Assert.False(walked.Contains(id),
                    id + " is written by app.js AND by the walker, so one of them loses");
                // A lore of some kind: a sentence is a string, a count phrase is an object
                // of CLDR categories, and an entry with neither is not an entry.
                Assert.True(catalog[id].TryGetProperty("lore", out var lore)
                            && (lore.ValueKind == JsonValueKind.String
                                || lore.ValueKind == JsonValueKind.Object),
                            id + " has no lore wording");
            }
        }

        /// <summary>
        /// Four sentences two surfaces share on purpose, because they are the same sentence.
        /// Spelling them twice is how a copy edit reaches one and not the other.
        /// </summary>
        [Fact]
        public void The_ids_two_surfaces_share_are_shared_deliberately()
        {
            var js = AppJs();

            // The swap dialog's own button and the row menu entry that opens it say the
            // same thing, so they are one key.
            Assert.Equal(2, Regex.Matches(js, Regex.Escape("T(\"mods.menu.install_thunderstore\")")).Count);
            // Both dialogs label the version the same way, and so would a third.
            Assert.Equal(2, Regex.Matches(js, Regex.Escape("T(\"common.label.version\")")).Count);
            // Playtime and Deaths both fall back to the same dash tooltip.
            Assert.Equal(2, Regex.Matches(js, Regex.Escape("T(\"vikings.row.nothing_recorded.title\")")).Count);

            // Opening a scroll and reloading the vault ask the same question over the same
            // unsaved work. The question and the button under it are one wording each; the
            // unsaved-work clause used to be a third shared key, spliced into both bodies,
            // and is now written out inside each whole message instead - so this holds the
            // two English halves together where the shared id used to.
            Assert.Equal(2, Regex.Matches(js, Regex.Escape("T(\"runes.dirty.title\")")).Count);
            var catalog = Catalog();
            const string unsaved = "{file} has unsaved rune-work. ";
            Assert.StartsWith(unsaved, Field(catalog["runes.dirty.open.body"], "lore"));
            Assert.StartsWith(unsaved, Field(catalog["runes.dirty.reload.body"], "lore"));
            Assert.StartsWith("{file} has unsaved changes. ", Field(catalog["runes.dirty.open.body"], "plain"));
            Assert.StartsWith("{file} has unsaved changes. ", Field(catalog["runes.dirty.reload.body"], "plain"));
            Assert.DoesNotContain("runes.dirty.sentence", js);
        }

        // ------------------------------------------------- B. the row menu and the roster

        /// <summary>
        /// Every label and every refusal in the mod row menu comes out of the catalog. A
        /// disabled entry still reads its reason out loud, so the tips matter as much as the
        /// labels, and a bare English one left among them would be the only line of the menu
        /// that never translates.
        /// </summary>
        [Fact]
        public void The_mod_row_menu_reads_every_label_and_every_refusal_from_the_catalog()
        {
            // The menu ends where the helper that writes a checked row begins: everything
            // after that belongs to the check itself and has its own inventory.
            var menu = Between(AppJs(), "function modRowItems(mod){",
                "/* What one check's answer does to the row");

            Assert.Equal(new[]
            {
                "mods.menu.check_one", "mods.menu.check_one.tip.bundled",
                "mods.menu.check_one.tip.no_package",
                "mods.menu.hexium", "mods.menu.install_hexium", "mods.menu.install_thunderstore",
                "mods.menu.remove", "mods.menu.thunderstore", "mods.menu.thunderstore.tip.missing",
                "mods.menu.update", "mods.menu.update.tip.current", "mods.menu.update.tip.hexium",
                "mods.menu.update.tip.not_listed", "mods.menu.update.tip.patcher",
            }, IdsAskedIn(menu).ToArray());

            // Nothing in the menu still spells a label or a reason out in English.
            Assert.DoesNotContain("label:\"", menu);
            Assert.DoesNotContain("tip:\"", menu);
            Assert.DoesNotContain("TT(\"", menu);
        }

        /// <summary>
        /// A dash in the roster is never bare: every one of them says why it is a dash, and
        /// the reasons are three different facts (not connected, nothing in the journal, no
        /// position without RCON) that a host reads to know which thing to go and fix.
        /// </summary>
        [Fact]
        public void Every_dash_in_the_roster_still_says_why_it_is_a_dash()
        {
            var catalog = Catalog();
            var roster = Between(AppJs(), "function renderPlayers(){", "wireSort(\"#page-vikings th.sortable\"");

            Assert.Contains("T(\"vikings.row.no_position.title\")", roster);
            Assert.Contains("T(\"vikings.row.not_online.title\")", roster);
            Assert.Contains("T(\"vikings.row.nothing_recorded.title\")", roster);
            Assert.Contains("T(\"vikings.row.online_now\")", roster);
            Assert.Contains("T(\"vikings.row.actions.title\")", roster);

            Assert.Equal("Not online right now", Field(catalog["vikings.row.not_online.title"], "lore"));
            Assert.Equal("Nothing recorded for this player yet",
                Field(catalog["vikings.row.nothing_recorded.title"], "lore"));
            Assert.Equal("online now", Field(catalog["vikings.row.online_now"], "lore"));
            // The one that names the mod a host has to install for the column to fill.
            Assert.Contains("RCON", Field(catalog["vikings.row.no_position.title"], "lore"));

            // Every one of them reaches the DOM through esc(), which is where a player's own
            // display name is also going.
            foreach (var id in new[]
            {
                "vikings.row.not_online.title", "vikings.row.nothing_recorded.title",
                "vikings.row.online_now", "vikings.row.actions.title",
            })
            {
                Assert.Contains("esc(T(\"" + id + "\"))", roster);
            }
            // The position tooltip is looked up once above the row loop rather than on every
            // row, so it is escaped where it is used instead.
            Assert.Contains("const noPos=T(\"vikings.row.no_position.title\");", roster);
            Assert.Contains("title=\"${esc(noPos)}\"", roster);
        }

        /// <summary>
        /// The two refusals on the roster menu are different sentences on purpose: a player
        /// still joining is CONNECTED, so a kick reaches them, while heal, smite, teleport
        /// and spawn want a character standing in the world. One sentence for both would say
        /// the wrong thing to half the menu.
        /// </summary>
        [Fact]
        public void The_roster_menus_two_refusals_stay_two_sentences()
        {
            var catalog = Catalog();
            var menu = Between(AppJs(), "async function openPlayerMenu(x,y,p){", "async function doPlayerAct(");

            Assert.Contains("const noLive=T(\"vikings.menu.tip.online_only\");", menu);
            Assert.Contains("const noConn=T(\"vikings.menu.tip.connected_only\");", menu);

            var online = Field(catalog["vikings.menu.tip.online_only"], "lore");
            var connected = Field(catalog["vikings.menu.tip.connected_only"], "lore");
            Assert.Equal("Only works while the player is online.", online);
            Assert.Equal("Only works while the player is connected.", connected);
            Assert.NotEqual(online, connected);

            // The two refusals a missing mod raises are the menu's other pair, and they
            // name different mods, so they are two sentences for the same reason.
            Assert.Contains("const noR=T(\"vikings.menu.tip.no_rcon\"), noD=T(\"vikings.menu.tip.no_devcommands\");", menu);
            Assert.Equal("requires the RCON mod", Field(catalog["vikings.menu.tip.no_rcon"], "lore"));
            Assert.Equal("requires the devcommands mods", Field(catalog["vikings.menu.tip.no_devcommands"], "lore"));

            // And nothing in the menu spells a label, a reason or a finished sentence out
            // in English any more, which is the rule the mod row menu already keeps.
            Assert.DoesNotContain("label:\"", menu);
            Assert.DoesNotContain("tip:\"", menu);
            Assert.DoesNotContain("TT(", menu);
        }

        // ------------------------------------------------- C. the plain register

        /// <summary>
        /// The Configs hall is where the Norse wording actually bites, and the catalog is now
        /// the thing that holds both registers. Two of its sentences carry Norse words the
        /// terminology switch rewords, so each names the plain wording the swap produced, and
        /// one does not, because the swap never reached it. Naming a plain register there
        /// would move English under a switch no English screenshot is ever taken with, which
        /// is exactly the regression nothing else in the suite can see.
        /// </summary>
        [Fact]
        public void The_config_sentences_carry_the_registers_the_terminology_switch_had()
        {
            var catalog = Catalog();

            Assert.Equal("no .cfg scrolls found", Field(catalog["runes.list.empty"], "lore"));
            Assert.Equal("no .cfg files found", Field(catalog["runes.list.empty"], "plain"));

            Assert.Equal("{file} has unsaved rune-work. Abandon it and open {next}?",
                         Field(catalog["runes.dirty.open.body"], "lore"));
            Assert.Equal("{file} has unsaved changes. Abandon it and open {next}?",
                         Field(catalog["runes.dirty.open.body"], "plain"));
            Assert.Equal("{file} has unsaved rune-work. Reload from disk anyway?",
                         Field(catalog["runes.dirty.reload.body"], "lore"));
            Assert.Equal("{file} has unsaved changes. Reload from disk anyway?",
                         Field(catalog["runes.dirty.reload.body"], "plain"));

            // The reload toast is the third sentence the swap reaches on this hall.
            Assert.Equal("Scrolls reloaded", Field(catalog["runes.reloaded.toast"], "lore"));
            Assert.Equal("Configs reloaded", Field(catalog["runes.reloaded.toast"], "plain"));

            // The count line carries both registers in every plural category it has, which
            // is the one shape the catalog gate reads category by category rather than as
            // a sentence: a plural that keeps only its lore half goes unnoticed there.
            foreach (var category in new[] { "one", "other" })
            {
                Assert.Contains("rune-scroll", Plural(catalog["runes.sub.count"], "lore", category));
                Assert.Contains("config file", Plural(catalog["runes.sub.count"], "plain", category));
                Assert.Contains("config vault", Plural(catalog["runes.sub.count"], "lore", category));
                Assert.Contains("config folder", Plural(catalog["runes.sub.count"], "plain", category));
            }

            // Two of those three sentences carry a plain register and the third does not,
            // and the entry is where that is decided now. The swap table that used to hold
            // the first two is gone, so there is nothing left to disagree with.

            Assert.Equal("no scroll carries that", Field(catalog["runes.list.no_match"], "lore"));
            Assert.Null(Field(catalog["runes.list.no_match"], "plain"));

            // And "no match" is plain in either register, so it has none either.
            Assert.Equal("no match", Field(catalog["runes.find.no_match"], "lore"));
            Assert.Null(Field(catalog["runes.find.no_match"], "plain"));
        }

        // ------------------------------------------------- D. what is deliberately left

        /// <summary>
        /// The five sentences these two halls were still building out of pieces in code, and
        /// what each of them became. The handover list this test used to hold is EMPTY now:
        /// every one of them is a whole sentence with named slots, so the call site is read
        /// for the id and the catalog for the words. A render that goes back to gluing
        /// pieces together fails on the call shape, and a catalog edit that rewords one
        /// fails on the English. The shapes that must never come back are named at the
        /// bottom, because "no longer composed" is the half of this that a new line can
        /// quietly undo.
        /// </summary>
        [Fact]
        public void The_composed_sentences_on_these_halls_became_whole_keyed_ones()
        {
            var js = AppJs();
            var catalog = Catalog();

            // 1. "showing N of M" on the Configs search box: two bridged words with two
            // numbers between them, now the same one key the Mods box asks for.
            Assert.Contains("T(\"common.showing\",{shown:shown,total:total})", js);
            Assert.Equal("showing {shown} of {total}", Field(catalog["common.showing"], "lore"));

            // 2. a plural built INSIDE the lookup's own argument, which no key lookup can
            // reproduce: it said "1 rune-scrolls" the moment the vault held one scroll.
            Assert.Contains("$(\"#runesSub\").textContent=T(\"runes.sub.count\",{count:CFG.files.length});", js);
            Assert.Equal("{count} rune-scroll in the config vault",
                         Plural(catalog["runes.sub.count"], "lore", "one"));
            Assert.Equal("{count} rune-scrolls in the config vault",
                         Plural(catalog["runes.sub.count"], "lore", "other"));
            Assert.Equal("count", catalog["runes.sub.count"].GetProperty("plural").GetString());

            // 3. the screen-reader label with a player's name welded to the end of it.
            Assert.Contains("esc(T(\"vikings.row.actions.aria\",{name:who}))", js);
            Assert.Equal("Actions for {name}", Field(catalog["vikings.row.actions.aria"], "lore"));

            // 4. the hidden-column note, which joined a list in English word order and then
            // said "need" whether it had one column or two. One key per count now, so the
            // verb is right and the pair is joined however the language joins a pair.
            Assert.Contains("T(\"vikings.cols.hidden.two\",{first:gone[0],second:gone[1]})", js);
            Assert.Contains("T(\"vikings.cols.hidden.one\",{column:gone[0]})", js);
            Assert.Contains(" needs a wider window", Field(catalog["vikings.cols.hidden.one"], "lore"));
            Assert.Contains(" need a wider window", Field(catalog["vikings.cols.hidden.two"], "lore"));
            Assert.Contains("{first} and {second}", Field(catalog["vikings.cols.hidden.two"], "lore"));

            // 5. the roster menu's labels, which were never wrapped at all.
            Assert.Contains("{r:\"ᛏ\",label:T(\"vikings.menu.heal\")", js);
            Assert.Equal("Heal", Field(catalog["vikings.menu.heal"], "lore"));

            // And the shapes that must not come back.
            foreach (var gone in new[]
            {
                "TT(\"showing\")", "TT(\"of\")", "TT(\" and \")", "TT(\"Actions for \")",
                "TT(\"position\")", "TT(\"deaths\")", "TT(\"spawn failed\")",
                "TT(\"level\")", "TT(\"quality\")",
                " rune-scroll\"+(CFG.files.length===1?",
                "\"+gone.join(",
            })
            {
                Assert.False(js.Contains(gone, StringComparison.Ordinal),
                    "a composed sentence came back: " + gone);
            }
        }

        /// <summary>
        /// The three the Mods slice took off that list, pinned where they landed. Each one
        /// is a whole sentence with named slots now, so the call site is read for the id and
        /// the catalog for the words: a render that went back to gluing pieces together
        /// fails on the fragment, and a catalog edit that reworded one fails on the English.
        /// </summary>
        [Fact]
        public void The_mods_hall_composed_sentences_became_whole_keyed_ones()
        {
            var js = AppJs();
            var catalog = Catalog();

            // The failed pill on a row: two entries rather than one sentence with a colon
            // left hanging when the run had nothing to say.
            Assert.Contains("esc(st.error?T(\"mods.row.failed\",{detail:st.error})"
                            + ":T(\"mods.row.failed.nodetail\"))", js);
            Assert.Equal("Failed: {detail}", Field(catalog["mods.row.failed"], "lore"));
            Assert.Equal("Failed", Field(catalog["mods.row.failed.nodetail"], "lore"));

            // The bulk bar: one whole sentence per shape, with the mod and both numbers in
            // named slots, because a language that puts the count first had nowhere to put
            // it while the words were glued to the values here.
            Assert.Contains("T(\"mods.progress.updating.one\",{mod:u.current,index:u.index,total:total})", js);
            Assert.Contains("T(\"mods.progress.updating.all\",{done:u.done||0,total:total})", js);
            Assert.Equal("Updating {mod} ({index} of {total})",
                         Field(catalog["mods.progress.updating.one"], "lore"));
            Assert.Equal("Updating mods ({done} of {total})",
                         Field(catalog["mods.progress.updating.all"], "lore"));

            // And the HELD pill's sentence, which stood as a const the bridge answered by
            // its English. The const is gone, so it cannot drift from the entry.
            Assert.Contains("title=\"${esc(T(\"mods.status.held.tip\"))}\"", js);
            Assert.DoesNotContain("MOD_HELD_TIP", js);
            Assert.Equal("installed from Hexium; BakaLoader will not replace it on its own",
                         Field(catalog["mods.status.held.tip"], "lore"));
        }

        // ------------------------------------------------- E. the first frame

        /// <summary>
        /// The three painters this slice taught to ask the catalog all run while app.js is
        /// still being evaluated, so all three are repainted the moment the words land.
        /// </summary>
        [Fact]
        public void The_three_halls_are_repainted_once_the_catalog_lands()
        {
            var repaint = Between(AppJs(), "function repaintBootCopy(){", "\n}");

            Assert.Contains("try{renderMods();}catch(_){}", repaint);
            Assert.Contains("try{renderPlayers();}catch(_){}", repaint);
            Assert.Contains("try{renderCfgList();}catch(_){}", repaint);

            // Each one re-renders from the state, never from what is on screen, which is what
            // makes running it twice a no-op for everything that is not a catalog word.
            Assert.Contains("const mods=sortedMods(S.mods||[]);", AppJs());
            Assert.Contains("const base=[...S.players].sort(", AppJs());
            Assert.Contains("const shown=cfgFilesForList();", AppJs());
        }

        [Theory]
        [InlineData("try{renderMods();}catch(_){}", "renderMods")]
        [InlineData("try{renderPlayers();}catch(_){}", "renderPlayers")]
        [InlineData("try{renderCfgList();}catch(_){}", "renderCfgList")]
        public void A_hall_dropped_from_the_repaint_is_refused(string line, string painter)
        {
            var said = RepoScript.Run(RepoScript.Python(), FirstFrameGate(),
                Fixture(line, "/* dropped */"));

            Assert.False(said.Ok, painter + " can be dropped from the repaint and nothing says so:\n" + said);
            Assert.Contains(painter, said.Output);
        }

        /// <summary>
        /// The reason the test above can be written at all. The repaint walk used to travel
        /// through concise arrow bodies, and the mod-update condition row wires its button
        /// with <c>()=&gt;goPage("mods")</c>, which refreshes the journal, which draws the
        /// roster. So repainting the MOD TABLE made the gate believe the ROSTER was
        /// repainted too, and a roster dropped from the repaint passed in silence: a false
        /// green in the one gate that exists to stop ids reaching the screen.
        /// <para>
        /// The walk out of repaintBootCopy follows straight calls only now. The gate still
        /// counts an arrow body when it is looking for what runs EARLY, because
        /// <c>x=&gt;renderThing(x)</c> handed to a subscription really does run.
        /// </para>
        /// </summary>
        [Fact]
        public void The_repaint_walk_does_not_travel_through_a_click_handler()
        {
            var gate = File.ReadAllText(FirstFrameGate());

            Assert.Contains("def statement_calls(fragment, known, arrows=True):", gate);
            Assert.Contains("statement_calls(masked[start:end], declared, arrows=False)", gate);
            Assert.Contains("statement_calls(masked[here:there], declared, arrows=False)", gate);

            // The path itself is still in app.js, so the looseness is still reachable and
            // the guard above is still load bearing.
            var condition = Between(AppJs(), "function conditionModUpdates(n){", "\n}");
            Assert.Contains("()=>goPage(\"mods\")", condition);
        }
    }
}
