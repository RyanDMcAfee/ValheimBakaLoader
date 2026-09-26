using Serilog;
using Serilog.Core;
using Serilog.Events;
using System;
using System.Collections.Generic;
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
        /// A Serilog logger that keeps what it was written and answers IsEnabled off a dial,
        /// which is the application logger's shape without its file sink.
        /// </summary>
        private sealed class Remembering : ILogger
        {
            public LoggingLevelSwitch Dial { get; } = new(LogEventLevel.Debug);

            public List<string> Lines { get; } = new();

            public void Write(LogEvent logEvent)
            {
                if (logEvent == null || !IsEnabled(logEvent.Level)) return;
                Lines.Add(logEvent.RenderMessage());
            }

            public bool IsEnabled(LogEventLevel level) => level >= Dial.MinimumLevel;
        }

        /// <summary>
        /// Troubleshooting, the FAQ and the Hearth page all tell a host that with Detailed log
        /// on, every web request BakaLoader makes writes one line into the application log.
        /// The connection test is the button the failed mod scan puts beside Try again, and
        /// the FAQ tells the host to turn Detailed log on before pressing it, so it is the
        /// last client in the app that may be silent.
        /// </summary>
        [Fact(Timeout = 120000)]
        public async Task The_connection_test_writes_a_request_line_like_every_other_client()
        {
            var stage = ConnectionDiagnostics.StageTimeout;
            var overall = ConnectionDiagnostics.OverallTimeout;

            try
            {
                // Nothing here opens a connection: the address cannot resolve, so each web
                // step fails on its own short clock. What is asserted is that it SAID so.
                ConnectionDiagnostics.StageTimeout = TimeSpan.FromSeconds(2);
                ConnectionDiagnostics.OverallTimeout = TimeSpan.FromSeconds(60);

                var logger = new Remembering();
                logger.Dial.MinimumLevel = LogEventLevel.Verbose;

                await ConnectionDiagnostics.RunAsync(
                    "https://a-host-that-cannot-resolve.invalid/listing.json", logger);

                var sent = logger.Lines
                    .Where(l => l.StartsWith(
                        "HTTP GET https://a-host-that-cannot-resolve.invalid/listing.json",
                        StringComparison.Ordinal))
                    .ToList();

                // The plain read and at least the first of the two the switches shape: "it
                // does not work" and "it does not work THIS way" are different answers and
                // the log has to carry both of them.
                Assert.True(sent.Count >= 2,
                    "the connection test wrote " + sent.Count + " request lines: "
                    + string.Join(" / ", logger.Lines));

                // Each one names where the bytes were going to go, which is the half of the
                // answer an exception message never carries.
                Assert.All(sent, line => Assert.Contains(" via ", line, StringComparison.Ordinal));

                // And the failure is written too, with the reason at the bottom of it.
                Assert.Contains(logger.Lines, l => l.StartsWith(
                    "HTTP FAILED https://a-host-that-cannot-resolve.invalid/listing.json",
                    StringComparison.Ordinal));

                // With the dial where every release has it, the same run says nothing at all.
                var quiet = new Remembering();
                await ConnectionDiagnostics.RunAsync(
                    "https://a-host-that-cannot-resolve.invalid/listing.json", quiet);
                Assert.Empty(quiet.Lines);
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
