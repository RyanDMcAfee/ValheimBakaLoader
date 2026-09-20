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
    /// The page side of BepInEx: the status row above the mods table, the add flow's two
    /// roads, the maintenance switch, the question asked once at the first start, and the
    /// notice that walks a host to the setting.
    /// <para>
    /// These are gates on the interface's own source, because what they guard against is a
    /// line somebody trims later. A row that quietly joins the mod list, an offer that
    /// stops being made because a reason code was renamed, or a dialog that silently keeps
    /// its wording through a language switch: none of the three is a compile error and none
    /// of them is a failing unit test anywhere else.
    /// </para>
    /// </summary>
    public class WebUiBepInExRowTests
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

        private static string Lore(string id)
        {
            var catalog = Catalog();
            Assert.True(catalog.ContainsKey(id), "the English catalog has no " + id);
            var entry = catalog[id];
            Assert.True(entry.TryGetProperty("lore", out var lore), id + " carries no English");
            return lore.ValueKind == JsonValueKind.String ? lore.GetString() : lore.ToString();
        }

        /// <summary>One function's body, from its opening line to the next one at column zero.</summary>
        private static string Body(string name)
        {
            var js = AppJs();
            var at = js.IndexOf("function " + name + "(", StringComparison.Ordinal);
            Assert.True(at >= 0, "app.js no longer declares " + name);
            var end = js.IndexOf("\n}\n", at, StringComparison.Ordinal);
            return end < 0 ? js.Substring(at) : js.Substring(at, end - at);
        }

        // ---------------------------------------------------------------- A. the row is not a mod

        /// <summary>
        /// The loader is drawn from the Mods hall's own painter, so it follows a language
        /// switch and a fresh status with everything else, and it is drawn BEFORE the
        /// unscanned branch returns: a hall nobody has scanned still has a loader.
        /// </summary>
        [Fact]
        public void The_row_is_painted_by_the_hall_and_before_the_hall_can_give_up()
        {
            var body = Body("renderMods");

            Assert.Contains("renderBepInExRow();", body, StringComparison.Ordinal);
            var row = body.IndexOf("renderBepInExRow();", StringComparison.Ordinal);
            var bail = body.IndexOf("if(!scanned){", StringComparison.Ordinal);
            Assert.True(bail > 0, "the unscanned branch is gone");
            Assert.True(row < bail,
                "the loader row is drawn after the hall gives up on an unscanned folder");
        }

        /// <summary>
        /// It is keyed "bepinex" and it is not a table row. BepInEx has no folder under
        /// plugins and no package on the list a scan reads, so it cannot key on FullName
        /// the way every mod row does, and it must not be written into the table body.
        /// </summary>
        [Fact]
        public void The_row_is_synthetic_and_lives_outside_the_table()
        {
            var body = Body("renderBepInExRow");

            Assert.Contains("data-key=\"bepinex\"", body, StringComparison.Ordinal);
            Assert.Contains("$(\"#bepinexRow\")", body, StringComparison.Ordinal);
            // Nothing the table owns is touched from here.
            foreach (var owned in new[] { "#modTable", "#updAllBtn", "#modUpdWrap", "#modCount" })
                Assert.DoesNotContain(owned, body, StringComparison.Ordinal);

            // And the container sits above the table in the document rather than inside it.
            var html = Html();
            var container = html.IndexOf("id=\"bepinexRow\"", StringComparison.Ordinal);
            var table = html.IndexOf("<tbody id=\"modTable\">", StringComparison.Ordinal);
            Assert.True(container > 0 && table > container,
                "the loader row is no longer above the mods table");
        }

        /// <summary>
        /// Update all and the waiting-updates pill both count S.mods, and the loader is
        /// never in S.mods: the row is built from its own DTO. This pins the two counters
        /// to the mod list so a later change cannot quietly fold the loader into either.
        /// </summary>
        [Fact]
        public void The_row_is_in_neither_update_all_nor_the_waiting_badge()
        {
            var body = Body("renderMods");

            Assert.Contains("const mods=sortedMods(S.mods||[]);", body, StringComparison.Ordinal);
            Assert.Contains("const upd=mods.filter(m=>m.UpdateAvailable);", body, StringComparison.Ordinal);
            Assert.Contains("T(\"mods.update_all.label\",{count:scanned?upd.length:\"-\"})", body, StringComparison.Ordinal);
            Assert.Contains("T(\"mods.updates_waiting\",{count:upd.length})", body, StringComparison.Ordinal);

            // sortedMods only ever sees what it is handed, and what it is handed is S.mods.
            var sorted = Body("sortedMods");
            Assert.DoesNotContain("bepinex", sorted, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("S.bepinex", Body("modsForBody"), StringComparison.Ordinal);
        }

        /// <summary>
        /// The states the row can be in, worst first, and the one thing each of them offers.
        /// A looked-after install offers nothing to press, because the next restart window is
        /// what moves it; the same install with the switch off keeps the button.
        /// <para>
        /// The three answers this was written for are still three of the rows in the table.
        /// The others arrived with the takeover work, and the ORDER is the thing worth
        /// pinning: several of these are true at once, only one can be drawn, and losing
        /// winhttp.dll to an antivirus must not read as "not installed" and send a host off
        /// to install something that is already there.
        /// </para>
        /// </summary>
        [Fact]
        public void The_row_has_a_state_for_each_of_the_three_answers()
        {
            var state = Body("bepInExState");
            Assert.Contains("if(!b.installed) return \"missing\";", state, StringComparison.Ordinal);
            Assert.Contains("if(!b.maintainedByBakaLoader) return \"outside\";", state, StringComparison.Ordinal);
            Assert.Contains("return bepInExConsent(b)?\"maintained\":\"manual\";", state, StringComparison.Ordinal);

            // worst first: a core nothing recognises, then a broken one, then a file that is
            // gone, and only then the question of whether anything is installed at all
            foreach (var pair in new[]
                     {
                         ("if(b.unrecognised) return \"unrecognised\";", "if(b.damaged) return \"damaged\";"),
                         ("if(b.damaged) return \"damaged\";", "if(bepInExMissingFileList(b).length) return \"incomplete\";"),
                         ("if(bepInExMissingFileList(b).length) return \"incomplete\";", "if(!b.installed) return \"missing\";"),
                     })
            {
                var first = state.IndexOf(pair.Item1, StringComparison.Ordinal);
                var second = state.IndexOf(pair.Item2, StringComparison.Ordinal);
                Assert.True(first >= 0 && second > first,
                    "the state order moved: " + pair.Item1 + " must be asked before " + pair.Item2);
            }

            var words = Between(AppJs(), "const BEPINEX_ROW_WORDS={", "\n};");
            // every state the chooser can answer has a pill and a sentence of its own
            foreach (var name in new[]
                     {
                         "missing", "maintained", "manual", "outside", "drifted",
                         "elsewhere", "foreign", "unrecognised", "damaged", "incomplete",
                     })
                Assert.Contains(name + ":", words, StringComparison.Ordinal);
            Assert.Contains("pillId:\"bepinex.row.pill.maintained\"", words, StringComparison.Ordinal);
            Assert.Contains("msgId:\"bepinex.row.state.maintained\"", words, StringComparison.Ordinal);
            Assert.Contains("pillId:\"bepinex.row.pill.manual\"", words, StringComparison.Ordinal);
            Assert.Contains("msgId:\"bepinex.row.state.outside\"", words, StringComparison.Ordinal);

            // the buttons each state offers, and the two that offer nothing to press
            var acts = Body("bepInExRowActions");
            Assert.Contains("if(state===\"maintained\") return [];", acts, StringComparison.Ordinal);
            Assert.Contains("if(state===\"missing\") return [\"install\"];", acts, StringComparison.Ordinal);
            // Repair, and Update beside it in the one shape where repair can never finish: the
            // pack the note names is the pack a repair fetches, and Thunderstore has taken it
            // down. Without the second button that row offers only a press that asks for the
            // same missing pack for ever.
            Assert.Contains(
                "if(state===\"incomplete\") return bepInExRepairPackGone(b)?[\"repair\",\"update\"]:[\"repair\"];",
                acts, StringComparison.Ordinal);
            Assert.Contains("b.newestBackup?[\"restore\",\"install\"]:[\"install\"]", acts, StringComparison.Ordinal);
            Assert.Contains("return [\"update\"];", acts, StringComparison.Ordinal);

            var body = Body("renderBepInExRow");
            Assert.Contains("T(\"bepinex.row.note.next_check\")", body, StringComparison.Ordinal);
            // waiting, and the misplaced folder, are facts about an install rather than states
            Assert.Contains("if(b.updateWaiting) notes.push(T(\"bepinex.row.note.waiting\",{version:b.updateWaiting}));",
                body, StringComparison.Ordinal);
            Assert.Contains("b.wrongLocationFolder", body, StringComparison.Ordinal);
            Assert.Contains("id=\"bepRemove\"", body, StringComparison.Ordinal);
        }

        /// <summary>
        /// A version read off the file is not the pack's version and cannot be compared to
        /// one, so the two numbers carry two different explanations rather than one label
        /// that would be a lie in half the cases.
        /// </summary>
        [Fact]
        public void The_two_versions_are_never_said_to_be_the_same_number()
        {
            var chooser = Body("bepInExRowVersion");

            Assert.Contains("titleId:\"bepinex.row.version.pack.title\"", chooser, StringComparison.Ordinal);
            Assert.Contains("titleId:\"bepinex.row.version.file.title\"", chooser, StringComparison.Ordinal);
            // the pack number is only ever shown for an install that has a note recording one
            Assert.Contains("if(b.maintainedByBakaLoader&&b.packVersion)", chooser, StringComparison.Ordinal);
            Assert.Contains("T(shown.titleId)", Body("renderBepInExRow"), StringComparison.Ordinal);
            Assert.Contains("Thunderstore pack version", Lore("bepinex.row.version.pack.title"), StringComparison.Ordinal);
            Assert.Contains("file version", Lore("bepinex.row.version.file.title"), StringComparison.Ordinal);
        }

        /// <summary>
        /// Every button on the row writes the loader, and the host refuses every one of them
        /// while a server on this install is up. Saying so on the button is the difference
        /// between a control that explains itself and one that fails when pressed.
        /// </summary>
        [Fact]
        public void The_buttons_are_refused_out_loud_while_a_server_on_the_install_is_up()
        {
            var body = Body("renderBepInExRow");
            var elements = Between(AppJs(), "const BEPINEX_ACTION_ELEMENTS={", "\n};");

            Assert.Contains("const stop=busy||up.length>0;", body, StringComparison.Ordinal);
            Assert.Contains("const gate=stop?` disabled title=\"${esc(why)}\"`:\"\";", body, StringComparison.Ordinal);
            Assert.Contains("T(\"bepinex.row.blocked.title\")", body, StringComparison.Ordinal);

            // every button the row can draw comes out of the one builder, and that builder
            // puts the gate on all of them
            foreach (var button in new[] { "bepInstall", "bepUpdate", "bepRestore", "bepRepair" })
                Assert.Contains("id:\"" + button + "\"", elements, StringComparison.Ordinal);
            Assert.Contains("id=\"${el2.id}\"${gate}", body, StringComparison.Ordinal);

            // and the one button that is not a loader write at all is gated the same way
            var remove = body.IndexOf("id=\"bepRemove\"", StringComparison.Ordinal);
            Assert.True(remove > 0, "the row lost id=\"bepRemove\"");
            Assert.Contains("${gate}", body.Substring(remove, Math.Min(90, body.Length - remove)),
                StringComparison.Ordinal);
        }

        /// <summary>
        /// The row is drawn only once the host has actually answered. Nothing answered is
        /// not "no loader": an install nobody has looked at must not be told it has none.
        /// </summary>
        [Fact]
        public void An_install_nothing_has_looked_at_is_never_called_empty()
        {
            Assert.Contains("if(!state){el.style.display=\"none\";el.innerHTML=\"\";return;}",
                Body("renderBepInExRow"), StringComparison.Ordinal);
            Assert.Contains("function bepInExMissing(){return !!S.bepinex&&!S.bepinex.installed;}",
                AppJs(), StringComparison.Ordinal);
            // and the hall's empty state is split on exactly that answer
            Assert.Contains(":(bepInExMissing()", Body("renderMods"), StringComparison.Ordinal);
            Assert.Contains("T(\"mods.empty.no_bepinex.title\")", Body("renderMods"), StringComparison.Ordinal);
            Assert.Contains("T(\"mods.empty.none.title\")", Body("renderMods"), StringComparison.Ordinal);
        }

        // ---------------------------------------------------------------- B. the add flow

        /// <summary>
        /// The three answers the add can come back with that are about the loader rather
        /// than about a mod, each on its own road. They are read before anything about the
        /// plugins folder, because two of the three never touched it.
        /// </summary>
        [Fact]
        public void The_add_flow_branches_on_the_reason_and_not_on_the_sentence()
        {
            var body = Body("doAddMod");

            Assert.Contains("if(r.Reason===\"alreadyMaintained\"){renderMods();noticeBepInExMaintained();return;}",
                body, StringComparison.Ordinal);
            Assert.Contains("if(r.Reason===\"bepinex\"){", body, StringComparison.Ordinal);
            Assert.Contains("if(r.Reason===\"noBepInEx\"){", body, StringComparison.Ordinal);

            // before the road that is about a mod
            var loader = body.IndexOf("if(r.Reason===\"alreadyMaintained\")", StringComparison.Ordinal);
            var mod = body.IndexOf("if(r.Reason===\"sourceOff\"", StringComparison.Ordinal);
            Assert.True(loader > 0 && mod > loader, "the loader answers are read after the mod ones");

            // and the refusal is never matched on its English
            Assert.DoesNotContain("BepInEx is not installed", body, StringComparison.Ordinal);
        }

        /// <summary>
        /// The pasted address is held in the flow's own closure and the SAME add is
        /// finished with it once the loader is in. There is no retry step and nothing for
        /// the host to type twice, which is the whole of D11.7a.
        /// </summary>
        [Fact]
        public void The_offer_finishes_the_add_that_was_interrupted()
        {
            Assert.Contains("bepInExOfferModal(()=>doAddMod(url));", Body("doAddMod"), StringComparison.Ordinal);
            // the offer hands that continuation through the write, and the write runs it
            Assert.Contains("bepInExWrite(\"bepinex.install\",link===BEPINEX_DEFAULT_URL?{}:{url:link},after);",
                Body("bepInExOfferModal"), StringComparison.Ordinal);
            Assert.Contains("if(typeof after===\"function\") after();", Body("bepInExWrite"), StringComparison.Ordinal);
        }

        /// <summary>
        /// With the switch ON the host side installs the loader inside the same call, so the
        /// page never sees the reason at all and only has to show the progress it is sent.
        /// The bar is opened by the first event that arrives rather than guessed at here,
        /// and it only ever opens for a write this window is part of: the unattended path
        /// posts the same event with nobody at the keyboard.
        /// </summary>
        [Fact]
        public void An_install_inside_an_add_shows_its_progress_and_nothing_elses()
        {
            var add = Body("doAddMod");
            Assert.Contains("BEP_ADD_IN_FLIGHT=true;", add, StringComparison.Ordinal);
            Assert.Contains("BEP_ADD_IN_FLIGHT=false;", add, StringComparison.Ordinal);
            Assert.Contains("bepInExProgressDone();", add, StringComparison.Ordinal);

            var bar = Body("renderBepInExProgress");
            Assert.Contains("if(!BEP_PROGRESS||!(BEP_WRITING||BEP_ADD_IN_FLIGHT)) return;", bar, StringComparison.Ordinal);
            // it names a rebuilder, so a language switch redraws it rather than freezing it
            Assert.Contains("renderBepInExProgress);", bar, StringComparison.Ordinal);
            // and the dialog is only closed when it was this one that put it up
            Assert.Contains("if(!BEP_PROG_OPEN) return;", Body("bepInExProgressDone"), StringComparison.Ordinal);
        }

        /// <summary>
        /// An untouched address box sends no address at all, so the pinned pack is resolved
        /// fresh on the host side. Sending the page's own link back would pin the version
        /// this file was written on for ever.
        /// </summary>
        [Fact]
        public void The_default_address_is_shown_but_never_sent()
        {
            var js = AppJs();

            Assert.Contains("const BEPINEX_DEFAULT_URL=\"https://thunderstore.io/c/valheim/p/denikson/BepInExPack_Valheim/\";",
                js, StringComparison.Ordinal);
            var offer = Body("bepInExOfferModal");
            Assert.Contains("id=\"mBepUrl\"", offer, StringComparison.Ordinal);
            Assert.Contains("BEP_URL_TYPED==null?BEPINEX_DEFAULT_URL:BEP_URL_TYPED", offer, StringComparison.Ordinal);
            Assert.Contains("link===BEPINEX_DEFAULT_URL?{}:{url:link}", offer, StringComparison.Ordinal);
            // what was typed survives the redraw, because it does not live in the element
            Assert.Contains("if(e.target&&e.target.id===\"mBepUrl\") BEP_URL_TYPED=e.target.value;",
                js, StringComparison.Ordinal);
        }

        // ---------------------------------------------------------------- C. the switch

        /// <summary>
        /// The switch sits with the other promise about the same restart window, carries
        /// its English in the document the way every static label does, and goes through
        /// the one save the rest of the card goes through.
        /// </summary>
        [Fact]
        public void The_maintenance_switch_sits_in_upkeep_and_saves_like_the_rest()
        {
            var html = Html();

            Assert.Contains("id=\"rowBepMaint\"", html, StringComparison.Ordinal);
            Assert.Contains("id=\"tBepMaint\"", html, StringComparison.Ordinal);
            Assert.Contains("data-i18n=\"hearth.upkeep.bepinex\"", html, StringComparison.Ordinal);
            Assert.Contains("data-i18n=\"hearth.upkeep.bepinex.note\"", html, StringComparison.Ordinal);

            // Right after the mod-update switch and its note, and BEFORE the second mod
            // site: the two promises about a scheduled restart belong together.
            var mods = html.IndexOf("data-i18n=\"hearth.upkeep.mod_updates.note\"", StringComparison.Ordinal);
            var bep = html.IndexOf("data-i18n=\"hearth.upkeep.bepinex\"", StringComparison.Ordinal);
            var hexium = html.IndexOf("id=\"hexiumSwitchLabel\"", StringComparison.Ordinal);
            Assert.True(mods > 0 && bep > mods && hexium > bep,
                "the loader switch is no longer right after the mod-update one");

            // The document's own English is the catalog's, so the first frame and every
            // frame after it read the same sentence.
            Assert.Contains("BepInEx kept up to date by BakaLoader", html, StringComparison.Ordinal);
            Assert.Equal("BepInEx kept up to date by BakaLoader", Lore("hearth.upkeep.bepinex"));

            var upkeep = Body("initUpkeep");
            // The state on show is the EFFECTIVE one. The preference defaults to on, so the
            // switch alone would draw a promise made on behalf of a host who has never been
            // asked, and that is exactly the reading the consent rule exists to stop.
            Assert.Contains("BEP_ANSWERED=!!up.BepInExMaintenanceAsked;", upkeep, StringComparison.Ordinal);
            Assert.Contains("setT(\"tBepMaint\",!!up.BepInExMaintained&&BEP_ANSWERED);", upkeep, StringComparison.Ordinal);
            // and it goes into the save only once there IS an answer: every switch on this
            // card rides in on one save, so leaving it in would have a click on Start with
            // Windows record an answer to the loader question
            Assert.Contains("BepInExMaintained:swOn(\"tBepMaint\"),BepInExMaintenanceAsked:true", upkeep,
                StringComparison.Ordinal);
            Assert.Contains("const bepSaved=()=>BEP_ANSWERED", upkeep, StringComparison.Ordinal);
            Assert.DoesNotContain("AutoUpdateMods:swOn(\"tAutoUpdMods\"),BepInExMaintained:", upkeep);
            Assert.Contains("$(\"#tBepMaint\")?.addEventListener(\"click\",()=>{", upkeep, StringComparison.Ordinal);
            // moving it by hand IS the answer, so the flag goes up before the save carries it
            var click = upkeep.IndexOf("$(\"#tBepMaint\")?.addEventListener", StringComparison.Ordinal);
            var answered = upkeep.IndexOf("BEP_ANSWERED=true;", click, StringComparison.Ordinal);
            var saved = upkeep.IndexOf("save();", click, StringComparison.Ordinal);
            Assert.True(answered > click && saved > answered,
                "the loader switch saves before it records that the question was answered");
            // and the row above the mods table follows the switch at once rather than
            // saying the opposite of it until the next status arrives
            Assert.Contains("S.bepinex.consent=on;", upkeep, StringComparison.Ordinal);
            Assert.Contains("S.bepinex.consentUnanswered=false;", upkeep, StringComparison.Ordinal);
            Assert.Contains("renderMods();", upkeep, StringComparison.Ordinal);
        }

        /// <summary>
        /// The notice has to REACH the host. The condition bar draws exactly one row at a
        /// time, and a standing BakaLoader-update row is the ordinary case, so storing the
        /// condition and hoping it is drawn is not an answer: a host who pasted the pack link
        /// while maintenance was on got a suppressed toast, an undrawn row, and nothing on
        /// screen at all. Two independent guarantees now: the press toasts, and the notice
        /// outranks the standing update row.
        /// </summary>
        [Fact]
        public void The_already_looked_after_notice_is_said_out_loud_as_well_as_stood_up()
        {
            var js = AppJs();
            var notice = Body("noticeBepInExMaintained");

            Assert.Contains("toast(\"ᛒ \"+T(\"bepinex.notice.already_maintained\"));", notice,
                StringComparison.Ordinal);
            Assert.Contains("conditionBepInExMaintained();", notice, StringComparison.Ordinal);

            // both roads that raise it go through the one function
            Assert.Contains("errorId===\"bepinex.alreadyMaintained\"){ noticeBepInExMaintained(); return FAIL; }",
                js, StringComparison.Ordinal);
            Assert.Contains("if(r.Reason===\"alreadyMaintained\"){renderMods();noticeBepInExMaintained();return;}",
                js, StringComparison.Ordinal);

            // and the toast is NOT part of the row's language replay, which redraws it
            Assert.DoesNotContain("toast(", Body("conditionBepInExMaintained"), StringComparison.Ordinal);
        }

        /// <summary>
        /// The order itself: the notice is raised by a press the host just made, and every
        /// row below it here is standing. Above appUpdate, because a standing app-update row
        /// is the common case and it hid the notice completely.
        /// </summary>
        [Fact]
        public void The_notice_outranks_the_standing_app_update_row()
        {
            var js = AppJs();
            var at = js.IndexOf("const CONDITION_ORDER=", StringComparison.Ordinal);
            Assert.True(at > 0, "the condition order is gone");

            var order = js.Substring(at, 400);
            var notice = order.IndexOf("\"bepinexNotice\"", StringComparison.Ordinal);
            var appUpdate = order.IndexOf("\"appUpdate\"", StringComparison.Ordinal);
            var waiting = order.IndexOf("\"bepinexWaiting\"", StringComparison.Ordinal);

            Assert.True(notice > 0 && appUpdate > 0 && waiting > 0, "a row left the order");
            Assert.True(notice < appUpdate, "the notice is still drawn under the app-update row");
            Assert.True(notice < waiting, "the notice is still drawn under the waiting row");
        }

        // ---------------------------------------------------------------- D. the first start

        /// <summary>
        /// Asked once, and only once, and only where there is a truthful answer to show.
        /// A host that predates the setting, or a status that has not come back yet, is
        /// never put a question the page cannot word.
        /// </summary>
        [Fact]
        public void The_first_start_question_is_gated_on_the_preference()
        {
            var gate = Body("bepInExAskOnce");

            Assert.Contains("if(!b||!bepInExUnanswered(b)||BEP_ASKED_THIS_RUN){next();return;}", gate, StringComparison.Ordinal);
            Assert.Contains("BEP_ASKED_THIS_RUN=true;", gate, StringComparison.Ordinal);
            Assert.Contains("bepInExFirstStartModal(go);", gate, StringComparison.Ordinal);

            // and unanswered is its own state: the switch alone is not an answer, because
            // the preference it reads defaults to on
            var rule = Body("bepInExUnanswered");
            Assert.Contains("if(\"consentUnanswered\" in b) return !!b.consentUnanswered;", rule, StringComparison.Ordinal);
            Assert.Contains("if(\"maintenanceAsked\" in b) return !b.maintenanceAsked;", rule, StringComparison.Ordinal);
        }

        /// <summary>
        /// The question sits in front of a press the host already made, so waving it away is
        /// an answer to the question and never a cancellation of the Start. A dismissal is the
        /// absence of a click, which nothing can see without watching the dialog itself, and
        /// the start must happen exactly once whichever way the dialog goes.
        /// </summary>
        [Fact]
        public void A_dismissed_first_start_question_still_starts_the_server()
        {
            var gate = Body("bepInExAskOnce");

            Assert.Contains("const go=()=>{if(went)return;went=true;next();};", gate, StringComparison.Ordinal);
            Assert.Contains("onModalDismissed(go);", gate, StringComparison.Ordinal);

            // and the watcher is real: it fires on the dialog going away, however it goes.
            var watcher = Body("onModalDismissed");
            Assert.Contains("new MutationObserver", watcher, StringComparison.Ordinal);
            Assert.Contains("if(modalIsOpen()) return;", watcher, StringComparison.Ordinal);
            Assert.Contains("ob.disconnect();", watcher, StringComparison.Ordinal);
        }

        /// <summary>
        /// Both starts in the app go through one guard, and the question sits in front of
        /// that guard rather than being copied into each of them.
        /// </summary>
        [Fact]
        public void Every_start_passes_the_question_because_every_start_passes_the_guard()
        {
            var js = AppJs();

            Assert.Contains("bepInExAskOnce(()=>launchCheckThenGo(go));", js, StringComparison.Ordinal);
            Assert.Equal(2, Regex.Matches(js, @"withLaunchGuard\(async answer=>\{").Count);
            Assert.Single(Regex.Matches(js, @"function withLaunchGuard\(go\)\{"));
        }

        /// <summary>
        /// Both answers write BOTH preferences in one save, so a window closed on the way
        /// past cannot leave the question marked answered with nothing chosen. The default
        /// is the one that says yes, and it is the one the keyboard lands on.
        /// </summary>
        [Fact]
        public void Both_answers_write_both_preferences_and_the_default_is_the_yes()
        {
            var dialog = Body("bepInExFirstStartModal");

            // The save itself moved into the one function every place the question is put
            // writes through, so the dialog, the standing row and the Upkeep switch cannot
            // record three different things.
            Assert.Contains("bepInExAnswerConsent(yes,\"at the first start\");", dialog, StringComparison.Ordinal);
            Assert.Contains("rpc(\"userprefs.save\",{prefs:{BepInExMaintained:yes,BepInExMaintenanceAsked:true}})",
                Body("bepInExAnswerConsent"), StringComparison.Ordinal);
            Assert.Contains("id=\"mBepYes\"", dialog, StringComparison.Ordinal);
            Assert.Contains("id=\"mBepNo\"", dialog, StringComparison.Ordinal);
            Assert.Contains("btn-ember btn-sm\" id=\"mBepYes\"", dialog, StringComparison.Ordinal);
            Assert.Contains("m.querySelector(\"#mBepYes\").focus();", dialog, StringComparison.Ordinal);
            // and it names a rebuilder, so the question follows a language switch
            Assert.Contains("()=>bepInExFirstStartModal(onDone));", dialog, StringComparison.Ordinal);

            Assert.Equal("Let BakaLoader look after BepInEx?", Lore("bepinex.dialog.first.title"));
            Assert.Equal("Yes, look after it", Lore("bepinex.dialog.first.yes"));
            Assert.Equal("No, I will handle it", Lore("bepinex.dialog.first.no"));
        }

        // ---------------------------------------------------------------- E. the notice

        /// <summary>
        /// "Open the setting" has to arrive at the setting. A card with eight switches in it
        /// is not an answer to where one of them is, so the row is moved to and lit.
        /// </summary>
        [Fact]
        public void The_notice_walks_to_the_switch_and_lights_it()
        {
            var js = AppJs();
            var open = Body("openUpkeepBepInEx");

            Assert.Contains("openUpkeepCard();", open, StringComparison.Ordinal);
            Assert.Contains("$(\"#rowBepMaint\")", open, StringComparison.Ordinal);
            Assert.Contains("row.scrollIntoView({block:\"nearest\"})", open, StringComparison.Ordinal);
            Assert.Contains("flashRow(row);", open, StringComparison.Ordinal);

            // the notice's own action is the one that calls it
            var notice = Body("conditionBepInExMaintained");
            Assert.Contains("openUpkeepBepInEx();", notice, StringComparison.Ordinal);

            // the flash is a real rule, and it steps aside for a host who asked for less motion
            Assert.Contains(".togglerow.rowflash{animation:rowflash", Css(), StringComparison.Ordinal);
            Assert.Contains("@keyframes rowflash{", Css(), StringComparison.Ordinal);
            Assert.Contains(".togglerow.rowflash{animation:none", Css(), StringComparison.Ordinal);

            // An opener that takes an argument must never be handed straight to a listener:
            // the click event would arrive as that argument.
            Assert.DoesNotContain("addEventListener(\"click\",openUpkeepCard)", js, StringComparison.Ordinal);
            Assert.DoesNotContain("addEventListener(\"click\",openUpkeepBepInEx)", js, StringComparison.Ordinal);
        }

        // ---------------------------------------------------------------- F. the words

        /// <summary>
        /// Every sentence this hall owns is asked for by the page, once, and the halves that
        /// live in the document are asked for there and nowhere else. An id with two owners
        /// is a sentence one of them overwrites.
        /// <para>
        /// Two shapes count as asking. The plain <c>T("id")</c> is one. The other is a
        /// chooser table naming the id in a property whose name ends in Id, which is how the
        /// row states, the button labels and the branches of the first-start question are
        /// written: the words have one owner that way, and the pure choosers can be driven as
        /// a table without a catalog. scripts/i18n/check_catalog.py counts the same two
        /// shapes, so an id that passes here is an id that is not an orphan there.
        /// </para>
        /// </summary>
        [Theory]
        [InlineData("bepinex.row.")]
        [InlineData("bepinex.dialog.")]
        [InlineData("bepinex.progress.")]
        [InlineData("mods.empty.no_bepinex.")]
        public void Every_sentence_this_hall_owns_is_asked_for_by_the_page(string prefix)
        {
            var js = AppJs();
            var html = Html();
            var mine = Catalog().Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList();

            Assert.NotEmpty(mine);
            foreach (var id in mine)
            {
                var asked = js.Contains("T(\"" + id + "\"", StringComparison.Ordinal)
                    || Regex.IsMatch(js, @"(?<![A-Za-z0-9_$])[A-Za-z0-9_$]*Id\s*:\s*""" + Regex.Escape(id) + @"""");
                Assert.True(asked, "app.js never asks for " + id);
                Assert.DoesNotContain("data-i18n=\"" + id + "\"", html);
                Assert.DoesNotContain("data-i18n-title=\"" + id + "\"", html);
            }
        }

        /// <summary>
        /// The switch's own two sentences are the document's, and its two toasts are the
        /// page's. Neither pair may drift into the other's half.
        /// </summary>
        [Fact]
        public void The_switch_words_are_the_documents_and_its_toasts_are_the_pages()
        {
            var js = AppJs();
            var html = Html();

            foreach (var id in new[] { "hearth.upkeep.bepinex", "hearth.upkeep.bepinex.note" })
            {
                Assert.Contains("data-i18n=\"" + id + "\"", html, StringComparison.Ordinal);
                Assert.DoesNotContain("T(\"" + id + "\"", js);
            }

            foreach (var id in new[] { "hearth.upkeep.bepinex.on.toast", "hearth.upkeep.bepinex.off.toast" })
            {
                Assert.Contains("T(\"" + id + "\")", js, StringComparison.Ordinal);
                Assert.DoesNotContain("data-i18n=\"" + id + "\"", html);
            }
        }

        /// <summary>
        /// The one sentence here that counts something counts it as a plural entry with the
        /// number in a named slot, so a language that inflects around it has both halves.
        /// </summary>
        [Fact]
        public void The_sentence_with_a_count_in_it_is_a_plural_entry()
        {
            var entry = Catalog()["bepinex.row.note.shared"];

            Assert.Equal("count", entry.GetProperty("plural").GetString());
            var lore = entry.GetProperty("lore");
            Assert.Equal(JsonValueKind.Object, lore.ValueKind);
            Assert.Contains("{count}", lore.GetProperty("one").GetString(), StringComparison.Ordinal);
            Assert.Contains("{count}", lore.GetProperty("other").GetString(), StringComparison.Ordinal);
            Assert.Contains("T(\"bepinex.row.note.shared\",{count:shared-1})", AppJs(), StringComparison.Ordinal);
        }

        /// <summary>
        /// Every sentence this hall prints as a toast carries the rune its call site prints,
        /// so the glyph is a property of the sentence rather than a letter glued to it.
        /// </summary>
        [Theory]
        [InlineData("bepinex.row.installed.toast", "ᛒ")]
        [InlineData("bepinex.row.installed.noversion.toast", "ᛒ")]
        [InlineData("bepinex.row.updated.toast", "ᛒ")]
        [InlineData("bepinex.row.already_current.toast", "ᛒ")]
        [InlineData("bepinex.row.adopted.toast", "ᛒ")]
        [InlineData("bepinex.row.restored.toast", "ᛒ")]
        [InlineData("bepinex.row.removed.toast", "ᛒ")]
        [InlineData("bepinex.add.pack_installed.toast", "ᛒ")]
        [InlineData("bepinex.dialog.first.yes.toast", "ᛒ")]
        [InlineData("bepinex.dialog.first.no.toast", "ᛒ")]
        [InlineData("bepinex.dialog.install.no_link.toast", "ᚦ")]
        [InlineData("hearth.upkeep.bepinex.on.toast", "ᛒ")]
        [InlineData("hearth.upkeep.bepinex.off.toast", "ᛒ")]
        public void Every_toast_this_hall_prints_carries_its_mark(string id, string mark)
        {
            var entry = Catalog()[id];

            Assert.True(entry.TryGetProperty("mark", out var carried), id + " carries no mark");
            Assert.Equal(mark, carried.GetString());
        }

        // ---------------------------------------------------------------- G. the seams

        /// <summary>
        /// A walk has to be able to reach every state and both dialogs without a native
        /// host, because none of them can be reached in a browser any other way.
        /// </summary>
        [Theory]
        [InlineData("bepinexStatus:")]
        [InlineData("bepinexProgress:")]
        [InlineData("bepinexProgressEnd:")]
        [InlineData("bepinexFirstDialog:")]
        [InlineData("bepinexOffer:")]
        [InlineData("bepinexTakeover:")]
        [InlineData("bepinexDowngrade:")]
        [InlineData("bepinexRestore:")]
        [InlineData("bepinexWrote:")]
        [InlineData("bepinexChoice:")]
        public void The_preview_can_drive_every_surface(string seam)
        {
            var preview = Between(AppJs(), "window.BakaPreview={", "\n};");
            Assert.Contains(seam, preview, StringComparison.Ordinal);
        }

        /// <summary>
        /// The fact is asked for once and pushed after that. A page that polled for it
        /// would be a page that is right most of the time.
        /// </summary>
        [Fact]
        public void The_status_is_read_once_and_pushed_afterwards()
        {
            var js = AppJs();

            Assert.Contains("await refreshBepInEx();", js, StringComparison.Ordinal);
            Assert.Contains("Native.on(\"bepinex.changed\",d=>{S.bepinex=d||null;renderMods();});", js, StringComparison.Ordinal);
            Assert.Contains("Native.on(\"bepinex.progress\",onBepInExProgress);", js, StringComparison.Ordinal);
            Assert.Contains("rpc(\"bepinex.status\")", js, StringComparison.Ordinal);
            Assert.Single(Regex.Matches(js, @"rpc\(""bepinex\.status""\)"));
        }

        /// <summary>
        /// Every write goes through one function, so the row's buttons, the add flow's
        /// question and the empty state's button cannot answer differently.
        /// </summary>
        [Fact]
        public void Every_write_goes_through_the_one_path()
        {
            var js = AppJs();
            var write = Body("bepInExWrite");

            foreach (var method in new[] { "bepinex.install", "bepinex.update", "bepinex.remove", "bepinex.restore" })
            {
                var calls = Regex.Matches(js, @"rpc\(""" + Regex.Escape(method) + @"""");
                Assert.True(calls.Count == 0,
                    method + " is called somewhere other than through bepInExWrite");
                Assert.Contains("bepInExWrite(\"" + method + "\"", js, StringComparison.Ordinal);
            }

            Assert.Contains("const r=await rpc(method,params||{});", write, StringComparison.Ordinal);
            Assert.Contains("S.bepinex=r;", write, StringComparison.Ordinal);
            Assert.Contains("if(BEP_WRITING) return;", write, StringComparison.Ordinal);
        }

        private static string Between(string source, string from, string to)
        {
            var start = source.IndexOf(from, StringComparison.Ordinal);
            if (start < 0) return "";
            var end = source.IndexOf(to, start, StringComparison.Ordinal);
            return end < 0 ? source.Substring(start) : source.Substring(start, end - start);
        }
    }
}
