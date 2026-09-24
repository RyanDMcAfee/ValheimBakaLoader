using Newtonsoft.Json;
using Serilog;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ValheimBakaLoader.Tools.Http
{
    /// <summary>
    /// Hands out HttpClient instances. Exists as an interface purely so tests can
    /// substitute a fake transport for the whole app in one registration.
    /// </summary>
    public interface IHttpClientProvider
    {
        HttpClient CreateClient();
    }

    /// <summary>
    /// The two answers the Connection settings card can give about how this machine
    /// reaches the internet. Both are off by default, because a machine where the
    /// ordinary way works must not be pushed off it.
    /// </summary>
    public sealed class HttpTransportOptions
    {
        /// <summary>
        /// Do not use the Windows proxy. .NET honours the system proxy and WPAD out of
        /// the box, so a machine with a proxy entry nothing is listening on, or a WPAD
        /// lookup that never answers, stalls every request while curl on the same box
        /// goes straight out. Turning this on makes the handler go direct.
        /// </summary>
        public bool BypassProxy { get; init; }

        /// <summary>
        /// Connect over IPv4 only. An AAAA record that routes nowhere is answered by a
        /// connect that hangs rather than by a refusal, and the happy-eyeballs fallback
        /// is not something this transport does for us.
        /// </summary>
        public bool IPv4Only { get; init; }

        /// <summary>Both off: what the app has always done.</summary>
        public static readonly HttpTransportOptions Default = new();

        public bool SameAs(HttpTransportOptions other) =>
            other != null && other.BypassProxy == BypassProxy && other.IPv4Only == IPv4Only;

        public override string ToString() =>
            (BypassProxy ? "no-proxy" : "system-proxy") + ", " + (IPv4Only ? "ipv4-only" : "ipv4+ipv6");
    }

    /// <summary>
    /// Where the provider reads the two switches from. The app implements this over the
    /// user's preferences; a test hands one in directly. Read on every CreateClient, so a
    /// switch flipped in the window takes effect on the next request rather than on the
    /// next launch.
    /// </summary>
    public interface IHttpTransportSettings
    {
        HttpTransportOptions Current { get; }
    }

    /// <summary>
    /// The one place in the app an HttpClient is made. Every remote client in the tree
    /// (Thunderstore, GitHub self-update, the language packs, BepInEx, Hexium, Discord,
    /// the heartbeat and the IP lookup) asks this for its client, so the two connection
    /// switches are wired once here and hold for all of them.
    /// <para>
    /// The handler is built once per shape of the switches and shared, and the clients
    /// handed out do not dispose it. Callers wrap their client in a using, which is what
    /// makes a handler owned per client a socket leak rather than a saving.
    /// </para>
    /// </summary>
    public class HttpClientProvider : IHttpClientProvider, IDisposable
    {
        private readonly IHttpTransportSettings Settings;
        private readonly object Gate = new();
        private SocketsHttpHandler Handler;
        private HttpTransportOptions Built;

        public HttpClientProvider() : this(null)
        {
        }

        public HttpClientProvider(IHttpTransportSettings settings)
        {
            Settings = settings;
        }

        public HttpClient CreateClient() => new(CurrentHandler(), disposeHandler: false);

        /// <summary>What the switches say right now, never throwing at a caller.</summary>
        public HttpTransportOptions CurrentOptions()
        {
            try
            {
                return Settings?.Current ?? HttpTransportOptions.Default;
            }
            catch
            {
                // Preferences that cannot be read are not a reason to stop making requests.
                return HttpTransportOptions.Default;
            }
        }

        private HttpMessageHandler CurrentHandler()
        {
            var wanted = CurrentOptions();
            lock (Gate)
            {
                if (Handler != null && Built.SameAs(wanted)) return Handler;

                var replaced = Handler;
                Handler = NewHandler(wanted);
                Built = wanted;
                // Whatever was in flight on the old handler keeps its own reference to it,
                // so this only releases the pooled connections nothing is using.
                try { replaced?.Dispose(); } catch { /* a handler that will not close is not news */ }
                return Handler;
            }
        }

        /// <summary>
        /// A handler shaped by the two switches. Public because the shape is what the
        /// tests assert: there is no way to ask a live request which of these it took.
        /// </summary>
        public static SocketsHttpHandler NewHandler(HttpTransportOptions options)
        {
            options ??= HttpTransportOptions.Default;

            var handler = new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                // The stall this was written for is a connect that never completes, and the
                // default here is infinite.
                ConnectTimeout = TimeSpan.FromSeconds(15),
            };

            if (options.BypassProxy)
            {
                handler.UseProxy = false;
                handler.Proxy = null;
            }

            if (options.IPv4Only)
            {
                handler.ConnectCallback = ConnectOverIPv4Async;
            }

            return handler;
        }

        /// <summary>
        /// Resolves A records only and connects to one of them. AAAA records are not asked
        /// for at all, so a machine that has them and cannot route them never waits on one.
        /// </summary>
        private static async ValueTask<Stream> ConnectOverIPv4Async(
            SocketsHttpConnectionContext context, CancellationToken token)
        {
            var host = context.DnsEndPoint.Host;
            var port = context.DnsEndPoint.Port;

            IPAddress[] addresses;
            if (IPAddress.TryParse(host, out var literal))
            {
                if (literal.AddressFamily != AddressFamily.InterNetwork)
                    throw new SocketException((int)SocketError.AddressFamilyNotSupported);
                addresses = new[] { literal };
            }
            else
            {
                addresses = await Dns
                    .GetHostAddressesAsync(host, AddressFamily.InterNetwork, token)
                    .ConfigureAwait(false);
            }

            if (addresses == null || addresses.Length == 0)
                throw new SocketException((int)SocketError.HostNotFound);

            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true,
            };

            try
            {
                await socket.ConnectAsync(addresses, port, token).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        /// <summary>
        /// What the system proxy makes of an address, as a sentence: the address of the
        /// proxy that would be used, or "(direct)" when there is none. This is the half of
        /// a stalled request that no exception message carries.
        /// </summary>
        public static string ProxyFor(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return "(not an address)";

            // Bounded, because the machine this question exists for is one whose automatic
            // proxy detection is the thing that does not answer. Asking it without a clock
            // would put the stall back into the very line that is there to explain the stall.
            try
            {
                var asking = Task.Run(() =>
                {
                    var proxy = HttpClient.DefaultProxy?.GetProxy(uri);
                    return proxy == null ? "(direct)" : Named(proxy);
                });

                return asking.Wait(TimeSpan.FromSeconds(3))
                    ? asking.Result
                    : "(the proxy settings did not answer within three seconds, which is itself the problem)";
            }
            catch (Exception e)
            {
                var innermost = e;
                while (innermost.InnerException != null) innermost = innermost.InnerException;
                return "(the proxy could not be resolved: " + innermost.GetType().Name + ")";
            }
        }

        /// <summary>
        /// A proxy named by scheme, host and port, and by nothing else.
        /// <para>
        /// A configured proxy may carry a user name and a password in the address, and Uri
        /// prints both when it is asked for its whole self. This answer goes into the log and
        /// onto the connection page, so what it carries is what a host pastes into a bug
        /// report. The host and the port are the whole of what the diagnosis needs.
        /// </para>
        /// </summary>
        private static string Named(Uri proxy)
        {
            if (proxy == null) return "(direct)";

            try
            {
                var port = proxy.IsDefaultPort ? "" : ":" + proxy.Port.ToString(CultureInfo.InvariantCulture);
                return proxy.Scheme + "://" + proxy.Host + port;
            }
            catch
            {
                // A proxy address that will not come apart is still not one to print whole.
                return "(a proxy is configured)";
            }
        }

        public void Dispose()
        {
            lock (Gate)
            {
                try { Handler?.Dispose(); } catch { /* nothing to be done about it */ }
                Handler = null;
                Built = null;
            }

            GC.SuppressFinalize(this);
        }
    }

    /// <summary>The two dependencies every API client needs, bundled for DI.</summary>
    public interface IRestClientContext
    {
        ILogger Logger { get; }

        IHttpClientProvider HttpClientProvider { get; }
    }

    public class RestClientContext : IRestClientContext
    {
        public ILogger Logger { get; }

        public IHttpClientProvider HttpClientProvider { get; }

        public RestClientContext(ILogger logger, IHttpClientProvider httpClientProvider)
        {
            Logger = logger;
            HttpClientProvider = httpClientProvider;
        }
    }

    /// <summary>
    /// Base class for the app's small JSON API clients (GitHub, IP lookup, crash
    /// reporting). A subclass describes a call with Get/Post, decorates it with
    /// headers or callbacks, and finishes with one of the SendAsync overloads.
    /// Failures never throw from SendAsync: they're logged and surfaced as null
    /// (or as the non-success response itself, so callers can read the error body).
    /// </summary>
    public abstract class RestClient
    {
        protected RestClient(IRestClientContext context)
        {
            Context = context;
        }

        public IRestClientContext Context { get; }

        public ILogger Logger => Context.Logger;

        protected ApiCall Get(string url) => new(Context, HttpMethod.Get, url);

        protected ApiCall Post(string url, object body) => new(Context, HttpMethod.Post, url, body);
    }

    /// <summary>
    /// One in-flight JSON web request. Collects headers and typed success
    /// callbacks fluently, then performs the exchange when awaited.
    /// </summary>
    public class ApiCall
    {
        private readonly IRestClientContext _context;
        private readonly HttpMethod _method;
        private readonly string _url;
        private readonly object _body;
        private readonly Dictionary<string, string> _headers = new();
        private readonly List<Func<string, Task>> _onSuccess = new();

        internal ApiCall(IRestClientContext context, HttpMethod method, string url, object body = null)
        {
            _context = context;
            _method = method;
            _url = url;
            _body = body;
        }

        public ApiCall WithHeader(string name, string value)
        {
            _headers[name] = value;
            return this;
        }

        /// <summary>
        /// Registers a handler that receives the response body deserialized as
        /// <typeparamref name="T"/>. Handlers only run when the request succeeds,
        /// and a handler that throws is logged without affecting the others.
        /// </summary>
        public ApiCall WithCallback<T>(EventHandler<T> handler)
        {
            _onSuccess.Add(json =>
            {
                var parsed = JsonConvert.DeserializeObject<T>(json);
                handler?.Invoke(this, parsed);
                return Task.CompletedTask;
            });
            return this;
        }

        /// <summary>
        /// Sends the request. Returns the response (even on a non-success status,
        /// so callers can inspect the error body), or null when the request itself
        /// failed to complete.
        /// </summary>
        public async Task<HttpResponseMessage> SendAsync()
        {
            HttpResponseMessage response;
            try
            {
                var client = _context.HttpClientProvider.CreateClient();
                var request = new HttpRequestMessage(_method, _url);

                foreach (var (name, value) in _headers)
                {
                    request.Headers.TryAddWithoutValidation(name, value);
                }

                if (_body != null)
                {
                    var json = JsonConvert.SerializeObject(_body);
                    request.Content = new StringContent(json, Encoding.UTF8, "application/json");
                }

                response = await client.SendAsync(request);
            }
            catch (Exception e)
            {
                _context.Logger.Error(e, "Web request to {url} failed", _url);
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                _context.Logger.Error(
                    "Web request to {url} returned {status} ({reason})",
                    _url, (int)response.StatusCode, response.ReasonPhrase);
                return response;
            }

            if (_onSuccess.Count > 0)
            {
                var content = await response.Content.ReadAsStringAsync();
                foreach (var callback in _onSuccess)
                {
                    try
                    {
                        await callback(content);
                    }
                    catch (Exception e)
                    {
                        _context.Logger.Error(e, "Response callback for {url} threw", _url);
                    }
                }
            }

            return response;
        }

        /// <summary>
        /// Sends the request and deserializes a successful response body as
        /// <typeparamref name="TResponse"/>. Returns null on any failure.
        /// </summary>
        public async Task<TResponse> SendAsync<TResponse>() where TResponse : class
        {
            var response = await SendAsync();
            if (response == null || !response.IsSuccessStatusCode) return null;

            try
            {
                var content = await response.Content.ReadAsStringAsync();
                return JsonConvert.DeserializeObject<TResponse>(content);
            }
            catch (Exception e)
            {
                _context.Logger.Error(e, "Could not parse the response from {url}", _url);
                return null;
            }
        }
    }
}
