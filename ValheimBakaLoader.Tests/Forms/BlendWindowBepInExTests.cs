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
    /// The BepInEx seam between the bridge and the page: the RPCs, the events, the reason
    /// codes, the launch slot, the unattended window and the catalog the refusals are worded
    /// from.
    /// <para>
    /// These are gates on the source, because what they guard against is a line somebody
    /// removes later. A reason code the page branches on that the bridge stops sending is not
    /// a compile error and not a failing unit test; it is a dialog that silently stops being
    /// offered.
    /// </para>
    /// </summary>
    public class BlendWindowBepInExTests
    {
        private static string Bridge() => AppSourceTree.Files()["BlendWindow.Bridge.cs"];

        private static string Server() => AppSourceTree.Files()["ValheimServer.cs"];

        private static string Service() => AppSourceTree.Files()["BepInExService.cs"];

        /// <summary>
        /// The other file host facing refusals are thrown from. A refusal belongs
        /// beside the rule it enforces, and the rule about what a world may be
        /// copied to lives in the world store rather than in the bridge that calls
        /// it, so the pairing table has to be able to find it there.
        /// </summary>
        private static string WorldStore() => AppSourceTree.Files()["WorldStore.cs"];

        private static string AppJs() => AppSourceTree.Web("app.js");

        private static Dictionary<string, JsonElement> Catalog()
        {
            using var document = JsonDocument.Parse(AppSourceTree.Web("i18n/en.json"));
            var map = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var entry in document.RootElement.GetProperty("keys").EnumerateObject())
                map[entry.Name] = entry.Value.Clone();
            return map;
        }

        // ------------------------------------------------------------------ the RPCs

        [Theory]
        [InlineData("bepinex.status")]
        [InlineData("bepinex.install")]
        [InlineData("bepinex.update")]
        [InlineData("bepinex.remove")]
        [InlineData("bepinex.restore")]
        [InlineData("bepinex.noticeSeen")]
        public void The_bridge_answers_every_bepinex_call_the_page_can_make(string method)
            => Assert.Contains("RegisterRpc(\"" + method + "\"", Bridge(), StringComparison.Ordinal);

        /// <summary>
        /// Every host-side reader of the switch goes through the consent rule, and none of them
        /// reads the preference on its own.
        /// <para>
        /// The preference defaults to ON. A host upgrading from 1.1.x is therefore "maintained"
        /// from the first second of the first launch, before anybody has been asked anything,
        /// and a profile that auto-starts never reaches the question at all. Each of these
        /// three is a path that would otherwise write a loader into somebody's server on the
        /// strength of a default, which is the one thing the question exists to stop.
        /// </para>
        /// </summary>
        [Fact]
        public void Every_path_that_writes_reads_the_answer_and_not_just_the_switch()
        {
            var bridge = Bridge().Replace("\r\n", "\n");

            // the restart window
            Assert.Contains(
                "Tools.BepInExConsent.Effective(prefs.BepInExMaintained, prefs.BepInExMaintenanceAsked)",
                bridge, StringComparison.Ordinal);

            // the loader-before-start step
            Assert.Contains(
                "if (!Tools.BepInExConsent.Effective(\n"
                + "                    startPrefs.BepInExMaintained, startPrefs.BepInExMaintenanceAsked)) return;",
                bridge, StringComparison.Ordinal);

            // and nothing left reading the switch on its own to decide whether to write
            Assert.DoesNotContain("if (!UserPrefsProvider.LoadPreferences().BepInExMaintained) return;",
                bridge, StringComparison.Ordinal);
        }

        /// <summary>
        /// Moving the Upkeep switch by hand IS an answer to the first-start question, so it is
        /// recorded as one. Without this a host who found the setting and turned it on before
        /// ever pressing Start would still count as unanswered, and the standing question would
        /// go on being asked after they had already given it.
        /// </summary>
        [Fact]
        public void Changing_the_upkeep_switch_by_hand_counts_as_answering()
        {
            var bridge = Bridge().Replace("\r\n", "\n");

            Assert.Contains(
                "Apply(\"BepInExMaintained\", v =>\n"
                + "                    {\n"
                + "                        prefs.BepInExMaintained = v.Value<bool>();\n"
                + "                        prefs.BepInExMaintenanceAsked = true;\n"
                + "                    });",
                bridge, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData("bepinex.progress")]
        [InlineData("bepinex.changed")]
        public void The_bridge_pushes_every_bepinex_event_the_page_listens_for(string name)
            => Assert.Contains("PostEvent(\"" + name + "\",", Bridge().Replace("\r\n", "\n"),
                StringComparison.Ordinal);

        /// <summary>
        /// Progress is reported inline on the reporting thread, so the bar never sees a "done"
        /// before its "installing". Progress&lt;T&gt; posts asynchronously and can reorder.
        /// </summary>
        [Fact]
        public void Bepinex_progress_is_reported_in_order()
            => Assert.Contains("new SynchronousProgress<Tools.BepInExProgress>", Bridge(), StringComparison.Ordinal);

        /// <summary>One write at a time, because the files are shared by every install on the base.</summary>
        [Fact]
        public void A_second_write_while_one_is_running_is_refused_by_name()
        {
            var bridge = Bridge();

            Assert.Contains("private readonly SemaphoreSlim _bepInExWriteSlot = new(1, 1);", bridge,
                StringComparison.Ordinal);
            Assert.Contains("HostFacingException(\"bepinex.busy\"", bridge, StringComparison.Ordinal);
            Assert.Contains("_bepInExWriteSlot.Release();", bridge, StringComparison.Ordinal);
        }

        /// <summary>
        /// ALL FOUR of them, not just the button. BepInEx is written from the row's own
        /// Install and Update, from the unattended restart window, from the loader put in
        /// place before a start, and from the removal of a pack unpacked into the wrong
        /// folder; three of those run with nobody watching. A guard that only the button path
        /// took left exactly the window it claimed to close, over a BepInEx/core that is
        /// CLEARED before it is copied.
        /// <para>
        /// This is a gate on the source because what it guards against is a fifth writer
        /// somebody adds later, or a <c>finally</c> somebody drops: every take is paired with
        /// a release, and the count of one is the count of the other.
        /// </para>
        /// </summary>
        [Fact]
        public void Every_bepinex_write_path_goes_through_the_one_slot()
        {
            var bridge = Bridge();

            // the host-driven writers and the removal cannot wait and are refused by name
            Assert.Contains("if (!TryBeginBepInExWrite()) throw BepInExBusy();", bridge, StringComparison.Ordinal);
            // the start waits, because it cannot be told to try again in a moment
            Assert.Contains("if (!BeginBepInExWrite(BepInExWriteWait)) throw BepInExBusy();",
                bridge, StringComparison.Ordinal);
            // and that wait is dropped on the window's own thread, where it could never end
            Assert.Contains("_bepInExWriteSlot.Wait(OnUiThread ? TimeSpan.Zero : wait);", bridge,
                StringComparison.Ordinal);
            // and the unattended window does not queue behind a person: it defers and says so
            Assert.Equal(2, Regex.Matches(bridge, @"_bepInExUpdateWaiting = latest;").Count);

            var takes = Regex.Matches(bridge, @"!(?:Try)?BeginBepInExWrite\(").Count;
            var releases = Regex.Matches(bridge, @"EndBepInExWrite\(\);").Count;

            Assert.True(takes >= 4, "only " + takes + " BepInEx write path(s) take the slot");
            Assert.True(takes == releases,
                "the slot is taken " + takes + " time(s) and handed back " + releases);
        }

        /// <summary>
        /// The row's own busy flag is the SLOT, so it is true for all four writers. A flag
        /// only the button path set drew the row's buttons enabled while an unattended window
        /// was writing the very files they would write.
        /// </summary>
        [Fact]
        public void The_row_reads_busy_off_the_slot_every_writer_takes()
            => Assert.Contains("busy = BepInExWriteInProgress,", Bridge(), StringComparison.Ordinal);

        /// <summary>
        /// The service asks whether it may write TWICE, and the second ask happens with the
        /// archive already on disk and nothing written yet. A fifty megabyte fetch is minutes
        /// on a slow line, and a realm started inside that window would otherwise have its
        /// BepInEx/core cleared and rewritten underneath it. The bridge has to hand in a
        /// sequence that answers freshly, which a list it built once cannot do.
        /// </summary>
        [Fact]
        public void The_writer_asks_again_after_the_download_and_the_bridge_can_answer_again()
        {
            var service = Service();

            var download = service.IndexOf("await DownloadAsync(", StringComparison.Ordinal);
            var write = service.IndexOf("var write = Written(() => WriteLoader(", StringComparison.Ordinal);

            Assert.True(download > 0 && write > download, "the install path is not shaped as this reads it");
            Assert.Contains("RefuseWhileServersAreUp(baseExePath, profiles);",
                service.Substring(download, write - download), StringComparison.Ordinal);

            var bridge = Bridge();
            Assert.Contains("private IEnumerable<Tools.BepInExProfileInstall> LiveInstalls()", bridge,
                StringComparison.Ordinal);
            Assert.Contains("foreach (var install in KnownInstalls()) yield return install;", bridge,
                StringComparison.Ordinal);

            // and every write hands in that live sequence rather than a snapshot
            Assert.DoesNotContain("KnownInstalls();\n\n                var result = update", bridge,
                StringComparison.Ordinal);
            Assert.Contains("var installs = LiveInstalls();", bridge, StringComparison.Ordinal);
        }

        /// <summary>
        /// A host who hands over a source while BakaLoader is looking after BepInEx is told
        /// where the setting is, rather than having their link quietly obeyed or quietly
        /// ignored.
        /// </summary>
        [Fact]
        public void A_link_handed_over_while_it_is_looked_after_is_turned_down_with_the_notice()
        {
            var bridge = Bridge();

            Assert.Contains("HostFacingException(\"bepinex.alreadyMaintained\"", bridge, StringComparison.Ordinal);

            // And it is the consent rule that decides, not the preference on its own: a host
            // upgrading has the switch on by default and has not been asked anything yet, so
            // the preference alone would tell them BakaLoader was already looking after a
            // loader they had never agreed to.
            Assert.Contains("Tools.BepInExConsent.Effective(\n                        writePrefs.BepInExMaintained, writePrefs.BepInExMaintenanceAsked)",
                bridge.Replace("\r\n", "\n"), StringComparison.Ordinal);
        }

        // ------------------------------------------------------------------ the reason codes

        /// <summary>
        /// The refusal the page branches on. Before this it carried only English prose, so
        /// "BepInEx is missing" and "Thunderstore is down" were the same answer as far as any
        /// page could tell.
        /// </summary>
        [Fact]
        public void The_add_from_a_link_refusal_carries_a_reason_beside_the_sentence()
        {
            var bridge = Bridge();

            Assert.Contains("object FailDto(string error, string reason = null) => new\n                {\n                    Installed = false,",
                bridge.Replace("\r\n", "\n"), StringComparison.Ordinal);
        }

        [Theory]
        [InlineData("noBepInEx")]
        [InlineData("noPlugins")]
        [InlineData("noServerPath")]
        [InlineData("alreadyMaintained")]
        [InlineData("badUrl")]
        [InlineData("busy")]
        public void Every_reason_the_page_can_branch_on_is_sent(string reason)
            => Assert.Contains("\"" + reason + "\"", Bridge(), StringComparison.Ordinal);

        /// <summary>
        /// BepInEx present and the plugins folder missing is the ordinary state of a fresh
        /// install: the pack does not ship one. It is created and the add carries on rather
        /// than failing at the host.
        /// </summary>
        [Fact]
        public void A_missing_plugins_folder_is_created_rather_than_refused()
        {
            var bridge = Bridge().Replace("\r\n", "\n");
            var at = bridge.IndexOf("if (!Directory.Exists(pluginsDir))\n                {\n                    try { Directory.CreateDirectory(pluginsDir); }",
                StringComparison.Ordinal);

            Assert.True(at > 0, "the add flow no longer creates a missing plugins folder");
        }

        /// <summary>
        /// The capability answer used to read an absent plugins folder as "nothing is
        /// missing", which is the exact opposite of the truth: a server with no BepInEx at all
        /// was reported as fully capable and the console typing box was left enabled on it.
        /// caps.install always checked this; caps.get never did.
        /// </summary>
        [Fact]
        public void The_capability_answer_checks_the_folder_is_there_and_not_merely_named()
        {
            var bridge = Bridge();
            var at = bridge.IndexOf("RegisterRpc(\"caps.get\"", StringComparison.Ordinal);

            Assert.True(at > 0, "caps.get is gone");
            var body = bridge.Substring(at, Math.Min(1600, bridge.Length - at));

            Assert.Contains("string.IsNullOrWhiteSpace(pluginsDir) || !Directory.Exists(pluginsDir)",
                body, StringComparison.Ordinal);
            Assert.Contains("missing = Tools.RequiredModChecker.RequiredMods.ToList();",
                body, StringComparison.Ordinal);
        }

        // ------------------------------------------------------------------ the launch slot

        /// <summary>
        /// The loader goes in before the plugins that load under it. Installing the companions
        /// into a folder no loader will ever read is work that looks like it worked.
        /// </summary>
        [Fact]
        public void The_loader_is_prepared_before_the_plugins_that_load_under_it()
        {
            var server = Server();

            Assert.Contains("public Action<string> PrepareBepInEx { get; set; }", server, StringComparison.Ordinal);
            Assert.Contains("Install(\"BepInEx\", () => PrepareBepInEx?.Invoke(exePath));", server, StringComparison.Ordinal);

            var loader = server.IndexOf("Install(\"BepInEx\"", StringComparison.Ordinal);
            var indexer = server.IndexOf("Install(\"item indexer\"", StringComparison.Ordinal);
            Assert.True(loader > 0 && indexer > loader,
                "BepInEx is no longer prepared before the companion plugins");
        }

        /// <summary>
        /// A loader that could not be fetched is reported like any other companion-plugin
        /// failure, so the condition bar and the server log both say the feature is off and
        /// why, instead of leaving a modded profile quietly running vanilla.
        /// </summary>
        [Fact]
        public void A_loader_that_could_not_be_fetched_is_recorded_against_the_profile()
        {
            var server = Server().Replace("\r\n", "\n");
            var at = server.IndexOf("void Install(string label, Action install)", StringComparison.Ordinal);

            Assert.True(at > 0, "the companion-plugin failure wrapper is gone");
            // Built from two pieces on purpose. Naming the record in one literal makes this
            // source gate look to another gate like a class that REACHES the record, and that
            // other gate is a real one worth not blunting.
            const string filed = "Tools.CompanionPluginStatus" + ".ReportFailure(label, ex.Message);";
            Assert.Contains(filed, server.Substring(at, Math.Min(1200, server.Length - at)),
                StringComparison.Ordinal);
        }

        [Fact]
        public void The_bridge_fills_the_launch_slot_with_the_profile_it_belongs_to()
            => Assert.Contains("session.Server.PrepareBepInEx = exePath => PrepareBepInExForStart(profile, exePath);",
                Bridge(), StringComparison.Ordinal);

        // ------------------------------------------------------------------ the unattended window

        /// <summary>
        /// BepInEx moves first, and on a hook of its own: the loader is what the mods load
        /// under, so moving it after them would leave one restart where new plugins meet an
        /// old core.
        /// <para>
        /// The loader step used to be the first line INSIDE the mod-update hook, and that hook
        /// is only reached when the restart was raised because mod updates were pending. A
        /// host with mod auto update off therefore restarted forever with the loader never
        /// once looked at. That is why the two are separate hooks now, and why this gate is on
        /// the separation rather than on the order inside one body.
        /// ValheimServerLoaderWindowTests drives the window itself and proves it fires.
        /// </para>
        /// </summary>
        [Fact]
        public void The_restart_window_moves_the_loader_on_a_hook_of_its_own_before_the_mods()
        {
            var bridge = Bridge().Replace("\r\n", "\n");

            Assert.Contains("session.Server.ApplyLoaderUpdate = () => ApplyBepInExUpdateAsync(profile);",
                bridge, StringComparison.Ordinal);

            // and it is no longer buried in the hook that only fires for pending mod updates
            var at = bridge.IndexOf("session.Server.ApplyModUpdates = async () =>", StringComparison.Ordinal);
            Assert.True(at > 0, "the unattended mod-update hook is gone");
            Assert.DoesNotContain("await ApplyBepInExUpdateAsync(profile);",
                bridge.Substring(at), StringComparison.Ordinal);

            // In the window itself the loader goes first, and it is not under the flag that
            // counts pending mod updates.
            var resume = Server().Replace("\r\n", "\n");
            var loader = resume.IndexOf("await ApplyLoaderUpdate();", StringComparison.Ordinal);
            var mods = resume.IndexOf("await ApplyModUpdates();", StringComparison.Ordinal);
            var gate = resume.IndexOf("if (ApplyUpdatesOnRestart && ApplyModUpdates != null)",
                StringComparison.Ordinal);

            Assert.True(loader > 0, "the restart window no longer runs the loader step");
            Assert.True(mods > loader, "BepInEx is no longer moved before the mods");
            Assert.True(gate > loader, "the loader step has been put back under the mod-update flag");
        }

        // --------------------------------------------------------------- telling the host

        /// <summary>
        /// The three writes a host can press now answer with what the write CAME TO, not only
        /// with the state afterwards. Without this the page cannot tell an adoption from a
        /// refusal from a write that moved a version: all three leave an install that is
        /// there, and the status DTO says the same thing about all three.
        /// </summary>
        [Fact]
        public void A_write_answers_with_what_it_came_to()
        {
            var bridge = Bridge().Replace("\r\n", "\n");

            Assert.Contains("return BuildBepInExDto(result);", bridge, StringComparison.Ordinal);
            Assert.Contains("return BuildBepInExDto(restored);", bridge, StringComparison.Ordinal);

            foreach (var field in new[]
                     {
                         "skipped = result.Skipped,", "skipReason = result.SkipReason,",
                         "nothingChanged = result.NothingChanged,",
                         "alreadyCurrent = result.AlreadyCurrent,", "adopted = result.Adopted,",
                         "restored = result.Restored,", "healed = result.Healed,",
                         "doorstopReplaced = result.DoorstopReplaced,", "version = result.Version,",
                         "previousVersion = result.PreviousVersion,",
                         "previousCoreVersion = result.PreviousCoreVersion,",
                         "coreVersion = result.CoreVersion,", "backupStamp = result.BackupStamp,",
                         "backupPath = BepInExBackupPath(",
                         "eligibleUtc = result.EligibleUtc,",
                         "profileLoaderFilesRefreshed = result.ProfileLoaderFilesRefreshed,",
                     })
                Assert.Contains(field, bridge, StringComparison.Ordinal);

            // and the write nobody asked for posts the same object on the event, because it
            // has no reply of its own to carry it
            Assert.Contains("PostBepInExChanged(windowResult);", bridge, StringComparison.Ordinal);
            Assert.Contains("PostBepInExChanged(startResult);", bridge, StringComparison.Ordinal);
        }

        /// <summary>
        /// A write inside a restart window happens with nobody at the keyboard, so there is no
        /// toast for it and very often no window to put one in. What it did, and what stopped
        /// it, STAND until a page has shown them: one AppLogger.Warning in a file nobody opens
        /// is not telling anybody, which is the whole of what section H is about.
        /// </summary>
        [Fact]
        public void An_unattended_write_leaves_something_standing_for_the_page()
        {
            var bridge = Bridge().Replace("\r\n", "\n");

            // recorded on all three unattended paths: the window, the loader-before-start step
            // and the repair of a write that did not finish
            Assert.Contains("RecordUnattendedBepInEx(profile, result);", bridge, StringComparison.Ordinal);
            Assert.Contains("RecordUnattendedBepInEx(null, result);", bridge, StringComparison.Ordinal);
            Assert.Contains("RecordUnattendedBepInExFailure(profile, e);", bridge, StringComparison.Ordinal);

            // and it travels on every answer, the load-time one and the event included
            Assert.Contains("lastUnattended = BepInExUnattendedDto(),", bridge, StringComparison.Ordinal);

            // the reason the page words it from, and the statement that goes with every reason
            // on that path, which is that the install is exactly as it was
            Assert.Contains("reason = last.Reason,", bridge, StringComparison.Ordinal);
            // A heal is not one of those either. It MOVED files, it just moved them back, so
            // "the install was left exactly as it was" is not a sentence it may carry.
            Assert.Contains(
                "leftAsItWas = last.Outcome != \"written\" && last.Outcome != \"healed\",",
                bridge, StringComparison.Ordinal);

            // and a Thunderstore that did not answer is NOT one of them: nothing was written
            // and nothing is wrong with the install, so there is nothing for a host to do
            Assert.Contains(
                "if (string.Equals(id, \"bepinex.offline\", StringComparison.Ordinal)) return;",
                bridge, StringComparison.Ordinal);
        }

        /// <summary>
        /// SECTION L. A write that stopped part way is UNDONE, not finished: the copy from
        /// before it goes back and the loader the host ends up with is the one they already
        /// had. It used to fall through to the written wording, so the one thing section L
        /// exists to tell a host read "BepInEx 5.4.23.5 was put in at a restart. Anything it
        /// replaced is kept in the BepInEx backups folder beside the install." Nothing was put
        /// in, nothing was updated, and when the core had already gone that folder is not even
        /// there.
        /// </summary>
        [Fact]
        public void A_write_that_was_undone_is_not_reported_as_a_write()
        {
            var bridge = Bridge().Replace("\r\n", "\n");
            var app = AppJs();

            // the branch, ahead of the written one
            Assert.Contains("if (result.Healed || result.Restored)", bridge, StringComparison.Ordinal);
            Assert.Contains("Outcome = \"healed\",", bridge, StringComparison.Ordinal);
            Assert.True(
                bridge.IndexOf("Outcome = \"healed\",", StringComparison.Ordinal)
                < bridge.IndexOf("Outcome = \"written\",", StringComparison.Ordinal),
                "the heal has to be asked before the write, or a heal is reported as a write");

            // and the page words it as its own thing
            Assert.Contains(
                "if(last&&last.outcome===\"healed\"){conditionBepInExHealed(last);return;}",
                app, StringComparison.Ordinal);
            Assert.Contains("T(\"bepinex.notice.healed.title\")", app, StringComparison.Ordinal);
            Assert.Contains("T(\"bepinex.notice.healed.body\")", app, StringComparison.Ordinal);

            foreach (var id in new[] { "bepinex.notice.healed.title", "bepinex.notice.healed.body" })
                Assert.True(Catalog().ContainsKey(id), id + " is not in the catalog");
        }

        /// <summary>
        /// SECTION B and O. A start never writes over a loader somebody else put here. The
        /// three odd shapes were already refused; the one that mattered was the ordinary one,
        /// because an install with no note whose winhttp.dll an antivirus took answers Installed
        /// false and walked straight past them into a fresh pack written over the host's core.
        /// What the question promised was the next SCHEDULED RESTART, which is the window.
        /// </summary>
        [Fact]
        public void A_start_never_writes_over_an_install_BakaLoader_did_not_make()
        {
            var bridge = Bridge().Replace("\r\n", "\n");

            Assert.Contains(
                "if (startStatus.Unrecognised || startStatus.DrivenElsewhere || startStatus.ForeignCore\n"
                + "                || (startStatus.CoreFilePresent && !startStatus.MaintainedByBakaLoader))",
                bridge, StringComparison.Ordinal);

            // and the fact the clamp reads is on the wire, because the row asks the same one
            Assert.Contains("coreFilePresent = status.CoreFilePresent,", bridge, StringComparison.Ordinal);
            Assert.Contains("loaderFileName = Tools.BepInExService.LoaderFileName,",
                bridge, StringComparison.Ordinal);
        }

        /// <summary>
        /// The page draws both of them, and both are in the order the bar reads: a condition
        /// raised under a name the order does not know is stored and never drawn, which reads
        /// to a host as nothing having happened.
        /// </summary>
        [Fact]
        public void The_page_draws_the_standing_notice_and_the_standing_condition()
        {
            var app = AppJs();

            Assert.Contains("conditionBepInExLeftAlone(bep.lastUnattended);", app, StringComparison.Ordinal);
            Assert.Contains("conditionBepInExWritten(bep.lastUnattended);", app, StringComparison.Ordinal);

            var order = app.Substring(app.IndexOf("const CONDITION_ORDER=", StringComparison.Ordinal), 400);
            Assert.Contains("\"bepinexLeftAlone\"", order, StringComparison.Ordinal);
            Assert.Contains("\"bepinexWritten\"", order, StringComparison.Ordinal);

            // and closing the notice is what tells the host side to stop sending it
            Assert.Contains("Native.call(\"bepinex.noticeSeen\"", app, StringComparison.Ordinal);
        }

        /// <summary>
        /// The decision is the rule's, not an inline branch's: the window runs while nobody is
        /// watching, and the wrong answer either writes a shared loader out from under a world
        /// that is up or leaves an install a version behind with no sign of it.
        /// </summary>
        [Fact]
        public void The_unattended_decision_goes_through_the_rule()
        {
            var bridge = Bridge();

            // The rule carries the order as well as the table now, so the window calls the one
            // entry point that reads the answer before it reads anything else. Decide itself is
            // still the table and is still walked, cell by cell, in BepInExRulesTests.
            Assert.Contains("Tools.BepInExUnattended.PlanAsync(", bridge, StringComparison.Ordinal);
            Assert.Contains("Tools.BepInExUnattendedAction.Defer", bridge, StringComparison.Ordinal);
            Assert.Contains("_bepInExUpdateWaiting = latest;", bridge, StringComparison.Ordinal);
        }

        /// <summary>The unattended window asks about every OTHER server, never about itself.</summary>
        [Fact]
        public void The_unattended_window_leaves_the_restarting_profile_out_of_the_question()
        {
            var bridge = Bridge().Replace("\r\n", "\n");
            var at = bridge.IndexOf("private async Task ApplyBepInExUpdateAsync(string profile)", StringComparison.Ordinal);

            Assert.True(at > 0, "the unattended BepInEx step is gone");
            Assert.Contains("!string.Equals(i.ProfileName, profile, StringComparison.OrdinalIgnoreCase)",
                bridge.Substring(at, Math.Min(1200, bridge.Length - at)), StringComparison.Ordinal);
        }

        /// <summary>A BepInEx write is written into the journal beside the mod installs.</summary>
        [Fact]
        public void A_loader_that_moved_is_written_into_the_journal()
        {
            var bridge = Bridge().Replace("\r\n", "\n");
            var at = bridge.IndexOf("private void RecordBepInExInstall(", StringComparison.Ordinal);

            Assert.True(at > 0, "the journal entry for a BepInEx write is gone");
            var body = bridge.Substring(at, Math.Min(900, bridge.Length - at));

            Assert.Contains("Kind = result.Replaced ? \"modup\" : \"modin\",", body, StringComparison.Ordinal);
            Assert.Contains("FromVersion = result.PreviousVersion,", body, StringComparison.Ordinal);
        }

        // ------------------------------------------------------------------ the status

        /// <summary>
        /// The two standing conditions ride on server.status beside the plugin failures,
        /// because that is where the page already learns facts about the install.
        /// </summary>
        [Fact]
        public void The_status_carries_the_two_standing_bepinex_conditions()
        {
            var bridge = Bridge();

            Assert.Contains("bepinex = BuildBepInExState(session),", bridge, StringComparison.Ordinal);
            Assert.Contains("notLoaded = BepInExDidNotLoad(session),", bridge, StringComparison.Ordinal);
            Assert.Contains("updateWaiting = waiting,", bridge, StringComparison.Ordinal);
        }

        /// <summary>
        /// The load check needs a start to compare the log against, and the clock is taken at
        /// the transition closest to the process being created.
        /// </summary>
        [Fact]
        public void The_start_is_timed_so_the_load_check_has_something_to_compare_against()
        {
            var bridge = Bridge();

            Assert.Contains("if (status == ServerStatus.Starting) ServerLaunchUtc[profile] = DateTime.UtcNow;",
                bridge, StringComparison.Ordinal);
            Assert.Contains("else if (status == ServerStatus.Stopped) ServerLaunchUtc.TryRemove(profile, out _);",
                bridge, StringComparison.Ordinal);
        }

        /// <summary>
        /// Nothing else asks again once the grace has passed: the status event only fires on a
        /// transition, and "the log never appeared" is not one.
        /// </summary>
        [Fact]
        public void The_load_check_asks_once_more_after_the_grace()
        {
            var bridge = Bridge();

            Assert.Contains("ScheduleBepInExLoadCheck(session);", bridge, StringComparison.Ordinal);
            Assert.Contains("Tools.BepInExLoadCheck.DefaultGraceSeconds + 5", bridge, StringComparison.Ordinal);
        }

        // ------------------------------------------------------------------ the words

        /// <summary>
        /// Every refusal the service or the bridge names has a sentence in the catalog for the
        /// page to read it out of, and a row pairing the two. A throw with no pair is a
        /// refusal that can only ever be shown in English.
        /// </summary>
        [Theory]
        [InlineData("bepinex.serversRunning", "bepinex.reason.servers_running")]
        [InlineData("bepinex.notALoader", "bepinex.reason.not_a_loader")]
        [InlineData("bepinex.integrity", "bepinex.reason.integrity")]
        [InlineData("bepinex.tooLarge", "bepinex.reason.too_large")]
        [InlineData("bepinex.offline", "bepinex.reason.offline")]
        [InlineData("bepinex.busy", "bepinex.reason.busy")]
        [InlineData("bepinex.noServerPath", "bepinex.reason.no_server_path")]
        [InlineData("bepinex.noWrongFolder", "bepinex.reason.no_wrong_folder")]
        [InlineData("bepinex.locked", "bepinex.reason.locked")]
        [InlineData("bepinex.newer", "bepinex.reason.newer")]
        [InlineData("bepinex.coreIsJunction", "bepinex.reason.core_is_junction")]
        [InlineData("bepinex.unrecognisedCore", "bepinex.reason.unrecognised_core")]
        [InlineData("bepinex.noBackup", "bepinex.reason.no_backup")]
        [InlineData("bepinex.writeFailed", "bepinex.reason.write_failed")]
        public void Every_bepinex_refusal_is_paired_with_a_sentence_the_page_owns(string throwId, string textId)
        {
            var thrown = Service() + Bridge();
            Assert.Contains("HostFacingException(\"" + throwId + "\"", thrown, StringComparison.Ordinal);

            Assert.True(Catalog().ContainsKey(textId), "the catalog has no " + textId);

            Assert.Contains("named:\"" + throwId + "\"", AppJs(), StringComparison.Ordinal);
            Assert.Contains("textId:\"" + textId + "\"", AppJs(), StringComparison.Ordinal);
        }

        /// <summary>
        /// Every id in the page's own pairing table names a throw that really exists. A row
        /// left behind after a refusal was renamed would silently stop wording anything.
        /// <para>
        /// HOST_SENTENCES is the table of THROWS, so the scan is that table rather than every
        /// named row in the file. The language pack's endings are named by the service in a
        /// result instead, in LANG_REASONS below it, and they are held to the same standard
        /// from the other end: BlendWindowLanguageBridgeTests derives the list from the
        /// service's own constants and asks the page for words for each one.
        /// </para>
        /// </summary>
        [Fact]
        public void The_pages_pairing_table_names_no_refusal_that_is_gone()
        {
            var thrown = Service() + Bridge() + WorldStore();
            var table = HostSentencesTable();
            var named = Regex.Matches(table, "named:\"([^\"]+)\"")
                .Cast<Match>()
                .Select(m => m.Groups[1].Value)
                .ToList();

            Assert.NotEmpty(named);

            var missing = named
                .Where(id => !thrown.Contains("HostFacingException(\"" + id + "\"", StringComparison.Ordinal))
                .ToList();

            Assert.True(missing.Count == 0,
                "the page pairs a sentence with refusals nothing throws any more: " + string.Join(", ", missing));
        }

        /// <summary>The HOST_SENTENCES table itself, from its opening bracket to its close.</summary>
        private static string HostSentencesTable() => NamedTable("const HOST_SENTENCES=[");

        /// <summary>The language pack endings table, from its opening bracket to its close.</summary>
        private static string LangReasonsTable() => NamedTable("const LANG_REASONS=[");

        /// <summary>The unattended BepInEx reasons table, from its opening bracket to its close.</summary>
        private static string BepInExReasonsTable() => NamedTable("const BEPINEX_REASONS=[");

        /// <summary>
        /// Every reason the page words is one the service can really name, and every one of
        /// them has a sentence in the catalog.
        /// <para>
        /// This is the gate that had to arrive with the third table. BEPINEX_REASONS pairs the
        /// endings an unattended write carries in its RESULT rather than in a throw, so
        /// nothing in the refusal gate above can see it: a row naming a reason that was
        /// renamed would word nothing, quietly, on the one path where a silent failure is the
        /// whole problem being solved.
        /// </para>
        /// <para>
        /// Held from the page's end rather than from the service's. Every reason the service
        /// can name has a row now, drift included; the rule the table keeps is the other
        /// direction, that nothing in it names a reason or a sentence that is gone.
        /// </para>
        /// </summary>
        [Fact]
        public void Every_reason_the_page_words_is_one_the_service_can_name()
        {
            var service = Service();
            var catalog = Catalog();
            var rows = Regex.Matches(BepInExReasonsTable(), "named:\\s*\"([^\"]+)\"\\s*,\\s*textId:\\s*\"([^\"]+)\"")
                .Cast<Match>()
                .Select(m => (Reason: m.Groups[1].Value, TextId: m.Groups[2].Value))
                .ToList();

            Assert.NotEmpty(rows);

            foreach (var (reason, textId) in rows)
            {
                Assert.Contains("= \"" + reason + "\";", service, StringComparison.Ordinal);
                Assert.True(catalog.ContainsKey(textId), "the catalog has no " + textId);
            }
        }

        /// <summary>One table in the page, from its opening bracket to the line that closes it.</summary>
        private static string NamedTable(string opens)
        {
            var app = AppJs();
            var at = app.IndexOf(opens, StringComparison.Ordinal);
            Assert.True(at > 0, "the page has no " + opens.Replace("const ", "").Replace("=[", "") + " table");

            var end = app.IndexOf("];", at, StringComparison.Ordinal);
            Assert.True(end > at, opens + " does not close");

            return app.Substring(at, end - at);
        }

        /// <summary>
        /// The net under the three tables above. Each of them is checked from one end or the
        /// other: HOST_SENTENCES against the throws in the source, LANG_REASONS against the
        /// service's own constants, and BEPINEX_REASONS against the skip reasons the BepInEx
        /// service can name. Between them they cover every paired row in the page TODAY. What
        /// none of them covers is a FOURTH table added tomorrow: rows in it would be wording
        /// something nothing checks, and all three existing gates would stay green while it
        /// rotted. So the page is held to these three, and a fourth one has to arrive with the
        /// gate that stands under it.
        /// </summary>
        [Fact]
        public void The_page_pairs_its_sentences_in_the_tables_that_are_guarded()
        {
            var app = AppJs();
            var everywhere = Regex.Matches(app, "named:\\s*\"([^\"]+)\"")
                .Cast<Match>()
                .Select(m => m.Groups[1].Value)
                .ToList();

            Assert.NotEmpty(everywhere);

            var guarded = HostSentencesTable() + LangReasonsTable() + BepInExReasonsTable();
            var loose = everywhere
                .Where(id => !Regex.IsMatch(guarded, "named:\\s*\"" + Regex.Escape(id) + "\""))
                .Distinct(StringComparer.Ordinal)
                .ToList();

            Assert.True(loose.Count == 0,
                "these rows pair a sentence outside HOST_SENTENCES, LANG_REASONS and BEPINEX_REASONS, "
                    + "where nothing checks them: " + string.Join(", ", loose));
        }

        /// <summary>
        /// The one id that is deliberately NOT in the page's refusal table. It is not a
        /// refusal the page reads out: it is a notice, answered with a toast and a standing
        /// row that carries the offer to open the setting.
        /// </summary>
        private static readonly string[] AnsweredWithoutARefusalSentence = { "bepinex.alreadyMaintained" };

        /// <summary>
        /// Derived from the source rather than from a list kept beside it. The table above
        /// says every id it names is paired; this says there is no id it does NOT name. A new
        /// HostFacingException("bepinex.…") added later with no row in the page's table and no
        /// sentence in the catalog used to pass every gate here, because the only other check
        /// on it was its shape.
        /// </summary>
        [Fact]
        public void Every_bepinex_refusal_the_source_throws_is_paired_with_a_sentence()
        {
            var thrown = Service() + Bridge();
            var catalog = Catalog();
            var js = AppJs();

            var ids = Regex.Matches(thrown, "HostFacingException\\(\"(bepinex\\.[^\"]+)\"")
                .Cast<Match>()
                .Select(m => m.Groups[1].Value)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            Assert.True(ids.Count >= 8, "only " + ids.Count + " bepinex refusal(s) found in the source");

            // the exempt one is exempt because the page really does answer it another way
            foreach (var id in AnsweredWithoutARefusalSentence)
            {
                Assert.Contains(id, ids);
                Assert.Contains("errorId===\"" + id + "\"", js, StringComparison.Ordinal);
            }

            var wanted = ids.Except(AnsweredWithoutARefusalSentence, StringComparer.Ordinal).ToList();

            var unpaired = wanted
                .Where(id => !js.Contains("named:\"" + id + "\"", StringComparison.Ordinal))
                .ToList();
            Assert.True(unpaired.Count == 0,
                "these refusals have no row in the page's pairing table: " + string.Join(", ", unpaired));

            // and the sentence each row names really is in the catalog
            foreach (var id in wanted)
            {
                var textId = Regex.Match(js, "named:\"" + Regex.Escape(id) + "\",\\s*textId:\"([^\"]+)\"")
                    .Groups[1].Value;

                Assert.False(string.IsNullOrEmpty(textId), "the row for " + id + " names no sentence");
                Assert.True(catalog.ContainsKey(textId), "the catalog has no " + textId);
            }
        }

        /// <summary>
        /// The same refusals reached through the add rather than through the direct call. The
        /// add answers with a REASON and the sentence's named values, so a BepInEx refusal that
        /// came back that way is worded out of the catalog exactly as it is on the direct road
        /// instead of being toasted as the raw English the host side wrote.
        /// </summary>
        [Fact]
        public void A_refusal_that_came_back_through_the_add_is_worded_from_the_catalog_too()
        {
            Assert.Contains("ErrorParams = errorParams,", Bridge(), StringComparison.Ordinal);
            Assert.Contains("HostFacingException.ParamsOf(bepError)", Bridge(), StringComparison.Ordinal);
            Assert.Contains("const own=hostSentence(r.Reason,r.ErrorParams);", AppJs(), StringComparison.Ordinal);
        }

        /// <summary>
        /// A link that NAMES a version is honoured on both roads. The offer dialog's edited
        /// address already was; the pasted pack link threw the version away and installed the
        /// latest pack while saying nothing about it. The address is still BUILT from the
        /// package identity, so a pinned package cannot be talked into fetching elsewhere.
        /// </summary>
        [Fact]
        public void A_versioned_pack_link_is_installed_at_the_version_it_names()
        {
            var bridge = Bridge();

            Assert.Contains("var pinned = string.IsNullOrWhiteSpace(reference.Version)", bridge,
                StringComparison.Ordinal);
            Assert.Contains("Tools.BepInExService.ConstructedDownloadUrl(", bridge, StringComparison.Ordinal);
            Assert.Contains("await WriteBepInExAsync(pinned, update: bepStatus.Installed);", bridge,
                StringComparison.Ordinal);
            Assert.DoesNotContain("await WriteBepInExAsync(null, update: bepStatus.Installed);", bridge,
                StringComparison.Ordinal);
        }

        /// <summary>The dotted shape every host-facing id has, applied to the new ones too.</summary>
        [Fact]
        public void Every_bepinex_refusal_id_is_a_dotted_name()
        {
            var ids = Regex.Matches(Service(), "HostFacingException\\(\"([^\"]+)\"")
                .Cast<Match>()
                .Select(m => m.Groups[1].Value)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            Assert.NotEmpty(ids);
            foreach (var id in ids)
            {
                Assert.True(Regex.IsMatch(id, "^[a-z][A-Za-z0-9]*(\\.[a-z][A-Za-z0-9]*){1,3}$"),
                    "'" + id + "' is not a dotted name");
            }
        }

        [Theory]
        [InlineData("bepinex.condition.waiting.title")]
        [InlineData("bepinex.condition.waiting.body")]
        [InlineData("bepinex.condition.not_loaded.title")]
        [InlineData("bepinex.condition.not_loaded.body")]
        [InlineData("bepinex.condition.not_loaded.action")]
        [InlineData("bepinex.notice.already_maintained")]
        [InlineData("bepinex.notice.already_maintained.title")]
        [InlineData("bepinex.notice.already_maintained.action")]
        public void Every_bepinex_row_has_its_words(string id)
        {
            Assert.True(Catalog().ContainsKey(id), "the catalog has no " + id);
            Assert.Contains("T(\"" + id + "\"", AppJs(), StringComparison.Ordinal);
        }

        /// <summary>
        /// The refusal that names the realms takes them as a slot, so a language that puts the
        /// list first can. A sentence with the names concatenated into it in English order
        /// cannot be translated at all.
        /// </summary>
        [Fact]
        public void The_running_server_refusal_names_the_realms_as_a_slot()
        {
            Assert.Contains("(\"names\", string.Join(\", \", blocked))", Service(), StringComparison.Ordinal);
            Assert.Contains("{names}", Catalog()["bepinex.reason.servers_running"].GetProperty("lore").GetString(),
                StringComparison.Ordinal);
        }

        // ------------------------------------------------------------------ the page rows

        [Fact]
        public void The_page_raises_both_bepinex_rows_from_the_status_it_already_reads()
        {
            var js = AppJs();

            Assert.Contains("if(\"bepinex\" in st) conditionBepInEx(st.bepinex,st.status);", js, StringComparison.Ordinal);
            Assert.Contains("function conditionBepInExWaiting(version,names)", js, StringComparison.Ordinal);
            Assert.Contains("function conditionBepInExNotLoaded(on)", js, StringComparison.Ordinal);
        }

        /// <summary>A row that is not in the order is a row that is never drawn.</summary>
        [Theory]
        [InlineData("bepinexWaiting")]
        [InlineData("bepinexNotLoaded")]
        [InlineData("bepinexNotice")]
        [InlineData("bepinexAsk")]
        [InlineData("bepinexNoMods")]
        [InlineData("bepinexMissingFiles")]
        public void Every_bepinex_row_has_a_place_in_the_order(string kind)
        {
            var js = AppJs().Replace("\r\n", "\n");
            var at = js.IndexOf("const CONDITION_ORDER=", StringComparison.Ordinal);

            Assert.True(at > 0, "the condition order is gone");
            Assert.Contains("\"" + kind + "\"", js.Substring(at, 400), StringComparison.Ordinal);
        }

        /// <summary>
        /// The notice offers the setting rather than a dead end, and the wiki page is reached
        /// through a named target rather than an address the page carries.
        /// </summary>
        [Fact]
        public void The_notice_opens_the_setting_and_the_row_opens_the_wiki()
        {
            var js = AppJs();

            Assert.Contains("openUpkeepCard();", js, StringComparison.Ordinal);
            Assert.Contains("Native.call(\"shell.openUrl\",{target:\"bepinex-wiki\"})", js, StringComparison.Ordinal);
            Assert.Contains("\"bepinex-wiki\" => BepInExWikiUrl,", Bridge(), StringComparison.Ordinal);
        }
    }
}
