using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using ValheimBakaLoader.Tools;
using ValheimBakaLoader.Tools.Http;
using Xunit;

namespace ValheimBakaLoader.Tests.Tools
{
    /// <summary>
    /// The two things the connection test owes a host who is already stuck.
    /// <para>
    /// It is opened BECAUSE something is not answering, so every step it runs is the slow kind.
    /// Eight steps at fifteen seconds each is two minutes of a page that says nothing, and the
    /// per-step clock is no cap on that at all. And what it prints goes into the log and into a
    /// bug report, so the proxy it names has to be a proxy, not a proxy with a host's user name
    /// and password sitting in it.
    /// </para>
    /// </summary>
    [Collection(WallClockCollection.Name)]
    public class ConnectionDiagnosticsBoundsTests : BaseTest
    {
        /// <summary>
        /// Nothing here opens a connection: the address is one that cannot resolve, so every
        /// stage fails on its own clock. What is asserted is the ceiling over the lot of them.
        /// </summary>
        [Fact(Timeout = 120000)]
        public async Task The_whole_report_is_over_inside_its_own_cap()
        {
            var stage = ConnectionDiagnostics.StageTimeout;
            var overall = ConnectionDiagnostics.OverallTimeout;

            try
            {
                // Each step would take a quarter of a minute, and the cap is already spent. A
                // per-step clock cannot express that: it is the whole report that has run out.
                ConnectionDiagnostics.StageTimeout = TimeSpan.FromSeconds(15);
                ConnectionDiagnostics.OverallTimeout = TimeSpan.Zero;

                var watch = Stopwatch.StartNew();
                var report = await ConnectionDiagnostics.RunAsync(
                    "https://a-host-that-cannot-resolve.invalid/listing.json");
                watch.Stop();

                Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10),
                    "the connection test ran for " + watch.Elapsed);

                // Every step is still in the report, and one that was not tried SAYS it was not
                // tried: "this failed" and "this was never tried" are different answers and a
                // host reading the report has to be able to tell them apart.
                Assert.Contains(report.Stages, s => s.Stage == "proxy");
                foreach (var name in new[] { "dns", "tcp", "tls", "http", "http_noproxy", "http_ipv4" })
                {
                    var found = report.Stages.SingleOrDefault(s => s.Stage == name);
                    Assert.True(found != null, name + " is missing from the report");
                    Assert.Contains("not tried", found.Detail, StringComparison.OrdinalIgnoreCase);
                }

                Assert.All(report.Stages, s => Assert.False(string.IsNullOrWhiteSpace(s.Detail)));
            }
            finally
            {
                ConnectionDiagnostics.StageTimeout = stage;
                ConnectionDiagnostics.OverallTimeout = overall;
            }
        }

        /// <summary>
        /// A configured proxy may carry a user name and a password in its address, and Uri
        /// prints both when it is asked for the whole of itself. This answer is logged and put
        /// on a page a host pastes into a bug report, so it names the host and the port and
        /// nothing else.
        /// </summary>
        [Fact]
        public void The_named_proxy_carries_no_user_name_and_no_password()
        {
            var previous = System.Net.Http.HttpClient.DefaultProxy;

            try
            {
                System.Net.Http.HttpClient.DefaultProxy = new System.Net.WebProxy(
                    new Uri("http://someone:hunter2@proxy.inside.example:3128"));

                var named = HttpClientProvider.ProxyFor("https://thunderstore.io/c/valheim/");

                Assert.DoesNotContain("someone", named, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("hunter2", named, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("@", named, StringComparison.Ordinal);

                // And it is still a useful answer: the thing a diagnosis is read against.
                Assert.Contains("proxy.inside.example", named, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("3128", named, StringComparison.Ordinal);
            }
            finally
            {
                System.Net.Http.HttpClient.DefaultProxy = previous;
            }
        }
    }
}
