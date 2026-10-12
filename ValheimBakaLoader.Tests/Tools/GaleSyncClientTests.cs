using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ValheimBakaLoader.Tools;
using ValheimBakaLoader.Tools.Http;
using Xunit;

namespace ValheimBakaLoader.Tests.Tools
{
    /// <summary>
    /// The client for Gale's sync service, driven through a fake handler. Nothing here reaches
    /// gale.kesomannen.com: the 200 body below is the shape the spec and the service's own
    /// answers describe, and the 400 and 404 bodies are the service's own text, copied once.
    /// Every outcome has to come back as an answer; a throw at the Mods hall is the bug.
    /// </summary>
    public class GaleSyncClientTests
    {
        private const string MetaBody =
            "{\"id\":\"ABCDEF\",\"owner\":{\"id\":\"1\",\"name\":\"host\"},\"createdAt\":\"2026-10-01T00:00:00Z\","
            + "\"updatedAt\":\"2026-10-06T12:34:56.789123+00:00\","
            + "\"manifest\":{\"profileName\":\"Final Sunset\",\"mods\":["
            + "{\"name\":\"denikson-BepInExPack_Valheim\",\"enabled\":true,\"version\":{\"major\":5,\"minor\":4,\"patch\":2202}},"
            + "{\"name\":\"ValheimModding-Jotunn\",\"enabled\":false,\"version\":{\"major\":2,\"minor\":20,\"patch\":1}}"
            + "]}}";

        /// <summary>The fake handler behind the app's own seam, the way the app hands the client its transport.</summary>
        private static IHttpClientProvider Over(HttpMessageHandler handler) => new HandlerProvider(handler);

        private sealed class HandlerProvider : IHttpClientProvider
        {
            private readonly HttpMessageHandler Handler;
            public HandlerProvider(HttpMessageHandler handler) => Handler = handler;
            public HttpClient CreateClient() => new(Handler, disposeHandler: false);
        }

        private static HttpResponseMessage Answer(HttpStatusCode code, string body) =>
            new(code) { Content = new StringContent(body ?? "", Encoding.UTF8, "application/json") };

        [Fact]
        public async Task A_200_is_read_into_mods_and_the_stamp_as_written()
        {
            var handler = new RecordingHttpHandler(_ => Answer(HttpStatusCode.OK, MetaBody));
            var client = new GaleSyncClient(Over(handler));

            var result = await client.GetMetaAsync("ABCDEF");

            Assert.True(result.Ok);
            Assert.Equal(GaleCheckOutcome.Ok, result.Outcome);
            Assert.Equal(200, result.StatusCode);
            Assert.Null(result.SentenceId);

            // Not turned into a DateTime and back: the service's text, to the microsecond and the offset.
            Assert.Equal("2026-10-06T12:34:56.789123+00:00", result.Meta.UpdatedAt);

            Assert.Equal(2, result.Meta.Mods.Count);
            var first = result.Meta.Mods[0];
            Assert.Equal("denikson-BepInExPack_Valheim", first.Name);
            Assert.True(first.Enabled);
            Assert.Equal("5.4.2202", first.Version);
            Assert.False(result.Meta.Mods[1].Enabled);

            // One request, to the one address, and nowhere else.
            Assert.Equal(new[] { "https://gale.kesomannen.com/api/profile/ABCDEF/meta" }, handler.Requests);
            Assert.All(handler.Hosts, h => Assert.Equal(GaleSyncClient.Host, h));
        }

        [Fact]
        public async Task A_bad_id_is_refused_before_anything_is_sent()
        {
            var handler = new RecordingHttpHandler(_ => Answer(HttpStatusCode.OK, MetaBody));
            var client = new GaleSyncClient(Over(handler));

            foreach (var id in new[] { "ABCDE", "../../etc", "ABCDEF/../x", "", null, "AAAAAAAAAAAAAAAAAAAAAA==" })
            {
                var result = await client.GetMetaAsync(id);
                Assert.Equal(GaleCheckOutcome.InvalidId, result.Outcome);
                Assert.Equal("mods.gale.id.bad", result.SentenceId);
                Assert.Null(result.Meta);
            }

            Assert.Equal(0, handler.Count);
        }

        [Fact]
        public async Task A_400_is_a_bad_request_answer()
        {
            var handler = new RecordingHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("Invalid URL: Invalid profile id: x. Must be a base-64 encoded UUID or a 6-character alphanumeric string."),
            });

            var result = await new GaleSyncClient(Over(handler)).GetMetaAsync("AAAAAAAAAAAAAAAAAAAAAA");

            Assert.Equal(GaleCheckOutcome.BadRequest, result.Outcome);
            Assert.Equal(400, result.StatusCode);
            Assert.Equal("mods.gale.check.bad_request", result.SentenceId);
            Assert.Null(result.Meta);
        }

        [Fact]
        public async Task A_404_is_a_not_found_answer()
        {
            var handler = new RecordingHttpHandler(_ => Answer(HttpStatusCode.NotFound, "{\"message\":\"Not found.\"}"));

            var result = await new GaleSyncClient(Over(handler)).GetMetaAsync("ab12CD");

            Assert.Equal(GaleCheckOutcome.NotFound, result.Outcome);
            Assert.Equal(404, result.StatusCode);
            Assert.Equal("mods.gale.check.not_found", result.SentenceId);
        }

        [Theory]
        [InlineData(HttpStatusCode.InternalServerError)]
        [InlineData(HttpStatusCode.BadGateway)]
        [InlineData(HttpStatusCode.TooManyRequests)]
        [InlineData(HttpStatusCode.Unauthorized)]
        public async Task Any_other_status_is_a_server_error_answer(HttpStatusCode code)
        {
            var handler = new RecordingHttpHandler(_ => Answer(code, "nope"));

            var result = await new GaleSyncClient(Over(handler)).GetMetaAsync("ab12CD");

            Assert.Equal(GaleCheckOutcome.ServerError, result.Outcome);
            Assert.Equal((int)code, result.StatusCode);
            Assert.Equal("mods.gale.check.server_error", result.SentenceId);
        }

        [Theory]
        [InlineData("")]
        [InlineData("not json")]
        [InlineData("<html>Fly says hello</html>")]
        [InlineData("[]")]
        [InlineData("null")]
        [InlineData("{}")]
        [InlineData("{\"manifest\":{\"mods\":[]}}")]                                    // no updatedAt
        [InlineData("{\"manifest\":{\"mods\":[]},\"updatedAt\":12}")]                   // updatedAt not a string
        [InlineData("{\"manifest\":{\"mods\":[]},\"updatedAt\":\"  \"}")]
        [InlineData("{\"manifest\":{\"mods\":{}},\"updatedAt\":\"x\"}")]                // mods not a list
        [InlineData("{\"manifest\":[],\"updatedAt\":\"x\"}")]
        [InlineData("{\"manifest\":{\"mods\":[]},\"updatedAt\":\"x\"")]                 // cut short
        [InlineData("{\"manifest\":{\"mods\":[]},\"updatedAt\":\"x\"} {}")]             // two documents
        public async Task A_200_that_is_not_a_meta_is_a_malformed_answer(string body)
        {
            var handler = new RecordingHttpHandler(_ => Answer(HttpStatusCode.OK, body));

            var result = await new GaleSyncClient(Over(handler)).GetMetaAsync("ab12CD");

            Assert.Equal(GaleCheckOutcome.Malformed, result.Outcome);
            Assert.Equal("mods.gale.check.malformed", result.SentenceId);
            Assert.Null(result.Meta);
        }

        [Fact]
        public async Task An_empty_mods_list_is_a_meta_not_a_malformed_one()
        {
            var handler = new RecordingHttpHandler(_ => Answer(HttpStatusCode.OK, "{\"manifest\":{\"mods\":[]},\"updatedAt\":\"x\"}"));

            var result = await new GaleSyncClient(Over(handler)).GetMetaAsync("ab12CD");

            Assert.True(result.Ok);
            Assert.Empty(result.Meta.Mods);
            Assert.Equal("x", result.Meta.UpdatedAt);
        }

        [Fact]
        public async Task A_body_past_the_size_bound_is_malformed_and_is_not_read_to_the_end()
        {
            var big = new string(' ', GaleSyncClient.MaxBodyBytes + 10);
            var handler = new RecordingHttpHandler(_ => Answer(HttpStatusCode.OK, big));

            var result = await new GaleSyncClient(Over(handler)).GetMetaAsync("ab12CD");

            Assert.Equal(GaleCheckOutcome.Malformed, result.Outcome);
        }

        [Fact]
        public async Task No_connection_is_a_network_answer()
        {
            var handler = new RecordingHttpHandler(_ => throw new HttpRequestException("No such host is known."));

            var result = await new GaleSyncClient(Over(handler)).GetMetaAsync("ab12CD");

            Assert.Equal(GaleCheckOutcome.NetworkError, result.Outcome);
            Assert.Null(result.StatusCode);
            Assert.Equal("mods.gale.check.network", result.SentenceId);
        }

        [Fact]
        public async Task Something_nobody_planned_for_is_still_an_answer()
        {
            var handler = new RecordingHttpHandler(_ => throw new InvalidOperationException("surprise"));

            var result = await new GaleSyncClient(Over(handler)).GetMetaAsync("ab12CD");

            Assert.Equal(GaleCheckOutcome.NetworkError, result.Outcome);
        }

        [Fact]
        public async Task A_service_that_never_answers_is_a_timeout_inside_the_bound()
        {
            var handler = new SilentHandler();
            var client = new GaleSyncClient(Over(handler)) { Bound = TimeSpan.FromMilliseconds(300) };

            var clock = Stopwatch.StartNew();
            var result = await client.GetMetaAsync("ab12CD");
            clock.Stop();

            Assert.Equal(GaleCheckOutcome.Timeout, result.Outcome);
            Assert.Equal("mods.gale.check.timeout", result.SentenceId);
            Assert.Null(result.StatusCode);
            // The bound is 300 ms. A loaded CI runner has taken 21 s to fire a 300 ms cancellation timer
            // under the parallel suite, so the clock here only guards against a wait that never ends.
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(60), "took " + clock.Elapsed);
        }

        [Fact]
        public async Task Headers_that_arrive_with_a_body_that_never_ends_still_time_out()
        {
            // HttpClient.Timeout lets go the moment the headers land. This is the stall it misses.
            var handler = new RecordingHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new StallingStream(Encoding.UTF8.GetBytes("{\"manifest\":"))),
            });
            var client = new GaleSyncClient(Over(handler)) { Bound = TimeSpan.FromMilliseconds(300) };

            var clock = Stopwatch.StartNew();
            var result = await client.GetMetaAsync("ab12CD");
            clock.Stop();

            Assert.Equal(GaleCheckOutcome.Timeout, result.Outcome);
            Assert.Equal(200, result.StatusCode);
            // The bound is 300 ms. A loaded CI runner has taken 21 s to fire a 300 ms cancellation timer
            // under the parallel suite, so the clock here only guards against a wait that never ends.
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(60), "took " + clock.Elapsed);
        }

        [Fact]
        public void The_app_bound_is_ten_seconds()
        {
            Assert.Equal(TimeSpan.FromSeconds(10), new GaleSyncClient(Over(new RecordingHttpHandler())).Bound);
        }

        [Fact]
        public async Task A_caller_who_calls_it_off_gets_cancelled_not_timeout()
        {
            var client = new GaleSyncClient(Over(new SilentHandler())) { Bound = TimeSpan.FromSeconds(30) };
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

            var result = await client.GetMetaAsync("ab12CD", cts.Token);

            Assert.Equal(GaleCheckOutcome.Cancelled, result.Outcome);
            Assert.Equal("mods.gale.check.cancelled", result.SentenceId);
        }

        [Fact]
        public async Task The_app_shape_goes_through_the_shared_transport()
        {
            var provider = new RecordingHttpClientProvider(_ => Answer(HttpStatusCode.OK, MetaBody));
            var client = new GaleSyncClient(provider);

            var result = await client.GetMetaAsync("AAAAAAAAAAAA-_AAAAAAAA");

            Assert.True(result.Ok);
            Assert.Equal("https://gale.kesomannen.com/api/profile/AAAAAAAAAAAA-_AAAAAAAA/meta", Assert.Single(provider.Handler.Requests));
        }

        [Fact]
        public async Task The_200_feeds_the_diff_and_the_row_rule_end_to_end()
        {
            var handler = new RecordingHttpHandler(_ => Answer(HttpStatusCode.OK, MetaBody));
            var result = await new GaleSyncClient(Over(handler)).GetMetaAsync("ABCDEF");

            // The profile carries the pack (which the mod scan never lists) and a mod it has
            // switched off. Neither is anything to do, so no row, however far the stamp moved.
            var diff = GaleProfileDiff.Compare(result.Meta, new[] { ("ValheimModding-Jotunn", "2.19.0") }, null);

            Assert.Equal(GaleDiffKind.Framework, diff.Rows.Single(r => r.Name == "denikson-BepInExPack_Valheim").Kind);
            Assert.Equal(GaleDiffKind.Disabled, diff.Rows.Single(r => r.Name == "ValheimModding-Jotunn").Kind);
            Assert.False(diff.HasWork);
            Assert.False(GaleProfileDiff.IsDue(diff, null));
            Assert.False(GaleProfileDiff.IsDue(diff, "2026-10-01T00:00:00Z"));

            // A server without the mod and a profile that switches it on: one add, and due.
            var withAdd = MetaBody.Replace("\"enabled\":false", "\"enabled\":true");
            var handlerOn = new RecordingHttpHandler(_ => Answer(HttpStatusCode.OK, withAdd));
            var on = await new GaleSyncClient(Over(handlerOn)).GetMetaAsync("ABCDEF");
            var due = GaleProfileDiff.Compare(on.Meta, Array.Empty<(string, string)>(), null);

            Assert.Equal(1, due.AddCount);
            Assert.Equal(GaleDiffKind.Add, due.Rows.Single(r => r.Name == "ValheimModding-Jotunn").Kind);
            Assert.True(GaleProfileDiff.IsDue(due, null));
            // Dismissing stores the stamp as read, and the same answer is then not due.
            Assert.False(GaleProfileDiff.IsDue(due, on.Meta.UpdatedAt));
        }

        [Theory]
        [InlineData("\"community\":\"atlyss\",", "atlyss")]
        [InlineData("\"community\":\" Valheim \",", "Valheim")]
        [InlineData("\"community\":7,", null)]
        [InlineData("", null)]
        public void The_Ok_meta_keeps_the_community_as_written(string field, string expected)
        {
            var json = "{\"updatedAt\":\"2026-10-01T00:00:00Z\",\"manifest\":{" + field + "\"mods\":[]}}";
            Assert.Equal(expected, GaleSyncClient.Parse(json).Community);
        }

        [Theory]
        [InlineData("valheim", true)]
        [InlineData("VALHEIM", true)]
        [InlineData("", true)]
        [InlineData(null, true)]
        [InlineData("atlyss", false)]
        [InlineData("bonelab", false)]
        public void Only_Valheim_or_no_game_is_compared(string community, bool valheim)
        {
            var ok = new GaleCheckResult
            {
                Outcome = GaleCheckOutcome.Ok,
                StatusCode = 200,
                Meta = new GaleProfileMeta { UpdatedAt = "x", Community = community },
            };

            var answer = GaleProfileDiff.RefuseOtherGame(ok);

            Assert.Equal(valheim, GaleProfileDiff.IsValheim(community));
            if (valheim)
            {
                Assert.Same(ok, answer);
            }
            else
            {
                Assert.Equal(GaleCheckOutcome.OtherGame, answer.Outcome);
                Assert.Equal("mods.gale.check.other_game", answer.SentenceId);
                Assert.Equal(community, answer.Game);
                Assert.Null(answer.Meta);
                Assert.False(answer.Ok);
            }
        }

        /// <summary>Never answers; gives up only when the token says so.</summary>
        private sealed class SilentHandler : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
                throw new InvalidOperationException("unreachable");
            }
        }

        /// <summary>Hands over a few bytes, then waits for ever unless the read is cancelled.</summary>
        private sealed class StallingStream : Stream
        {
            private readonly byte[] Head;
            private bool Sent;

            public StallingStream(byte[] head) => Head = head;

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                if (!Sent)
                {
                    Sent = true;
                    Head.CopyTo(buffer);
                    return Head.Length;
                }

                await Task.Delay(Timeout.Infinite, cancellationToken);
                return 0;
            }

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
                ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
