using Newtonsoft.Json.Linq;
using ValheimBakaLoader.Service.Bridge;

namespace ValheimBakaLoader.Service.Tests;

/// <summary>
/// A throw shaped like the Core library's HostFacingException: the English sentence, a
/// stable id and the named values, so the transport's reply can be held to the shape the
/// page reads without the Core library being referenced yet.
/// </summary>
public sealed class FakeHostFacingException : Exception, IHostFacingError
{
    public FakeHostFacingException(string messageId, string english, IReadOnlyDictionary<string, object>? parameters)
        : base(english)
    {
        MessageId = messageId;
        Params = parameters;
    }

    public string MessageId { get; }
    public IReadOnlyDictionary<string, object>? Params { get; }
}

/// <summary>
/// The bridge the tests talk to. A handful of methods with known behaviour, a counter that
/// catches two calls running at once, and a way to push an event into every socket.
/// </summary>
public sealed class FakeBridge : IPageBridge
{
    private int _inFlight;
    private int _maxInFlight;
    private int _calls;
    private readonly TaskCompletionSource _shutdownStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public event EventHandler<BridgePush>? Pushed;

    public int Calls => _calls;
    public int MaxInFlight => _maxInFlight;
    public bool ShutDownCalled { get; private set; }
    public bool HangOnShutdown { get; set; }
    public Task ShutdownStarted => _shutdownStarted.Task;

    public async Task<BridgeReply> InvokeAsync(string method, JObject parameters, CancellationToken ct)
    {
        Interlocked.Increment(ref _calls);
        var now = Interlocked.Increment(ref _inFlight);
        int seen;
        do
        {
            seen = _maxInFlight;
        } while (now > seen && Interlocked.CompareExchange(ref _maxInFlight, now, seen) != seen);

        try
        {
            switch (method)
            {
                case "echo":
                    return BridgeReply.Succeeded(parameters);

                case "slow":
                    await Task.Delay(60, ct);
                    return BridgeReply.Succeeded(new JObject { ["n"] = parameters["n"] });

                case "throws":
                    throw new FakeHostFacingException(
                        "fake.boom",
                        "The thing went wrong: x.",
                        new Dictionary<string, object> { ["name"] = "x" });

                case "throwsPlain":
                    throw new InvalidOperationException("plain failure");

                default:
                    return BridgeReply.UnknownMethod(method);
            }
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }

    public void Push(string eventName, object? data) => Pushed?.Invoke(this, new BridgePush(eventName, data));

    public async Task ShutDownAsync(CancellationToken ct)
    {
        ShutDownCalled = true;
        _shutdownStarted.TrySetResult();
        if (HangOnShutdown) await Task.Delay(Timeout.InfiniteTimeSpan, CancellationToken.None);
    }
}
