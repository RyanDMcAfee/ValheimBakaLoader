using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using ValheimBakaLoader.Tools;
using Xunit;

namespace ValheimBakaLoader.Tests.Tools
{
    /// <summary>
    /// The catalog behind the sentences that never reach a page: the restart countdown a
    /// player reads in the middle of their screen and the Discord post a community reads in a
    /// channel. It is the same catalog file the interface reads, embedded in the assembly, and
    /// the whole of what it has to get right is here: the English it answers with is the
    /// English that was in the code before it existed, a pack's own words win when there are
    /// any, a line the pack is missing still gets said, and a count picks the plural its own
    /// language has rather than the one English has.
    /// </summary>
    public class HostCatalogTests
    {
        // ------------------------------------------------------------------ the file is there

        /// <summary>
        /// The catalog is embedded rather than read off the disk beside the exe, because the
        /// install folder can be read only and a self update robocopies over it. If the csproj
        /// ever stops embedding it, every host-facing sentence becomes an id in a Discord post,
        /// and nothing else in the suite would notice.
        /// </summary>
        [Fact]
        public void The_english_catalog_is_inside_the_assembly()
        {
            using var stream = typeof(HostCatalog).Assembly
                .GetManifestResourceStream(HostCatalog.EnglishResourceName);

            Assert.NotNull(stream);
            Assert.True(HostCatalog.EnglishKeyCount > 1000,
                "the embedded catalog holds " + HostCatalog.EnglishKeyCount + " lines");
            Assert.True(HostCatalog.EmbeddedEnglish.Has("host.countdown.restart_in"));
            Assert.Equal("en", HostCatalog.EmbeddedEnglish.Code);
        }

        // ------------------------------------------------------------------ the English, to the byte

        /// <summary>
        /// Every sentence that moved out of the code and into the catalog, beside the English
        /// the code used to write. This is the promise that makes the move additive: a host
        /// who never installs a pack reads exactly what they read before, punctuation and all.
        /// </summary>
        [Theory]
        // The restart countdown, over RCON, in the middle of every player's screen.
        [InlineData("host.countdown.restart_now", "Server restarting NOW!")]
        [InlineData("host.countdown.cancelled", "Server restart cancelled.")]
        // The Discord posts, one per event.
        [InlineData("host.discord.started.title", "Server Started")]
        [InlineData("host.discord.stopped.title", "Server Stopped")]
        [InlineData("host.discord.crashed.title", "Server Crashed")]
        [InlineData("host.discord.joined.title", "Player Joined")]
        [InlineData("host.discord.left.title", "Player Left")]
        [InlineData("host.discord.held.title", "Start Held")]
        [InlineData("host.discord.updated.title", "Server Updated")]
        [InlineData("host.discord.updated.starting", "Starting it now.")]
        [InlineData("host.discord.updated.when_ready", "Start it when you are ready.")]
        [InlineData("host.discord.update_failed.title", "Server Update Failed")]
        [InlineData("host.discord.legacy_world.title", "Old World Format")]
        // The self-editing status post's field names and the two words in it that are words.
        [InlineData("host.status.title", "Valheim server")]
        [InlineData("host.status.field.status", "Status")]
        [InlineData("host.status.field.players", "Players online")]
        [InlineData("host.status.field.world", "World")]
        [InlineData("host.status.field.address", "Join address")]
        [InlineData("host.status.field.mods", "Mods")]
        [InlineData("host.status.field.last_mod_update", "Last mod update")]
        [InlineData("host.status.field.next_restart", "Next restart")]
        [InlineData("host.status.field.password", "Password")]
        [InlineData("host.status.state.online", "Online")]
        [InlineData("host.status.state.starting", "Starting up…")]
        [InlineData("host.status.state.stopping", "Shutting down…")]
        [InlineData("host.status.state.offline", "Offline")]
        [InlineData("host.status.value.unknown", "Unknown")]
        [InlineData("host.status.value.vanilla", "vanilla")]
        [InlineData("host.status.value.not_scheduled", "not scheduled")]
        public void A_sentence_with_nothing_in_it_reads_as_it_always_did(string id, string english)
        {
            Assert.Equal(english, HostCatalog.EmbeddedEnglish.Say(id));
        }

        /// <summary>The same promise for the sentences that carry a value.</summary>
        [Fact]
        public void A_sentence_with_something_in_it_reads_as_it_always_did()
        {
            var english = HostCatalog.EmbeddedEnglish;

            Assert.Equal(
                "Server restarting in 5 minutes!",
                english.Say("host.countdown.restart_in", ("time", "5 minutes")));

            Assert.Equal("**Asgard** is now online.", english.Say("host.discord.started.body", ("server", "Asgard")));
            Assert.Equal("**Asgard** has been shut down.", english.Say("host.discord.stopped.body", ("server", "Asgard")));
            Assert.Equal("**Asgard** has crashed!", english.Say("host.discord.crashed.body", ("server", "Asgard")));
            Assert.Equal(
                "**Asgard** has crashed! Auto-restarting in 30 seconds...",
                english.Say("host.discord.crashed.body_restarting", ("server", "Asgard"), ("seconds", 30)));
            Assert.Equal(
                "**Bjorn** joined **Asgard**.",
                english.Say("host.discord.joined.body", ("player", "Bjorn"), ("server", "Asgard")));
            Assert.Equal(
                "**Bjorn** left **Asgard**.",
                english.Say("host.discord.left.body", ("player", "Bjorn"), ("server", "Asgard")));
            Assert.Equal(
                "**Asgard** was not started automatically. The build on disk is not the one it last ran.",
                english.Say(
                    "host.discord.held.body",
                    ("server", "Asgard"),
                    ("reason", "The build on disk is not the one it last ran.")));
            Assert.Equal("**Asgard** finished updating.", english.Say("host.discord.updated.body", ("server", "Asgard")));
            Assert.Equal("It is now on build 17250562.", english.Say("host.discord.updated.build", ("build", "17250562")));
            Assert.Equal(
                "**Asgard** was not updated. Steam would not answer. The server was not started.",
                english.Say(
                    "host.discord.update_failed.body",
                    ("server", "Asgard"),
                    ("reason", "Steam would not answer.")));
            Assert.Equal(
                "**Asgard** loaded **Midgard** in the pre-1.0 save format. "
                + "The next save converts it, and older servers will not be able to load it afterwards.",
                english.Say("host.discord.legacy_world.body", ("server", "Asgard"), ("world", "Midgard")));
            Assert.Equal(
                "Valheim BakaLoader v1.2.0 · this post updates itself",
                english.Say("host.status.footer", ("version", "1.2.0")));
        }

        /// <summary>
        /// The plural sentences, which is where the old code wrote an "s" on the end of a word
        /// and the catalog writes a form. The English on both sides of every one of these is
        /// what the ternary used to produce.
        /// </summary>
        [Theory]
        [InlineData(1, "(1 mod update pending)")]
        [InlineData(2, "(2 mod updates pending)")]
        [InlineData(17, "(17 mod updates pending)")]
        public void The_update_note_counts_the_way_it_always_did(int count, string english)
        {
            Assert.Equal(english, HostCatalog.EmbeddedEnglish.Say("host.countdown.update_note", ("count", count)));
        }

        /// <summary>
        /// And the countdown's own spans of time, which this catalog writes for the players
        /// while the interface catalog writes the same spans for the window. Both halves of
        /// each pair, because "1 hours" is exactly the kind of thing a plural rule is for.
        /// </summary>
        [Theory]
        [InlineData("host.time.hours", 1, "1 hour")]
        [InlineData("host.time.hours", 2, "2 hours")]
        [InlineData("host.time.minutes", 1, "1 minute")]
        [InlineData("host.time.minutes", 5, "5 minutes")]
        [InlineData("host.time.seconds", 1, "1 second")]
        [InlineData("host.time.seconds", 30, "30 seconds")]
        public void A_span_of_time_reads_as_it_always_did(string id, int count, string english)
        {
            Assert.Equal(english, HostCatalog.EmbeddedEnglish.Say(id, ("count", count)));
        }

        // ------------------------------------------------------------------ the lookup itself

        /// <summary>
        /// An id nothing answers for comes back as itself rather than as an empty string. An
        /// empty Discord field is a post nobody can read; an id in one is a bug report.
        /// </summary>
        [Fact]
        public void An_id_with_no_words_behind_it_answers_with_itself()
        {
            Assert.Equal("host.nothing.here", HostCatalog.EmbeddedEnglish.Say("host.nothing.here"));
            Assert.Equal("", HostCatalog.EmbeddedEnglish.Say(null));
        }

        /// <summary>
        /// A slot with no value behind it is left standing, exactly as the page's own lookup
        /// leaves it. A gap reads as a wording mistake somebody lives with; a literal {time}
        /// gets reported the same day.
        /// </summary>
        [Fact]
        public void A_slot_with_nothing_behind_it_is_left_standing()
        {
            Assert.Equal("Server restarting in {time}!", HostCatalog.EmbeddedEnglish.Say("host.countdown.restart_in"));
        }

        // ------------------------------------------------------------------ a pack's own words

        private static string WritePack(string root, string code, string version, object keys)
        {
            var folder = Path.Combine(root, code, version);
            Directory.CreateDirectory(folder);

            File.WriteAllText(
                Path.Combine(folder, "strings.json"),
                JsonConvert.SerializeObject(new { _meta = new { lang = code, language = code }, keys }),
                Encoding.UTF8);

            return folder;
        }

        [Fact]
        public void A_pack_answers_for_the_lines_it_carries_and_english_answers_for_the_rest()
        {
            var root = Path.Combine(Path.GetTempPath(), "bakaloader-hostcat-" + Guid.NewGuid().ToString("N"));

            try
            {
                WritePack(root, "ru", "1.2.0", new Dictionary<string, object>
                {
                    ["host.countdown.restart_now"] = new { lore = "Server restarting NOW!", translation = "Сервер NOW" },
                    ["app.title"] = new { lore = "BakaLoader", translation = "BakaLoader" },
                });

                var catalog = HostCatalog.Load(root, "ru", "1.2.0");

                Assert.Equal("ru", catalog.Code);
                Assert.Equal("Сервер NOW", catalog.Say("host.countdown.restart_now"));

                // A line the pack does not carry is still said, in English, with no branch at
                // any call site.
                Assert.Equal("Server restart cancelled.", catalog.Say("host.countdown.cancelled"));
            }
            finally
            {
                try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch (Exception) { }
            }
        }

        /// <summary>
        /// A pack is a file downloaded from a release page, so it is the untrusted half of
        /// this exchange, and a half filled translation file is what it most often looks
        /// like: the key is there and there are no words inside it. That has to be answered
        /// the same way a missing key is, because the alternative is the id itself going out
        /// in a Discord post or an RCON broadcast, where "host.countdown.restart_now" is what
        /// a community reads.
        /// <para>
        /// Three shapes of empty, all of them things a real editor produces: an entry with no
        /// value at all, one whose value is null, and a plural object with no category the
        /// language uses. The one after them is the control, so a pass here cannot be the
        /// whole pack quietly failing to load.
        /// </para>
        /// </summary>
        [Fact]
        public void An_entry_a_pack_carries_with_no_words_in_it_falls_through_to_english()
        {
            var root = Path.Combine(Path.GetTempPath(), "bakaloader-hostcat-" + Guid.NewGuid().ToString("N"));

            try
            {
                WritePack(root, "ru", "1.2.0", new Dictionary<string, object>
                {
                    ["host.countdown.restart_now"] = new { },
                    ["host.countdown.cancelled"] = new { translation = (string)null, lore = (string)null },
                    ["host.time.minutes"] = new { translation = new { }, plural = "count" },
                    ["host.countdown.update_note"] = new
                    {
                        translation = new { one = "{count} штука", few = "{count} штуки", many = "{count} штук", other = "{count} штук" },
                        plural = "count",
                    },
                });

                var catalog = HostCatalog.Load(root, "ru", "1.2.0");

                Assert.Equal("Server restarting NOW!", catalog.Say("host.countdown.restart_now"));
                Assert.Equal("Server restart cancelled.", catalog.Say("host.countdown.cancelled"));

                // And the fall-through reads English words, so it counts by English rules:
                // five minutes is "other" here and would have been "many" in Russian.
                Assert.Equal("5 minutes", catalog.Say("host.time.minutes", ("count", 5)));

                // Has says what Say will do rather than only that a key exists.
                Assert.True(catalog.Has("host.countdown.restart_now"));
                Assert.False(catalog.Has("host.nothing.here"));

                // The control: the pack IS loaded and its own words do win.
                Assert.Equal("5 штук", catalog.Say("host.countdown.update_note", ("count", 5)));
            }
            finally
            {
                try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch (Exception) { }
            }
        }

        /// <summary>
        /// A pack's plural is the pack's language's plural. Russian has four categories where
        /// English has two, and a count of five is the one that shows it: English picks other
        /// and Russian picks many.
        /// </summary>
        [Fact]
        public void A_pack_counts_in_its_own_language()
        {
            var root = Path.Combine(Path.GetTempPath(), "bakaloader-hostcat-" + Guid.NewGuid().ToString("N"));

            try
            {
                WritePack(root, "ru", "1.2.0", new Dictionary<string, object>
                {
                    ["host.time.minutes"] = new
                    {
                        lore = new { one = "{count} minute", other = "{count} minutes" },
                        translation = new
                        {
                            one = "{count} one",
                            few = "{count} few",
                            many = "{count} many",
                            other = "{count} other",
                        },
                        plural = "count",
                    },
                });

                var catalog = HostCatalog.Load(root, "ru", "1.2.0");

                Assert.Equal("1 one", catalog.Say("host.time.minutes", ("count", 1)));
                Assert.Equal("2 few", catalog.Say("host.time.minutes", ("count", 2)));
                Assert.Equal("5 many", catalog.Say("host.time.minutes", ("count", 5)));
                Assert.Equal("11 many", catalog.Say("host.time.minutes", ("count", 11)));
                Assert.Equal("21 one", catalog.Say("host.time.minutes", ("count", 21)));

                // And a line the pack does not carry fell through to the English, so it is
                // English plural rules that pick its form rather than Russian ones.
                Assert.Equal("(5 mod updates pending)", catalog.Say("host.countdown.update_note", ("count", 5)));
            }
            finally
            {
                try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch (Exception) { }
            }
        }

        [Theory]
        [InlineData("en", 1, "one")]
        [InlineData("en", 0, "other")]
        [InlineData("en", 2, "other")]
        [InlineData("ru", 1, "one")]
        [InlineData("ru", 21, "one")]
        [InlineData("ru", 11, "many")]
        [InlineData("ru", 3, "few")]
        [InlineData("ru", 14, "many")]
        [InlineData("ru", 0, "many")]
        [InlineData("ja", 1, "other")]
        [InlineData("zh-Hans", 1, "other")]
        [InlineData("zh-Hant", 5, "other")]
        public void The_plural_categories_are_the_published_ones(string code, int count, string category)
        {
            Assert.Equal(category, HostCatalog.PluralCategory(code, count));
        }

        /// <summary>
        /// A pack that is not on disk, a language the app does not know, and English itself
        /// all answer with the English inside the app. A lookup that threw would take a
        /// countdown announcement with it.
        /// </summary>
        [Theory]
        [InlineData("en", "1.2.0")]
        [InlineData("ru", null)]
        [InlineData("kl", "1.2.0")]
        [InlineData(null, "1.2.0")]
        public void A_pack_that_is_not_there_answers_in_english(string code, string version)
        {
            var root = Path.Combine(Path.GetTempPath(), "bakaloader-hostcat-" + Guid.NewGuid().ToString("N"));
            Assert.Same(HostCatalog.EmbeddedEnglish, HostCatalog.Load(root, code, version));
        }

        // ------------------------------------------------------------------ which language

        /// <summary>
        /// The host and the people on their server are not always reading the same language,
        /// so "same" is what makes the second preference optional rather than a second thing
        /// to keep in step. A spelling nothing answers to falls back to the interface's
        /// language, because the alternative is a server that says nothing at all.
        /// </summary>
        [Theory]
        [InlineData("ru", "same", "ru")]
        [InlineData("ru", null, "ru")]
        [InlineData("ru", "", "ru")]
        [InlineData("ru", "en", "en")]
        [InlineData("en", "ja", "ja")]
        [InlineData("ru", "klingon", "ru")]
        [InlineData(null, "same", "en")]
        [InlineData("zh-hans", "same", "zh-Hans")]
        public void The_players_language_follows_the_interface_unless_it_was_told_otherwise(
            string interfaceLanguage, string playerMessages, string expected)
        {
            Assert.Equal(expected, HostCatalog.EffectiveCode(interfaceLanguage, playerMessages));
        }
    }
}
