using System.Threading.Channels;

namespace ValheimBakaLoader.Service.Transport;

/// <summary>
/// Runs bridge calls one after another, whoever sent them.
/// <para>
/// The WinForms handlers never ran two at once because the UI thread would not let them,
/// and plenty of them lean on that: a profile save racing a server start is the kind of
/// bug that passes every test. Two browser tabs on a service can send at the same instant,
/// so the ordering has to be explicit. One unbounded channel, one reader, and a call waits
/// its turn rather than the bridge growing locks.
/// </para>
/// </summary>
public sealed class CallSerializer : IAsyncDisposable
{
    private readonly Channel<Func<Task>> _queue = Channel.CreateUnbounded<Func<Task>>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    private readonly Task _loop;

    public CallSerializer()
    {
        _loop = Task.Run(ReadLoopAsync);
    }

    /// <summary>
    /// Queues one unit of work and answers with its result once every call ahead of it has
    /// finished. A cancelled token drops the work if it has not started yet.
    /// </summary>
    public Task<T> RunAsync<T>(Func<Task<T>> work, CancellationToken ct)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task Job()
        {
            if (ct.IsCancellationRequested)
            {
                done.TrySetCanceled(ct);
                return;
            }

            try
            {
                done.TrySetResult(await work().ConfigureAwait(false));
            }
            catch (Exception ex)
            {
                done.TrySetException(ex);
            }
        }

        if (!_queue.Writer.TryWrite(Job))
            done.TrySetException(new ObjectDisposedException(nameof(CallSerializer)));

        return done.Task;
    }

    private async Task ReadLoopAsync()
    {
        await foreach (var job in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            // Job never throws: every exception ends in its own completion source. Guarded
            // all the same, because a reader that dies takes every later call with it.
            try { await job().ConfigureAwait(false); }
            catch { /* reported on the call's own task */ }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        try { await _loop.ConfigureAwait(false); }
        catch { /* nothing left to run */ }
    }
}
