using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using ValheimBakaLoader.Tests.Tools;
using ValheimBakaLoader.Tools;
using Xunit;

namespace ValheimBakaLoader.Tests.Forms
{
    /// <summary>
    /// The two halves of the World field a host can now reach: a world name TYPED rather
    /// than picked, and a world COPIED under a new name.
    /// <para>
    /// These read the source, the way every gate in this suite does, and they read it from
    /// both ends: the page has to ask for the id and the catalog has to answer it, the
    /// field has to feed the same name to every surface that follows it, and every refusal
    /// the native side words has to be paired with a sentence the page owns. What the
    /// files on disk do is held in the world store's own suite; what is here is the seam.
    /// </para>
    /// </summary>
    public class WebUiWorldCopyTests
    {
        private static string AppJs() => AppSourceTree.Web("app.js");

        private static string Html() => AppSourceTree.Web("index.html");

        private static string Bridge() => AppSourceTree.Files()["BlendWindow.Bridge.cs"];

        private static string Store() => AppSourceTree.Files()["WorldStore.cs"];

        private static Dictionary<string, JsonElement> Catalog()
        {
            using var document = JsonDocument.Parse(AppSourceTree.Web("i18n/en.json"));
            var map = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var entry in document.RootElement.GetProperty("keys").EnumerateObject())
                map[entry.Name] = entry.Value.Clone();
            return map;
        }

        private static string Lore(Dictionary<string, JsonElement> catalog, string id)
        {
            Assert.True(catalog.ContainsKey(id), "the catalog has no " + id);
            return catalog[id].GetProperty("lore").GetString();
        }

        // ================================================================= A. the World field

        /// <summary>
        /// The list ends with an entry that is not a world. Its value is a string no world
        /// can be called, so the field can tell "type one" apart from "there are none",
        /// which is the state the field carries before any world exists.
        /// </summary>
        [Fact]
        public void The_world_list_ends_with_an_entry_that_opens_a_name_box()
        {
            var js = AppJs();

            Assert.Contains("const WORLD_NEW_VALUE=\"*new*\";", js);
            Assert.Contains("<option value=\"${WORLD_NEW_VALUE}\">${esc(T(\"world.new.option\"))}</option>", js);
            Assert.Equal("New world…", Lore(Catalog(), "world.new.option"));

            // The value is one no world could ever answer to, which is what makes it safe.
            Assert.NotNull(WorldStore.WorldNameProblem("*new*"));
        }

        /// <summary>
        /// The box itself, its placeholder and the line under it are in the markup, so the
        /// walker fills them and the first frame reads English rather than an id.
        /// </summary>
        [Fact]
        public void The_name_box_is_in_the_markup_where_the_walker_can_reach_it()
        {
            var html = Html();

            Assert.Contains("id=\"fWorldNew\"", html);
            Assert.Contains("data-i18n-placeholder=\"world.new.placeholder\"", html);
            Assert.Contains("id=\"fWorldNote\"", html);
            Assert.Equal("name the new world", Lore(Catalog(), "world.new.placeholder"));
        }

        /// <summary>
        /// One reader answers "which world is this hall pointed at", and every surface that
        /// follows the field reads it: the difficulty dials, the seed line, the bar that
        /// names what is being edited, and the save itself. A second reader is how two of
        /// them end up describing two different worlds.
        /// </summary>
        [Fact]
        public void Every_surface_that_follows_the_world_field_reads_the_same_value()
        {
            var js = AppJs();

            Assert.Contains("function worldFieldValue(){", js);
            // The dials, the seed line and the dial that is turned.
            Assert.Equal(3, Regex.Matches(js, @"const world=worldFieldValue\(\);").Count);
            // The edit bar.
            Assert.Contains("world:worldSel?worldFieldValue():(p.WorldName||\"\"),", js);
            // And Save Config.
            Assert.Contains("WorldName:worldFieldValue(),", js);

            // Nothing reads the raw select value for a world name any more.
            Assert.DoesNotContain("WorldName:$(\"#fWorld\").value", js);
            Assert.DoesNotContain("const world=$(\"#fWorld\").value||S.prefs?.WorldName||\"\";", js);
        }

        /// <summary>
        /// A typed name goes through the same rule the native side holds it to, and it goes
        /// through it BEFORE anything is written. The two lengths are written down in two
        /// languages, so this is the one place they are held together.
        /// </summary>
        [Fact]
        public void A_typed_name_is_held_to_the_native_sides_own_rule()
        {
            var js = AppJs();

            Assert.Contains("const WORLD_NAME_MAX=64;", js);
            Assert.Equal(64, WorldStore.WorldNameMaxLength);

            Assert.Contains("function worldNameProblem(name,taken){", js);
            Assert.Contains("const typedProblem=worldNewProblem();", js);
            Assert.Contains("if(typedProblem){toast(\"ᚦ \"+typedProblem);$(\"#fWorldNew\")?.focus();return;}", js);

            // The rules themselves, each one the native side's.
            Assert.Contains("T(\"world.name.problem.required\")", js);
            Assert.Contains("T(\"world.name.problem.too_long\",{limit:WORLD_NAME_MAX})", js);
            Assert.Contains("T(\"world.name.problem.bad_characters\")", js);
            Assert.Contains("T(\"world.name.problem.taken\",{world:v})", js);
        }

        /// <summary>
        /// The empty box is only a refusal when answering it would change what the hall is
        /// pointed at. A realm forged with the world box left blank has "" saved and nothing
        /// in its world list, so the field picks New world by itself and the box opens empty:
        /// asking for a name there would hold the port, the password, the RCON fields and the
        /// backup dials hostage on the one screen that sets them, and that hall used to save.
        /// A hall that HAS a world saved is still asked, because an empty box there would
        /// quietly wipe a name the host chose.
        /// </summary>
        [Fact]
        public void An_empty_box_on_a_hall_with_no_world_of_its_own_is_not_a_refusal()
        {
            var js = AppJs();
            var at = js.IndexOf("function worldNewProblem(){", StringComparison.Ordinal);
            Assert.True(at > 0, "the page no longer has worldNewProblem");
            var body = js.Substring(at, 320);

            Assert.Contains("const typed=String($(\"#fWorldNew\")?.value??\"\").trim();", body);
            Assert.Contains("if(!typed&&!(S.prefs?.WorldName||\"\")) return \"\";", body);
            Assert.Contains("return worldNameProblem(typed,WORLD_NAMES);", body);

            // And what the hall saves in that state is still the empty name it already had,
            // not a name invented for it.
            Assert.Contains("WorldName:worldFieldValue(),", js);
        }

        /// <summary>
        /// The refusal is worded before the save goes out, which means the preview arm of
        /// Save Config is behind it too: a name that could not be a folder is refused in
        /// the browser the same way it is refused in the window.
        /// </summary>
        [Fact]
        public void The_name_is_checked_ahead_of_the_preview_arm_of_save_config()
        {
            var js = AppJs();
            var typed = js.IndexOf("const typedProblem=worldNewProblem();", StringComparison.Ordinal);
            var preview = js.IndexOf("T(\"world.saved.preview.toast\")", StringComparison.Ordinal);

            Assert.True(typed > 0 && preview > 0);
            Assert.True(typed < preview, "Save Config checks the typed name after its preview arm returns");
        }

        /// <summary>
        /// The realm forge is the one place that took a typed world name before this, and
        /// it took anything at all. It reads the same rule now, so the two cannot drift.
        /// </summary>
        [Fact]
        public void The_realm_forge_holds_a_typed_world_name_to_the_same_rule()
        {
            var js = AppJs();

            Assert.Contains("const worldProblem=worldNameProblem(world,null);", js);
            Assert.Contains("if(worldProblem){statusN.textContent=worldProblem;worldI.focus();return;}", js);
            Assert.DoesNotContain("world:worldI.value.trim(),", js);
        }

        /// <summary>
        /// A typed name is saved in the profile before it is a world on disk, so the list
        /// alone would not answer with it on the next read. The union is what puts it back
        /// on screen as the chosen entry rather than letting the field jump to another world.
        /// </summary>
        [Fact]
        public void A_typed_name_comes_back_chosen_because_the_list_unions_the_saved_one()
        {
            var js = AppJs();

            Assert.Contains("const names=[...new Set([...(Array.isArray(r)&&r!==FAIL?r:[]),...(cur?[cur]:[])])];", js);
            Assert.Contains("names.map(n=>`<option${n===cur?\" selected\":\"\"}>${esc(n)}</option>`)", js);
        }

        /// <summary>
        /// The language switch draws the entry again rather than fetching the world list
        /// again, so a half typed name and the host's chosen entry both survive it.
        /// </summary>
        [Fact]
        public void The_language_switch_repaints_the_new_world_entry()
        {
            var js = AppJs();

            Assert.Contains("function repaintWorldSelectCopy(){", js);
            Assert.Contains("try{repaintWorldSelectCopy();}catch(_){}", js);

            var start = js.IndexOf("function repaintWorldSelectCopy(){", StringComparison.Ordinal);
            var body = js.Substring(start, 400);
            Assert.Contains("entry.textContent=T(\"world.new.option\")", body);
            Assert.Contains("syncWorldNew()", body);
            Assert.DoesNotContain("renderWorldSelect()", body);
        }

        /// <summary>
        /// A keystroke in the box is a change of world, so the bar, the dials and the seed
        /// line all follow it. The list's own change event never fires on a keystroke, which
        /// is exactly the gap that would leave three surfaces on the previous world.
        /// </summary>
        [Fact]
        public void A_keystroke_in_the_box_moves_everything_the_field_moves()
        {
            var js = AppJs();

            Assert.Contains("$(\"#fWorldNew\")?.addEventListener(\"input\",scheduleWorldNew);", js);
            Assert.Contains("$(\"#fWorldNew\")?.addEventListener(\"input\",scheduleEditBar);", js);

            var start = js.IndexOf("function scheduleWorldNew(){", StringComparison.Ordinal);
            Assert.True(start > 0, "the page no longer has scheduleWorldNew");
            var body = js.Substring(start, 400);
            Assert.Contains("syncWorldNew();", body);
            Assert.Contains("renderWorldMods();", body);
            Assert.Contains("renderWorldSeed()", body);
        }

        // ================================================================= B. copy world as

        /// <summary>
        /// One dialog, reached from the hall the world is chosen on and from the Barrow's
        /// world list, exactly the way the delete is.
        /// </summary>
        [Fact]
        public void Copy_world_as_is_offered_where_worlds_are_listed()
        {
            var js = AppJs();
            var html = Html();

            // The Settings hall, beside the field itself.
            Assert.Contains("id=\"copyWorldAs\"", html);
            Assert.Contains("data-i18n=\"world.copy.chip\"", html);
            Assert.Contains("$(\"#copyWorldAs\")?.addEventListener(\"click\"", js);

            // The Barrow's world list, as a row action.
            Assert.Contains("copychip bCopyAs", js);
            Assert.Contains("m.querySelectorAll(\".bCopyAs\").forEach", js);

            // And the Barrow's world view, beside the delete.
            Assert.Contains("id=\"mCopyAs\"", js);
            Assert.Contains("m.querySelector(\"#mCopyAs\").addEventListener(\"click\",()=>barrowCopyWorld(g,null));", js);

            Assert.Equal("COPY AS", Lore(Catalog(), "world.copy.chip"));
            Assert.Equal("Copy world as", Lore(Catalog(), "world.copy.button"));
        }

        /// <summary>
        /// The chip sits inside a row that opens the world when it is pressed, so the press
        /// has to stop there. Without it one press would open the drill-down behind the name
        /// box and the copy would land on whatever the drill-down then showed.
        /// </summary>
        [Fact]
        public void The_row_action_inside_a_tappable_row_stops_the_row_from_firing_too()
        {
            var js = AppJs();
            var start = js.IndexOf("m.querySelectorAll(\".bCopyAs\").forEach", StringComparison.Ordinal);
            Assert.True(start > 0);

            Assert.Contains("e.stopPropagation();", js.Substring(start, 300));
        }

        /// <summary>
        /// The name is asked for in the dialog that survives a language switch, and both of
        /// its sentences are handed over as something that can be asked again.
        /// </summary>
        [Fact]
        public void The_name_is_asked_for_in_a_dialog_that_can_be_worded_again()
        {
            var js = AppJs();
            var start = js.IndexOf("function worldCopyModal(ctx,after){", StringComparison.Ordinal);
            Assert.True(start > 0, "the page no longer has worldCopyModal");
            var body = js.Substring(start, 1200);

            Assert.Contains("promptModal(", body);
            Assert.Contains("()=>T(\"world.copy.title\",{world:ctx.world}),", body);
            Assert.Contains("()=>T(\"world.copy.placeholder\"),", body);
        }

        /// <summary>
        /// The typed name goes through the same rule as everywhere else before a byte moves,
        /// and the names already in this save folder are part of it: a host is told the name
        /// is taken while the dialog is still open rather than by a refusal afterwards. The
        /// rule is said ONCE, as the dialog's check, and the arm behind the press does not
        /// repeat it: the press runs the check before it hands the name on, so a second copy
        /// of the rule there could only ever agree with the first.
        /// </summary>
        [Fact]
        public void The_copys_name_is_checked_against_the_same_rule_and_the_names_already_there()
        {
            var js = AppJs();
            var start = js.IndexOf("function worldCopyModal(ctx,after){", StringComparison.Ordinal);
            var body = js.Substring(start, 2400);

            Assert.Contains("typed=>worldNameProblem(typed,ctx.taken||[]));", body);
            Assert.Single(Regex.Matches(body, @"worldNameProblem\(typed,ctx\.taken\|\|\[\]\)"));
            Assert.Contains("rpc(\"worlds.copyAs\",", body);
            Assert.Contains("{source:ctx.world,target,folder:ctx.folder||\"\",sub:ctx.sub||\"\"}", body);
        }

        /// <summary>
        /// The rule goes INTO the dialog rather than being read after it has shut. A name
        /// the rule turns down is said under the box with the name still in it, because
        /// that is the only place a host can act on it: a toast over a closed dialog meant
        /// opening the dialog again and typing the whole name again to change one
        /// character of it. The wording is asked for on the redraw rather than kept, so a
        /// language switch re-words the line the way it re-words the rest of the dialog.
        /// </summary>
        [Fact]
        public void A_name_the_rule_turns_down_keeps_the_dialog_open_with_the_name_in_it()
        {
            var js = AppJs();

            // The dialog takes a rule, and the copy hands it one.
            Assert.Contains("function promptModal(title,placeholder,onOk,check){", js);
            var start = js.IndexOf("function worldCopyModal(ctx,after){", StringComparison.Ordinal);
            Assert.Contains("typed=>worldNameProblem(typed,ctx.taken||[]));", js.Substring(start, 2400));

            // The press asks the rule, and a refusal draws the dialog again rather than
            // shutting it: modalClose and onOk are both behind the refusal, not in front.
            var dialog = js.IndexOf("function promptModal(", StringComparison.Ordinal);
            var shape = js.Substring(dialog, 2200);
            Assert.Contains("if(check&&String(check(v)||\"\")){typed=inp.value;refused=true;again();return;}", shape);
            Assert.Contains("const problem=refused&&check?String(check(typed)||\"\"):\"\";", shape);
            Assert.Contains("id=\"mInNote\"", shape);
        }

        /// <summary>
        /// The Barrow lists every save folder BakaLoader knows in one go, and a name is
        /// only taken inside ONE of them. Asking the whole listing told a host with a realm
        /// on its own save folder that a name was taken when it was free where the copy
        /// would land, and there is no way past that from the row.
        /// </summary>
        [Fact]
        public void The_names_a_copy_is_held_against_are_the_ones_in_its_own_save_folder()
        {
            var js = AppJs();
            var start = js.IndexOf("function barrowCopyWorld(g,groups){", StringComparison.Ordinal);
            Assert.True(start > 0, "the Barrow no longer has barrowCopyWorld");
            var body = js.Substring(start, 1400);

            Assert.Contains("const here=groups?groups.filter(x=>x.folder===g.folder):null;", body);
            Assert.Contains("const taken=here?here.map(x=>x.world)", body);
            // The world view still guesses nothing in the app: it was handed one world and
            // no listing. In the browser preview the mock listing stands in for the native
            // side, and it is cut to the same save folder, or the drill down would turn down
            // a name the row it was opened from takes: one feature answering two ways on one
            // page is a walk written up wrong.
            Assert.Contains(
                "(Native.available?[]:BARROW_MOCK.filter(x=>x.folder===g.folder).map(x=>x.world));",
                body);
            Assert.DoesNotContain("BARROW_MOCK.map(x=>x.world)", body);

            // The native side is the one that answers for certain, and it answers per save
            // folder, which is the boundary the page is now cutting on.
            Assert.Contains("FindWorldFilesOnDisk(world.SaveFolder, targetName)", Store());
        }

        /// <summary>
        /// The browser preview has to show the field on its own first frame the way the app
        /// shows it. It used to write one hand made option over the top of the field, which
        /// dropped the New world entry off the end and left the names the field is holding
        /// saying something other than the names on screen, before a walk had driven
        /// anything: a walker reading the preview would have called the feature missing.
        /// <para>
        /// The entry is laid down wordless and repaintBootCopy fills it, because app.js is
        /// still being evaluated here and the catalog has not landed. check_first_frame.py
        /// is the gate on that, and it fails on a renderWorldSelect() call in this block.
        /// </para>
        /// </summary>
        [Fact]
        public void The_preview_lays_the_world_field_down_with_its_new_world_entry_on_the_end()
        {
            var js = AppJs();

            Assert.DoesNotContain("$(\"#fWorld\").innerHTML=`<option>Final Sunset</option>`;", js);
            Assert.Contains("if(!WORLD_LIST_MOCK.includes(\"Final Sunset\")) WORLD_LIST_MOCK.unshift(\"Final Sunset\");", js);
            Assert.Contains("WORLD_NAMES=WORLD_LIST_MOCK.slice();", js);
            Assert.Contains("+`<option value=\"${WORLD_NEW_VALUE}\"></option>`;", js);

            // The word arrives with the catalog, through the repaint that already owns it.
            Assert.Contains("try{repaintWorldSelectCopy();}catch(_){}", js);

            // And a copy taken in the preview reaches the list it landed in, the way the
            // Barrow's mock listing already grew one.
            Assert.Contains("if(!Native.available&&target&&!WORLD_LIST_MOCK.includes(target)) WORLD_LIST_MOCK.push(target);", js);
        }

        /// <summary>
        /// A world that is being played is not a world to copy: the server rewrites it as it
        /// saves, so the copy would be half of one save and half of another. Whoever merely
        /// has it CHOSEN is not asked, because a copy leaves the source alone.
        /// </summary>
        [Fact]
        public void A_running_world_is_refused_before_the_dialog_opens()
        {
            var js = AppJs();
            var start = js.IndexOf("function worldCopyBlock(ctx){", StringComparison.Ordinal);
            Assert.True(start > 0, "the page no longer has worldCopyBlock");
            var body = js.Substring(start, 400);

            Assert.Contains("T(\"world.copy.block.no_world\")", body);
            Assert.Contains("T(\"world.copy.block.running\",{owner:ctx.owner||\"\"})", body);
            // An owner alone never blocks a copy: that is the difference from a delete.
            Assert.DoesNotContain("world.delete.block.owned", body);
        }

        // ================================================================= C. the seam

        /// <summary>
        /// The RPC the dialog calls, and the four things that have to be true before a byte
        /// moves. The fifth, whether the new name is free, is deliberately NOT here: that
        /// answer goes stale between a check and a copy, so the world store refuses an
        /// occupied name at the moment it would land on it.
        /// </summary>
        [Fact]
        public void The_bridge_answers_copy_as_and_makes_its_checks_on_the_way_in()
        {
            var bridge = Bridge();

            Assert.Contains("RegisterRpc(\"worlds.copyAs\"", bridge);

            var at = bridge.IndexOf("RegisterRpc(\"worlds.copyAs\"", StringComparison.Ordinal);
            var body = bridge.Substring(at, 4000);

            Assert.Contains("WorldStore.IsSafeReferenceToken(source)", body);
            Assert.Contains("WorldStore.WorldNameProblem(target)", body);
            Assert.Contains("KnownSaveFolders().Any(k => SameFolder(k, folder))", body);
            Assert.Contains("session.Server.Status == ServerStatus.Stopped", body);
            Assert.Contains("worlds.copyServerRunning", body);
            Assert.Contains("WorldStore.CopyWorldAs(world, target)", body);

            // Off the UI thread, because a 1.0 world is a whole directory tree.
            Assert.Contains("await Task.Run(() => WorldStore.CopyWorldAs(world, target))", body);
        }

        /// <summary>
        /// Every refusal the copy can come back with names itself, and every one of those
        /// names is paired with a sentence the page owns. A refusal with no pairing reaches
        /// the host in English whatever they are reading.
        /// </summary>
        [Theory]
        [InlineData("worlds.copySourceRequired", "world.copy.reason.source_required")]
        [InlineData("worlds.copyBadSourceRef", "world.copy.reason.bad_source")]
        [InlineData("worlds.copyBadSubfolder", "world.copy.reason.bad_subfolder")]
        [InlineData("worlds.copyUnknownSaveFolder", "world.copy.reason.unknown_save_folder")]
        [InlineData("worlds.copyTargetRequired", "world.copy.reason.target_required")]
        [InlineData("worlds.copyTargetTooLong", "world.copy.reason.target_too_long")]
        [InlineData("worlds.copyBadTargetRef", "world.copy.reason.bad_target")]
        [InlineData("worlds.copyTargetExists", "world.copy.reason.target_exists")]
        [InlineData("worlds.copyServerRunning", "world.copy.reason.server_running")]
        [InlineData("worlds.copyNoSuchWorld", "world.copy.reason.no_such_world")]
        [InlineData("worlds.copyUnreadable", "world.copy.reason.unreadable")]
        [InlineData("worlds.copyFailed", "world.copy.reason.failed")]
        public void Every_copy_refusal_is_paired_with_a_sentence_the_page_owns(string throwId, string textId)
        {
            var thrown = Bridge() + Store();

            Assert.Contains("HostFacingException(\"" + throwId + "\"", thrown, StringComparison.Ordinal);
            Assert.True(Catalog().ContainsKey(textId), "the catalog has no " + textId);
            Assert.Contains("named:\"" + throwId + "\"", AppJs(), StringComparison.Ordinal);
            Assert.Contains("textId:\"" + textId + "\"", AppJs(), StringComparison.Ordinal);
        }

        /// <summary>
        /// The table above says every id it names is paired. This one is derived from the
        /// source instead of kept beside it, so it says the other half: there is no copy
        /// refusal the source throws that the table, the page and the catalog do not know.
        /// A thirteenth HostFacingException("worlds.copy…") added later with no row and no
        /// sentence would otherwise pass every gate here.
        /// </summary>
        [Fact]
        public void Every_copy_refusal_the_source_throws_is_paired_with_a_sentence()
        {
            var thrown = Bridge() + Store();
            var js = AppJs();
            var catalog = Catalog();

            var ids = Regex.Matches(thrown, "HostFacingException\\(\"(worlds\\.copy[A-Za-z]*)\"")
                .Cast<Match>()
                .Select(m => m.Groups[1].Value)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            Assert.True(ids.Count >= 12, "only " + ids.Count + " copy refusal(s) found in the source");

            var unpaired = ids
                .Where(id => !js.Contains("named:\"" + id + "\"", StringComparison.Ordinal))
                .ToList();
            Assert.True(unpaired.Count == 0,
                "these copy refusals have no row in the page's pairing table: " + string.Join(", ", unpaired));

            foreach (var id in ids)
            {
                var textId = Regex.Match(js, "named:\"" + Regex.Escape(id) + "\",\\s*textId:\"([^\"]+)\"")
                    .Groups[1].Value;

                Assert.False(string.IsNullOrEmpty(textId), "the row for " + id + " names no sentence");
                Assert.True(catalog.ContainsKey(textId), "the catalog has no " + textId);
            }
        }

        /// <summary>
        /// The values a refusal carries are the slots its sentence names. A sentence that
        /// asked for a name the refusal never sends would render a literal brace pair.
        /// </summary>
        [Theory]
        [InlineData("world.copy.reason.server_running", "profile", "world")]
        [InlineData("world.copy.reason.target_exists", "target")]
        [InlineData("world.copy.reason.bad_target", "target")]
        [InlineData("world.copy.reason.unreadable", "world")]
        [InlineData("world.copy.reason.no_such_world", "world")]
        [InlineData("world.copy.reason.failed", "world")]
        [InlineData("world.copy.reason.target_too_long", "limit")]
        public void A_refusals_sentence_names_only_values_the_refusal_sends(string textId, params string[] slots)
        {
            var entry = Catalog()[textId];
            var declared = entry.GetProperty("params").EnumerateObject().Select(p => p.Name)
                .OrderBy(x => x, StringComparer.Ordinal).ToList();

            Assert.Equal(slots.OrderBy(x => x, StringComparer.Ordinal).ToList(), declared);
        }

        /// <summary>
        /// The copy is a world the moment it lands, so the list it came from is read again.
        /// A field left showing the list it was painted from is a world a host cannot pick.
        /// </summary>
        [Fact]
        public void The_list_is_read_again_once_the_copy_has_landed()
        {
            var js = AppJs();

            var at = js.IndexOf("$(\"#copyWorldAs\")?.addEventListener(\"click\"", StringComparison.Ordinal);
            var handler = js.Substring(at, 1200);
            Assert.Contains("await renderWorldSelect();", handler);
            // And the entry the host had chosen is put back on top of the fresh list, so a
            // copy never quietly undoes a pick that was not saved yet.
            Assert.Contains("back.value=keep;", handler);

            var barrow = js.IndexOf("function barrowCopyWorld(g,groups){", StringComparison.Ordinal);
            Assert.True(barrow > 0, "the Barrow no longer has barrowCopyWorld");
            Assert.Contains("barrowModal();", js.Substring(barrow, 1900));
        }
    }
}
