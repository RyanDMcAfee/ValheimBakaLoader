using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using ValheimBakaLoader.Tests.Tools;
using ValheimBakaLoader.Tools;
using Xunit;

namespace ValheimBakaLoader.Tests.Forms
{
    /// <summary>
    /// The reply envelope, and the ids that ride on it.
    /// <para>
    /// Every uncaught exception out of an RPC becomes a toast, because the page shows
    /// <c>ex.Message</c> exactly as it arrives. A sentence is not an identity, so nothing
    /// downstream could ever key off one: the only way to say a refusal in another
    /// language is for the refusal to name itself. That name is what these guard.
    /// </para>
    /// <para>
    /// The rule that makes this safe to land on its own is that it is additive in both
    /// directions. <c>error</c> still carries the English sentence, so a page that reads
    /// only <c>error</c> is unchanged; <c>errorId</c> is null for everything that did not
    /// name itself, so a framework failure reads exactly as it did before.
    /// </para>
    /// </summary>
    public class HostFacingMessageTests
    {
        private static string BlendWindow() => AppSourceTree.Files()["BlendWindow.cs"];

        private static string Bridge() => AppSourceTree.Files()["BlendWindow.Bridge.cs"];

        private static string AppJs() => AppSourceTree.Web("app.js");

        /// <summary>The English catalog, read the way the lookup reads it.</summary>
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

        // ------------------------------------------------------------------ A. the exception

        [Fact]
        public void An_id_and_the_english_sentence_travel_together()
        {
            var refused = new HostFacingException(
                "profiles.remove.serverRunning",
                "Stop the server 'Final Sunset' before removing its profile.",
                ("name", "Final Sunset"));

            Assert.Equal("profiles.remove.serverRunning", refused.MessageId);
            Assert.Equal("Stop the server 'Final Sunset' before removing its profile.", refused.Message);
            Assert.Equal("Final Sunset", refused.Params["name"]);
        }

        [Fact]
        public void A_sentence_with_nothing_to_interpolate_carries_an_empty_bag_not_a_null_one()
        {
            var refused = new HostFacingException("servers.create.nameRequired", "A server name is required.");

            Assert.NotNull(refused.Params);
            Assert.Empty(refused.Params);
        }

        [Fact]
        public void An_id_is_required_because_an_unnamed_one_would_be_worse_than_none()
        {
            Assert.Throws<ArgumentException>(() => new HostFacingException(null, "text"));
            Assert.Throws<ArgumentException>(() => new HostFacingException("   ", "text"));
        }

        /// <summary>
        /// The question the envelope asks of every exception it answers. An ordinary
        /// failure has no id and no values, which is exactly the shape the page reads as
        /// "show the English sentence".
        /// </summary>
        [Fact]
        public void An_ordinary_exception_has_no_id_and_no_values()
        {
            var ordinary = new IOException("The process cannot access the file.");

            Assert.Null(HostFacingException.IdOf(ordinary));
            Assert.Null(HostFacingException.ParamsOf(ordinary));
            Assert.Null(HostFacingException.IdOf(null));
        }

        [Fact]
        public void A_named_exception_answers_both_questions()
        {
            var named = new HostFacingException("mods.noSuchMod", "No installed mod named 'X'", ("fullName", "X"));

            Assert.Equal("mods.noSuchMod", HostFacingException.IdOf(named));
            Assert.Equal("X", HostFacingException.ParamsOf(named)["fullName"]);
        }

        /// <summary>A named exception with no values answers null rather than an empty object.</summary>
        [Fact]
        public void A_named_exception_with_nothing_to_interpolate_sends_no_values_at_all()
        {
            var named = new HostFacingException("atlas.render.alreadyRunning", "A map render is already in progress.");

            Assert.Equal("atlas.render.alreadyRunning", HostFacingException.IdOf(named));
            Assert.Null(HostFacingException.ParamsOf(named));
        }

        // ------------------------------------------------------------------ B. the envelope

        /// <summary>
        /// Three fields, in this order, and <c>error</c> first among them: the English
        /// sentence is what every existing reader takes, and it never moves.
        /// </summary>
        [Fact]
        public void The_reply_carries_the_sentence_and_the_id_and_the_values()
        {
            var source = BlendWindow();

            Assert.Contains("PostJson(new { id, ok, result, error, errorId, errorParams });", source);
            Assert.Contains("string errorId = null,", source);
            Assert.Contains("object errorParams = null)", source);
        }

        [Fact]
        public void A_failed_rpc_answers_with_whatever_the_throw_named_itself()
        {
            var source = BlendWindow();

            Assert.Contains("error: ex.Message,", source);
            Assert.Contains("errorId: HostFacingException.IdOf(ex),", source);
            Assert.Contains("errorParams: HostFacingException.ParamsOf(ex));", source);
        }

        /// <summary>
        /// The page hangs the id off the Error it rejects with, so the catch can reach it
        /// without anything that reads err.message having to change.
        /// </summary>
        [Fact]
        public void The_page_carries_the_id_onto_the_error_it_rejects_with()
        {
            var js = AppJs();

            Assert.Contains("err.errorId = m.errorId ?? null;", js);
            Assert.Contains("err.errorParams = m.errorParams ?? null;", js);
        }

        /// <summary>
        /// The hook the catalog lookup will read, and the sentence the host reads until the
        /// native side words its refusals by id. That sentence is a catalog entry now, and
        /// the method name and the reason travel as named slots rather than as three pieces
        /// concatenated in an order only English keeps.
        /// </summary>
        [Fact]
        public void The_rpc_catch_records_the_id_and_toasts_the_keyed_sentence()
        {
            var js = AppJs();
            var catalog = Catalog();

            Assert.Contains("window.BAKA_ERR_ID=err?.errorId||null;", js);
            Assert.Contains("window.BAKA_ERR_PARAMS=err?.errorParams||null;", js);
            Assert.Contains("T(\"common.rpc.failed.toast\",", js);
            Assert.Contains("{method,detail:err?.message||T(\"common.error.unknown\")}", js);
            Assert.Equal("{method} failed · {detail}", Lore(catalog, "common.rpc.failed.toast"));
            Assert.Equal("unknown error", Lore(catalog, "common.error.unknown"));
        }

        // ------------------------------------------------------------------ C. the ids themselves

        private static List<string> IdsInBridge() =>
            Regex.Matches(Bridge(), "HostFacingException\\(\"([^\"]+)\"")
                .Cast<Match>()
                .Select(m => m.Groups[1].Value)
                .ToList();

        /// <summary>
        /// The first wave: the throws a host actually reaches, across realms, worlds, mods,
        /// spawning and the map. The number is a floor, not a target, so converting more
        /// later never fails this.
        /// </summary>
        [Fact]
        public void The_host_visible_throws_name_themselves()
        {
            Assert.True(IdsInBridge().Count >= 20,
                "the first wave of host-facing throws is " + IdsInBridge().Count + ", which is fewer than 20");
        }

        /// <summary>
        /// An id is an identity. Two throws sharing one would make a catalog entry mean two
        /// different things, and the second would be mistranslated forever.
        /// </summary>
        [Fact]
        public void No_two_throws_share_an_id()
        {
            var duplicates = IdsInBridge()
                .GroupBy(id => id, StringComparer.Ordinal)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToList();

            Assert.True(duplicates.Count == 0, "these ids are used more than once: " + string.Join(", ", duplicates));
        }

        /// <summary>
        /// Dotted, lower camel within a segment, no spaces. The shape is what makes an id
        /// sortable, groupable by the RPC it belongs to, and safe as a JSON key.
        /// </summary>
        [Fact]
        public void Every_id_is_a_dotted_name()
        {
            foreach (var id in IdsInBridge())
            {
                Assert.True(Regex.IsMatch(id, "^[a-z][A-Za-z0-9]*(\\.[a-z][A-Za-z0-9]*){1,3}$"),
                    "'" + id + "' is not a dotted name");
            }
        }

        /// <summary>
        /// Every group the first wave was meant to cover is actually covered. A wave that
        /// quietly converted only the easy file would pass a count and fail its purpose.
        /// </summary>
        [Theory]
        [InlineData("profiles.")]
        [InlineData("servers.")]
        [InlineData("worlds.")]
        [InlineData("mods.")]
        [InlineData("atlas.render.")]
        [InlineData("players.spawn.")]
        public void The_first_wave_reaches_every_surface_it_was_meant_to(string prefix)
        {
            Assert.Contains(IdsInBridge(), id => id.StartsWith(prefix, StringComparison.Ordinal));
        }

        /// <summary>
        /// The English is unchanged by the conversion. Spot-checked on the sentences a host
        /// meets most, because "kept verbatim" is the promise that makes this additive.
        /// </summary>
        [Theory]
        [InlineData("profiles.delete.lastServer", "This is your only active server. Archive it instead of deleting the last one.")]
        [InlineData("servers.create.nameRequired", "A server name is required.")]
        [InlineData("worlds.delete.unknownSaveFolder", "Unknown save folder.")]
        [InlineData("mods.scan.alreadyRunning", "A mod scan is already in progress")]
        [InlineData("atlas.render.alreadyRunning", "A map render is already in progress.")]
        public void The_english_beside_an_id_is_the_sentence_that_was_always_there(string id, string english)
        {
            var bridge = Bridge();
            var at = bridge.IndexOf("HostFacingException(\"" + id + "\"", StringComparison.Ordinal);

            Assert.True(at > 0, "no throw carries the id " + id);
            Assert.Contains(english, bridge.Substring(at, Math.Min(400, bridge.Length - at)), StringComparison.Ordinal);
        }

        // ------------------------------------------------------------------ D. the refusal code

        /// <summary>
        /// A refusal the host worded itself now carries a reason beside the wording. The
        /// page already owns this exact sentence, so it is the one refusal that can be said
        /// in another language the day the catalog exists, with no further bridge change.
        /// </summary>
        [Fact]
        public void A_refused_start_says_why_in_a_code_as_well_as_in_a_sentence()
        {
            var bridge = Bridge();

            Assert.Contains("private static object RefusedRpc(string error, string reason = null) => new { ok = false, error, reason };", bridge);
            Assert.Equal(2, Regex.Matches(bridge, "RefusedRpc\\(ValheimServer\\.LaunchBlockedMessage, \"updateRunning\"\\)").Count);
            Assert.DoesNotContain("RefusedRpc(ValheimServer.LaunchBlockedMessage)", bridge);
        }

        /// <summary>
        /// The page reads error before reason, so adding the code changed nothing about
        /// what a host is shown today.
        /// </summary>
        [Fact]
        public void The_page_still_shows_the_sentence_ahead_of_the_code()
        {
            Assert.Contains("const why=String(r.error||r.reason||\"\").trim();", AppJs());
        }
    }
}
