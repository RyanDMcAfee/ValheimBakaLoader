using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ValheimBakaLoader.Service.Bridge;

namespace ValheimBakaLoader.Service.Transport;

/// <summary>
/// GET /ws: the page's Native block over a WebSocket instead of WebView2's postMessage.
/// <para>
/// Each text frame in is one {id, method, params}; each answer goes back over the same
/// socket as {id, ok, result, error, errorId, errorParams}, the exact shape the Windows host
/// posts, so the page's pending map works unchanged. A push from the bridge goes to every
/// open socket as {event, data}. The win.* messages are the custom title bar's and mean
/// nothing without a window, so they are dropped on the floor here rather than answered.
/// </para>
/// <para>
/// The socket keeps itself alive with a protocol ping every <see cref="ServiceOptions.PingInterval"/>
/// and is closed once no pong has come back for <see cref="ServiceOptions.IdleTimeout"/>:
/// browsers answer pings on their own, so a tab that is merely quiet stays connected and
/// only a peer that has gone away is dropped.
/// </para>
/// </summary>
public sealed class WebSocketTransport : IDisposable
{
    // Bigger than any config body the page sends today, small enough that a hostile peer
    // cannot grow the process by holding a frame open.
    private const int MaxMessageBytes = 16 * 1024 * 1024;

    private readonly IPageBridge _bridge;
    private readonly CallSerializer _serializer;
    private readonly ServiceOptions _options;
    private readonly ILogger<WebSocketTransport> _log;
    private readonly ConcurrentDictionary<long, Connection> _open = new();
    private long _nextId;

    public WebSocketTransport(
        IPageBridge bridge,
        CallSerializer serializer,
        ServiceOptions options,
        ILogger<WebSocketTransport> log)
    {
        _bridge = bridge;
        _serializer = serializer;
        _options = options;
        _log = log;
        _bridge.Pushed += OnPushed;
    }

    /// <summary>How many pages are connected right now.</summary>
    public int OpenCount => _open.Count;

    public async Task ServeAsync(HttpContext context)
    {
        if (!OriginMatchesHost(context.Request))
        {
            // A page on another site cannot read the reply, but with a cookie it could still
            // send commands; the browser always puts the opener's Origin on an upgrade, so
            // a mismatch is somebody else's page and it is refused before the upgrade.
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsync("Origin does not match this host.");
            return;
        }

        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("Expected a WebSocket upgrade.");
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync(new WebSocketAcceptContext
        {
            KeepAliveInterval = _options.PingInterval,
            KeepAliveTimeout = _options.IdleTimeout,
        });

        var connection = new Connection(Interlocked.Increment(ref _nextId), socket);
        _open[connection.Id] = connection;
        _log.LogInformation("Page connected ({count} open)", _open.Count);

        var sending = connection.SendLoopAsync(context.RequestAborted);
        try
        {
            await ReceiveLoopAsync(connection, context.RequestAborted);
        }
        catch (OperationCanceledException) { /* the request ended */ }
        catch (WebSocketException ex)
        {
            _log.LogDebug("Page socket ended: {reason}", ex.Message);
        }
        finally
        {
            _open.TryRemove(connection.Id, out _);
            connection.Complete();
            try { await sending; } catch { /* the socket is gone either way */ }
            _log.LogInformation("Page disconnected ({count} open)", _open.Count);
        }
    }

    internal static bool OriginMatchesHost(HttpRequest request)
    {
        var origin = request.Headers.Origin.ToString();
        if (string.IsNullOrEmpty(origin)) return false;
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)) return false;

        var expectedScheme = request.IsHttps ? "https" : "http";
        if (!string.Equals(uri.Scheme, expectedScheme, StringComparison.OrdinalIgnoreCase)) return false;

        // Uri.Authority drops a default port the same way a browser's Host header does, so
        // "http://box:80" and a Host of "box" still agree.
        return string.Equals(uri.Authority, request.Host.Value, StringComparison.OrdinalIgnoreCase);
    }

    private async Task ReceiveLoopAsync(Connection connection, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        var message = new MemoryStream();
        var socket = connection.Socket;

        while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var frame = await socket.ReceiveAsync(buffer, ct);

            if (frame.MessageType == WebSocketMessageType.Close)
            {
                try { await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", ct); }
                catch { /* the peer may already be gone */ }
                return;
            }

            if (frame.MessageType != WebSocketMessageType.Text)
            {
                // The page never sends binary; whatever this is, it is not a call.
                if (frame.EndOfMessage) message.SetLength(0);
                continue;
            }

            if (message.Length + frame.Count > MaxMessageBytes)
            {
                await socket.CloseAsync(WebSocketCloseStatus.MessageTooBig, "message too big", ct);
                return;
            }

            message.Write(buffer, 0, frame.Count);
            if (!frame.EndOfMessage) continue;

            var text = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
            message.SetLength(0);
            HandleMessage(connection, text, ct);
        }
    }

    private void HandleMessage(Connection connection, string text, CancellationToken ct)
    {
        JObject message;
        try
        {
            message = JObject.Parse(text);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "The page sent an unparseable message");
            return;
        }

        var method = message.Value<string>("method");
        if (string.IsNullOrEmpty(method)) return;

        if (method.StartsWith("win.", StringComparison.Ordinal)) return;

        var id = message["id"];
        if (id == null || id.Type == JTokenType.Null)
        {
            _log.LogWarning("The page called {method} without an id - ignored", method);
            return;
        }

        var parameters = message["params"] as JObject ?? new JObject();

        // The receive loop must not wait for the call: the next frame on this socket may be
        // the one that cancels it. The answer finds its way back through the send channel.
        _ = DispatchAsync(connection, id, method, parameters, ct);
    }

    private async Task DispatchAsync(Connection connection, JToken id, string method, JObject parameters, CancellationToken ct)
    {
        BridgeReply reply;
        try
        {
            reply = await _serializer.RunAsync(() => _bridge.InvokeAsync(method, parameters, ct), ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Page RPC {method} failed", method);
            reply = BridgeReply.FromException(ex);
        }

        connection.Enqueue(ReplyJson(id, reply));
    }

    /// <summary>The reply envelope, property for property what BlendWindow.PostReply writes.</summary>
    internal static string ReplyJson(JToken id, BridgeReply reply)
    {
        var envelope = new JObject
        {
            ["id"] = id,
            ["ok"] = reply.Ok,
            ["result"] = ToToken(reply.Result),
            ["error"] = reply.Error == null ? JValue.CreateNull() : new JValue(reply.Error),
            ["errorId"] = reply.ErrorId == null ? JValue.CreateNull() : new JValue(reply.ErrorId),
            ["errorParams"] = ToToken(reply.ErrorParams),
        };
        return envelope.ToString(Formatting.None);
    }

    internal static string PushJson(BridgePush push)
    {
        var envelope = new JObject
        {
            ["event"] = push.Event,
            ["data"] = ToToken(push.Data),
        };
        return envelope.ToString(Formatting.None);
    }

    private static JToken ToToken(object? value) => value switch
    {
        null => JValue.CreateNull(),
        JToken token => token,
        _ => JToken.FromObject(value),
    };

    private void OnPushed(object? sender, BridgePush push)
    {
        if (_open.IsEmpty) return;

        string json;
        try
        {
            json = PushJson(push);
        }
        catch (Exception ex)
        {
            // A push that cannot be serialized must never take the host down, and it must
            // not stop the next push either.
            _log.LogWarning(ex, "A {event} push could not be serialized", push.Event);
            return;
        }

        foreach (var connection in _open.Values) connection.Enqueue(json);
    }

    public void Dispose()
    {
        _bridge.Pushed -= OnPushed;
    }

    /// <summary>
    /// One open page. Everything written to the socket goes through one channel and one
    /// writer, because a WebSocket refuses two sends at once and replies and pushes come
    /// from different threads.
    /// </summary>
    private sealed class Connection
    {
        private readonly Channel<string> _outbound = Channel.CreateUnbounded<string>(
            new UnboundedChannelOptions { SingleReader = true });

        public Connection(long id, WebSocket socket)
        {
            Id = id;
            Socket = socket;
        }

        public long Id { get; }
        public WebSocket Socket { get; }

        public void Enqueue(string json) => _outbound.Writer.TryWrite(json);

        public void Complete() => _outbound.Writer.TryComplete();

        public async Task SendLoopAsync(CancellationToken ct)
        {
            try
            {
                await foreach (var json in _outbound.Reader.ReadAllAsync(ct))
                {
                    if (Socket.State != WebSocketState.Open) return;
                    var bytes = Encoding.UTF8.GetBytes(json);
                    await Socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
                }
            }
            catch (OperationCanceledException) { }
            catch (WebSocketException) { }
            catch (ObjectDisposedException) { }
        }
    }
}
