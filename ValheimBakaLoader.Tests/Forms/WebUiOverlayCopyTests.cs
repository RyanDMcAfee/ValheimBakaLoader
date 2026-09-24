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
    /// The things that float above the halls: the command palette, and the five
    /// containers every dialog, menu, tooltip and toast is built inside.
    /// <para>
    /// WebUiPaletteKeyTests already proves the palette's ROWS name a command and a
    /// sentence separately. This file is the rest of that overlay: the little badge
    /// beside each row, the hints along the bottom, and the record that the modal
    /// layer really does hold no words of its own, so a reader looking for a missing
    /// dialog sentence knows to look in app.js rather than here.
    /// </para>
    /// </summary>
    public class WebUiOverlayCopyTests
    {
        private static string Html() => AppSourceTree.Web("index.html");

        private static string AppJs() => AppSourceTree.Web("app.js");

        private static Dictionary<string, JsonElement> Catalog()
        {
            using var document = JsonDocument.Parse(AppSourceTree.Web("i18n/en.json"));
            var map = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var entry in document.RootElement.GetProperty("keys").EnumerateObject())
                map[entry.Name] = entry.Value.Clone();
            return map;
        }

        private static string Lore(Dictionary<string, JsonElement> catalog, string id) =>
            catalog.TryGetValue(id, out var entry) && entry.TryGetProperty("lore", out var lore)
                ? lore.GetString() : null;

        private static string Plain(Dictionary<string, JsonElement> catalog, string id) =>
            catalog.TryGetValue(id, out var entry) && entry.TryGetProperty("plain", out var plain)
                ? plain.GetString() : null;

        /// <summary>The palette, opening tag to closing div of its list and footer.</summary>
        private static string Palette()
        {
            var html = Html();
            var from = html.IndexOf("<!-- COMMAND PALETTE -->", StringComparison.Ordinal);
            var to = html.IndexOf("<!-- CONTEXT MENU", from, StringComparison.Ordinal);
            Assert.True(to > from && from > 0, "index.html no longer has the command palette");
            return html.Substring(from, to - from);
        }

        // ------------------------------------------------------------- A. the badges

        /// <summary>
        /// The word in the corner of each palette row is a word a host reads, so it is
        /// an entry like any other. Twenty one rows say fourteen words between them and
        /// each word is one entry, because a translator handed "path" four times will
        /// eventually write it three ways.
        /// </summary>
        [Fact]
        public void Every_palette_badge_reads_its_word_out_of_the_catalog()
        {
            var palette = Palette();
            var catalog = Catalog();

            var badges = Regex.Matches(palette, @"<span class=""k"" data-i18n=""([^""]+)"">([^<]+)</span>")
                              .Select(m => (Id: m.Groups[1].Value, Word: m.Groups[2].Value))
                              .ToList();

            // 22 with Clear cheat marks, which is a viking row like the kick beside it.
            Assert.Equal(22, badges.Count);
            Assert.DoesNotContain("<span class=\"k\">", palette);   // none left unkeyed

            foreach (var (id, word) in badges)
            {
                Assert.Equal("pal.k." + word, id);
                Assert.Equal(word, Lore(catalog, id));
            }

            Assert.Equal(14, badges.Select(b => b.Id).Distinct().Count());
        }

        /// <summary>
        /// Seven of those fourteen are words the plain swap has always reworded, so the
        /// entry carries both registers and the two ways of asking give the same answer.
        /// The other seven read the same either way and name no plain register, because
        /// one that said anything else would be a new wording rather than a translation.
        /// </summary>
        [Fact]
        public void The_badges_the_swap_rewords_carry_both_registers()
        {
            var catalog = Catalog();
            var js = AppJs();
            // The seven whose two registers really differ. They used to differ because a
            // pair in the swap table said so; they differ because the entry says so now,
            // and the English on screen is the same in both registers either way.

            var both = new (string Word, string Plain)[]
            {
                ("rite", "action"), ("hearth", "server"), ("saga", "log"), ("viking", "player"),
                ("forge", "mods"), ("waystone", "domain"), ("skald", "analytics"),
            };

            foreach (var (word, plain) in both)
                Assert.Equal(plain, Plain(catalog, "pal.k." + word));

            // Three more carry a plain register since the swap table went: the table only
            // held a pair for a badge whose word it happened to name, and barrow, herald
            // and vellum were not in it. A host with the Norse names off was reading the
            // Norse badge on those three rows and the plain one on the other seven.
            foreach (var (word, plain) in new (string Word, string Plain)[]
                     { ("barrow", "backups"), ("herald", "discord"), ("vellum", "log settings") })
                Assert.Equal(plain, Plain(catalog, "pal.k." + word));

            // The four that are plain English already, in either register.
            foreach (var word in new[] { "rcon", "steam", "net", "path" })
                Assert.Null(Plain(catalog, "pal.k." + word));

            // Nothing reaches these badges but the walker, and the register it writes is
            // picked inside the lookup rather than by a regex over what is on screen.
            Assert.DoesNotContain("const TERM_STATIC_SEL", js);
        }

        /// <summary>
        /// Translating a badge cannot change what the palette finds. palLabel reads the
        /// row's OWN text nodes, so the badge, which is inside a span, was never part of
        /// the haystack: a host types the words in the middle of the row and that is what
        /// is matched. Written down because the opposite would be silent, and because the
        /// Mods search does the other thing on purpose.
        /// </summary>
        [Fact]
        public void A_badge_is_not_part_of_what_the_palette_searches()
        {
            var js = AppJs();

            Assert.Contains("const palLabel=it=>[...it.childNodes].filter(n=>n.nodeType===3)", js);
            Assert.Contains("palLabel(it).toLowerCase().includes(q)", js);
        }

        // ------------------------------------------------------------- B. the footer

        /// <summary>
        /// Three of the four hints along the bottom are one word after their key, and
        /// each one is now its own span so the entry is the bare word rather than a word
        /// with a space glued to the front of it. The fourth puts the key in the middle
        /// of a sentence, which is two text nodes either side of a kbd: the walker fills
        /// the first one it finds, so keying it would translate "no match? " and leave
        /// the rest of the sentence in English. It waits for a value that may hold its
        /// own markup, with the other composed messages.
        /// </summary>
        [Fact]
        public void The_palette_hints_that_are_one_word_are_keyed_and_the_sentence_waits()
        {
            var palette = Palette();
            var catalog = Catalog();

            foreach (var (key, word) in new[]
            {
                ("navigate", "navigate"), ("invoke", "invoke"), ("dismiss", "dismiss"),
            })
            {
                Assert.Contains("<span data-i18n=\"pal.foot." + key + "\">" + word + "</span>", palette);
                Assert.Equal(word, Lore(catalog, "pal.foot." + key));
            }

            var foot = palette.Substring(palette.IndexOf("<div class=\"pfoot\">", StringComparison.Ordinal));
            var sentence = foot.Substring(foot.IndexOf("no match?", StringComparison.Ordinal) - 20, 90);
            Assert.DoesNotContain("data-i18n", sentence);
            Assert.Contains("no match? <kbd>Enter</kbd> sends it to the console", foot);
        }

        /// <summary>
        /// The box a host types into carries its id at last. It was kept out of the
        /// catalog while applyTerms cached that placeholder and four like it so it could
        /// put the Norse wording back when the switch went the other way: an id there
        /// would have let the cache restore the English seed over a pack's word on the
        /// first toggle. The cache and the key had to move in one change or not at all,
        /// and this is that change. The seed stays in index.html because that is what a
        /// window whose catalog never arrived reads.
        /// </summary>
        [Fact]
        public void The_palette_box_keys_its_placeholder_now_that_the_cache_is_gone()
        {
            var palette = Palette();
            var js = AppJs();

            var box = palette.Substring(palette.IndexOf("<input id=\"palInput\"", StringComparison.Ordinal), 160);
            Assert.Contains("placeholder=\"Summon a command…\"", box);
            Assert.Contains("data-i18n-placeholder=\"pal.input.placeholder\"", box);

            // Both registers, out of one entry, which is what the cache used to fake.
            var catalog = Catalog();
            Assert.Equal("Summon a command…", Lore(catalog, "pal.input.placeholder"));
            Assert.Equal("Type a command…", Plain(catalog, "pal.input.placeholder"));

            Assert.DoesNotContain("$(\"#palInput\"),$(\"#cfgEditor\")", js);
            Assert.DoesNotContain("[\"Summon a command…\",\"Type a command…\"]", js);
        }

        // ----------------------------------------------------------- C. the overlays

        /// <summary>
        /// Every dialog, menu, tooltip and toast is built in app.js and dropped into one
        /// of these five containers, so none of them holds a word of its own. This is the
        /// record of that, and the reason there are no dialog sentences in the catalog
        /// yet: they are template literals in app.js, and they belong to the slice that
        /// keys run-time messages. A container that grows words without an id fails here.
        /// </summary>
        [Fact]
        public void The_overlay_containers_hold_no_words_of_their_own()
        {
            var html = Html();

            foreach (var id in new[] { "ctxMenu", "modalBg", "wgTip", "toasts" })
            {
                var open = html.IndexOf("id=\"" + id + "\"", StringComparison.Ordinal);
                Assert.True(open > 0, "index.html no longer has #" + id);
                var close = html.IndexOf("</div>", open, StringComparison.Ordinal);
                var inside = html.Substring(html.IndexOf('>', open) + 1, close - html.IndexOf('>', open) - 1);
                Assert.True(inside.Trim().Length == 0,
                    "#" + id + " now holds words of its own, which nothing translates: " + inside);
            }

            // the eight resize grips are invisible and carry no text either
            Assert.Equal(8, Regex.Matches(html, @"<div class=""grip"" data-edge=""[a-z]+""></div>").Count);

            // and the dialogs really are built in app.js, so the words are somewhere
            Assert.Contains("function modalOpen(", AppJs());
            Assert.Contains("function ctxOpen(", AppJs());
        }
    }
}
