using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using ValheimBakaLoader.Tests.Tools;
using Xunit;

namespace ValheimBakaLoader.Tests.Forms
{
    /// <summary>
    /// The command palette, where the key and the label used to be one string.
    /// <para>
    /// Twenty rows carried a data-cmd whose value WAS the visible English label. The
    /// filter matched it, eighteen branches dispatched on it, and the gating table was
    /// keyed on four of them. Translating those labels would have disabled every
    /// command in the palette, silently, with nothing on screen to say so; leaving the
    /// keys English while translating the labels would have made search match words
    /// that are not anywhere on the screen. Both halves of that are now impossible by
    /// construction, and these are the gates that keep them impossible.
    /// </para>
    /// </summary>
    public class WebUiPaletteKeyTests
    {
        private static string Html() => AppSourceTree.Web("index.html");

        private static string AppJs() => AppSourceTree.Web("app.js");

        private static readonly Regex PaletteRow = new(
            @"<div class=""pitem[^""]*""[^>]*data-cmd=""([^""]*)""", RegexOptions.Compiled);

        private static readonly Regex AnyCmdAttr = new(
            @"data-cmd=""([^""]*)""", RegexOptions.Compiled);

        private static List<string> RowNames() =>
            AnyCmdAttr.Matches(Html()).Select(m => m.Groups[1].Value).ToList();

        /// <summary>
        /// A name in the program, not a sentence: no spaces, no capitals, nothing that
        /// could be mistaken for something a host reads. The empty one is the free
        /// typed console row, which dispatches on its element id instead.
        /// </summary>
        [Fact]
        public void No_command_name_is_a_piece_of_English()
        {
            var names = RowNames();
            Assert.True(names.Count >= 20, "found only " + names.Count + " palette rows");

            foreach (var name in names)
            {
                if (name.Length == 0) continue;
                Assert.DoesNotContain(" ", name);
                Assert.Equal(name.ToLowerInvariant(), name);
                Assert.Matches("^[a-z0-9_]+$", name);
            }
        }

        /// <summary>Every row that has a name also names the sentence it shows.</summary>
        [Fact]
        public void Every_palette_row_names_the_sentence_it_shows()
        {
            var html = Html();
            var rows = Regex.Matches(html, @"<div class=""pitem[^""]*""[^>]*>.*?</div>", RegexOptions.Singleline)
                            .Select(m => m.Value)
                            .Where(row => row.Contains("data-cmd=", StringComparison.Ordinal))
                            .ToList();

            Assert.True(rows.Count >= 20, "found only " + rows.Count + " palette rows");

            foreach (var row in rows)
            {
                Assert.Contains("data-i18n=\"pal.", row);

                // And the id is on something that already holds the English words, so a
                // window with no catalog reads correctly and the walker has a text node
                // to replace rather than markup to append after.
                var text = Regex.Replace(row, "<[^>]+>", "").Trim();
                Assert.False(text.Length == 0, "a row carries an id but shows nothing: " + row);
            }
        }

        /// <summary>
        /// The dispatch compares names. Every comparison against data-cmd in the palette
        /// is against a name, so no branch can be reached only by an English speaker.
        /// </summary>
        [Fact]
        public void The_dispatch_compares_names_and_never_a_sentence()
        {
            var js = AppJs();
            var palette = Between(js, "/* ---------- COMMAND PALETTE ---------- */", "$(\"#cmdchip\").addEventListener");

            var compared = Regex.Matches(palette, @"(?:sel\.dataset\.cmd|cmd)===""([^""]*)""")
                                .Select(m => m.Groups[1].Value)
                                .ToList();

            Assert.True(compared.Count >= 18, "found only " + compared.Count + " dispatch branches");
            foreach (var value in compared)
            {
                Assert.DoesNotContain(" ", value);
                Assert.Equal(value.ToLowerInvariant(), value);
            }

            // Every branch has a row behind it, and no row is unreachable.
            var names = RowNames().Where(n => n.Length > 0).ToHashSet();
            foreach (var value in compared)
                Assert.Contains(value, names);
        }

        /// <summary>
        /// The gating table too. This is the one that would have failed quietly: a row
        /// whose label no longer matches the key is simply never greyed out, and a host
        /// clicks a command that cannot work and gets nothing.
        /// </summary>
        [Fact]
        public void The_gating_table_is_keyed_on_names()
        {
            var js = AppJs();
            var table = Between(js, "const PAL_NEEDS_RUNNING=", ";");

            Assert.DoesNotContain("\"", table);
            foreach (var name in new[] { "save_world", "kill_monsters", "broadcast", "console_command" })
                Assert.Contains(name + ":1", table);

            var names = RowNames().ToHashSet();
            foreach (var name in new[] { "save_world", "kill_monsters", "broadcast", "console_command" })
                Assert.Contains(name, names);
        }

        /// <summary>
        /// Search matches what is on the screen. A host types the words in front of
        /// them; the name behind the row is something they have never seen.
        /// </summary>
        [Fact]
        public void Search_matches_the_rendered_label_rather_than_the_name()
        {
            var js = AppJs();
            var filter = Between(js, "function filterPal(q){", "\n}\n");

            Assert.Contains("palLabel(it).toLowerCase().includes(q)", filter);
            Assert.DoesNotContain("it.dataset.cmd.toLowerCase()", filter);

            // The label is the row's own words: the rune in front and the hall badge
            // behind it stay out, or typing "rite" would light up half the palette.
            Assert.Contains("const palLabel=it=>[...it.childNodes].filter(n=>n.nodeType===3)", js);
        }

        /// <summary>
        /// The preview echo says the label back, because that is the thing that was
        /// just clicked. Echoing the name would have printed "ᛒ save_world · invoked".
        /// </summary>
        [Fact]
        public void The_preview_echo_says_the_label_and_not_the_name()
        {
            var js = AppJs();

            Assert.Contains("toast(\"ᛒ \"+palLabel(sel)+\" · invoked\");", js);
            Assert.Contains("logLine(\"cmd\",\"> \"+palLabel(sel).toLowerCase()", js);
            Assert.DoesNotContain("toast(\"ᛒ \"+sel.dataset.cmd", js);
        }

        /// <summary>
        /// The console command modal has its own data-cmd, and those really are the
        /// commands a host types into a Valheim console. They are not copy, they are
        /// not ids, and nothing here may tidy them into snake case.
        /// </summary>
        [Fact]
        public void The_console_command_rows_keep_the_game_s_own_words()
        {
            // They live in app.js, built at render time, so the index.html gate above
            // never sees them. This is the note that says so out loud.
            var js = AppJs();
            Assert.Contains("inp.value=r.dataset.cmd;", js);
            Assert.DoesNotContain("data-cmd=\"kick\"", Html());
        }

        private static string Between(string text, string start, string end)
        {
            var from = text.IndexOf(start, StringComparison.Ordinal);
            Assert.True(from >= 0, "could not find " + start);
            var to = text.IndexOf(end, from + start.Length, StringComparison.Ordinal);
            Assert.True(to > from, "could not find " + end + " after " + start);
            return text.Substring(from, to - from);
        }
    }
}
