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

        private static List<string> IdsAskedIn(string block) =>
            AskedById.Matches(block).Select(m => m.Groups[1].Value)
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
            new object[] { "vikings.menu.", "the roster row menu's refusals" },
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
                Assert.NotNull(Field(catalog[id], "lore"));
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
            // unsaved work.
            Assert.Equal(2, Regex.Matches(js, Regex.Escape("T(\"runes.dirty.sentence\")")).Count);
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

            Assert.Equal("has unsaved rune-work.", Field(catalog["runes.dirty.sentence"], "lore"));
            Assert.Equal("has unsaved changes.", Field(catalog["runes.dirty.sentence"], "plain"));

            // TERM_PAIRS holds both of those swaps and holds nothing for the third, so the
            // third has no plain register and the pairs table is where that is decided.
            var pairs = Between(AppJs(), "const TERM_PAIRS=[", "\n];");
            Assert.Contains("[\"no .cfg scrolls found\",\"no .cfg files found\"]", pairs);
            Assert.Contains("[\"unsaved rune-work\",\"unsaved changes\"]", pairs);
            Assert.DoesNotContain("no scroll carries that", pairs);

            Assert.Equal("no scroll carries that", Field(catalog["runes.list.no_match"], "lore"));
            Assert.Null(Field(catalog["runes.list.no_match"], "plain"));

            // And "no match" is plain in either register, so it has none either.
            Assert.Equal("no match", Field(catalog["runes.find.no_match"], "lore"));
            Assert.Null(Field(catalog["runes.find.no_match"], "plain"));
        }

        // ------------------------------------------------- D. what is deliberately left

        /// <summary>
        /// The sentences on these three halls that are still built out of pieces in code, each
        /// with the reason it could not move yet. One key per WHOLE sentence is the rule, and
        /// none of these is a whole sentence: they are words glued to a number, a name or an
        /// error the server wrote. Rewriting them as keyed sentences with named slots is the
        /// composed-message pass, and this list is its handover. A line that quietly migrates
        /// half of one fails here instead of shipping a trailing separator or a "1 settings".
        /// </summary>
        [Theory]
        [InlineData("TT(\"showing\")+\" \"+shown+\" \"+TT(\"of\")+\" \"+total",
            "showing N of M, two words around two numbers, on both search boxes")]
        [InlineData("esc(TT(\"Failed\")+\": \"+(st.error||TT(\"unknown\")))",
            "a word, a colon and whatever the update path wrote")]
        [InlineData("TT(\"Updating \")+u.current+\" (\"+u.index+\" \"+TT(\"of\")+\" \"+total+\")\"",
            "the bar's label, built from a mod name and two numbers")]
        [InlineData("TT(CFG.files.length+\" rune-scroll\"+(CFG.files.length===1?\"\":\"s\")+\" in the config vault\")",
            "a plural built inside the lookup's own argument, which no key can reproduce")]
        [InlineData("esc(TT(\"Actions for \")+who)",
            "a screen-reader label with a player's name welded to it")]
        [InlineData("gone.join(TT(\" and \"))+TT(\" need a wider window",
            "a list joined in English word order")]
        [InlineData("esc(TT(MOD_HELD_TIP))",
            "a const the bridge still answers by its English")]
        [InlineData("{r:\"ᛏ\",label:\"Heal\"",
            "the roster menu's labels were never wrapped at all, so they are not a TT() site")]
        public void The_composed_sentences_are_still_composed_and_still_where_they_were(
            string fragment, string why)
        {
            Assert.True(AppJs().Contains(fragment, StringComparison.Ordinal),
                "this was left for the composed-message pass (" + why + ") and has moved or "
                + "half moved: " + fragment);
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
