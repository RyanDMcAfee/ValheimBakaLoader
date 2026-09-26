using Newtonsoft.Json;
using Serilog;
using Serilog.Events;
using System;
using System.Collections.Generic;
using System.Diagnostics;
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
        private readonly ILogger Tracer;
        private readonly object Gate = new();
        private HttpMessageHandler Handler;
        private HttpTransportOptions Built;

        public HttpClientProvider() : this(null, null)
        {
        }

        public HttpClientProvider(IHttpTransportSettings settings) : this(settings, null)
        {
        }

        /// <summary>
        /// The same provider, with the logger the wire trace writes to. The app registers
        /// this shape; a test that does not care about the trace uses the shorter one and
        /// gets a handler with no trace on it at all.
        /// </summary>
        public HttpClientProvider(IHttpTransportSettings settings, ILogger tracer)
        {
            Settings = settings;
            Tracer = tracer;
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
                Handler = NewTracedHandler(wanted, Tracer);
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
        public static SocketsHttpHandler NewHandler(HttpTransportOptions options) =>
            NewHandler(options, null);

        /// <summary>
        /// The same handler, with the logger its connect callback writes to. Only the IPv4
        /// path says anything, and only at Verbose, so a Debug log looks exactly as it did.
        /// </summary>
        public static SocketsHttpHandler NewHandler(HttpTransportOptions options, ILogger tracer)
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
                handler.ConnectCallback = (context, token) =>
                    ConnectOverIPv4Async(context, token, tracer);
            }

            return handler;
        }

        /// <summary>
        /// The handler every client in the app is actually handed: the shaped
        /// <see cref="NewHandler(HttpTransportOptions, ILogger)"/> with the wire trace
        /// wrapped around it. With no logger, or with the log at Debug, the wrapper stands
        /// aside and the bytes take exactly the path they always did.
        /// </summary>
        public static HttpMessageHandler NewTracedHandler(HttpTransportOptions options, ILogger tracer)
        {
            options ??= HttpTransportOptions.Default;
            var inner = NewHandler(options, tracer);
            return tracer == null ? inner : new WireTraceHandler(inner, tracer, options);
        }

        /// <summary>
        /// Resolves A records only and connects to one of them. AAAA records are not asked
        /// for at all, so a machine that has them and cannot route them never waits on one.
        /// </summary>
        private static async ValueTask<Stream> ConnectOverIPv4Async(
            SocketsHttpConnectionContext context, CancellationToken token, ILogger tracer = null)
        {
            var host = context.DnsEndPoint.Host;
            var port = context.DnsEndPoint.Port;
            var clock = Stopwatch.StartNew();

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

                // What was actually dialled, which is the half of an IPv4-only stall that no
                // exception message carries. Verbose only, so the ordinary log is unchanged.
                if (WireTrace.Wanted(tracer))
                {
                    var landed = socket.RemoteEndPoint?.ToString() ?? host + ":" + port;
                    WireTrace.Say(tracer,
                        "HTTP connect " + landed + " in " + WireTrace.Count(clock.ElapsedMilliseconds) + " ms");
                }

                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception problem)
            {
                // A connect that did not land is worth a line at Debug: this is the switch a
                // host is told to turn on, and "it still does not work" needs a reason beside it.
                tracer?.Debug("{Trace:l}",
                    "HTTP connect failed for " + host + ":" + port + " after "
                    + WireTrace.Count(clock.ElapsedMilliseconds) + " ms: "
                    + problem.GetType().Name + ": " + WireTrace.Innermost(problem));
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

    /// <summary>
    /// The words the wire trace is written in, and the one question it asks before it writes
    /// any of them.
    /// <para>
    /// Everything here is at Verbose and nothing here is anywhere else, which is the whole
    /// arrangement: with the log at its ordinary Debug level a host's file carries exactly
    /// what it carried before, and with Detailed log on it carries one line per request.
    /// </para>
    /// <para>
    /// An address is written as scheme, host and path and as nothing else, and the path is
    /// read for secrets before it is written. A query string carries search terms, tokens and
    /// package names a host did not choose to publish, and a webhook address carries the key
    /// to a channel inside the path; this text is what a host pastes into a bug report, so
    /// neither of them goes in.
    /// </para>
    /// </summary>
    public static class WireTrace
    {
        /// <summary>True when the log is open wide enough for a trace line to be worth building.</summary>
        public static bool Wanted(ILogger logger)
        {
            if (logger == null) return false;
            try { return logger.IsEnabled(LogEventLevel.Verbose); }
            catch { return false; }
        }

        /// <summary>
        /// One trace line. The sentence is passed as a VALUE with the literal format, so a
        /// path carrying braces is text rather than a template, and nothing comes out quoted.
        /// </summary>
        public static void Say(ILogger logger, string line)
        {
            if (logger == null || string.IsNullOrEmpty(line)) return;
            try { logger.Verbose("{Trace:l}", line); }
            catch { /* a line about a request must never be what fails the request */ }
        }

        /// <summary>
        /// Scheme, host and path. No query, no fragment, no user information, and no secret
        /// that lives in the path itself.
        /// <para>
        /// A Discord webhook carries its key as a path segment rather than in the query:
        /// https://discord.com/api/webhooks/{id}/{key}. Anyone holding that key can post to
        /// the channel, and this text is what a host attaches to a bug report, so dropping
        /// the query alone would not have been enough.
        /// </para>
        /// </summary>
        public static string Address(Uri uri)
        {
            if (uri == null) return "(no address)";
            try
            {
                return uri.IsAbsoluteUri
                    ? uri.Scheme + "://" + uri.Host + (uri.IsDefaultPort ? "" : ":" + uri.Port.ToString(CultureInfo.InvariantCulture)) + SafePath(uri.AbsolutePath)
                    : "(a relative address)";
            }
            catch { return "(an address that would not come apart)"; }
        }

        /// <summary>
        /// The path with any secret segment taken out of it. The one shape this app sends
        /// that holds a secret in its path is a webhook: the segment after "webhooks" is the
        /// channel's own number, and the one after that is the key that lets anybody post
        /// there. The key is the one that goes. Whatever follows it stays, because
        /// /messages/{id} is what tells an edit from a new post.
        /// </summary>
        public static string SafePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            if (path.IndexOf("webhooks", StringComparison.OrdinalIgnoreCase) < 0) return path;

            var parts = path.Split('/');
            for (var here = 0; here < parts.Length; here++)
            {
                if (!string.Equals(parts[here], "webhooks", StringComparison.OrdinalIgnoreCase)) continue;
                for (var secret = here + 2; secret < parts.Length; secret++)
                {
                    if (parts[secret].Length == 0) continue;
                    parts[secret] = "(the webhook key)";
                    return string.Join("/", parts);
                }
                break;
            }
            return path;
        }

        /// <summary>A whole number with thousands separators, the same in every locale.</summary>
        public static string Count(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

        /// <summary>The message at the bottom of an exception, which is where the reason usually is.</summary>
        public static string Innermost(Exception problem)
        {
            if (problem == null) return "no reason given";
            var innermost = problem;
            while (innermost.InnerException != null) innermost = innermost.InnerException;
            return innermost.Message;
        }
    }

    /// <summary>
    /// A line when a request goes out and a line when it comes back, at Verbose, wrapped
    /// around the one handler every remote client in the app sends through.
    /// <para>
    /// It exists for the host whose machine cannot reach Thunderstore while curl on the same
    /// box goes straight out. The log used to say the read failed and nothing else: not which
    /// address, not whether a proxy was in front of it, not how long it sat there. The pair
    /// of lines answers all of that, and they are only written when a host has turned
    /// Detailed log on, because on a busy install they arrive fast.
    /// </para>
    /// <para>
    /// Nothing from a header and nothing from a body ever reaches the log. The request may
    /// carry an Authorization header, and a Discord webhook keeps the key to a channel in its
    /// path rather than in its query, so the trace names the method, the address with its
    /// query dropped and its webhook key replaced, the proxy, the status, the size and the
    /// clock.
    /// </para>
    /// </summary>
    public sealed class WireTraceHandler : DelegatingHandler
    {
        private readonly ILogger Tracer;
        private readonly HttpTransportOptions Options;

        public WireTraceHandler(HttpMessageHandler inner, ILogger tracer, HttpTransportOptions options)
            : base(inner)
        {
            Tracer = tracer;
            Options = options ?? HttpTransportOptions.Default;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // Asked once, before anything is built. At Debug this handler costs one call.
            if (!WireTrace.Wanted(Tracer))
                return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

            var where = WireTrace.Address(request.RequestUri);
            var method = request.Method?.Method ?? "GET";

            WireTrace.Say(Tracer, "HTTP " + method + " " + where + " via " + Via(request.RequestUri)
                + (Options.IPv4Only ? ", IPv4 only" : ""));

            var clock = Stopwatch.StartNew();
            try
            {
                var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
                var length = Length(response);
                // "to headers", because that is what this clock measures and nothing else.
                // A handler hands its answer back the moment the status line and the headers
                // are in; the body is read afterwards, by HttpClient for a caller that asked
                // for the whole thing and by the caller itself for one that streams. The
                // full package listing is the read where the difference is minutes, and it
                // already writes its own start and finish lines with the total bytes and the
                // total milliseconds on them, so the honest word here is enough.
                WireTrace.Say(Tracer, "HTTP " + (int)response.StatusCode + " " + where + " "
                    + length + " " + WireTrace.Count(clock.ElapsedMilliseconds) + " ms to headers");
                return response;
            }
            catch (Exception problem)
            {
                WireTrace.Say(Tracer, "HTTP FAILED " + where + " after "
                    + WireTrace.Count(clock.ElapsedMilliseconds) + " ms: "
                    + problem.GetType().Name + ": " + WireTrace.Innermost(problem));
                throw;
            }
        }

        /// <summary>
        /// The proxy this request would go through, by scheme, host and port, or the word for
        /// going straight out. With the no-proxy switch on there is nothing to ask.
        /// </summary>
        private string Via(Uri uri)
        {
            if (Options.BypassProxy) return "direct (the Windows proxy is switched off)";
            if (uri == null || !uri.IsAbsoluteUri) return "direct";

            // Asked once per host and then remembered. The machine this whole trace exists
            // for is one whose automatic proxy detection is the thing that does not answer,
            // and asking it again on every request would put that wait into every line.
            var key = uri.Scheme + "://" + uri.Host;
            lock (Proxies)
            {
                if (Proxies.TryGetValue(key, out var held)) return held;
            }

            var answer = HttpClientProvider.ProxyFor(key + "/");
            if (string.Equals(answer, "(direct)", StringComparison.Ordinal)) answer = "direct";

            lock (Proxies)
            {
                Proxies[key] = answer;
            }
            return answer;
        }

        /// <summary>What the proxy settings said about a host, kept for the life of the handler.</summary>
        private readonly Dictionary<string, string> Proxies = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The body's size when the answer named one, and the truth when it did not.</summary>
        private static string Length(HttpResponseMessage response)
        {
            var declared = response?.Content?.Headers?.ContentLength;
            return declared.HasValue ? WireTrace.Count(declared.Value) + " bytes" : "size unknown";
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
