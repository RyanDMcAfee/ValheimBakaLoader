using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ValheimBakaLoader.Tools.Http;

namespace ValheimBakaLoader.Tests.Tools
{
    /// <summary>
    /// A transport that writes down every request it is handed and answers from a
    /// script instead of the network. Nothing in these tests reaches hexium.gg or
    /// thunderstore.io: the fixtures under Resources/hexium were cut from captures
    /// taken once, by hand, and the tests read those.
    /// <para>
    /// The recorded list is the proof for "with the switch off, nothing is contacted":
    /// an empty list is a stronger statement than any flag the code could set.
    /// </para>
    /// </summary>
    public sealed class RecordingHttpHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> Responder;

        public RecordingHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responder = null)
        {
            Responder = responder;
        }

        /// <summary>Every address asked for, in order.</summary>
        public List<string> Requests { get; } = new();

        /// <summary>Every host asked for, in order.</summary>
        public List<string> Hosts { get; } = new();

        public int Count => Requests.Count;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri?.ToString() ?? "");
            Hosts.Add(request.RequestUri?.Host ?? "");

            var response = Responder?.Invoke(request)
                ?? new HttpResponseMessage(HttpStatusCode.NotImplemented);

            response.RequestMessage ??= request;
            return Task.FromResult(response);
        }
    }

    /// <summary>
    /// Hands out clients over one shared <see cref="RecordingHttpHandler"/>. The handler
    /// is deliberately not disposed with the client, because the code under test wraps
    /// each client in a using and would otherwise tear the recorder down after one call.
    /// </summary>
    public sealed class RecordingHttpClientProvider : IHttpClientProvider
    {
        public RecordingHttpClientProvider(Func<HttpRequestMessage, HttpResponseMessage> responder = null)
        {
            Handler = new RecordingHttpHandler(responder);
        }

        public RecordingHttpHandler Handler { get; }

        public HttpClient CreateClient() => new(Handler, disposeHandler: false);
    }
}
