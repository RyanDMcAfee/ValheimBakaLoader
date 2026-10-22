using ValheimBakaLoader.Service.Bridge;

namespace ValheimBakaLoader.Service.Hosting;

/// <summary>
/// SIGTERM to the bridge, with a deadline. systemd gives the unit TimeoutStopSec and then
/// kills what is left, so the bridge gets a little less than that to stop every running
/// server and wait for the world saves; a bridge that ignores its token is left behind
/// rather than holding the process past the deadline.
/// </summary>
public static class ShutdownCoordinator
{
    /// <summary>True when the bridge finished in time.</summary>
    public static async Task<bool> StopAsync(IPageBridge bridge, TimeSpan deadline, ILogger log)
    {
        using var cts = new CancellationTokenSource(deadline);
        log.LogInformation("Stopping: asking the bridge to shut down, {seconds}s at most", (int)deadline.TotalSeconds);

        Task shutdown;
        try
        {
            shutdown = bridge.ShutDownAsync(cts.Token);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "The bridge failed to start shutting down");
            return false;
        }

        var finished = await Task.WhenAny(shutdown, Task.Delay(deadline));
        if (finished == shutdown && shutdown.IsCompletedSuccessfully)
        {
            log.LogInformation("The bridge shut down cleanly");
            return true;
        }

        if (shutdown.IsFaulted)
            log.LogError(shutdown.Exception?.GetBaseException(), "The bridge failed while shutting down");
        else
            log.LogWarning("The bridge did not finish shutting down within {seconds}s", (int)deadline.TotalSeconds);
        return false;
    }
}
