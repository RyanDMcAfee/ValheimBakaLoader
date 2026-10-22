using Newtonsoft.Json.Linq;

namespace ValheimBakaLoader.Service.Bridge;

/// <summary>
/// The bridge the service runs with until the Core library is split out and its handlers
/// are registered behind <see cref="IPageBridge"/>. It knows no method, so the page gets the
/// same "Unknown method" answer it would get from a host missing a handler, which its
/// error path already copes with; nothing crashes and the login, the transport and the
/// static files can be exercised on a real box in the meantime.
/// </summary>
public sealed class UnwiredBridge : IPageBridge
{
    public event EventHandler<BridgePush>? Pushed { add { } remove { } }

    public Task<BridgeReply> InvokeAsync(string method, JObject parameters, CancellationToken ct) =>
        Task.FromResult(BridgeReply.UnknownMethod(method));

    public Task ShutDownAsync(CancellationToken ct) => Task.CompletedTask;
}
