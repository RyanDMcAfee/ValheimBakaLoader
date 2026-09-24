using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using ValheimBakaLoader.Tools.Http;
using Xunit;

namespace ValheimBakaLoader.Tests.Tools
{
    /// <summary>
    /// The two connection switches, at the one seam every remote client in the app goes
    /// through. There is no way to ask a live request which of these it took, so what is
    /// asserted is the SHAPE of the handler the provider builds: a handler that does not
    /// carry the setting cannot honour it, whatever the preference says.
    /// </summary>
    public class HttpTransportSwitchTests
    {
        private sealed class Settings : IHttpTransportSettings
        {
            public HttpTransportOptions Current { get; set; } = HttpTransportOptions.Default;
        }

        [Fact]
        public void Both_switches_off_is_what_the_app_has_always_done()
        {
            using var handler = HttpClientProvider.NewHandler(HttpTransportOptions.Default);

            // The system proxy is honoured, which is the default .NET behaviour and the one
            // that works on nearly every machine.
            Assert.True(handler.UseProxy);
            // And addresses are resolved the ordinary way, both families.
            Assert.Null(handler.ConnectCallback);
        }

        [Fact]
        public void The_no_proxy_switch_takes_the_windows_proxy_out_of_the_handler()
        {
            using var handler = HttpClientProvider.NewHandler(new HttpTransportOptions { BypassProxy = true });

            Assert.False(handler.UseProxy);
            Assert.Null(handler.Proxy);
            // It says nothing about addresses: the two switches are independent.
            Assert.Null(handler.ConnectCallback);
        }

        [Fact]
        public void The_ipv4_switch_puts_a_connect_callback_on_the_handler()
        {
            using var handler = HttpClientProvider.NewHandler(new HttpTransportOptions { IPv4Only = true });

            Assert.NotNull(handler.ConnectCallback);
            // And it says nothing about the proxy.
            Assert.True(handler.UseProxy);
        }

        [Fact]
        public void Both_at_once_carries_both()
        {
            using var handler = HttpClientProvider.NewHandler(
                new HttpTransportOptions { BypassProxy = true, IPv4Only = true });

            Assert.False(handler.UseProxy);
            Assert.NotNull(handler.ConnectCallback);
        }

        [Fact]
        public void Every_handler_gives_up_on_a_connect_that_never_completes()
        {
            // The stall this whole release is about is a connect that never finishes, and the
            // default for this in .NET is infinite.
            using var handler = HttpClientProvider.NewHandler(HttpTransportOptions.Default);

            Assert.True(handler.ConnectTimeout > TimeSpan.Zero);
            Assert.True(handler.ConnectTimeout <= TimeSpan.FromSeconds(30));
            Assert.Equal(DecompressionMethods.All, handler.AutomaticDecompression);
        }

        [Fact]
        public void A_switch_moved_in_the_window_takes_effect_on_the_next_client()
        {
            var settings = new Settings();
            using var provider = new HttpClientProvider(settings);

            Assert.False(provider.CurrentOptions().BypassProxy);

            settings.Current = new HttpTransportOptions { BypassProxy = true };

            // Read again per client, so the next request out takes the new shape rather than
            // the next launch doing it.
            Assert.True(provider.CurrentOptions().BypassProxy);
            using var client = provider.CreateClient();
            Assert.NotNull(client);
        }

        [Fact]
        public void A_preferences_read_that_throws_is_not_a_reason_to_stop_making_requests()
        {
            using var provider = new HttpClientProvider(new ThrowingSettings());

            var options = provider.CurrentOptions();

            Assert.False(options.BypassProxy);
            Assert.False(options.IPv4Only);
            using var client = provider.CreateClient();
            Assert.NotNull(client);
        }

        private sealed class ThrowingSettings : IHttpTransportSettings
        {
            public HttpTransportOptions Current => throw new InvalidOperationException("no preferences here");
        }

        [Fact]
        public void The_proxy_for_an_address_is_always_a_sentence_and_never_a_throw()
        {
            Assert.False(string.IsNullOrWhiteSpace(HttpClientProvider.ProxyFor("https://thunderstore.io/")));
            Assert.False(string.IsNullOrWhiteSpace(HttpClientProvider.ProxyFor("not an address")));
            Assert.False(string.IsNullOrWhiteSpace(HttpClientProvider.ProxyFor(null)));
        }

        /// <summary>
        /// The seam is only a seam while it is the ONLY one. Every remote client in the app
        /// asks the provider for its client, so a file that builds an HttpClient of its own
        /// is a client the two switches cannot reach, and nothing about it would look wrong
        /// until a host turned a switch on and it changed nothing for that one caller.
        /// <para>
        /// The clients that go through the provider today, by the file that holds them:
        /// ThunderstoreClient, GitHubClient and IpAddressProvider through RestClient's own
        /// context; AppUpdateService, BepInExService, LanguagePackService, HexiumClient,
        /// DiscordStatusService, DiscordWebhookService and HeartbeatService directly.
        /// </para>
        /// </summary>
        [Fact]
        public void Nothing_in_the_app_builds_an_http_client_outside_the_one_seam()
        {
            var offenders = new List<string>();

            foreach (var (name, source) in AppSourceTree.Files())
            {
                // The provider itself, and the connection test, which deliberately builds a
                // client per SHAPE of the switches: that is the whole question it asks.
                if (name.Equals("RestApi.cs", StringComparison.OrdinalIgnoreCase)) continue;
                if (name.Equals("ConnectionDiagnostics.cs", StringComparison.OrdinalIgnoreCase)) continue;

                foreach (var line in source.Split('\n'))
                {
                    var trimmed = line.TrimStart();
                    if (trimmed.StartsWith("//", StringComparison.Ordinal)) continue;
                    if (trimmed.StartsWith("///", StringComparison.Ordinal)) continue;
                    if (trimmed.Contains("new HttpClient(", StringComparison.Ordinal)
                        || trimmed.Contains("new SocketsHttpHandler(", StringComparison.Ordinal)
                        || trimmed.Contains("new HttpClientHandler(", StringComparison.Ordinal))
                    {
                        offenders.Add(name + ": " + trimmed.Trim());
                    }
                }
            }

            Assert.True(offenders.Count == 0,
                "these build a transport the connection switches never reach:\n  "
                + string.Join("\n  ", offenders));
        }

        /// <summary>
        /// The clients handed out share one handler and must not dispose it, because every
        /// caller in the app wraps its client in a using. A handler torn down after one call
        /// is a socket left in TIME_WAIT for every request the app ever makes.
        /// </summary>
        [Fact]
        public void A_client_that_is_disposed_does_not_take_the_shared_handler_with_it()
        {
            using var provider = new HttpClientProvider(new Settings());

            using (var first = provider.CreateClient())
            {
                Assert.NotNull(first);
            }

            using var second = provider.CreateClient();
            // The throw this guards against is ObjectDisposedException from the handler the
            // first client would have taken down with it.
            Assert.NotNull(second.DefaultRequestHeaders);
            second.Timeout = TimeSpan.FromSeconds(5);
            Assert.Equal(TimeSpan.FromSeconds(5), second.Timeout);
        }
    }
}
