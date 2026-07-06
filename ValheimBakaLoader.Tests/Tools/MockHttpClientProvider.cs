using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ValheimBakaLoader.Tools.Http;

namespace ValheimBakaLoader.Tests.Tools
{
    /// <summary>
    /// IHttpClientProvider fake whose clients answer every request in-memory
    /// with 501 Not Implemented; nothing ever touches the network.
    /// </summary>
    public class MockHttpClientProvider : IHttpClientProvider
    {
        public HttpClient CreateClient() => new(new StubHandler());

        private sealed class StubHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
                => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotImplemented)
                {
                    RequestMessage = request,
                });
        }
    }
}
