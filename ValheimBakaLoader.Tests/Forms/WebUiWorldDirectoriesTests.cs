using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using ValheimBakaLoader.Tests.Tools;
using Xunit;

namespace ValheimBakaLoader.Tests.Forms
{
    /// <summary>
    /// The Settings hall's Directories section and the unsaved signs that came with it.
    /// <para>
    /// A reporter who had just finished the first time setup read two empty boxes, a
    /// tooltip that named no folder, a focus glow they took to mean "saved", and an Open
    /// button that opened the old folder after they had typed a new one. Each of those is
    /// a seam between the markup, the render and the catalog, and each one is held here.
    /// </para>
    /// </summary>
    public class WebUiWorldDirectoriesTests
    {
        private static string AppJs() => AppSourceTree.Web("app.js");

        private static string Html() => AppSourceTree.Web("index.html");

        private static string Css() => AppSourceTree.Web("app.css");

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

        /// <summary>
        /// One function of app.js, from its declaration to its own closing brace at column
        /// zero. Read this way rather than by a length, because a comment growing inside a
        /// function is exactly the change that would silently shorten a fixed window and
        /// turn a gate on the last line of a function into a gate on nothing.
        /// </summary>
        private static string Body(string js, string name)
        {
            var at = js.IndexOf("function " + name + "(){", StringComparison.Ordinal);
            Assert.True(at > 0, "app.js no longer declares " + name);
            return js.Substring(at, js.IndexOf("\n}\n", at, StringComparison.Ordinal) - at);
        }

        /// <summary>The markup of one element, from its id back to its opening angle
        /// bracket and on to the end of the tag.</summary>
        private static string Tag(string html, string id)
        {
            var at = html.IndexOf("id=\"" + id + "\"", StringComparison.Ordinal);
            Assert.True(at > 0, "index.html no longer has #" + id);
            var open = html.LastIndexOf('<', at);
            return html.Substring(open, html.IndexOf('>', at) - open + 1);
        }

        // ========================================================== A. the Currently line

        /// <summary>
        /// The line that answers the report. It is in the markup so the walker can fill the
        /// one static word on it, and its value, its source word and Open all carry no id
        /// at all: those three need a path to say anything, so renderWorldDirs owns them.
        /// </summary>
        [Fact]
        public void Each_path_field_carries_a_line_saying_what_is_in_force()
        {
            var html = Html();

            foreach (var (value, word) in new[]
                     {
                         ("curServerExe", "curServerExeSrc"),
                         ("curSaveDir", "curSaveDirSrc"),
                     })
            {
                Assert.Contains("id=\"" + value + "\"", html);
                Assert.Contains("id=\"" + word + "\"", html);
            }

            // Two lines, each with the one static word on it.
            Assert.Equal(2, Regex.Matches(html, @"data-i18n=""world\.dir\.currently""").Count);
            Assert.Equal("Currently", Lore(Catalog(), "world.dir.currently"));

            // The value keeps the monospaced face and stays selectable: the line exists so
            // a host can read a path off it and paste it somewhere else. The face comes
            // from the class's own rule rather than from a shared one, because the page's
            // mono class has no rule behind it and would have styled nothing.
            Assert.Contains("class=\"dircur-v\" id=\"curServerExe\"", html);
            var rule = Css().Substring(Css().IndexOf(".dircur-v{", StringComparison.Ordinal), 120);
            Assert.Contains("font-family:var(--mono)", rule);
            Assert.Contains("user-select:text", rule);
        }

        /// <summary>
        /// One owner per element. The three that the render writes carry no data-i18n of
        /// any kind, and the two it does not are static both ways.
        /// </summary>
        [Fact]
        public void The_rendered_parts_of_the_line_carry_no_static_id_and_the_static_parts_do()
        {
            var html = Html();

            foreach (var id in new[] { "curServerExe", "curServerExeSrc", "curSaveDir", "curSaveDirSrc",
                                       "btnOpenSrv", "btnOpenSave", "fServerExe", "fSaveDir" })
                Assert.DoesNotContain("data-i18n", Tag(html, id));

            foreach (var id in new[] { "btnBrowseSrv", "btnBrowseSave" })
            {
                var tag = Tag(html, id);
                Assert.Contains("data-i18n=\"world.dir.browse\"", tag);
                Assert.Contains("data-i18n-title=\"world.dir.browse.", tag);
            }
        }

        /// <summary>
        /// The source word, both ways round, and the shape the reply has to arrive in for
        /// the page to know which one to say.
        /// </summary>
        [Fact]
        public void The_line_says_whether_the_path_was_set_for_this_server_or_is_the_default()
        {
            var js = AppJs();
            var catalog = Catalog();

            Assert.Contains("const own=p[d.srcKey]===\"profile\";", js);
            Assert.Contains("word.textContent=own?T(\"world.dir.source.profile\"):T(\"world.dir.source.default\");", js);
            Assert.Equal("set for this server", Lore(catalog, "world.dir.source.profile"));
            Assert.Equal("default", Lore(catalog, "world.dir.source.default"));

            // Both keys of the reply, spelled the way the bridge spells them.
            Assert.Contains("effKey:\"EffectiveServerExePath\", srcKey:\"ServerExePathSource\"", js);
            Assert.Contains("effKey:\"EffectiveSaveDataFolderPath\", srcKey:\"SaveDataFolderPathSource\"", js);

            var bridge = AppSourceTree.Files()["BlendWindow.Bridge.cs"];
            foreach (var key in new[] { "EffectiveServerExePath", "ServerExePathSource",
                                        "EffectiveSaveDataFolderPath", "SaveDataFolderPathSource" })
                Assert.Contains("dto[\"" + key + "\"]", bridge);
        }

        /// <summary>
        /// Open moved onto the Currently line, because that line is what it acts on.
        /// </summary>
        [Fact]
        public void Open_sits_on_the_line_it_acts_on()
        {
            var html = Html();

            foreach (var id in new[] { "btnOpenSrv", "btnOpenSave" })
            {
                var at = html.IndexOf("id=\"" + id + "\"", StringComparison.Ordinal);
                var line = html.LastIndexOf("class=\"dircur\"", at, StringComparison.Ordinal);
                var box = html.LastIndexOf("class=\"pathrow\"", at, StringComparison.Ordinal);
                Assert.True(line > box, id + " is no longer on the Currently line");
                Assert.Contains("dircur-open", Tag(html, id));
            }
        }

        // ============================================================ B. Open's tooltip

        /// <summary>
        /// The tooltip names the exact folder, and says something different while the box
        /// underneath holds something else. That second sentence is the whole of one
        /// report: a path typed, Open pressed, the old folder opened, no word said.
        /// </summary>
        [Fact]
        public void Opens_tooltip_names_the_folder_and_has_a_word_for_an_unsaved_box()
        {
            var js = AppJs();
            var catalog = Catalog();

            Assert.Contains("open.title=!path?T(\"world.dir.open.none.title\")", js);
            Assert.Contains(":dirty.indexOf(d.input)>=0?T(\"world.dir.open.dirty.title\",{path})", js);
            Assert.Contains(":T(\"world.dir.open.title\",{path});", js);

            // Both sentences name the folder, and the dirty one says which of the two.
            Assert.Contains("{path}", Lore(catalog, "world.dir.open.title"));
            var dirty = Lore(catalog, "world.dir.open.dirty.title");
            Assert.Contains("{path}", dirty);
            Assert.Contains("saved", dirty);

            // Two buttons both read Open, so each carries the folder it belongs to as its
            // accessible name. The two old tooltip ids are what say it.
            Assert.Contains("open.setAttribute(\"aria-label\",T(d.openAriaId));", js);
            Assert.Contains("openAriaId:\"world.server_exe.open.title\"", js);
            Assert.Contains("openAriaId:\"world.save_dir.open.title\"", js);
        }

        // ============================================================== C. the two boxes

        /// <summary>
        /// The box is still the override, and an empty one still means the default. Both
        /// placeholders say which default, and the note under the section says it in words
        /// once rather than three times.
        /// </summary>
        [Fact]
        public void Both_boxes_offer_the_default_as_a_placeholder_and_the_section_says_what_empty_means()
        {
            var js = AppJs();
            var html = Html();
            var catalog = Catalog();

            Assert.Contains("if(box) box.placeholder=fallback?T(\"world.dir.placeholder\",{path:fallback}):T(d.placeholderId);", js);
            Assert.Equal("default: {path}", Lore(catalog, "world.dir.placeholder"));

            // The floor each box reads before that answer has arrived, one per kind.
            Assert.Contains("placeholderId:\"world.server_exe.placeholder\"", js);
            Assert.Contains("placeholderId:\"world.save_dir.placeholder\"", js);
            Assert.StartsWith("default: ", Lore(catalog, "world.server_exe.placeholder"));
            Assert.StartsWith("default: ", Lore(catalog, "world.save_dir.placeholder"));

            // And the sentence that says what an empty box does, in the markup.
            Assert.Contains("data-i18n=\"world.dir.empty_note\"", html);
            var note = Lore(catalog, "world.dir.empty_note");
            Assert.Contains("empty", note);
            Assert.Contains("Save Config", note);

            // The app wide default arrives with its variables filled in.
            var bridge = AppSourceTree.Files()["BlendWindow.Bridge.cs"];
            Assert.Contains("DefaultServerExePath = PathCheck.Expand(prefs.ServerExePath),", bridge);
            Assert.Contains("DefaultSaveDataFolderPath = PathCheck.Expand(prefs.SaveDataFolderPath),", bridge);
            Assert.Contains("S.userPaths={exe:up.DefaultServerExePath||null,dir:up.DefaultSaveDataFolderPath||null};", js);
        }

        /// <summary>
        /// Browse fills the box and marks it changed. It never saves: what it produces is a
        /// path the host still has to keep, like anything else typed on this hall.
        /// </summary>
        [Fact]
        public void Browse_fills_the_box_marks_it_and_saves_nothing()
        {
            var js = AppJs();
            // To the handler's own end at its indent rather than by a length: a comment
            // growing inside it would otherwise quietly shorten the window and turn a gate
            // on the last line of the handler into a gate on nothing.
            var at = js.IndexOf("$(\"#\"+d.browse)?.addEventListener", StringComparison.Ordinal);
            Assert.True(at > 0, "app.js no longer wires Browse");
            var browse = js.Substring(at, js.IndexOf("\n  });\n", at, StringComparison.Ordinal) - at);

            Assert.Contains("const r=await rpc(d.pick,{kind:d.kind});", browse);
            Assert.Contains("if(r===FAIL||!r||r.cancelled||!r.path) return;", browse);
            // Through the same rule a paste goes through, so there is one way a path gets
            // into this box from outside rather than one cleaned road and one raw one.
            Assert.Contains("box.value=unquotePath(r.path);", browse);
            Assert.Contains("renderWorldDirty();", browse);
            Assert.Contains("schedulePathCheck(d.kind);", browse);
            Assert.DoesNotContain("profiles.save", browse);

            // And the preview says so rather than doing nothing at all.
            Assert.Contains("toast(\"ᛃ \"+T(\"world.dir.browse.preview.toast\"));", browse);
            Assert.Contains("preview only", Lore(Catalog(), "world.dir.browse.preview.toast"));
        }

        /// <summary>
        /// A path pasted with the quotes Explorer puts round it goes into the box as the
        /// path.
        /// <para>
        /// Explorer's own Copy as path wraps what it puts on the clipboard in double
        /// quotes, which makes a quoted path the commonest way one arrives in either of
        /// these boxes. The native rule already takes a matched pair off before it judges
        /// anything, so the line under the box has been right; what was still wrong is the
        /// box itself, and the box is what Save Config writes to the profile. So the same
        /// rule runs here, on the one event a quoted path arrives by.
        /// </para>
        /// <para>
        /// On a PASTE rather than on every keystroke, and that is the whole of why it is
        /// written the long way. A host typing a quote deliberately would have it vanish
        /// under the caret the moment they typed a second one, and a box that eats what is
        /// typed into it is worse than a box that takes a path with quotes on it.
        /// </para>
        /// </summary>
        [Fact]
        public void A_path_pasted_with_quotes_round_it_loses_them_on_the_way_into_the_box()
        {
            var js = AppJs();

            // The rule, and the same rule the native side has: one matched pair round the
            // whole thing, trimmed either side, and nothing else touched.
            Assert.Contains("const PATH_QUOTE='\"';", js);
            Assert.Contains("function unquotePath(v){", js);
            Assert.Contains("if(last>=1&&s[0]===PATH_QUOTE&&s[last]===PATH_QUOTE) s=s.slice(1,last).trim();", js);

            var at = js.IndexOf("$(\"#\"+d.input)?.addEventListener(\"paste\"", StringComparison.Ordinal);
            Assert.True(at > 0, "the two path boxes no longer handle a paste");
            var paste = js.Substring(at, js.IndexOf("\n  });\n", at, StringComparison.Ordinal) - at);

            // What was pasted, rather than what the box holds after the browser has put it
            // there: the box is read too late to tell a paste from anything else.
            Assert.Contains("(e.clipboardData||window.clipboardData)?.getData(\"text\")", paste);
            // A paste that was not wrapped falls through to the browser untouched, which
            // keeps the caret, the selection and the undo stack the browser's business.
            Assert.Contains("if(!clean||clean===pasted) return;", paste);
            Assert.Contains("e.preventDefault();", paste);
            // Dropped at the caret, over the selection, rather than replacing the box.
            Assert.Contains("box.value=box.value.slice(0,from)+clean+box.value.slice(to);", paste);
            // And the event the rest of the hall hangs off is said out loud, because
            // preventDefault took the browser's own with it. Without this the marker, the
            // notice and the check under the box all miss a pasted path.
            Assert.Contains("box.dispatchEvent(new Event(\"input\",{bubbles:true}));", paste);

            // The native half of the same rule, so the two cannot drift apart.
            var source = AppSourceTree.Read("ValheimBakaLoader", "Tools", "PathCheck.cs");
            Assert.Contains("if (trimmed.Length >= 2 && trimmed[0] == quote && trimmed[^1] == quote)", source);
        }

        /// <summary>
        /// The check is asked while the host types, no oftener than an answer can be read,
        /// and an answer for an older keystroke can never land on a newer one. The note is
        /// only ever shown against the path it was asked about.
        /// </summary>
        [Fact]
        public void The_check_is_debounced_guarded_and_never_shown_against_another_path()
        {
            var js = AppJs();

            Assert.Contains("const PATH_CHECK_MS=320;", js);
            Assert.Contains("_pathCheckT[kind]=setTimeout(", js);
            Assert.Contains("const seq=(_pathCheckSeq[kind]=(_pathCheckSeq[kind]||0)+1);", js);
            Assert.Contains("if(seq!==_pathCheckSeq[kind]) return;", js);
            Assert.Contains("S.pathChecks[kind]=Object.assign({},r,{forPath:typed});", js);
            Assert.Contains("if(!typed||!answer||answer.forPath!==typed){", js);

            // An id the page has no sentence for shows no note rather than a dotted name.
            Assert.Contains("if(answer.problemId&&!row){", js);
        }

        // =========================================================== D. unsaved changes

        /// <summary>
        /// The snapshot is taken where the form is filled from what is saved, and again
        /// after a save. Nothing else takes one.
        /// <para>
        /// The world list is the one control filled later than the rest, because it is
        /// fetched, and it ADOPTS on its own rather than taking a second snapshot of the
        /// whole form. A second snapshot would declare everything on the hall saved as of
        /// whenever that fetch happened to land, which is a real window: a reader typing a
        /// server name into a hall that is already on screen would have had it swallowed.
        /// </para>
        /// </summary>
        [Fact]
        public void The_form_is_snapshotted_after_a_render_and_after_a_save()
        {
            var js = AppJs();

            // In renderWorldForm: one snapshot, and the world adopted on its own when the
            // list lands. Read to the function's own closing brace at column zero rather
            // than by a length, so a comment growing cannot hide a line.
            var opens = js.IndexOf("function renderWorldForm(){", StringComparison.Ordinal);
            Assert.True(opens > 0, "app.js no longer has renderWorldForm");
            var form = js.Substring(opens, js.IndexOf("\n}\n", opens, StringComparison.Ordinal) - opens);
            Assert.Contains("worldFormSnapshot();", form);
            Assert.Contains("renderWorldSelect().then(()=>worldFormAdopt(\"world\")).catch(()=>{});", form);
            // Exactly one snapshot in here: the late one is an adopt or it is a window in
            // which anything typed is silently declared saved.
            Assert.Single(Regex.Matches(form, Regex.Escape("worldFormSnapshot();")));

            // And after a save that landed. Read to the handler's own end, at column zero.
            var pressed = js.IndexOf("$(\"#saveCfgBtn\").addEventListener", StringComparison.Ordinal);
            Assert.True(pressed > 0, "app.js no longer wires Save Config");
            var save = js.Substring(pressed, js.IndexOf("\n});\n", pressed, StringComparison.Ordinal) - pressed);
            var landed = save.IndexOf("renderAllFromPrefs();", StringComparison.Ordinal);
            Assert.True(landed > 0, "the save no longer redraws the hall from what it wrote");
            Assert.Contains("worldFormSnapshot();", save.Substring(landed));

            // The clean state the hall starts in, taken at the bottom of the World block
            // so every control it reads is already in the page. Anchored on the statement
            // being at column zero: indented, it would be inside something that runs later.
            Assert.Matches(new Regex(@"^worldFormSnapshot\(\);$", RegexOptions.Multiline), js);
            var opening = Regex.Matches(js, @"^worldFormSnapshot\(\);$", RegexOptions.Multiline);
            Assert.Single(opening);
            Assert.True(opening[0].Index > js.IndexOf("\nrenderMaxPlayers();", StringComparison.Ordinal),
                "the opening snapshot is taken before the controls it reads are filled");
        }

        /// <summary>
        /// A control the app fills LATER than the snapshot is adopted rather than
        /// re-snapshotted, so nothing else in the form is quietly declared saved. Without
        /// this, every profile load raised the notice about the max players count and the
        /// five dials, none of which the host had touched.
        /// </summary>
        [Fact]
        public void A_value_the_app_fills_later_is_adopted_without_clearing_the_rest()
        {
            var js = AppJs();

            Assert.Contains("function worldFormAdopt(...ids){", js);
            Assert.Contains("for(const id of ids) if(Object.prototype.hasOwnProperty.call(now,id)) WORLD_SNAP[id]=now[id];", js);
            Assert.Contains("try{worldFormAdopt(\"fMaxPlayers\");}catch(_){}", js);
            Assert.Contains("try{worldFormAdopt(...Object.values(WORLDGEN).map(def=>def.sel));}catch(_){}", js);

            // The count is adopted INSIDE the arm that filled the box, never beside it. A
            // call that failed leaves whatever was in the box, and adopting then declares
            // a number the reader may well have typed themselves to be the saved one.
            Assert.Contains(
                "if(r!==FAIL&&r?.count!=null){\n    $(\"#fMaxPlayers\").value=r.count;\n"
                + "    try{worldFormAdopt(\"fMaxPlayers\");}catch(_){}\n  }", js);

            // The dials are adopted only on the arm that LOADED a world's stored values.
            var opens = js.IndexOf("async function renderWorldMods(){", StringComparison.Ordinal);
            Assert.True(opens > 0, "app.js no longer declares renderWorldMods");
            var mods = js.Substring(opens, js.IndexOf("\n}\n", opens, StringComparison.Ordinal) - opens);
            var kept = mods.IndexOf("if(S.worldMods&&S.worldMods.world===world)", StringComparison.Ordinal);
            var adopt = mods.IndexOf("worldFormAdopt(...Object.values(WORLDGEN)", StringComparison.Ordinal);
            Assert.True(kept > 0 && adopt > kept);
            Assert.DoesNotContain("worldFormAdopt", mods.Substring(kept, adopt - kept - 1).Split("return;")[0]);
        }

        /// <summary>
        /// The one window between filling the form and the world list landing, and what it
        /// is closed with.
        /// <para>
        /// renderWorldForm fills every control from what is saved and snapshots. Every
        /// control but one: the world's list is FETCHED, so the entry that is really the
        /// saved world is not in the field yet when that snapshot is taken. Something has
        /// to declare it clean when it lands, and the obvious way to do it is to snapshot
        /// again. That is the window: a second snapshot declares the WHOLE form saved as of
        /// whenever the fetch happened to land, and a reader who was already on the hall and
        /// started typing a server name in the meantime would have had it swallowed. The
        /// fetch takes as long as the disk does, so the window is small and real rather
        /// than theoretical.
        /// </para>
        /// <para>
        /// It is closed rather than documented: the late pass adopts the ONE control it
        /// filled, by name, and leaves every other key in the snapshot exactly as it was.
        /// Anything typed during the fetch is still unsaved afterwards, which is the truth.
        /// </para>
        /// </summary>
        [Fact]
        public void The_world_list_landing_late_adopts_only_the_world_and_swallows_nothing()
        {
            var js = AppJs();

            var opens = js.IndexOf("function renderWorldForm(){", StringComparison.Ordinal);
            Assert.True(opens > 0, "app.js no longer has renderWorldForm");
            var form = js.Substring(opens, js.IndexOf("\n}\n", opens, StringComparison.Ordinal) - opens);

            // One snapshot, taken once, before the fetch is started.
            Assert.Single(Regex.Matches(form, Regex.Escape("worldFormSnapshot();")));
            // And the late pass is an adopt of exactly one key, which is the world.
            var late = Regex.Matches(form, @"worldFormAdopt\(([^)]*)\)");
            Assert.Single(late);
            Assert.Equal("\"world\"", late[0].Groups[1].Value);
            Assert.True(form.IndexOf("worldFormSnapshot();", StringComparison.Ordinal)
                        < form.IndexOf("renderWorldSelect()", StringComparison.Ordinal),
                "the snapshot is no longer taken before the world list is asked for");

            // That key is the world field's, and the marker it wears is #fWorld's. The two
            // have to agree or the adopt would be clearing a key nothing is tracked under.
            Assert.Contains("out.world=worldFieldValue();", js);
            Assert.Contains("const WORLD_FORM_MARKER={world:\"fWorld\"};", js);

            // The adopt itself only ever writes the keys it was handed, which is what makes
            // "adopt one" different from "snapshot everything".
            Assert.Contains(
                "for(const id of ids) if(Object.prototype.hasOwnProperty.call(now,id)) WORLD_SNAP[id]=now[id];",
                js);

            // And the window is named where the choice was made, so the next reader does
            // not swap the adopt back for a snapshot to save a line.
            Assert.Contains("A second snapshot would also swallow anything typed into any other box", form);
        }

        /// <summary>
        /// What is tracked is what Save Config sends, and nothing else. A marker on a
        /// control nothing saves would be a lie, so the read-only seed and the process
        /// priority are both out; the five dials and the max players count are both in,
        /// because both of them ride along with the save.
        /// </summary>
        [Fact]
        public void The_tracked_set_is_what_the_save_actually_sends()
        {
            var js = AppJs();

            // Each declaration, read to its own closing bracket rather than by a length.
            string Declaration(string name)
            {
                var at = js.IndexOf("const " + name + "=[", StringComparison.Ordinal);
                Assert.True(at > 0, "app.js no longer declares " + name);
                return js.Substring(at, js.IndexOf("];", at, StringComparison.Ordinal) - at);
            }

            var tracked = Declaration("WORLD_FORM_FIELDS");
            foreach (var id in new[] { "fName", "fPassword", "fPort", "fSaveInterval", "fBackups",
                                       "fBackShort", "fBackLong", "fEmptyDelay", "fSchedHours",
                                       "fCrashDelay", "fRconPort", "fRconPw", "fServerExe",
                                       "fSaveDir", "fArgs", "fMaxPlayers" })
                Assert.Contains("\"" + id + "\"", tracked);

            Assert.DoesNotContain("\"fSeed\"", tracked);
            Assert.DoesNotContain("\"prioSel\"", tracked);
            Assert.DoesNotContain("\"fWorld\"", tracked);   // tracked as one key, below

            // The eight switches, and the world as one key rather than two controls.
            var toggles = Declaration("WORLD_FORM_TOGGLES");
            foreach (var id in new[] { "tPublic", "tCrossplay", "tEmpty", "tSched", "tRcon",
                                       "tLogs", "tAutoStart", "tCrash" })
                Assert.Contains("\"" + id + "\"", toggles);

            Assert.Contains("out.world=worldFieldValue();", js);
            Assert.Contains("const WORLD_FORM_MARKER={world:\"fWorld\"};", js);

            // The five dials, read through the same table the dials themselves come from.
            Assert.Contains("for(const key in WORLDGEN){const el=$(\"#\"+WORLDGEN[key].sel);", js);
        }

        /// <summary>
        /// The marker rides on the field rather than on the control, which is what makes it
        /// survive blur. It is a shape the focus glow is not: a dot beside the label and a
        /// rule down the left edge.
        /// </summary>
        [Fact]
        public void A_changed_field_keeps_its_marker_after_the_caret_leaves_it()
        {
            var js = AppJs();
            var css = Css();

            Assert.Contains("const field=el&&el.closest?el.closest(\".field\"):null;", js);
            Assert.Contains("if(field) field.classList.toggle(\"field-dirty\",changed);", js);

            // Nothing hangs the marker on focus or blur, which is the whole point.
            Assert.DoesNotContain("field-dirty\",document.activeElement", js);
            Assert.Contains(".field.field-dirty::before{", css);
            Assert.Contains(".field.field-dirty>label::after{", css);
        }

        /// <summary>
        /// And it costs the field no layout, which is a different question and was the
        /// defect.
        /// <para>
        /// The first shape of the marker stood its rule in room the field made for it:
        /// <c>.field.field-dirty{position:relative;padding-left:9px}</c>. Padding is layout,
        /// so the label and the box both slid 9px right and the box lost 9px of its width
        /// the instant the first character was typed. Measured on the page: the server name
        /// box sat at left 131 clean and left 140 dirty, in the middle of typing into it. A
        /// marker that moves the thing it is marking is worse than no marker.
        /// </para>
        /// <para>
        /// The rule now stands in a gutter that is already there: 16px between the grid's
        /// two columns, and 16px inside the card's own edge. This gate is on the shape of
        /// that, and scripts/ui/dirty_marker_probe.js is the one that measures it, on the
        /// real page, at 1440x900 and at the narrowest the window can be dragged to. That
        /// probe goes red on the pre-fix stylesheet at every field and every width.
        /// </para>
        /// </summary>
        [Fact]
        public void The_marker_is_drawn_in_the_gutter_rather_than_in_room_the_field_makes()
        {
            var css = Css();

            // The whole of the marker's own two rules, read to the end of each.
            var field = css.IndexOf(".field.field-dirty{", StringComparison.Ordinal);
            Assert.True(field > 0, "app.css no longer styles a changed field");
            var rule = css.Substring(field, css.IndexOf('}', field) - field);

            // Nothing in it is layout. Padding was the defect; a width, a margin or a
            // border on the field itself would each be the same defect wearing another
            // face, so none of them is allowed here either.
            Assert.Contains("position:relative", rule);
            Assert.DoesNotContain("padding", rule);
            Assert.DoesNotContain("margin", rule);
            Assert.DoesNotContain("width", rule);
            Assert.DoesNotContain("border", rule);

            // The rule is drawn outside the field's own box, in the gutter beside it.
            var bar = css.IndexOf(".field.field-dirty::before{", StringComparison.Ordinal);
            var drawn = css.Substring(bar, css.IndexOf('}', bar) - bar);
            Assert.Contains("position:absolute", drawn);
            var left = Regex.Match(drawn, @"left:(-?\d+)px");
            Assert.True(left.Success, "the marker's rule no longer says where it stands");
            var px = int.Parse(left.Groups[1].Value);
            // Negative, so it is outside the field. Not so far out that the card's own
            // overflow:hidden clips it: the card leaves 16px inside its edge and the glow
            // reaches 8px past the bar.
            Assert.InRange(px, -8, -1);

            // The probe that measures the thing itself rather than the shape of the code.
            var probe = AppSourceTree.Read("scripts", "ui", "dirty_marker_probe.js");
            Assert.Contains("field-dirty", probe);
            Assert.Contains("1440", probe);
            // The narrowest the window can be, which is where a 9px shove costs the most.
            var min = AppSourceTree.Files()["BlendWindow.cs"];
            Assert.Contains("private const int DesignMinWidth = 1024;", min);
            Assert.Contains("{ width: 1024, height: 680 }", probe);
            // It measures the CONTROL and not only the field: the padding left the field's
            // own box where it was and moved everything inside it, so a probe that watched
            // the field alone would have stayed green on the defect.
            Assert.Contains("cleft: round(c.left), cwidth: round(c.width),", probe);
            Assert.Contains("box left ", probe);
            Assert.Contains("box width ", probe);
        }

        /// <summary>
        /// The notice, the pulse and the rail's dot, all from one comparison, and the
        /// notice carries no button: the only one it could carry is already on the other
        /// corner of the hall.
        /// </summary>
        [Fact]
        public void The_notice_the_pulse_and_the_rail_dot_all_follow_the_same_count()
        {
            var js = AppJs();
            var html = Html();
            var css = Css();

            Assert.Contains("const notice=$(\"#worldUnsaved\"); if(notice) notice.classList.toggle(\"on\",any);", js);
            Assert.Contains("if(save) save.classList.toggle(\"pulse\",any);", js);
            Assert.Contains("if(rail) rail.classList.toggle(\"has-unsaved\",any);", js);

            // No button in the notice, and no dialog anywhere near navigating away.
            var notice = html.Substring(html.IndexOf("id=\"worldUnsaved\"", StringComparison.Ordinal), 420);
            Assert.DoesNotContain("<button", notice);
            Assert.DoesNotContain("btn ", notice);
            Assert.DoesNotContain("beforeunload", js);
            Assert.DoesNotContain("world.unsaved.confirm", js);

            // It has nothing to press, so it never takes a click either.
            var rule = css.IndexOf(".worldunsaved{", StringComparison.Ordinal);
            Assert.True(rule > 0, "app.css no longer styles the notice");
            Assert.Contains("pointer-events:none;",
                css.Substring(rule, css.IndexOf('}', rule) - rule));

            // The rail's dot is drawn rather than added to the markup.
            Assert.Contains(".navitem.has-unsaved::after{", css);
            Assert.DoesNotContain("navdot", html);
        }

        /// <summary>
        /// The notice stands beside the halls rather than inside one, and the hall gives up
        /// a band at its foot while it is up.
        /// <para>
        /// This is the shape of a real defect rather than a preference. A hall is
        /// <c>position:absolute;inset:0</c> of the pane and scrolls inside itself, so the
        /// first version of this, pinned to the bottom of the hall, had the form sliding
        /// underneath it: at seven of the eight scroll positions the hall can be read at,
        /// the notice covered a control, and one of them was the very box that had just
        /// been typed into. Lifting the hall's own bottom edge shortens the SCROLLING AREA,
        /// which is the only arrangement where nothing can pass behind it at any scroll
        /// position.
        /// </para>
        /// <para>
        /// How DEEP the band has to be is a separate question and is not asserted here.
        /// It is the notice's own measured height, which no number in a stylesheet can
        /// stand in for, and the two gates for it are the test below this one and
        /// scripts/ui/unsaved_band_probe.js, which drives the real page.
        /// </para>
        /// </summary>
        [Fact]
        public void The_notice_stands_outside_the_hall_and_the_hall_makes_room_for_it()
        {
            var html = Html();
            var css = Css();
            var js = AppJs();

            // Outside the hall: after the hall's own closing tag, before the next one opens.
            var closes = html.IndexOf("</section>", html.IndexOf("id=\"page-world\"", StringComparison.Ordinal),
                StringComparison.Ordinal);
            var at = html.IndexOf("id=\"worldUnsaved\"", StringComparison.Ordinal);
            var next = html.IndexOf("<section class=\"page\" id=\"page-atlas\"", StringComparison.Ordinal);
            Assert.True(at > closes, "the notice is back inside the hall, where the form scrolls under it");
            Assert.True(at < next, "the notice drifted out of the Settings hall's own stretch of markup");

            // Pinned to the pane's corner rather than to the form's last line.
            var rule = css.Substring(css.IndexOf(".worldunsaved{", StringComparison.Ordinal), 160);
            Assert.Contains("position:absolute", rule);
            Assert.DoesNotContain("position:sticky", rule);

            // The band: one token, read by the hall's own bottom edge. What is written
            // INTO that token is the next test's question, not this one's.
            Assert.Contains("--unsaved-band:", css);
            Assert.Contains("#page-world.unsaved-room{bottom:var(--unsaved-band)}", css);
            Assert.Contains("if(hall) hall.classList.toggle(\"unsaved-room\",any);", js);
            // And it is toggled on the SCROLLING element rather than on some wrapper, so
            // lifting the edge really does shorten what scrolls.
            Assert.Contains("overflow-y:auto", css.Substring(css.IndexOf(".page{", StringComparison.Ordinal), 120));
            // And the fade that says there is more below follows the fold up with it.
            Assert.Contains(".pages:has(> #page-world.active.unsaved-room)::after{bottom:var(--unsaved-band)}", css);

            // Out there it would hang over every other hall, so being on screen is the form
            // having something unsaved AND this being the hall that owns it. The dot on the
            // rail is what carries the state to the others.
            Assert.Contains(".worldunsaved{position:absolute", css);
            Assert.Contains("display:none;flex-direction:column", css);
            Assert.Contains("#page-world.active ~ .worldunsaved.on{display:flex", css);
            // No inline display left in the markup to fight the stylesheet for it.
            Assert.DoesNotContain("style=\"display:none\"", Tag(html, "worldUnsaved"));
        }

        /// <summary>
        /// How deep the band is, is the notice's own measured height, and nothing else.
        /// <para>
        /// This is the gate on a defect that had already been through one fix. The notice
        /// was moved out of the hall and the hall was given a band to stand it in, and the
        /// band was written down as a number: <c>--unsaved-band:70px</c>. The notice is two
        /// sentences and sentences wrap, so its height is set by the language and by how
        /// wide the window is, and a number cannot be right for all of those at once. At
        /// 1408x880 in the pseudo catalog the notice stands 75px tall, the band gave up 70,
        /// and the last 5px of the form went back behind an opaque notice. Same defect,
        /// same section, one locale over.
        /// </para>
        /// <para>
        /// So this asserts the MECHANISM: the depth is read off the element at runtime and
        /// written back into the token, every way the height can change reaches that
        /// measurement, and the stylesheet's number is only what stands before the first
        /// one. The geometry itself is asserted by scripts/ui/unsaved_band_probe.js, which
        /// drives the real page at every width in both catalogs; this test would go red on
        /// the code that shipped the defect, and that one measures the thing itself.
        /// </para>
        /// </summary>
        [Fact]
        public void The_band_is_the_notices_measured_height_rather_than_a_number_beside_it()
        {
            var js = AppJs();
            var css = Css();

            var band = Body(js, "sizeUnsavedBand");
            // Measured off the element, not computed from anything written down.
            Assert.Contains("notice.getBoundingClientRect()", band);
            Assert.Contains("document.documentElement.style.setProperty(", band);
            Assert.Contains("\"--unsaved-band\"", band);
            // The gap under the notice is read back out of the stylesheet rather than
            // repeated here, so the two cannot drift apart.
            Assert.Contains("getComputedStyle(notice).bottom", band);
            Assert.Contains("box.height+foot+UNSAVED_BAND_SLACK", band);
            // An unmeasurable notice leaves the last good depth alone rather than writing
            // a zero band, which would be the defect with the notice fully on top.
            Assert.Contains("if(!box.height) return;", band);

            // The first frame the notice appears in is measured synchronously, and every
            // other way its height can change is covered by one observer rather than by a
            // call at each site.
            Assert.Contains("sizeUnsavedBand();", Body(js, "renderWorldDirty"));
            Assert.Contains("new ResizeObserver(()=>sizeUnsavedBand()).observe(watched);", js);

            // And the stylesheet's own number is only what stands before that first
            // measurement. Nothing else in app.css writes the token.
            Assert.Single(Regex.Matches(css, Regex.Escape("--unsaved-band:")));
            Assert.Contains(":root{--unsaved-band:70px}", css);

            // The probe that measures the thing itself, rather than the shape of the code.
            var probe = AppSourceTree.Read("scripts", "ui", "unsaved_band_probe.js");
            Assert.Contains("hall.bottom", probe);
            Assert.Contains("notice.top", probe);
            // It sweeps both catalogs and more than one width, which is what the fixed
            // number passed at and failed one step either side of.
            Assert.Contains("loadLanguage", probe);
            Assert.Contains("1408", probe);
        }

        /// <summary>
        /// The pulse is motion, so it has a rule for a host who has asked for less of it:
        /// the animation is dropped outright and a steady outline says the same thing.
        /// </summary>
        [Fact]
        public void The_pulse_has_a_reduced_motion_rule_that_still_says_the_same_thing()
        {
            var css = Css();

            Assert.Contains("@keyframes saveCfgPulse{", css);
            Assert.Contains("#saveCfgBtn.pulse{animation:saveCfgPulse", css);

            var at = css.IndexOf("@media (prefers-reduced-motion:reduce){", StringComparison.Ordinal);
            Assert.True(at > 0, "app.css has no reduced motion rule for the pulse");
            var block = css.Substring(at, css.IndexOf("\n}", at, StringComparison.Ordinal) - at);
            Assert.Contains("#saveCfgBtn.pulse{animation:none;outline:", block);

            // And the notice's own slide, turned off with the SAME selector it is turned on
            // with. A media query carries no weight of its own, so a plain .worldunsaved
            // here would lose to the id in the rule that starts the slide, and the notice
            // would go on sliding in for the one host who asked it not to. That is a silent
            // loss: the stylesheet still reads as though it had been handled.
            var starts = css.IndexOf("~ .worldunsaved.on{display:flex;animation:", StringComparison.Ordinal);
            Assert.True(starts > 0, "the notice no longer slides in, or does it from somewhere else");
            Assert.Contains("#page-world.active ~ .worldunsaved.on{animation:none}", block);
        }

        /// <summary>
        /// A warning folded inside a shut section is a warning nobody reads, so the section
        /// opens itself for a change or a problem in one of its boxes.
        /// <para>
        /// It never SHUTS the section, and it does open it AGAIN. The docstring that used
        /// to stand here said "it only ever opens: a host who folds it away afterwards
        /// keeps it folded", which was half true and repeated a comment in app.js that was
        /// half true in the same place. The first half holds: nothing in the function
        /// removes the class. The second half never did. The function runs from
        /// renderWorldDirs, which runs from renderWorldDirty, which runs on every keystroke
        /// on the hall, and the only thing that stops it re-opening a folded section is the
        /// section already being open. So a host who folds it away while a box inside it is
        /// unsaved has it back at the next keystroke, and the gate below is on that being
        /// the real shape rather than on the sentence somebody wrote about it.
        /// </para>
        /// </summary>
        [Fact]
        public void The_directories_section_opens_itself_for_a_change_or_a_problem()
        {
            var js = AppJs();
            var body = Body(js, "syncDirsSection");

            Assert.Contains("function syncDirsSection(){", js);
            Assert.Contains("if(!sec||sec.classList.contains(\"open\")) return;", js);
            Assert.Contains("if(dirty.indexOf(d.input)>=0) return true;", js);
            // A PROBLEM note, not a confirmation: a host who folded the section away is not
            // owed a fold back to be told the path they saved is still fine.
            Assert.Contains("return !!(note&&note.style.display!==\"none\"&&note.dataset.problem===\"1\");", js);
            Assert.Contains("if(row) el.dataset.problem=\"1\"; else delete el.dataset.problem;", js);
            Assert.Contains("sec.classList.add(\"open\");", js);
            // The folded header's own count follows it open.
            Assert.Contains("try{syncCollapsibleTerms();}catch(_){}", js);
            // Nothing ever shuts it from here.
            Assert.DoesNotContain("classList.remove(\"open\")", body);

            // And nothing remembers that a host folded it, which is what makes it open
            // again rather than stay folded: the guard on the first line reads the class
            // that is on the section right now and nothing else. A flag would be the shape
            // of the behaviour the old sentence described, and there is none.
            Assert.DoesNotContain("dirsFolded", js);
            Assert.DoesNotContain("wasFolded", js);
            // Two early returns and no third: the section already being open, and there
            // being nothing worth opening it for. Neither of them remembers anything.
            Assert.Equal(2, Regex.Matches(body, Regex.Escape("return;")).Count);

            // The re-opening road, spelled out: every keystroke on the hall reaches this
            // through the same two functions, so the one guard above is all there is
            // between a folded section and it opening again.
            Assert.Contains("syncDirsSection();", Body(js, "renderWorldDirs"));
            Assert.Contains("renderWorldDirs();", Body(js, "renderWorldDirty"));
            Assert.Contains("$(\"#page-world\")?.addEventListener(\"input\",()=>{renderWorldDirty();});", js);

            // And the comment above it says exactly that rather than the opposite. This is
            // the gate on the sentence itself: it went in because the one that used to be
            // here promised a fold that stays folded and the code never had one.
            var at = js.IndexOf("function syncDirsSection(){", StringComparison.Ordinal);
            var opens = js.LastIndexOf("/* A warning folded", at, StringComparison.Ordinal);
            Assert.True(opens > 0, "app.js no longer explains what syncDirsSection does");
            var comment = js.Substring(opens, at - opens);
            Assert.DoesNotContain("keeps it folded", comment);
            Assert.Contains("It never SHUTS the section", comment);
            Assert.Contains("It does open the section again.", comment);
        }

        // ============================================================ E. the language

        /// <summary>
        /// A language switch redraws the two lines, both notes, both placeholders and the
        /// notice, because it redraws the one function all of them come out of.
        /// </summary>
        [Fact]
        public void A_language_switch_redraws_every_sentence_this_work_added()
        {
            var js = AppJs();

            Assert.Contains("try{renderWorldDirty();}catch(_){}", js);
            // renderWorldDirty is what repaintBootCopy runs, and it is the one door to the
            // lines: the tooltip on Open depends on whether the box below has been touched.
            Assert.Contains("renderWorldDirs();", Body(js, "renderWorldDirty"));
            var dirs = Body(js, "renderWorldDirs");
            Assert.Contains("renderPathNote(d);", dirs);
            Assert.Contains("syncDirsSection();", dirs);

            // applyLanguage runs repaintBootCopy, which is where that line sits.
            Assert.Contains("renderWorldDirty();", Body(js, "repaintBootCopy"));
        }

        // ============================================================== F. the preview

        /// <summary>
        /// Every state of this section depends on an answer only the app can give, so a
        /// browser could photograph exactly one of them: empty. The seam hands all three in
        /// and answers with what ended up on screen, so a walk can assert rather than only
        /// photograph.
        /// </summary>
        [Fact]
        public void The_preview_can_reach_every_state_of_the_section()
        {
            var js = AppJs();
            var opens = js.IndexOf("  worldDirs:o=>{", StringComparison.Ordinal);
            Assert.True(opens > 0, "app.js no longer carries the worldDirs seam");
            // To the seam's own closing brace at its indent, rather than a length: a comment
            // growing inside it would otherwise quietly shorten the window and turn a gate
            // on the last line of the answer into a gate on nothing.
            var seam = js.Substring(opens, js.IndexOf("\n  },\n", opens, StringComparison.Ordinal) - opens);

            Assert.Contains("if(d.prefs) S.prefs=Object.assign({},S.prefs||{},d.prefs);", seam);
            Assert.Contains("if(d.defaults) S.userPaths=Object.assign({},S.userPaths,d.defaults);", seam);
            Assert.Contains("if(d.typed) for(const kind in d.typed){", seam);
            Assert.Contains("if(d.checks) for(const kind in d.checks){", seam);
            Assert.Contains("if(d.snapshot) worldFormSnapshot();", seam);
            Assert.Contains("const dirty=renderWorldDirty();", seam);

            foreach (var answered in new[] { "dirty", "exe:", "exeSource:", "save:", "saveSource:",
                                             "openTitle:", "notice:", "band:", "dirsOpen:" })
                Assert.Contains(answered, seam);
        }

        /// <summary>
        /// The four keys the reply adds are answers, not settings, so they are dropped on
        /// the way back. The native side ignores a key it has no field for, which makes
        /// this belt and braces rather than load bearing, and it is here because a payload
        /// that says things it does not mean is how one of them gets read one day.
        /// </summary>
        [Fact]
        public void The_answers_the_reply_carries_are_not_sent_back_as_settings()
        {
            var js = AppJs();

            Assert.Contains("delete prefs.EffectiveServerExePath; delete prefs.ServerExePathSource;", js);
            Assert.Contains("delete prefs.EffectiveSaveDataFolderPath; delete prefs.SaveDataFolderPathSource;", js);

            // And the two the form does send are still the profile's own override fields.
            Assert.Contains("ServerExePath:$(\"#fServerExe\").value.trim(),", js);
            Assert.Contains("SaveDataFolderPath:$(\"#fSaveDir\").value.trim(),", js);
        }
    }
}
