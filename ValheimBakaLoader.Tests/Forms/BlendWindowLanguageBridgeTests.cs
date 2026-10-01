using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using ValheimBakaLoader.Forms;
using ValheimBakaLoader.Tests.Tools;
using Xunit;

namespace ValheimBakaLoader.Tests.Forms
{
    /// <summary>
    /// The language block of the bridge, read as source.
    /// <para>
    /// An RPC handler lives inside a WinForms window that owns a WebView2, so nothing here
    /// can call one: what is gated is the shape of the block instead, which is the shape the
    /// page's contract rests on. SPEC section 9 item 12 asks for exactly this and names the
    /// two that matter most, the busy refusal and the honest cancel; the rest are the same
    /// question asked of the other three methods.
    /// </para>
    /// </summary>
    public class BlendWindowLanguageBridgeTests
    {
        private static string Bridge() => AppSourceTree.Files()["BlendWindow.Bridge.cs"];

        private static string Splash() => AppSourceTree.Files()["SplashForm.cs"];

        private static string AppJs() => AppSourceTree.Web("app.js");

        private static Dictionary<string, JsonElement> Catalog()
        {
            using var document = JsonDocument.Parse(AppSourceTree.Web("i18n/en.json"));
            var map = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var entry in document.RootElement.GetProperty("keys").EnumerateObject())
                map[entry.Name] = entry.Value.Clone();
            return map;
        }

        /// <summary>
        /// ONE RPC HANDLER, from its registration to the next one's. Every rule below used to take
        /// a window measured in characters, and every time the block grew a paragraph of comment
        /// one of them quietly stopped reaching the line it was aimed at: three of them had to be
        /// widened by hand in 1.2.7 alone, and a rule that silently stops looking at the thing it
        /// names is worse than no rule. The next registration is the boundary because that is what
        /// the file actually has between two handlers.
        /// </summary>
        private static string Handler(string method)
        {
            var bridge = Bridge();
            var at = bridge.IndexOf("RegisterRpc(\"" + method + "\"", StringComparison.Ordinal);
            Assert.True(at > 0, "the bridge no longer registers " + method);

            var next = bridge.IndexOf("RegisterRpc(\"", at + 13, StringComparison.Ordinal);
            return next > at ? bridge.Substring(at, next - at) : bridge.Substring(at);
        }

        /// <summary>
        /// ONE METHOD of the bridge, from its signature to the brace that closes it at the class's
        /// own indent. Same reason as <see cref="Handler"/>: a window counted in characters stops
        /// reaching what it was aimed at the moment the method grows a paragraph.
        /// </summary>
        private static string Member(string signature)
        {
            var bridge = Bridge();
            var at = bridge.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(at > 0, "the bridge no longer has " + signature);

            var end = bridge.IndexOf("\n        }", at, StringComparison.Ordinal);
            Assert.True(end > at, signature + " does not close");
            return bridge.Substring(at, end - at);
        }

        // ------------------------------------------------------------------ the five methods

        [Theory]
        [InlineData("lang.list")]
        [InlineData("lang.status")]
        [InlineData("lang.download")]
        [InlineData("lang.cancel")]
        [InlineData("lang.set")]
        public void The_page_has_a_method_for_each_thing_it_can_ask(string method)
        {
            Assert.Contains("RegisterRpc(\"" + method + "\"", Bridge(), StringComparison.Ordinal);
        }

        /// <summary>
        /// The globe menu's answer carries the switch's state rather than acting on it. The
        /// service reaches the release page from ListAsync on purpose, because opening the
        /// menu is the host asking out loud; what the menu still has to be able to say is that
        /// update checking is off, so packs will not refresh on their own.
        /// </summary>
        [Fact]
        public void The_menus_answer_says_whether_update_checking_is_on()
        {
            var block = Handler("lang.list");
            Assert.Contains("checkEnabled = prefs.CheckForUpdates", block, StringComparison.Ordinal);
            Assert.Contains("busy = LanguagePacks.IsBusy", block, StringComparison.Ordinal);
            Assert.Contains("manifest = new", block, StringComparison.Ordinal);
            Assert.Contains("errorId = listing.Manifest?.ErrorId", block, StringComparison.Ordinal);
        }

        /// <summary>
        /// WHICH pack is being written, on both answers the page reads a row from. A flag alone
        /// cannot tell a row that it is the one coming down, so a window that opened the globe in
        /// the middle of the app's own refresh drew an Update button over a pack that was being
        /// replaced as it drew, and the press came back as a busy refusal.
        /// </summary>
        [Theory]
        [InlineData("lang.list")]
        [InlineData("lang.status")]
        public void Both_answers_say_which_pack_is_being_written(string method)
        {
            var block = Handler(method);
            Assert.Contains("busyCode = LanguagePacks.BusyCode", block, StringComparison.Ordinal);

            // And the service answers it from the run it is holding rather than from a field
            // somebody has to remember to clear.
            Assert.Contains("get { lock (Gate) return Current?.Code; }",
                AppSourceTree.Files()["LanguagePackService.cs"], StringComparison.Ordinal);
        }

        // ------------------------------------------------------------------ item 12: the two that matter

        /// <summary>
        /// One pack at a time, app wide, and the second asker is told so by name rather than
        /// by a sentence. The service refuses a second run too; this is the bridge saying it
        /// before a run is even asked for, so the page can leave the row alone.
        /// </summary>
        [Fact]
        public void A_second_download_is_refused_by_name()
        {
            var bridge = Bridge();
            var block = Handler("lang.download");
            Assert.Contains("if (LanguagePacks.IsBusy)", block, StringComparison.Ordinal);
            Assert.Contains("throw LanguageIsBusy(LanguagePacks.BusyCode);", block, StringComparison.Ordinal);

            // One factory for the refusal both language methods give a code the app has never
            // heard of, written once because an id is an identity.
            Assert.Contains("?? throw UnknownLanguage();", block, StringComparison.Ordinal);
            Assert.Contains(
                "new HostFacingException(\"lang.unknownCode\", \"BakaLoader has no language by that name.\");",
                bridge,
                StringComparison.Ordinal);

            // The bar is driven by pushes made in the order the service makes them, out of the one
            // producer both roads into a pack write use.
            Assert.Contains("var progress = LanguageProgressToPage();", block, StringComparison.Ordinal);
        }

        /// <summary>
        /// A refusal that NAMES the pack being written. "A language pack is already downloading" is
        /// no help when the write is the app's own refresh after an update: the host pressed a row,
        /// read that, and had seen nothing anywhere saying a download was running. The nameless
        /// sentence is kept for the boot sweep, which holds the same latch for no one language.
        /// </summary>
        [Fact]
        public void The_busy_refusal_names_the_pack_that_is_being_written()
        {
            var bridge = Bridge();
            var block = Member("private static HostFacingException LanguageIsBusy(");
            Assert.Contains("LanguageCodes.Find(runningCode)?.NativeName", block, StringComparison.Ordinal);
            Assert.Contains("new HostFacingException(\"lang.busy\", \"A language pack is already downloading.\")",
                block, StringComparison.Ordinal);
            Assert.Contains("\"lang.busyLanguage\"", block, StringComparison.Ordinal);
            Assert.Contains("(\"language\", name)", block, StringComparison.Ordinal);

            // And the page has words for it, in its own table, with the slot the refusal fills.
            Assert.Contains("{named:\"lang.busyLanguage\", textId:\"lang.reason.busy_language\"}",
                AppJs(), StringComparison.Ordinal);

            var catalog = Catalog();
            Assert.True(catalog.ContainsKey("lang.reason.busy_language"),
                "the catalog has no lang.reason.busy_language");
            Assert.Contains("{language}",
                catalog["lang.reason.busy_language"].GetProperty("lore").GetString() ?? "",
                StringComparison.Ordinal);
        }

        /// <summary>
        /// One producer for the reports both roads into a pack write make, so a pack the app
        /// fetched on its own paints the row exactly the way a pack the host pressed does. Two
        /// copies of this are two chances for the quiet one to be written without a handler, which
        /// is what it had.
        /// </summary>
        [Fact]
        public void The_progress_reports_have_one_producer()
        {
            var bridge = Bridge();
            var block = Member("private IProgress<LanguagePackProgress> LanguageProgressToPage()");
            Assert.Contains("new SynchronousProgress<LanguagePackProgress>", block, StringComparison.Ordinal);
            Assert.Contains("PostEvent(\"lang.downloadProgress\"", block, StringComparison.Ordinal);

            // Nothing else in the bridge posts that event, so neither road can drift from the other.
            Assert.Equal(1, Regex.Matches(bridge, Regex.Escape("PostEvent(\"lang.downloadProgress\"")).Count);
        }

        /// <summary>
        /// The cancel answers what the service answered and nothing else. A literal true here
        /// would be the one lie a cancel button must never tell: the row would say stopped
        /// while the pack went into place behind it.
        /// </summary>
        [Fact]
        public void The_cancel_answers_what_the_service_answered()
        {
            var bridge = Bridge();
            Assert.Contains(
                "RegisterRpc(\"lang.cancel\", p =>\n                Task.FromResult<object>(new { cancelled = LanguagePacks.Cancel(p.Value<string>(\"code\")) }));",
                bridge,
                StringComparison.Ordinal);

            // And nothing in the handler itself decides the answer for the service. The
            // picker's own reply carries a literal true and is a different question, so the
            // scan is the language block rather than the whole file.
            var at = bridge.IndexOf("RegisterRpc(\"lang.cancel\"", StringComparison.Ordinal);
            var block = bridge.Substring(at, bridge.IndexOf("RegisterRpc(\"lang.set\"", StringComparison.Ordinal) - at);
            Assert.DoesNotContain("cancelled = true", block, StringComparison.Ordinal);
        }

        // ------------------------------------------------------------------ item 19: the switch

        /// <summary>
        /// A language the machine does not have is refused, because the page would then fetch
        /// a catalog that is not there and paint ids over every label. English is the one that
        /// never has to be installed: it is inside the app.
        /// </summary>
        [Fact]
        public void The_switch_refuses_a_language_that_is_not_downloaded()
        {
            var block = Handler("lang.set");
            Assert.Contains("LanguageCodes.IsEnglish(code) ? null : LanguagePacks.InstalledAny(code)", block, StringComparison.Ordinal);
            Assert.Contains(
                "if (!LanguageCodes.IsEnglish(code) && install == null)\n                    throw new HostFacingException(\"lang.notInstalled\"",
                block,
                StringComparison.Ordinal);

            // The one legal writer, and the event every other window follows.
            Assert.Contains("UserPrefsProvider.Mutate(prefs => prefs.Language = code)", block, StringComparison.Ordinal);
            Assert.Contains("LanguagePacks.NotifyLanguageChanged(code, version)", block, StringComparison.Ordinal);

            // And the answer the page switches on.
            Assert.Contains("stringsUrl = LanguageStringsUrl(code, install?.Version)", block, StringComparison.Ordinal);
            Assert.Contains("fonts = LanguageFonts(install)", block, StringComparison.Ordinal);
            Assert.Contains("missingKeys = LanguageMissingKeys(install)", block, StringComparison.Ordinal);
        }

        /// <summary>
        /// Every id the bridge refuses a language with has words on the page. Without the
        /// pairing the host reads "lang.notInstalled" in a toast, which is worse than the
        /// English sentence the throw already carries.
        /// </summary>
        [Theory]
        [InlineData("lang.busy", "lang.reason.busy")]
        [InlineData("lang.unknownCode", "lang.reason.unknown_code")]
        [InlineData("lang.notInstalled", "lang.reason.not_installed")]
        public void Every_language_refusal_has_words_on_the_page(string named, string textId)
        {
            Assert.Contains("HostFacingException(\"" + named + "\"", Bridge(), StringComparison.Ordinal);
            Assert.Matches(
                new Regex("\\{named:\"" + Regex.Escape(named) + "\",\\s*textId:\"" + Regex.Escape(textId) + "\"\\}"),
                AppJs());
            Assert.True(Catalog().ContainsKey(textId), "the catalog has no " + textId);
        }

        /// <summary>
        /// The service names an ending for every way a download can stop, and the page has
        /// words for each one. An id the page cannot word is an id the page would have to
        /// print, and an id printed at a host has told them nothing.
        /// </summary>
        [Fact]
        public void Every_ending_the_service_can_name_has_words_on_the_page()
        {
            var app = AppJs();
            var catalog = Catalog();

            var reasons = typeof(ValheimBakaLoader.Tools.LanguagePackReasons)
                .GetFields()
                .Where(f => f.IsLiteral && f.FieldType == typeof(string))
                .Select(f => (string)f.GetRawConstantValue())
                .ToList();

            Assert.True(reasons.Count >= 13, "the service names " + reasons.Count + " endings");

            foreach (var reason in reasons)
            {
                var row = Regex.Match(app, "\\{named:\"" + Regex.Escape(reason) + "\",\\s*textId:\"([a-z0-9_.]+)\"\\}");
                Assert.True(row.Success, "nothing on the page words " + reason);
                Assert.True(catalog.ContainsKey(row.Groups[1].Value),
                    "the catalog has no " + row.Groups[1].Value + " for " + reason);
            }
        }

        // ------------------------------------------------------------------ item 13: the first frame

        /// <summary>
        /// A pack cut for the version before this one is still the host's language, so the
        /// status answer reads the newest pack on disk rather than only the one that matches
        /// this build. Without that, a host who updates BakaLoader gets an English window
        /// until a download finishes, which is the flash the whole boot path exists to stop.
        /// </summary>
        [Fact]
        public void The_status_answer_reads_the_newest_pack_on_disk()
        {
            var block = Handler("lang.status");
            Assert.Contains("LanguagePacks.InstalledAny(code)", block, StringComparison.Ordinal);
            Assert.Contains("installedVersion = install?.Version", block, StringComparison.Ordinal);
            Assert.Contains("stringsUrl = LanguageStringsUrl(code, install?.Version)", block, StringComparison.Ordinal);
            Assert.Contains("quietFetch = QuietLanguageFetch(prefs, code)", block, StringComparison.Ordinal);
        }

        /// <summary>
        /// The quiet fetch is started once per LANGUAGE per window, off the thread the page is
        /// waiting on. An await here would make the first frame wait for a download on a household
        /// connection, and the whole point of the quiet fetch is that nobody waits for it.
        /// <para>
        /// It was once per window, from one field, filled with the language saved at the first
        /// frame. On a machine whose window opens in English that answer is "nothing to do", and it
        /// was then the answer for every language the host switched to for the rest of the session:
        /// the owner's Japanese pack was two releases behind and no number of trips to the globe
        /// could refresh it.
        /// </para>
        /// </summary>
        [Fact]
        public void The_quiet_fetch_runs_on_a_worker_and_once_per_language_per_window()
        {
            var block = Member("private string QuietLanguageFetch(");
            Assert.Contains("if (LanguageQuietFetchAnswers.TryGetValue(key, out var answered)) return answered;",
                block, StringComparison.Ordinal);
            Assert.Contains("LanguageQuietFetchAnswers[key] = decision;", block, StringComparison.Ordinal);
            Assert.Contains("LanguagePacks.QuietFetchDecision(", block, StringComparison.Ordinal);
            Assert.Contains("_ = Task.Run(async () =>", block, StringComparison.Ordinal);
            Assert.Contains("EnsureCurrentQuietlyAsync(", block, StringComparison.Ordinal);

            // AND THE WINDOW IS TOLD. It passed no progress handler at all, so while the refresh
            // ran the row offered an Update for the pack that was being replaced as it drew and the
            // press came back as a busy refusal. The handler is the same one a press is given.
            Assert.Contains("var progress = LanguageProgressToPage();", block, StringComparison.Ordinal);
            Assert.Contains("prefs?.CheckForUpdates ?? true, code, version, progress);",
                block, StringComparison.Ordinal);
            // And the service hands it straight on rather than keeping the old null.
            var service = AppSourceTree.Files()["LanguagePackService.cs"];
            Assert.Contains("await DownloadAsync(code, progress, ct, userInitiated: false);",
                service, StringComparison.Ordinal);
            Assert.DoesNotContain("DownloadAsync(code, progress: null", service, StringComparison.Ordinal);

            // The one field that made the answer the window's rather than the language's is gone,
            // not merely unused: left standing it is the thing somebody reads next.
            Assert.DoesNotContain("LanguageQuietFetchAnswer ", Bridge(), StringComparison.Ordinal);
            Assert.DoesNotContain("LanguageQuietFetchAnswer;", Bridge(), StringComparison.Ordinal);
        }

        /// <summary>
        /// And it is asked again after every manifest fetch, for the language the window is reading
        /// NOW. lang.list is the one call that reaches the release page, so it is the one moment the
        /// app knows a newer pack exists, and the host switching language in-session goes through it.
        /// </summary>
        [Fact]
        public void The_menus_own_call_asks_about_the_language_on_screen()
        {
            var block = Handler("lang.list");
            var listed = block.IndexOf("await LanguagePacks.ListAsync()", StringComparison.Ordinal);
            var asked = block.IndexOf("QuietLanguageFetch(prefs, CurrentLanguage(prefs));", StringComparison.Ordinal);

            Assert.True(listed > 0, "the menu's call no longer reads the listing");
            Assert.True(asked > 0,
                "lang.list does not ask about the language on screen, so a host who switches language"
                + " in-session never refreshes that language's pack");
            Assert.True(listed < asked,
                "the quiet fetch is asked for BEFORE the manifest has been fetched, so it decides on a"
                + " manifest this window has not read yet");
        }

        /// <summary>
        /// The two facts the row needs to offer a newer pack, carried on every row. Without them the
        /// page can only ask whether a pack is installed at all, which is the question that produced
        /// "newer sentences in English until the 1.2.6 pack is out" over a manifest naming it.
        /// </summary>
        [Fact]
        public void Every_row_says_whether_a_newer_pack_is_published()
        {
            var block = Handler("lang.list");

            Assert.Contains("packVersion = l.PackVersion", block, StringComparison.Ordinal);
            Assert.Contains("updateAvailable = l.UpdateAvailable", block, StringComparison.Ordinal);

            // And the page reads both of them rather than working it out for itself: the version the
            // pack would install as is the manifest's answer, not this app's version.
            var app = AppJs();
            Assert.Contains("l.updateAvailable", app, StringComparison.Ordinal);
            Assert.Contains("(l&&l.packVersion)||langAppVersion()", app, StringComparison.Ordinal);
        }

        // ------------------------------------------------------------------ the events

        /// <summary>
        /// One window's globe menu changes the language for the app, so every open window has
        /// to follow. The subscription sits beside the release banner's, in the method that
        /// runs once per window, which is what makes "every window" true rather than "the
        /// window that did it".
        /// </summary>
        [Fact]
        public void Every_open_window_follows_the_language()
        {
            var bridge = Bridge();
            var at = bridge.IndexOf("private void RegisterServiceEvents()", StringComparison.Ordinal);
            Assert.True(at > 0);

            var block = bridge.Substring(at, bridge.IndexOf("private void PostAppUpdateAvailable", StringComparison.Ordinal) - at);
            Assert.Contains("LanguagePacks.LanguageChanged +=", block, StringComparison.Ordinal);
            Assert.Contains("PostEvent(\"lang.changed\", new { code = changed?.Code, version = changed?.Version });", block, StringComparison.Ordinal);
        }

        // ------------------------------------------------------------------ the preferences

        /// <summary>
        /// Both language preferences travel to the page and back. A preference the DTO does
        /// not carry is a preference the Settings hall cannot show, and one the save handler
        /// does not apply is one it cannot change.
        /// </summary>
        [Fact]
        public void Both_language_preferences_are_carried_and_applied()
        {
            var bridge = Bridge();

            Assert.Contains("prefs.Language,", bridge, StringComparison.Ordinal);
            Assert.Contains("prefs.PlayerMessageLanguage,", bridge, StringComparison.Ordinal);
            Assert.Contains("Apply(\"Language\", v =>", bridge, StringComparison.Ordinal);
            Assert.Contains("Apply(\"PlayerMessageLanguage\", v =>", bridge, StringComparison.Ordinal);

            // A spelling nothing answers to leaves the preference where it was, rather than
            // saving a language the app cannot read.
            Assert.Contains("LanguageCodes.Normalize(v.Value<string>())) ?? prefs.Language", bridge, StringComparison.Ordinal);

            // And the interface language carries lang.set's SECOND guard as well, because the
            // two roads have to refuse the same things. A Language saved with no pack on disk
            // leaves lang.status naming a language with nowhere to read it from: the window
            // comes up English while the globe draws that row as the current one.
            Assert.Contains("ReadableLanguage(LanguageCodes.Normalize(v.Value<string>()))", bridge, StringComparison.Ordinal);
            Assert.Contains("LanguagePacks.InstalledAny(normalized) != null ? normalized : null", bridge, StringComparison.Ordinal);

            // The player-message choice is NOT held to that: English and "same" are always
            // answerable, and a pack named there with nothing on disk falls back rather than
            // leaving the hall unable to save.
            Assert.Contains("LanguageCodes.Normalize(asked) ?? prefs.PlayerMessageLanguage", bridge, StringComparison.Ordinal);
        }

        /// <summary>
        /// The sentences the players read are written from whichever catalog the two
        /// preferences pick, so a save that touched either one picks it again.
        /// </summary>
        [Fact]
        public void A_saved_language_repoints_the_player_message_catalog()
        {
            var bridge = Bridge();

            Assert.Contains("HostCatalog.Use(HostCatalog.Load(GetLanguagesDir(), code, install?.Version))", bridge, StringComparison.Ordinal);
            Assert.Contains("HostCatalog.EffectiveCode(prefs?.Language, prefs?.PlayerMessageLanguage)", bridge, StringComparison.Ordinal);

            // Three roads reach it: the window opening, the switch, and a save of either
            // preference from the Settings hall.
            Assert.True(
                Regex.Matches(bridge, "RefreshHostCatalog\\(\\);").Count >= 4,
                "the host catalog is repointed from too few places");
        }

        // ------------------------------------------------------------------ the boot sweep

        /// <summary>
        /// The sweep runs after the windows are up and on a worker of its own. A launch step
        /// is a thing the splash bar waits on, and deleting old language folders is not worth
        /// one second of a host's launch.
        /// </summary>
        [Fact]
        public void The_boot_sweep_happens_after_the_windows_open_and_is_not_a_launch_step()
        {
            var splash = Splash();

            var opens = splash.IndexOf("private void OpenMainWindows()", StringComparison.Ordinal);
            var sweep = splash.IndexOf("LanguagePacks.PruneOnBoot()", StringComparison.Ordinal);
            var shown = splash.IndexOf("window.Show();", StringComparison.Ordinal);

            Assert.True(opens > 0 && sweep > opens, "the sweep is not inside OpenMainWindows");
            Assert.True(sweep > shown, "the sweep runs before the windows are shown");
            Assert.Contains("_ = Task.Run(() =>", splash.Substring(opens, sweep - opens), StringComparison.Ordinal);

            // Never a launch step: those are the things the splash bar counts.
            var steps = Regex.Matches(splash, "new LaunchStep\\(\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToList();
            Assert.DoesNotContain(steps, name => name.Contains("anguage", StringComparison.Ordinal));
        }

        /// <summary>
        /// And it is asked for once. Two sweeps would be harmless and would also mean nobody
        /// knew where the one was.
        /// </summary>
        [Fact]
        public void The_boot_sweep_is_asked_for_once()
        {
            Assert.Equal(1, Regex.Matches(Splash(), "PruneOnBoot\\(\\)").Count);
            Assert.DoesNotContain("PruneOnBoot()", Bridge(), StringComparison.Ordinal);
        }

        // ------------------------------------------------------------------ A2: the first frame

        /// <summary>
        /// The decision itself, driven rather than read. A window is worth holding the paint
        /// back for only when all three hold: the saved language is one this app knows, it is
        /// not English, and a pack for it is on disk.
        /// </summary>
        [Theory]
        // English, in every spelling that reaches the preference. Nothing to say: the markup
        // in index.html is already English and the first frame is already right.
        [InlineData("en", true, null)]
        [InlineData("EN", true, null)]
        [InlineData(null, true, null)]
        [InlineData("", true, null)]
        // A spelling the app does not answer to is not a language to hold a window for.
        [InlineData("klingon", true, null)]
        [InlineData("xx", true, null)]
        // A language with no pack on disk would be hidden and then painted in English, which
        // is the flash this exists to remove, twice as long.
        [InlineData("ru", false, null)]
        [InlineData("ja", false, null)]
        // And the case it is for.
        [InlineData("ru", true, "ru")]
        [InlineData("ja", true, "ja")]
        [InlineData("zh-hans", true, "zh-Hans")]
        public void The_page_is_told_the_language_only_when_holding_the_paint_back_is_worth_it(
            string saved, bool packInstalled, string expected)
        {
            Assert.Equal(expected, BlendWindow.LanguageToAnnounce(saved, _ => packInstalled));
        }

        /// <summary>
        /// An English window never asks the pack service anything at all. The short circuit
        /// is what keeps this off the launch path for the hosts who are already reading the
        /// interface in the language it ships in.
        /// </summary>
        [Fact]
        public void An_english_window_never_asks_about_a_pack()
        {
            var asked = new List<string>();

            Assert.Null(BlendWindow.LanguageToAnnounce("en", code => { asked.Add(code); return true; }));
            Assert.Empty(asked);

            Assert.Equal("ru", BlendWindow.LanguageToAnnounce("ru", code => { asked.Add(code); return true; }));
            Assert.Equal(new[] { "ru" }, asked);
        }

        /// <summary>
        /// And with nothing to ask, nothing is decided. A window with no pack service is a
        /// window with no packs, so there is no language to hold the paint back for.
        /// </summary>
        [Fact]
        public void With_no_way_to_ask_about_a_pack_nothing_is_announced()
        {
            Assert.Null(BlendWindow.LanguageToAnnounce("ru", null));
        }

        /// <summary>
        /// The host half of A2: the language is handed over the same way the version is, on
        /// the document-created hook, so it is set before a line of the page's own script
        /// runs and before the navigation that loads it.
        /// </summary>
        [Fact]
        public void The_host_hands_the_page_its_language_before_the_page_runs()
        {
            var window = AppSourceTree.Files()["BlendWindow.cs"];

            Assert.Contains("window.BAKA_LANG=", window, StringComparison.Ordinal);
            Assert.Contains("AnnounceLanguageToPageAsync(core)", window, StringComparison.Ordinal);

            var announced = window.IndexOf("await AnnounceLanguageToPageAsync(core);", StringComparison.Ordinal);
            var navigate = window.IndexOf("core.Navigate(", StringComparison.Ordinal);

            Assert.True(announced > 0, "the language is never handed over");
            Assert.True(navigate > announced, "the page is navigated before it is told its language");

            // Through the decision, never by reading the preference at the call.
            Assert.Contains("LanguageToAnnounce(", window, StringComparison.Ordinal);
            Assert.Contains("LanguagePacks?.InstalledAny(language) != null", window, StringComparison.Ordinal);
        }
    }
}
