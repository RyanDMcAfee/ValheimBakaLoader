using Newtonsoft.Json.Linq;

namespace ValheimBakaLoader.Service.Bridge;

/// <summary>
/// The seam between the page's transport and the game logic.
/// <para>
/// On Windows the page talks to BlendWindow through WebView2: a call is {id, method, params},
/// its answer is {id, ok, result, error, errorId, errorParams}, and the host pushes
/// {event, data} whenever something happens. This interface is that conversation with the
/// window taken out of it, so the same handlers can sit behind a WebSocket on a Linux box.
/// The transport owns the ids and the sockets; the bridge owns the methods.
/// </para>
/// <para>
/// One call runs at a time. The WinForms handlers were written under a UI thread that
/// serialized them for free, so the transport queues calls through a single reader and the
/// bridge may assume the previous call has finished before the next one starts.
/// </para>
/// </summary>
public interface IPageBridge
{
    /// <summary>
    /// Runs one method for the page. An unknown method answers <see cref="BridgeReply.UnknownMethod"/>;
    /// a method that throws may either answer <see cref="BridgeReply.FromException"/> itself or
    /// let the exception out, in which case the transport shapes the same reply.
    /// </summary>
    Task<BridgeReply> InvokeAsync(string method, JObject parameters, CancellationToken ct);

    /// <summary>Raised from any thread whenever the host has something to tell the page.</summary>
    event EventHandler<BridgePush>? Pushed;

    /// <summary>
    /// SIGTERM. Stops every running server the gentle way and waits for the saves, within the
    /// token's deadline; the process exits after this returns or the deadline passes.
    /// </summary>
    Task ShutDownAsync(CancellationToken ct);
}

/// <summary>One {event, data} message on its way to every open page.</summary>
public sealed record BridgePush(string Event, object? Data);

/// <summary>
/// An exception the page is meant to read, with a stable id beside the English sentence.
/// The Core library's HostFacingException carries exactly these two members; it implements
/// this once it is referenced, and until then a transport can still shape the reply
/// without knowing the concrete type.
/// </summary>
public interface IHostFacingError
{
    string MessageId { get; }
    IReadOnlyDictionary<string, object>? Params { get; }
}

/// <summary>
/// The answer to one call, in the shape the page's Native block already reads.
/// </summary>
public sealed class BridgeReply
{
    public bool Ok { get; }
    public object? Result { get; }
    public string? Error { get; }
    public string? ErrorId { get; }
    public object? ErrorParams { get; }

    private BridgeReply(bool ok, object? result, string? error, string? errorId, object? errorParams)
    {
        Ok = ok;
        Result = result;
        Error = error;
        ErrorId = errorId;
        ErrorParams = errorParams;
    }

    public static BridgeReply Succeeded(object? result) => new(true, result, null, null, null);

    public static BridgeReply Failed(string error, string? errorId = null, object? errorParams = null) =>
        new(false, null, error, errorId, errorParams);

    /// <summary>The sentence BlendWindow answers for a method it has no handler for, word for word.</summary>
    public static BridgeReply UnknownMethod(string method) => Failed($"Unknown method: {method}");

    /// <summary>
    /// The reply BlendWindow shapes for a handler that threw: the English message always, and
    /// the id and the named values beside it when the throw wrote them.
    /// </summary>
    public static BridgeReply FromException(Exception exception)
    {
        var facing = exception as IHostFacingError;
        var parameters = facing?.Params;
        return Failed(
            exception.Message,
            facing?.MessageId,
            parameters == null || parameters.Count == 0 ? null : parameters);
    }
}
