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
    /// The run-time half of the Hearth: the app bar, the status card, the Upkeep card's
    /// own sentences, and the four surfaces that talk about an update - the BakaLoader
    /// update dialog, the launch guard, the Valheim server update, and the condition bar
    /// they all report into. Their words used to be English literals inside app.js and
    /// are now catalog ids.
    /// <para>
    /// WebUiStaticCopyTests holds the static half of the same idea, for the whole page at
    /// once. This file is what a general gate cannot be: the inventory of which run-time
    /// sentences are keyed, which are deliberately still composed in code, and the three
    /// decisions inside those keys that nothing else would notice going wrong - the plain
    /// register of a sentence the terminology switch rewords, the rune a toast leads with,
    /// and the ids two surfaces share on purpose.
    /// </para>
    /// <para>
    /// And the defect this migration introduced and closed: app.js paints while it is
    /// still being evaluated, the catalog is fetched, so a T() call on that road answers
    /// with its own id. The gate for it is <c>scripts/i18n/check_first_frame.py</c>, run
    /// here over the shipped tree and then over one deliberately broken copy per rule.
    /// </para>
    /// </summary>
    public class WebUiUpdateCopyTests : IDisposable
    {
        private static string AppJs() => AppSourceTree.Web("app.js");

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
        /// has to bite: a fixture that quietly failed to change anything would make every
        /// assertion below meaningless.</summary>
        private string Fixture(string find, string replace)
        {
            var source = AppJs();
            Assert.Contains(find, source);
            var folder = Path.Combine(Path.GetTempPath(), "vbl-firstframe-" + Guid.NewGuid().ToString("N"));
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

        /// <summary>
        /// Where app.js first asks for this id, whichever way it asks: <c>T("id")</c> for a
        /// sentence that stands on its own, <c>T("id",{...})</c> for one that names slots.
        /// Minus one when nothing asks for it. Written once here because a rule that only
        /// knew the first shape would quietly stop covering every sentence with a value in
        /// it, which is the half of the catalog a translator needs most.
        /// </summary>
        private static int AskedAnyhow(string source, string id)
        {
            var bare = source.IndexOf("T(\"" + id + "\")", StringComparison.Ordinal);
            var withParams = source.IndexOf("T(\"" + id + "\",", StringComparison.Ordinal);
            if (bare < 0) return withParams;
            if (withParams < 0) return bare;
            return Math.Min(bare, withParams);
        }

        // ------------------------------------------------- A. the inventory of the surfaces

        /// <summary>
        /// Every prefix this slice owns, with the surface it belongs to. A key under one of
        /// these is run-time copy: app.js asks for it by id, and the walker must never also
        /// write it, because an element the walker fills and app.js overwrites is a sentence
        /// with two owners and the loser is whichever ran second.
        /// </summary>
        public static IEnumerable<object[]> Prefixes() => new[]
        {
            new object[] { "hearth.appbar.", 6 },
            new object[] { "hearth.card.", 5 },
            new object[] { "hearth.upkeep.auto_update.gated.", 2 },
            new object[] { "hearth.upkeep.hexium.", 2 },
            new object[] { "appupd.", 24 },
            new object[] { "guard.", 14 },
            // 30, not 29: the pre-update world copy's refusal joined this family when the
            // remaining TT() sites were keyed.
            new object[] { "srvupd.", 30 },
            new object[] { "cond.", 28 },
        };

        [Theory]
        [MemberData(nameof(Prefixes))]
        public void Every_run_time_key_is_asked_for_by_app_js_and_by_nothing_else(string prefix, int howMany)
        {
            var source = AppJs();
            var html = AppSourceTree.Web("index.html");
            var mine = Catalog().Keys.Where(id => id.StartsWith(prefix, StringComparison.Ordinal))
                                     .OrderBy(id => id, StringComparer.Ordinal).ToList();

            Assert.Equal(howMany, mine.Count);
            foreach (var id in mine)
            {
                Assert.Contains("T(\"" + id + "\")", source);
                Assert.DoesNotContain("data-i18n=\"" + id + "\"", html);
                Assert.DoesNotContain("data-i18n-title=\"" + id + "\"", html);
                Assert.DoesNotContain("data-i18n-placeholder=\"" + id + "\"", html);
                Assert.DoesNotContain("data-i18n-aria=\"" + id + "\"", html);
            }
        }

        /// <summary>
        /// The sentences these surfaces still compose in code rather than key, named so the
        /// pass that keys them is a decision and not a discovery. Each one is a piece of a
        /// sentence, not a sentence: a translator handed " online" or "The update did not
        /// finish: " cannot produce correct Russian from it, which is why they wait for the
        /// composed-message pass instead of becoming keys that read as fragments forever.
        /// </summary>
        [Fact]
        public void The_fragments_these_surfaces_still_compose_are_still_composed()
        {
            var source = AppJs();
            foreach (var fragment in new[]
            {
                "\" online\"",                              // the app bar's player count
                "\"last ran Valheim \"",                    // the status card's version line
                "\"session · \"",                      // the chronicle's per-session rule
                "\"ready\"", "\"is ready\"",                // the sidebar and the pill
                "\"a build it has not recorded\"",          // the guard's build names
                "\"of\"", "\"Steam is downloading: \"",     // the megabyte pair
                "\"no reason was given\"",
                "\"The update did not finish · \"",
                "\"The update did not finish: \"",
                "\"The server could not write the world to disk\"",
                "\" is available.\"",
            })
            {
                Assert.Contains("TT(" + fragment + ")", source);
            }
        }

        // ------------------------------------------- B. the three decisions inside the keys

        /// <summary>
        /// The five sentences the plain-terminology switch rewords. The plain register has
        /// to be exactly what the swap produces today, awkward wording included: this pass
        /// moved where the words come from, and was not licensed to change them. Two of
        /// these read badly - "Stop the server: stops the server" is what a host with plain
        /// wording on has always read on that tooltip, because the swap turns both "Douse"
        /// and "hearth" into the same word. Written down here so the writer who fixes it
        /// does so on purpose, in a change that says it is changing English.
        /// </summary>
        [Fact]
        public void The_sentences_the_plain_switch_rewords_carry_the_wording_it_produces()
        {
            var catalog = Catalog();
            foreach (var (id, lore, plain) in new[]
            {
                ("hearth.appbar.lifecycle.stop.title",
                 "Douse the hearth: stops the server", "Stop the server: stops the server"),
                ("hearth.appbar.lifecycle.start.title",
                 "Kindle the hearth: starts the server", "Start the server: starts the server"),
                ("hearth.card.state.stopping",
                 "STOPPING · dousing the embers", "STOPPING · shutting down"),
                ("hearth.card.state.stopped",
                 "STOPPED · embers doused", "STOPPED"),
                ("guard.note.backup",
                 "A backup copies every world in this server's save folder aside first, and the Barrow can put one back.",
                 "A backup copies every world in this server's save folder aside first, and the backup manager can put one back."),
            })
            {
                Assert.True(catalog.ContainsKey(id), "the catalog lost " + id);
                Assert.Equal(lore, Field(catalog[id], "lore"));
                Assert.Equal(plain, Field(catalog[id], "plain"));
            }
        }

        /// <summary>
        /// A sentence that leads with a rune carries that rune in its mark field so the
        /// composed-message pass can fold the two together without going back to the call
        /// site to find out which glyph it was. Until then the call site still prints it,
        /// so the two have to agree: this reads the LAST glyph literal printed before the
        /// call and matches it against the field.
        /// <para>
        /// It reads the glyph rather than the word toast( because the field is not only a
        /// toast's any more: the Waystone wizard's four status lines and the Herald
        /// wizard's password warning lead with one too, and they are written into an
        /// element rather than shown as a toast. Anchoring on toast( would have meant
        /// either leaving those entries unmarked or letting them go unchecked.
        /// </para>
        /// </summary>
        [Fact]
        public void Every_marked_sentence_carries_the_rune_its_call_site_prints()
        {
            var source = AppJs();
            var catalog = Catalog();
            // Either shape of the call: T("id") for a sentence that stands on its own, and
            // T("id",{...}) for one with values in named slots. A toast that interpolates a
            // version or a mod name still leads with a rune, so the second shape counts here
            // exactly as the first does.
            var marked = catalog.Where(pair => Field(pair.Value, "mark") != null)
                                .Select(pair => pair.Key)
                                .Where(id => AskedAnyhow(source, id) >= 0)
                                .OrderBy(id => id, StringComparer.Ordinal).ToList();

            // Every entry that carries a mark is asked for by app.js: a mark on a sentence
            // nothing prints would be a rune waiting to appear out of nowhere.
            Assert.Equal(catalog.Count(pair => Field(pair.Value, "mark") != null), marked.Count);
            Assert.Equal(41, marked.Count);

            // A one-character string literal of a glyph, followed by the space that always
            // rides with it: "ᛊ ". The two shapes in the page are toast("ᛊ "+T(id)) and the
            // same pair inside a ternary, so the nearest one before the call is the one
            // printed either way.
            var glyph = new Regex("\"([^\\x00-\\x7F]) \"", RegexOptions.Compiled);
            foreach (var id in marked)
            {
                var call = AskedAnyhow(source, id);
                var window = source.Substring(Math.Max(0, call - 160), Math.Min(160, call));
                var printed = glyph.Matches(window).Cast<Match>().LastOrDefault();
                Assert.True(printed != null, id + " carries a mark but nothing prints a glyph before it");
                Assert.Equal(Field(catalog[id], "mark"), printed.Groups[1].Value);
            }
        }

        /// <summary>
        /// "Update finished." is the one sentence that is both, so it carries no mark: the
        /// progress bar prints it with no rune and the toast beside it prints one. A mark
        /// on it would put a rune in the bar the day the pass starts using the field.
        /// </summary>
        [Fact]
        public void The_sentence_that_is_both_a_bar_line_and_a_toast_carries_no_rune()
        {
            var catalog = Catalog();
            Assert.Equal("Update finished.", Field(catalog["srvupd.phase.finished"], "lore"));
            Assert.Null(Field(catalog["srvupd.phase.finished"], "mark"));
            Assert.Contains("if(k===\"finished\") return T(\"srvupd.phase.finished\");", AppJs());
            Assert.Contains("T(\"srvupd.done.starting.toast\"):T(\"srvupd.phase.finished\")", AppJs());
        }

        /// <summary>
        /// The ids two surfaces share. The launch guard asks its question in a modal when
        /// the host is watching and in the condition bar when they were not, and both have
        /// to say the same words: that is the whole reason guardBody exists. Pinned by count
        /// so a later edit that wants to word one of them differently has to fork the id
        /// here rather than quietly let the two drift.
        /// </summary>
        [Fact]
        public void The_ids_two_surfaces_share_are_shared_on_purpose()
        {
            var source = AppJs();
            foreach (var (id, times) in new[]
            {
                ("guard.btn.not_now", 3),          // both guard button sets, and the bar
                ("guard.btn.open_steam", 2),       // the modal's link and the bar's button
                ("guard.btn.backup_and_start", 2),
                ("guard.btn.start_anyway", 2),
                ("guard.btn.start_no_backup", 2),
                ("guard.note.backup", 2),          // the guard, and the update's own prompt
                ("cond.btn.open_log", 3),          // a failed save, a crash, a missing plugin
                ("srvupd.row.title", 2),           // the row's title and its stopped button
                ("srvupd.phase.finished", 2),      // the bar line and the toast
            })
            {
                Assert.Equal(times, Regex.Matches(source, @"(?<![A-Za-z0-9_$])T\(""" + Regex.Escape(id) + @"""\)").Count);
            }
        }

        // ------------------------------------- C. the copy painted before the catalog lands

        /// <summary>
        /// The shipped tree, through the gate the copy gate runs.
        /// </summary>
        [Fact]
        public void The_shipped_page_paints_no_catalog_id_on_the_first_frame()
        {
            var said = RepoScript.Run(RepoScript.Python(), FirstFrameGate());
            Assert.True(said.Ok, "app.js paints an id before the catalog arrives:\n" + said);
            Assert.Contains("TOTAL 0", said.Output);
        }

        /// <summary>
        /// The repaint itself, said in app.js rather than only in the gate: the boot walk
        /// runs it, and only when a catalog actually landed. Repainting after a failed
        /// fetch would put ids over the English every static element still carries.
        /// </summary>
        [Fact]
        public void The_boot_repaints_the_dynamic_copy_only_when_the_words_arrived()
        {
            var source = AppJs();
            Assert.Contains("if(ok){try{repaintBootCopy();}catch(_){}}", source);
            Assert.Contains("function repaintBootCopy(){", source);
            foreach (var painter in new[]
            {
                "renderHearth", "syncUpkeepGates", "renderSideVer",
                "renderAppUpdatePill", "rerenderConditions",
            })
            {
                Assert.Contains("try{" + painter + "();}catch(_){}", source);
            }
        }

        /// <summary>
        /// The condition bar cannot be repainted by drawing it again: setCondition stores
        /// the SENTENCE, so renderConditionBar would only re-draw words already chosen. So
        /// every raiser hands in an `again` closure over its own arguments and
        /// rerenderConditions replays them. This was not theoretical: the preview raises the
        /// BakaLoader-update row while app.js is still being evaluated, and before this the
        /// bar came up reading "cond.appupd.title" on every load.
        /// </summary>
        [Fact]
        public void Every_condition_hands_the_bar_a_way_to_build_itself_again()
        {
            var source = AppJs();
            foreach (var again in new[]
            {
                "again:renderLaunchHold,",
                "again:renderServerUpdate,",
                "again:()=>conditionSaveFailed(ms),",
                "again:()=>conditionBackupFailed(err),",
                "again:()=>conditionCrashed(),",
                "again:()=>conditionPluginFailures(fails,status),",
                "again:()=>conditionModUpdates(n),",
                "again:()=>conditionRestartPending(on,sig),",
                "again:()=>conditionAppUpdate(APP_UPDATE_V),",
            })
            {
                Assert.Contains(again, source);
            }

            // One per setCondition call, so a condition added later without one is visible.
            Assert.Equal(
                Regex.Matches(source, @"(?<![A-Za-z0-9_$])setCondition\(").Count - 1,   // minus the declaration
                Regex.Matches(source, @"^\s*again\s*:", RegexOptions.Multiline).Count);
            Assert.Contains("if(cond&&typeof cond.again===\"function\"){try{cond.again();}catch(_){}}", source);
        }

        /// <summary>A boot that never repaints is refused.</summary>
        [Fact]
        public void A_boot_that_never_repaints_is_refused()
        {
            var said = RepoScript.Run(RepoScript.Python(), FirstFrameGate(),
                Fixture("    if(ok){try{repaintBootCopy();}catch(_){}}\n", ""));
            Assert.False(said.Ok, "a page that never repaints its copy passed:\n" + said);
            Assert.Contains("never calls repaintBootCopy", said.Output);
        }

        /// <summary>A repaint that runs whatever the fetch did is refused: it would paint
        /// ids over the English a failed fetch leaves standing.</summary>
        [Fact]
        public void A_repaint_that_runs_after_a_failed_fetch_is_refused()
        {
            var said = RepoScript.Run(RepoScript.Python(), FirstFrameGate(),
                Fixture("if(ok){try{repaintBootCopy();}catch(_){}}", "try{repaintBootCopy();}catch(_){}"));
            Assert.False(said.Ok, "an unconditional repaint passed:\n" + said);
            Assert.Contains("not held behind ok", said.Output);
        }

        /// <summary>A painter dropped from the repaint is refused, by the name of the
        /// function that actually asks for the words.</summary>
        [Fact]
        public void A_painter_dropped_from_the_repaint_is_refused()
        {
            var said = RepoScript.Run(RepoScript.Python(), FirstFrameGate(),
                Fixture("try{renderHearth();}catch(_){}", "/* dropped */"));
            Assert.False(said.Ok, "a Hearth that is never repainted passed:\n" + said);
            // Named by the function that actually asks for the words, not by the one that
            // was dropped: renderHearth carries no T() call of its own.
            Assert.Contains("renderHearthNative", said.Output);
            Assert.Contains("renderUpdatePill", said.Output);
            Assert.Contains("repaintBootCopy runs neither of them again", said.Output);
            // renderAppBar is the one helper under the Hearth the gate does NOT name any
            // more, and rightly: the roster repaint reaches it too, so dropping the Hearth
            // no longer leaves the app bar showing ids. A second owner is the difference
            // between a helper that is covered and one that only looked covered.
            Assert.DoesNotContain("renderAppBar", said.Output);
        }

        /// <summary>And the same one level down, where a painter's helper would hide.
        /// renderEditBar is the case: nothing calls it at column zero, goPage does, and
        /// goPage runs while app.js is still being evaluated. Drop it from the repaint and
        /// the words it asks for are the first frame's, so the gate has to say so.</summary>
        [Fact]
        public void A_helper_under_an_unrepainted_painter_is_refused()
        {
            var said = RepoScript.Run(RepoScript.Python(), FirstFrameGate(),
                Fixture("  try{renderEditBar()?.catch(()=>{});}catch(_){}", "  /* dropped */"));
            Assert.False(said.Ok, "a first-frame T() call passed:\n" + said);
            Assert.Contains("renderEditBar", said.Output);
        }

        /// <summary>The copy gate runs it, so a commit that never opens this suite still
        /// cannot land a page that paints an id.</summary>
        [Fact]
        public void The_copy_gate_runs_the_first_frame_check()
        {
            Assert.Contains("check_first_frame.py",
                File.ReadAllText(RepoScript.At("scripts", "copy-gate", "copy_gate.sh")));
        }
    }
}
