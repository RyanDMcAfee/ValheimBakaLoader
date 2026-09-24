using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using ValheimBakaLoader.Tools.Http;

namespace ValheimBakaLoader.Tools
{
    /// <summary>One step of the connection test, and what it came to.</summary>
    public sealed class ConnectionStage
    {
        /// <summary>
        /// Which step this is, as a short stable name the page words from its own catalog:
        /// proxy, dns, tcp, tls, http, http_noproxy, http_ipv4.
        /// </summary>
        public string Stage { get; init; }

        public bool Ok { get; init; }

        /// <summary>
        /// What was found, as data rather than a sentence: addresses, a status code, a byte
        /// count, or the reason it did not work. Not translated, because it is not language.
        /// </summary>
        public string Detail { get; init; }

        public int Ms { get; init; }
    }

    /// <summary>The whole test.</summary>
    public sealed class ConnectionReport
    {
        public string Host { get; init; }

        public IReadOnlyList<ConnectionStage> Stages { get; init; }

        /// <summary>
        /// Which combination got through, as the id of the sentence that tells the host what
        /// to do: ok, noproxy, ipv4, both, or none.
        /// </summary>
        public string Verdict { get; init; }
    }

    /// <summary>
    /// Asks thunderstore.io the same questions BakaLoader asks it, one at a time, and says
    /// which of the two connection switches would have made the difference.
    /// <para>
    /// This exists because of a host whose curl reached the site and whose BakaLoader did not.
    /// Everything between those two is invisible from inside an exception message: the Windows
    /// proxy settings .NET honours and curl does not, an AAAA record that routes nowhere, a
    /// handshake a security tool sits in the middle of. Each one is its own step here, so the
    /// answer names the step rather than saying the site did not answer.
    /// </para>
    /// <para>
    /// Every step has its own clock and nothing here throws: a diagnosis that falls over is
    /// worse than no diagnosis, because the page it runs from is the one a stuck host is on.
    /// </para>
    /// </summary>
    public static class ConnectionDiagnostics
    {
        /// <summary>The longest any one step may take.</summary>
        public static TimeSpan StageTimeout { get; set; } = TimeSpan.FromSeconds(15);

        /// <summary>
        /// The longest the whole report may take, however many steps it ends up running.
        /// <para>
        /// The per-step clock alone is not a cap on the report: eight steps at fifteen seconds
        /// each is two minutes, and the host waiting on it is on a page they opened BECAUSE
        /// something was not answering, so every one of those steps is the slow kind. When this
        /// runs out the steps that finished are the report, and a report that names the step it
        /// stopped at is still a diagnosis.
        /// </para>
        /// </summary>
        public static TimeSpan OverallTimeout { get; set; } = TimeSpan.FromSeconds(60);

        /// <summary>
        /// Runs the whole test against Thunderstore's listing index. Never throws.
        /// </summary>
        public static async Task<ConnectionReport> RunAsync(string url = null)
        {
            url ??= ThunderstoreClient.ListingIndexUrl;

            var overall = Stopwatch.StartNew();
            bool OutOfTime() => overall.Elapsed >= OverallTimeout;

            var stages = new List<ConnectionStage>();
            Uri uri;
            try
            {
                uri = new Uri(url);
            }
            catch (Exception e)
            {
                stages.Add(new ConnectionStage { Stage = "proxy", Ok = false, Detail = e.Message });
                return new ConnectionReport { Host = url, Stages = stages, Verdict = "none" };
            }

            // Every step below is skipped once the overall clock is out, and a skipped step
            // says so rather than going missing: a host reading the report has to be able to
            // tell "this did not work" from "this was never tried".
            ConnectionStage Skipped(string stage) => new()
            {
                Stage = stage,
                Ok = false,
                Detail = "not tried: the test was already past its "
                         + ((int)OverallTimeout.TotalSeconds).ToString(CultureInfo.InvariantCulture)
                         + " second limit",
                Ms = 0,
            };

            stages.Add(Proxy(uri));
            stages.Add(OutOfTime() ? Skipped("dns") : await DnsAsync(uri.Host));
            stages.Add(OutOfTime()
                ? Skipped("tcp")
                : await TcpAsync(uri.Host, uri.Port == -1 ? 443 : uri.Port));
            stages.Add(OutOfTime()
                ? Skipped("tls")
                : await TlsAsync(uri.Host, uri.Port == -1 ? 443 : uri.Port));

            var plain = OutOfTime()
                ? Skipped("http")
                : await HttpAsync("http", url, HttpTransportOptions.Default);
            stages.Add(plain);

            // The same request again with each switch in turn, because "it does not work" and
            // "it does not work THIS way" are different answers and only the second one tells
            // a host which switch to reach for.
            var noProxy = OutOfTime()
                ? Skipped("http_noproxy")
                : await HttpAsync("http_noproxy", url, new HttpTransportOptions { BypassProxy = true });
            stages.Add(noProxy);

            var ipv4 = OutOfTime()
                ? Skipped("http_ipv4")
                : await HttpAsync("http_ipv4", url, new HttpTransportOptions { IPv4Only = true });
            stages.Add(ipv4);

            string verdict;
            if (plain.Ok) verdict = "ok";
            else if (noProxy.Ok) verdict = "noproxy";
            else if (ipv4.Ok) verdict = "ipv4";
            else
            {
                var both = OutOfTime()
                    ? Skipped("http_both")
                    : await HttpAsync("http_both", url,
                        new HttpTransportOptions { BypassProxy = true, IPv4Only = true });
                stages.Add(both);
                verdict = both.Ok ? "both" : "none";
            }

            return new ConnectionReport { Host = uri.Host, Stages = stages, Verdict = verdict };
        }

        private static ConnectionStage Proxy(Uri uri)
        {
            var watch = Stopwatch.StartNew();
            var resolved = HttpClientProvider.ProxyFor(uri.ToString());
            watch.Stop();

            return new ConnectionStage
            {
                Stage = "proxy",
                // A proxy being configured is not a failure; it is the fact the rest of the
                // report is read against.
                Ok = true,
                Detail = resolved,
                Ms = (int)watch.ElapsedMilliseconds,
            };
        }

        private static async Task<ConnectionStage> DnsAsync(string host)
        {
            var watch = Stopwatch.StartNew();
            var said = new List<string>();
            var any = false;

            foreach (var (family, label) in new[]
                     {
                         (AddressFamily.InterNetwork, "A"),
                         (AddressFamily.InterNetworkV6, "AAAA"),
                     })
            {
                try
                {
                    using var deadline = new CancellationTokenSource(StageTimeout);
                    var addresses = await Dns.GetHostAddressesAsync(host, family, deadline.Token);
                    if (addresses == null || addresses.Length == 0)
                    {
                        said.Add(label + " none");
                        continue;
                    }

                    any = true;
                    said.Add(label + " " + string.Join(", ", addresses.Take(4).Select(a => a.ToString())));
                }
                catch (Exception e)
                {
                    said.Add(label + " " + Short(e));
                }
            }

            watch.Stop();
            return new ConnectionStage
            {
                Stage = "dns",
                Ok = any,
                Detail = string.Join(" · ", said),
                Ms = (int)watch.ElapsedMilliseconds,
            };
        }

        private static async Task<ConnectionStage> TcpAsync(string host, int port)
        {
            var watch = Stopwatch.StartNew();
            try
            {
                using var deadline = new CancellationTokenSource(StageTimeout);
                using var socket = new TcpClient();
                await socket.ConnectAsync(host, port, deadline.Token);
                watch.Stop();
                return new ConnectionStage
                {
                    Stage = "tcp",
                    Ok = true,
                    Detail = "open",
                    Ms = (int)watch.ElapsedMilliseconds,
                };
            }
            catch (Exception e)
            {
                watch.Stop();
                return new ConnectionStage
                {
                    Stage = "tcp",
                    Ok = false,
                    Detail = Short(e),
                    Ms = (int)watch.ElapsedMilliseconds,
                };
            }
        }

        private static async Task<ConnectionStage> TlsAsync(string host, int port)
        {
            var watch = Stopwatch.StartNew();
            try
            {
                using var deadline = new CancellationTokenSource(StageTimeout);
                using var socket = new TcpClient();
                await socket.ConnectAsync(host, port, deadline.Token);
                await using var tls = new SslStream(socket.GetStream(), leaveInnerStreamOpen: false);
                await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = host,
                }, deadline.Token);
                watch.Stop();

                return new ConnectionStage
                {
                    Stage = "tls",
                    Ok = true,
                    Detail = tls.SslProtocol + ", certificate issued to " + IssuedTo(tls),
                    Ms = (int)watch.ElapsedMilliseconds,
                };
            }
            catch (Exception e)
            {
                watch.Stop();
                return new ConnectionStage
                {
                    Stage = "tls",
                    Ok = false,
                    Detail = Short(e),
                    Ms = (int)watch.ElapsedMilliseconds,
                };
            }
        }

        /// <summary>
        /// Who the certificate names. A security tool in the middle answers with its own name
        /// here, which is exactly the thing a host cannot see any other way.
        /// </summary>
        private static string IssuedTo(SslStream tls)
        {
            try
            {
                return tls.RemoteCertificate?.Subject ?? "(no certificate)";
            }
            catch
            {
                return "(the certificate could not be read)";
            }
        }

        private static async Task<ConnectionStage> HttpAsync(string stage, string url, HttpTransportOptions options)
        {
            var watch = Stopwatch.StartNew();
            using var handler = HttpClientProvider.NewHandler(options);
            using var client = new HttpClient(handler, disposeHandler: false)
            {
                Timeout = Timeout.InfiniteTimeSpan,
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("ValheimBakaLoader");

            try
            {
                using var deadline = new CancellationTokenSource(StageTimeout);
                using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                var body = await response.Content.ReadAsByteArrayAsync(deadline.Token);
                watch.Stop();

                return new ConnectionStage
                {
                    Stage = stage,
                    Ok = response.IsSuccessStatusCode,
                    Detail = "HTTP " + (int)response.StatusCode + ", " + body.Length + " bytes",
                    Ms = (int)watch.ElapsedMilliseconds,
                };
            }
            catch (Exception e)
            {
                watch.Stop();
                return new ConnectionStage
                {
                    Stage = stage,
                    Ok = false,
                    Detail = Short(e),
                    Ms = (int)watch.ElapsedMilliseconds,
                };
            }
        }

        /// <summary>
        /// The exception as one short phrase: its type and the message at the bottom of it,
        /// which is where the reason lives when the outer one says only that a request failed.
        /// </summary>
        private static string Short(Exception problem)
        {
            if (problem == null) return "no reason given";
            if (problem is OperationCanceledException) return "timed out";

            var innermost = problem;
            while (innermost.InnerException != null) innermost = innermost.InnerException;
            return innermost.GetType().Name + ": " + innermost.Message;
        }
    }
}
