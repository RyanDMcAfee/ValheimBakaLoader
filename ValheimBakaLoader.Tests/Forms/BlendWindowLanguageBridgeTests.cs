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
            var bridge = Bridge();
            var at = bridge.IndexOf("RegisterRpc(\"lang.list\"", StringComparison.Ordinal);
            Assert.True(at > 0);

            var block = bridge.Substring(at, 2000);
            Assert.Contains("checkEnabled = prefs.CheckForUpdates", block, StringComparison.Ordinal);
            Assert.Contains("busy = LanguagePacks.IsBusy", block, StringComparison.Ordinal);
            Assert.Contains("manifest = new", block, StringComparison.Ordinal);
            Assert.Contains("errorId = listing.Manifest?.ErrorId", block, StringComparison.Ordinal);
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
            var at = bridge.IndexOf("RegisterRpc(\"lang.download\"", StringComparison.Ordinal);
            Assert.True(at > 0);

            var block = bridge.Substring(at, 2200);
            Assert.Contains("if (LanguagePacks.IsBusy)", block, StringComparison.Ordinal);
            Assert.Contains("throw new HostFacingException(\"lang.busy\"", block, StringComparison.Ordinal);

            // One factory for the refusal both language methods give a code the app has never
            // heard of, written once because an id is an identity.
            Assert.Contains("?? throw UnknownLanguage();", block, StringComparison.Ordinal);
            Assert.Contains(
                "new HostFacingException(\"lang.unknownCode\", \"BakaLoader has no language by that name.\");",
                bridge,
                StringComparison.Ordinal);

            // The bar is driven by pushes made in the order the service makes them.
            Assert.Contains("new SynchronousProgress<LanguagePackProgress>", block, StringComparison.Ordinal);
            Assert.Contains("PostEvent(\"lang.downloadProgress\"", block, StringComparison.Ordinal);
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
            var bridge = Bridge();
            var at = bridge.IndexOf("RegisterRpc(\"lang.set\"", StringComparison.Ordinal);
            Assert.True(at > 0);

            var block = bridge.Substring(at, 2200);
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
            var bridge = Bridge();
            var at = bridge.IndexOf("RegisterRpc(\"lang.status\"", StringComparison.Ordinal);
            Assert.True(at > 0);

            var block = bridge.Substring(at, 1600);
            Assert.Contains("LanguagePacks.InstalledAny(code)", block, StringComparison.Ordinal);
            Assert.Contains("installedVersion = install?.Version", block, StringComparison.Ordinal);
            Assert.Contains("stringsUrl = LanguageStringsUrl(code, install?.Version)", block, StringComparison.Ordinal);
            Assert.Contains("quietFetch = QuietLanguageFetch(prefs, code)", block, StringComparison.Ordinal);
        }

        /// <summary>
        /// The quiet fetch is started once per window, off the thread the page is waiting on.
        /// An await here would make the first frame wait for a download on a household
        /// connection, and the whole point of the quiet fetch is that nobody waits for it.
        /// </summary>
        [Fact]
        public void The_quiet_fetch_runs_on_a_worker_and_only_once_per_window()
        {
            var bridge = Bridge();
            var at = bridge.IndexOf("private string QuietLanguageFetch(", StringComparison.Ordinal);
            Assert.True(at > 0);

            var block = bridge.Substring(at, 1400);
            Assert.Contains("if (LanguageQuietFetchAnswer != null) return LanguageQuietFetchAnswer;", block, StringComparison.Ordinal);
            Assert.Contains("LanguagePacks.QuietFetchDecision(", block, StringComparison.Ordinal);
            Assert.Contains("_ = Task.Run(async () =>", block, StringComparison.Ordinal);
            Assert.Contains("EnsureCurrentQuietlyAsync(", block, StringComparison.Ordinal);
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
    }
}
