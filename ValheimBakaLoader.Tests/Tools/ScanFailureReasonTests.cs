using System;
using System.Globalization;
using ValheimBakaLoader.Forms;
using ValheimBakaLoader.Tools;
using Xunit;

namespace ValheimBakaLoader.Tests.Tools
{
    /// <summary>
    /// What the mods.scan reply says about a scan that came back with nothing: which sentence
    /// the panel is worded from, and the wait that goes under it.
    /// <para>
    /// WHY THIS EXISTS. The page knows two things about a failed scan: that nothing came
    /// back, and whether its own one-minute ceiling was what ended it. The host side knows
    /// what kind of failure it was and how long every caller is refused the trip for, and
    /// until 1.2.4 it threw all of that away. What it hands over now is DATA, never English,
    /// because the sentence it lands in is translated: a name out of a closed list and a
    /// moment in time.
    /// </para>
    /// </summary>
    public class ScanFailureReasonTests
    {
        private static readonly DateTime Noon = new(2026, 9, 25, 12, 0, 0, DateTimeKind.Utc);

        private static ThunderstoreFailureMemo Memo(string reason, TimeSpan backOff) =>
            new() { LastFailureUtc = Noon, Reason = reason, Streak = 1, BackOff = backOff };

        // ----------------------------------------------------------- which sentence it is

        /// <summary>
        /// A press that ran out of time waiting for another scan's turn never asked the site
        /// anything, and a site that ANSWERED with something that was not its package list
        /// was reached. Neither of those is "could not reach Thunderstore", and the answered
        /// one used to be framed as exactly that: "could not reach Thunderstore (the site
        /// answered with something that was not its package list)" contradicts itself inside
        /// one bracket.
        /// </summary>
        [Theory]
        [InlineData("busy", "mods.empty.failed.reason.busy")]
        [InlineData("answered", "mods.empty.failed.reason.answered")]
        [InlineData("timeout", "mods.empty.failed.reason.unreachable")]
        [InlineData("connect", "mods.empty.failed.reason.unreachable")]
        [InlineData("tls", "mods.empty.failed.reason.unreachable")]
        [InlineData("http", "mods.empty.failed.reason.unreachable")]
        [InlineData("read", "mods.empty.failed.reason.unreachable")]
        // A name this build has never heard of falls to the general frame rather than to
        // nothing at all, which is the floor every one of these sits on.
        [InlineData("something new", "mods.empty.failed.reason.unreachable")]
        [InlineData("", "mods.empty.failed.reason.unreachable")]
        public void The_kind_of_failure_picks_the_frame_the_panel_is_worded_from(string reason, string id)
        {
            Assert.Equal(id, BlendWindow.ScanFailureId(Memo(reason, TimeSpan.FromMinutes(15))));
        }

        /// <summary>No memo at all is still a sentence, and it is the general one.</summary>
        [Fact]
        public void No_memo_falls_to_the_general_frame()
        {
            Assert.Equal("mods.empty.failed.reason.unreachable", BlendWindow.ScanFailureId(null));
        }

        /// <summary>
        /// Every frame the host side can name is one the page keeps a sentence for. An id the
        /// page has no words for would be shown as the id, which tells a host nothing.
        /// </summary>
        [Fact]
        public void Every_frame_the_host_can_name_is_in_the_catalog_and_on_the_page()
        {
            var catalog = AppSourceTree.Read("ValheimBakaLoader", "WebUI", "i18n", "en.json");
            var page = AppSourceTree.Web("app.js");

            foreach (var reason in new[] { "busy", "answered", "timeout", null })
            {
                var id = BlendWindow.ScanFailureId(
                    reason == null ? null : Memo(reason, TimeSpan.FromMinutes(15)));

                Assert.Contains("\"" + id + "\"", catalog);
                Assert.Contains("\"" + id + "\"", page);
            }
        }

        // ------------------------------------------------------------------- and the wait

        /// <summary>
        /// The wait travels as the MOMENT it ends and not only as the seconds that were left
        /// when the reply was written. The failed panel is persistent: a host who leaves the
        /// Mods hall open is still reading the same sentence a quarter of an hour later, and
        /// a number worked out at reply time would by then be telling them to wait a quarter
        /// of an hour they have just spent.
        /// </summary>
        [Fact]
        public void The_wait_travels_as_the_moment_it_ends()
        {
            var memo = Memo("timeout", TimeSpan.FromMinutes(15));

            Assert.Equal("2026-09-25T12:15:00Z", BlendWindow.ScanRetryAtUtc(memo));

            // And it parses back to the moment the memo itself names.
            Assert.Equal(
                memo.RetryAtUtc,
                DateTime.Parse("2026-09-25T12:15:00Z", CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal));

            Assert.Null(BlendWindow.ScanRetryAtUtc(null));
        }

        /// <summary>
        /// The seconds a page from an older build reads, at the boundaries that matter: the
        /// whole wait, the moment it runs out, and every moment after that. A wait that has
        /// passed is nought and never a negative number, because the page words "none left"
        /// as a sentence of its own rather than as a quantity.
        /// </summary>
        [Fact]
        public void The_seconds_left_are_counted_from_now_and_never_go_below_nought()
        {
            var memo = Memo("timeout", TimeSpan.FromMinutes(15));

            Assert.Equal(900, BlendWindow.ScanRetrySeconds(memo, Noon));
            Assert.Equal(600, BlendWindow.ScanRetrySeconds(memo, Noon.AddMinutes(5)));
            Assert.Equal(60, BlendWindow.ScanRetrySeconds(memo, Noon.AddMinutes(14)));
            Assert.Equal(1, BlendWindow.ScanRetrySeconds(memo, Noon.AddSeconds(899)));

            // The moment it runs out, and long after it.
            Assert.Equal(0, BlendWindow.ScanRetrySeconds(memo, Noon.AddMinutes(15)));
            Assert.Equal(0, BlendWindow.ScanRetrySeconds(memo, Noon.AddHours(3)));

            Assert.Equal(0, BlendWindow.ScanRetrySeconds(null, Noon));
        }

        /// <summary>
        /// The shortest backoff step and the longest one both come out as a number of
        /// seconds, because the page owns the words for "a moment", "seconds" and "minutes"
        /// and picks between them with its own plural rules. Nothing here is ever a phrase.
        /// </summary>
        [Fact]
        public void The_wait_is_a_number_of_seconds_at_both_ends_of_the_backoff()
        {
            var first = Memo("timeout", ThunderstoreClient.BackoffSteps[0]);
            var last = Memo("timeout", ThunderstoreClient.BackoffSteps[^1]);

            Assert.Equal((int)ThunderstoreClient.BackoffSteps[0].TotalSeconds,
                BlendWindow.ScanRetrySeconds(first, Noon));
            Assert.Equal((int)ThunderstoreClient.BackoffSteps[^1].TotalSeconds,
                BlendWindow.ScanRetrySeconds(last, Noon));
        }
    }
}
