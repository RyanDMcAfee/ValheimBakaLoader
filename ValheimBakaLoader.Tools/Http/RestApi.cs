using Newtonsoft.Json;
using Serilog;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
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

    public class HttpClientProvider : IHttpClientProvider
    {
        public HttpClient CreateClient() => new();
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
