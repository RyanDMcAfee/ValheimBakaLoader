using System;
using System.Text.Json;
using System.Text.RegularExpressions;
using ValheimBakaLoader.Tests.Tools;
using Xunit;

namespace ValheimBakaLoader.Tests.Forms
{
    /// <summary>
    /// Three defects that were always bugs and become worse the moment a second language
    /// exists. None of them needed a language to be wrong; all three are closed here.
    /// <para>
    /// 1. A toast found its ember glyph by splitting on the first space. Chinese does not
    ///    put spaces between words, so the whole sentence went into the glyph slot, as raw
    ///    innerHTML, leaving an empty body: a layout bug and an injection surface in the
    ///    same three lines. And any message whose first word is a word put that word in
    ///    ember, which already happened in English.
    /// 2. The status bar clock appended a hardcoded " JST", right on exactly one machine.
    /// 3. The server update bar preferred the host's sentence over its own phase table, and
    ///    the host sets a sentence on every phase it reports, so the table was dead code.
    ///    The launch hold toast had the same shape: it printed the host's prose while the
    ///    banner beside it built the same sentence from the facts.
    /// </para>
    /// </summary>
    public class WebUiToastAndClockTests
    {
        private static string AppJs() => AppSourceTree.Web("app.js");

        private static string Html() => AppSourceTree.Web("index.html");

        private static string Between(string source, string from, string to)
        {
            var start = source.IndexOf(from, StringComparison.Ordinal);
            Assert.True(start >= 0, "could not find " + from);
            var end = source.IndexOf(to, start, StringComparison.Ordinal);
            return end < 0 ? source.Substring(start) : source.Substring(start, end - start);
        }

        /// <summary>The English the catalog holds for an id, or null when it holds none.
        /// The update bar's sentences live there now, so a gate that only read app.js would
        /// pass on an id nothing answers.</summary>
        private static string Lore(string id)
        {
            using var document = JsonDocument.Parse(AppSourceTree.Web("i18n/en.json"));
            if (!document.RootElement.GetProperty("keys").TryGetProperty(id, out var entry)) return null;
            return entry.TryGetProperty("lore", out var lore) && lore.ValueKind == JsonValueKind.String
                ? lore.GetString()
                : null;
        }

        // ------------------------------------------------------------------ 1. the toast mark

        /// <summary>
        /// The mark is a single glyph, recognised by what it is. The Runic block is the set
        /// the halls draw from; the two symbol marks already in use are named beside it so
        /// those call sites do not quietly lose their glyph.
        /// </summary>
        [Fact]
        public void The_mark_is_one_glyph_recognised_by_its_codepoint()
        {
            var js = AppJs();

            Assert.Contains("const TOAST_MARK_RE=/^([\\u16A0-\\u16FF\\u2302\\u21BA])\\s/;", js);
            Assert.Contains("function toast(msg,opts){", js);
            Assert.Contains("let mark=(opts&&opts.mark)?String(opts.mark):\"\";", js);
        }

        /// <summary>
        /// The split on the first space is gone, and with it the case where a sentence with
        /// no spaces became the mark and the body was empty.
        /// </summary>
        [Fact]
        public void A_toast_no_longer_guesses_the_mark_from_the_first_space()
        {
            var body = Between(AppJs(), "function toast(msg,opts){", "/* ---------- SAGA TERMINAL");

            Assert.DoesNotContain("msg.split(\" \")", body);
            Assert.DoesNotContain("parts[0]", body);
            Assert.DoesNotContain("parts.slice(1)", body);
        }

        /// <summary>
        /// Both halves escaped, unconditionally. The mark went into innerHTML raw before,
        /// which mattered the moment the mark could be any part of a translated sentence.
        /// </summary>
        [Fact]
        public void The_mark_and_the_body_are_both_escaped()
        {
            var body = Between(AppJs(), "function toast(msg,opts){", "/* ---------- SAGA TERMINAL");

            Assert.Contains("(mark?`<span class=\"r\">${esc(mark)}</span>`:\"\")+", body);
            Assert.Contains("`<span>${esc(body)}</span>", body);
        }

        /// <summary>
        /// Every existing call site still works: 145 of them open with a rune and a space,
        /// and the three that open with a symbol mark are covered by the same range check.
        /// A sentence with no mark at all renders as a sentence rather than putting its
        /// first word in ember.
        /// </summary>
        [Fact]
        public void Every_mark_the_halls_actually_use_is_covered_by_the_rule()
        {
            var js = AppJs();
            var covered = 0;

            foreach (Match call in Regex.Matches(js, "(?<![A-Za-z0-9_.])toast\\(\\s*(?:TT\\(\\s*)?[\"'`](.)"))
            {
                var first = call.Groups[1].Value[0];
                var isMark = (first >= 'ᚠ' && first <= '᛿') || first == '⌂' || first == '↺';
                var looksLikeAMark = !char.IsLetterOrDigit(first) || (first >= 'ᚠ' && first <= '᛿');

                if (isMark) covered++;
                else
                    Assert.False(looksLikeAMark,
                        "a toast opens with a glyph the mark rule does not know: U+" + ((int)first).ToString("X4"));
            }

            Assert.True(covered > 120, "only " + covered + " toast call sites were recognised as carrying a mark");
        }

        // ------------------------------------------------------------------ 2. the clock

        [Fact]
        public void The_status_clock_no_longer_claims_one_time_zone_for_everybody()
        {
            Assert.DoesNotContain("\" JST\"", AppJs());
            Assert.DoesNotContain("JST", Html());
        }

        /// <summary>
        /// The options are still the same three; what changed is where the formatter
        /// comes from. It used to be built once at boot against whatever locale the
        /// runtime had, which is a clock that keeps writing in the language that was
        /// active when the window opened. It is now asked for per paint, off the
        /// lookup, so it follows the language the host is actually reading.
        /// </summary>
        [Fact]
        public void The_status_clock_is_formatted_for_the_active_language_and_names_its_zone()
        {
            var js = AppJs();
            var options = Between(js, "const CLOCK_OPTS=", "function clockFmt");

            Assert.Contains("timeZoneName:\"short\"", options);
            Assert.Contains("hourCycle:\"h23\"", options);

            var formatter = Between(js, "function clockFmt(){", "function clockOffsetName");
            Assert.Contains("const L=intl(); if(L) return L.dateTimeFormat(CLOCK_OPTS);", formatter);
            Assert.Contains("new Intl.DateTimeFormat(undefined,CLOCK_OPTS)", formatter);
        }

        /// <summary>
        /// A runtime with no Intl still says which clock it is showing, rather than silently
        /// going back to bare digits that could be any zone on earth.
        /// </summary>
        [Fact]
        public void With_no_formatter_the_clock_still_names_its_offset()
        {
            var js = AppJs();

            Assert.Contains("function clockOffsetName(d){", js);
            Assert.Contains("return \"UTC\"+sign+hh+(mm?\":\"+String(mm).padStart(2,\"0\"):\"\");", js);
            Assert.Contains("paintClockSeg();", js);
        }

        // ------------------------------------------------------------------ 3. phase and hold

        /// <summary>
        /// The phase the page knows wins. The host sets a message on every phase it reports,
        /// so taking the message first made the whole table below it unreachable in a real
        /// run: seven sentences the page already owned were never shown.
        /// </summary>
        [Fact]
        public void The_pages_own_phase_sentence_wins_over_the_hosts_prose()
        {
            var js = AppJs();
            var text = Between(js, "function updPhaseText(u){", "\n}");

            Assert.Contains("const own=updPhaseSentence(updPhaseKey(u&&u.phase),u);", text);
            Assert.Contains("if(own) return own;", text);

            var ownIndex = text.IndexOf("const own=", StringComparison.Ordinal);
            var saidIndex = text.IndexOf("const said=", StringComparison.Ordinal);
            Assert.True(ownIndex >= 0 && saidIndex > ownIndex,
                "the host's sentence is still read before the page's own");
        }

        /// <summary>
        /// And the host's sentence is still there for the phases the page has no wording
        /// for, a failure above all: only the host knows why it failed.
        /// </summary>
        [Fact]
        public void The_hosts_sentence_is_still_the_fallback_for_a_phase_the_page_cannot_word()
        {
            var text = Between(AppJs(), "function updPhaseText(u){", "\n}");

            // The wording moved into the catalog, so the fallback is named by id here and the
            // sentence itself is read out of the catalog: the old assertion would have passed
            // on an id that answers with nothing.
            Assert.Contains("return said||T(\"srvupd.phase.unknown\");", text);
            Assert.Equal("Updating the server", Lore("srvupd.phase.unknown"));
        }

        [Fact]
        public void Every_phase_the_page_words_is_still_worded()
        {
            var table = Between(AppJs(), "function updPhaseSentence(k,u){", "\n}");

            // The branch is still there AND the id it answers with still has words. Before
            // the sentences moved into the catalog the first half was the whole gate; on its
            // own it would now pass on a phase that renders its own id on the progress bar.
            foreach (var (phase, id, english) in new[]
            {
                ("backingUp", "srvupd.phase.backing_up", "Backing up worlds"),
                ("askingSteam", "srvupd.phase.asking_steam", "Asking Steam to download the update"),
                ("downloading", "srvupd.phase.downloading", "Steam is downloading the update"),
                ("verifying", "srvupd.phase.verifying", "Verifying the install"),
                ("runningSteamCmd", "srvupd.phase.running_steamcmd", "Running steamcmd"),
                ("finished", "srvupd.phase.finished", "Update finished."),
                ("cancelled", "srvupd.phase.cancelled", "Stopped waiting. Steam carries on downloading on its own."),
            })
            {
                Assert.Contains("k===\"" + phase + "\"", table);
                Assert.Contains("T(\"" + id + "\")", table);
                Assert.Equal(english, Lore(id));
            }

            // A phase it has no wording for answers nothing, so the caller can fall back.
            Assert.Contains("return \"\";", table);
        }

        /// <summary>
        /// One builder for the held-start sentence. The banner and the modal already used
        /// it; the toast printed the host's prose beside them, so the same event said two
        /// different things a few pixels apart.
        /// </summary>
        [Fact]
        public void A_held_start_toasts_the_same_sentence_the_banner_puts()
        {
            var handler = Between(AppJs(), "Native.on(\"server.launchHold\"", "Native.on(\"server.launchHoldCleared\"");

            // guardBody is handed an object whatever arrives, so an event with no payload
            // is a plainly worded hold rather than a handler that throws half way through
            // and never reaches the log line under it.
            Assert.Contains("toast(\"ᛊ \"+T(\"guard.held.toast\",{reason:guardBody(d||{})}));", handler);
            // One sentence with the reason in a named slot, rather than two words glued in
            // front of a sentence guardBody already built.
            Assert.DoesNotContain("TT(\"Start held · \")", handler);
            // The toast no longer prints the host's prose. The log line beside it still
            // does, deliberately: the log stays English and verbatim by decision.
            Assert.DoesNotContain("T(\"guard.held.toast\",{reason:d?.message", handler);
            Assert.Contains("logLine(\"warn\",\"[BakaLoader] start held: \"+(d?.message", handler);
        }
    }
}
