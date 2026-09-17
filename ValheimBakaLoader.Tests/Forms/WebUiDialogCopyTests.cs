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
    /// The last of the app.js wording to move into the catalog: the realm dialogs, the
    /// Log settings dialog, the Network card, the world-delete and Barrow dialogs, the
    /// Statistics reset, the Settings hall's own lines, the Herald and Waystone wizards,
    /// the Map hall's save note, the empty-state table and the world-difficulty dials.
    /// <para>
    /// These read the SOURCE, the way every gate in this suite does, and they read it
    /// from both ends: the page has to ask for the id, and the catalog has to answer it
    /// with the words that were there before. An assertion on only one of the two would
    /// pass on a dialog that draws a dotted name, or on an entry nothing reads.
    /// </para>
    /// </summary>
    public class WebUiDialogCopyTests
    {
        private static string AppJs() => AppSourceTree.Web("app.js");
        private static string Html() => AppSourceTree.Web("index.html");

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
                ? value.GetString() : null;

        private static readonly Regex TCall =
            new(@"(?<![A-Za-z0-9_$])T\(""([a-z][A-Za-z0-9_.]*)""\)", RegexOptions.Compiled);

        /// <summary>Every TT("literal") still in the page, by the English it carries.</summary>
        private static List<string> BridgedLiterals()
        {
            var source = AppJs();
            var found = new List<string>();
            foreach (Match match in Regex.Matches(source, @"(?<![A-Za-z0-9_$])TT\(""((?:[^""\\\n]|\\.)*)""\)"))
                found.Add(match.Groups[1].Value);
            return found;
        }

        // ------------------------------------------------- A. the dialogs ask by id

        /// <summary>
        /// One row per dialog this pass keyed: a sentence only that dialog says, the id
        /// it now asks for, and the English that id has to answer with. A dialog that
        /// went back to spelling its own words fails on the id; a catalog edit that
        /// reworded one of them fails on the English.
        /// </summary>
        public static IEnumerable<object[]> Sentences() => new[]
        {
            new object[] { "realm.delete.title", "Delete realm" },
            new object[] { "realm.restore.orphans.head", "Past worlds on disk" },
            new object[] { "realm.new.title", "Found a new realm" },
            new object[] { "realm.new.status.provisioning", "provisioning a separate install (this can take a moment)…" },
            new object[] { "saga.vellum.modal.title", "Log settings" },
            new object[] { "saga.vellum.folder.label", "Logs folder" },
            new object[] { "saga.divider.earlier", "earlier this session" },
            new object[] { "hearth.net.game_version", "Game version" },
            new object[] { "hearth.net.players.empty", "no vikings connected" },
            new object[] { "world.delete.title", "Delete this world?" },
            new object[] { "world.delete.warning", "There is no undo, and nothing else on this machine holds this world." },
            new object[] { "barrow.title", "Backups" },
            new object[] { "barrow.layer.restore.chip", "RESTORE" },
            new object[] { "barrow.layer.block.running", "Stop the server first" },
            new object[] { "skald.reset.confirm.title", "Reset the statistics?" },
            new object[] { "world.seed.not_created", "not created yet · seed set on first launch" },
            new object[] { "world.editbar.editing", "Editing" },
            new object[] { "herald.wiz.intro.title", "Summon the Herald" },
            new object[] { "herald.wiz.publish", "Publish the post" },
            new object[] { "waystone.wiz.intro.title", "Raise a Waystone" },
            new object[] { "waystone.wiz.check.stat.match", "the name answers with this server's IP, perfect" },
            new object[] { "atlas.save.none", "No save file yet. The clock starts with the first launch." },
            new object[] { "atlas.wx.anchor.live", "Anchored to the last world save." },
            new object[] { "setup.wiz.done.note", "Everything can be changed any time in the <strong>WORLD</strong> hall. Name your server, pick a world, and sail forth." },
        };

        [Theory]
        [MemberData(nameof(Sentences))]
        public void The_dialog_asks_for_the_id_and_the_catalog_answers_with_the_words(string id, string lore)
        {
            Assert.Contains("T(\"" + id + "\")", AppJs());
            var catalog = Catalog();
            Assert.True(catalog.ContainsKey(id), "the catalog lost " + id);
            Assert.Equal(lore, Field(catalog[id], "lore"));
        }

        /// <summary>
        /// The words a whole run-time family owns are asked for by app.js and by nothing
        /// else, the same rule the update and condition families already keep: an element
        /// the walker fills and a dialog overwrites is a sentence with two owners.
        /// </summary>
        [Theory]
        [InlineData("realm.", 44)]
        [InlineData("barrow.", 25)]
        [InlineData("waystone.wiz.", 37)]
        [InlineData("world.wg.", 65)]
        public void Every_dialog_key_is_asked_for_by_app_js_and_by_nothing_else(string prefix, int howMany)
        {
            var source = AppJs();
            var html = Html();
            var mine = Catalog().Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal))
                                     .OrderBy(k => k, StringComparer.Ordinal).ToList();

            Assert.Equal(howMany, mine.Count);
            foreach (var id in mine)
            {
                Assert.True(source.Contains("\"" + id + "\"", StringComparison.Ordinal),
                            "app.js never asks for " + id);
                Assert.DoesNotContain("data-i18n=\"" + id + "\"", html);
                Assert.DoesNotContain("data-i18n-title=\"" + id + "\"", html);
                Assert.DoesNotContain("data-i18n-placeholder=\"" + id + "\"", html);
                Assert.DoesNotContain("data-i18n-aria=\"" + id + "\"", html);
            }
        }

        // ------------------------------------------------- B. the empty-state table

        /// <summary>
        /// emptyState() words nothing itself any more. It used to run its title, reason
        /// and button label through TT(), which made the table of empty states a table of
        /// English that no gate could see and no translator could reach. Every caller now
        /// asks the catalog first and hands the words in.
        /// </summary>
        [Fact]
        public void The_empty_state_shape_words_nothing_itself()
        {
            var body = Between(AppJs(), "function emptyState(o){", "\n}");

            Assert.DoesNotContain("TT(", body);
            Assert.Contains("${esc(o.title||T(\"common.empty.title\"))}", body);
            Assert.Contains("${esc(o.reason)}", body);
            Assert.Contains("${esc(a.label)}", body);
            Assert.Equal("Nothing here yet", Field(Catalog()["common.empty.title"], "lore"));
        }

        /// <summary>
        /// And every call site hands in catalog words. The one exception says TT() out
        /// loud: the mod search's reason counts the rows it is hiding, so it is a composed
        /// sentence and stays on the bridge until the composed-message pass gives it a
        /// slot. Pinned by count so a new empty state written in English fails here.
        /// </summary>
        [Fact]
        public void Every_empty_state_hands_in_words_the_catalog_owns()
        {
            var source = AppJs();
            var calls = Regex.Matches(source, @"emptyState\(\{(?:[^{}]|\{[^{}]*\})*\}\)");

            Assert.Equal(17, calls.Count);
            var composed = 0;
            foreach (Match call in calls)
            {
                var text = call.Value;
                foreach (var field in new[] { "title:", "reason:", "label:" })
                {
                    var at = text.IndexOf(field, StringComparison.Ordinal);
                    if (at < 0) continue;
                    var rest = text.Substring(at + field.Length);
                    if (rest.StartsWith("TT(", StringComparison.Ordinal)) { composed++; continue; }
                    Assert.True(rest.StartsWith("T(\"", StringComparison.Ordinal),
                                "an empty state still spells its own " + field + " " + rest.Substring(0, Math.Min(60, rest.Length)));
                }
            }

            Assert.Equal(1, composed);
            Assert.Contains("reason:TT(\"Nothing in this server's mod list carries every word that was typed. "
                            + "Clear the box to see all \"+mods.length+\" again.\")", source);
        }

        // ------------------------------------------------- C. the world-difficulty dials

        /// <summary>
        /// The dial table holds catalog ids now. Its `label` is the one field that keeps
        /// its English beside the id, because two composed sentences still read it: the
        /// forge dialog's help button and the first-run wizard's summary line. Keeping
        /// both halves is only safe while something holds them together, and that is the
        /// gate rule below this test.
        /// </summary>
        [Fact]
        public void The_world_dials_read_their_wording_out_of_the_catalog()
        {
            var source = AppJs();
            var catalog = Catalog();

            foreach (var dial in new[] { "combat", "deathpenalty", "resources", "raids", "portals" })
            {
                Assert.Contains("labelId:\"world.wg." + dial + ".label\"", source);
                Assert.Contains("introId:\"world.wg." + dial + ".intro\"", source);
                Assert.True(catalog.ContainsKey("world.wg." + dial + ".intro"),
                            "the catalog lost the " + dial + " dial's own sentence");
            }

            Assert.Equal("Combat", Field(catalog["world.wg.combat.label"], "lore"));
            Assert.Equal("Death penalty", Field(catalog["world.wg.deathpenalty.label"], "lore"));
            Assert.Equal("Hardcore, items and skills lost",
                         Field(catalog["world.wg.deathpenalty.hardcore.label"], "lore"));
            Assert.Equal("BakaLoader applies these settings every time the server starts, and clears any "
                         + "leftover difficulty keys first. A difficulty change made with the in-game "
                         + "console does not survive a restart.",
                         Field(catalog["world.wg.own_note"], "lore"));

            // the panel, the dropdown and the line under a dial all read the ids
            Assert.Contains("${esc(T(h.labelId))}", source);
            Assert.Contains("${esc(T(h.introId))}", source);
            Assert.Contains("${esc(T(o.labelId))}", source);
            Assert.Contains("${esc(T(o.explainId))}", source);
            Assert.Contains("return o?T(o.explainId):\"\";", source);
        }

        /// <summary>
        /// The keys the game itself sets are NOT wording. They are what a host reads off
        /// their own world-modifier menu to check a BakaLoader world against it, so they
        /// stay verbatim, are not in the catalog, and are not asked for by id.
        /// </summary>
        [Fact]
        public void The_console_keys_under_each_dial_are_never_translated()
        {
            var source = AppJs();
            var catalog = Catalog();

            Assert.Contains("effects:\"playerdamage 125, enemydamage 50, enemyspeedsize 90\"", source);
            Assert.Contains("effects:\"deathdeleteunequipped, skillreductionrate 150\"", source);
            Assert.Contains("effects:\"no keys set\"", source);
            Assert.DoesNotContain("effectsId:", source);
            Assert.DoesNotContain("${esc(T(o.effects", source);

            foreach (var entry in catalog.Values)
            {
                var lore = Field(entry, "lore");
                if (lore == null) continue;
                Assert.DoesNotContain("resourcerate", lore);
                Assert.DoesNotContain("eventrate", lore);
                Assert.DoesNotContain("playerdamage", lore);
            }
        }

        // ------------------------------------------------- D. what is deliberately left

        /// <summary>
        /// What the bridge still answers, and why. Every literal left is a piece of a
        /// sentence rather than a sentence: a joiner, a fallback that reads as half a
        /// clause, or a fragment either side of a value. Handing " for good:" or "of" to
        /// a translator produces nothing usable in Russian, so they wait for the pass
        /// that turns each one into a whole sentence with a named slot.
        /// <para>
        /// The count is what makes this a gate rather than a note: a new English sentence
        /// written straight into app.js and wrapped in TT() lands here.
        /// </para>
        /// </summary>
        [Fact]
        public void What_the_bridge_still_answers_is_fragments_and_is_counted()
        {
            var left = BridgedLiterals();
            Assert.Equal(136, left.Count);

            // and the shape of what is left: a handful named, so the list cannot quietly
            // become a place to leave a whole sentence.
            foreach (var fragment in new[]
            {
                " for good:", "of", " and ", " online", "preview only",
                "its .fwl and .db pair", "and its paired .db", "unknown error",
            })
            {
                Assert.Contains(fragment, left);
            }

            // Nothing this pass keyed is still spelled out beside its id.
            foreach (var sentence in new[]
            {
                "Delete realm", "Log settings", "Reset the statistics?", "Summon the Herald",
                "Raise a Waystone", "Backups", "Layers", "World", "Cancel", "Name", "Password",
            })
            {
                Assert.DoesNotContain(sentence, left);
            }
        }

        /// <summary>
        /// The Log settings dialog keeps one sentence on the bridge for a reason worth
        /// writing down: it spells a file name with angle-bracket placeholders, and the
        /// catalog's markup rule reads &lt;realm&gt; as a tag. Marking the entry allowsHtml
        /// would be a lie on a value that goes through esc(), so it waits for the pass
        /// that gives it {slot} placeholders instead.
        /// </summary>
        [Fact]
        public void The_one_note_with_angle_brackets_waits_for_its_slots()
        {
            var left = BridgedLiterals();
            Assert.Contains(left, line => line.StartsWith("each server session writes its own scroll: ServerLogs-<realm>-",
                                                          StringComparison.Ordinal));
            Assert.DoesNotContain("saga.vellum.serverlog", string.Join(" ", Catalog().Keys));
        }

        /// <summary>
        /// allowsHtml is on the entries whose call site writes them into the page without
        /// esc(), and on no others. The wizards are the only surfaces that do that, and
        /// they do it because their steps carry &lt;strong&gt; and &lt;span class="mono"&gt;.
        /// </summary>
        [Fact]
        public void Only_the_wizard_steps_are_allowed_to_carry_markup()
        {
            var catalog = Catalog();
            var html = catalog.Where(pair => pair.Value.TryGetProperty("allowsHtml", out var flag)
                                             && flag.ValueKind == JsonValueKind.True)
                              .Select(pair => pair.Key)
                              .OrderBy(k => k, StringComparer.Ordinal).ToList();

            Assert.NotEmpty(html);
            foreach (var id in html)
            {
                Assert.True(id.StartsWith("herald.wiz.", StringComparison.Ordinal)
                            || id.StartsWith("waystone.wiz.", StringComparison.Ordinal)
                            || id.StartsWith("setup.wiz.", StringComparison.Ordinal),
                            id + " says allowsHtml but is not a wizard step");
                Assert.True(AppJs().Contains("${T(\"" + id + "\")}", StringComparison.Ordinal)
                            || AppJs().Contains("+T(\"" + id + "\")+", StringComparison.Ordinal),
                            id + " says allowsHtml but its call site escapes it");
            }
        }

        private static string Between(string source, string open, string shut)
        {
            var from = source.IndexOf(open, StringComparison.Ordinal);
            Assert.True(from >= 0, "the source no longer holds " + open);
            var to = source.IndexOf(shut, from + open.Length, StringComparison.Ordinal);
            Assert.True(to > from, "the source no longer closes " + open);
            return source.Substring(from, to - from);
        }
    }
}
