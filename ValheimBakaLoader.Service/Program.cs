using ValheimBakaLoader.Service;
using ValheimBakaLoader.Service.Bridge;
using ValheimBakaLoader.Service.Cli;
using ValheimBakaLoader.Service.Hosting;
using ValheimBakaLoader.Service.Web;

return await ServiceCli.RunAsync(
    args,
    Environment.GetEnvironmentVariable,
    Console.In,
    Console.Out,
    Console.Error,
    RunServerAsync);

static async Task<int> RunServerAsync(ServiceOptions options)
{
    // The game bridge is wired here once the Core library exists; until then the page
    // gets "Unknown method" for everything and the rest of the service can be tried.
    IPageBridge bridge = new UnwiredBridge();

    WebApplication app;
    try
    {
        app = ServiceApp.Build(options, bridge);
    }
    catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException)
    {
        Console.Error.WriteLine(ex.Message);
        return 1;
    }

    var stopping = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    app.Lifetime.ApplicationStopping.Register(() => stopping.TrySetResult());

    try
    {
        await app.StartAsync();
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "The service could not start");
        return 1;
    }

    ServiceApp.LogListening(options, app.Logger);
    ServiceApp.AnnounceSetupIfNeeded(app);
    app.Logger.LogInformation("BakaLoader service {version} ready; data in {dir}", options.Version, options.DataDir);

    // SIGTERM and SIGINT both land here through the console lifetime. The bridge gets its
    // deadline first, so the servers save; only then does the listener close.
    await stopping.Task;
    await ShutdownCoordinator.StopAsync(bridge, options.ShutdownTimeout, app.Logger);

    using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    await app.StopAsync(stopCts.Token);
    await app.DisposeAsync();
    return 0;
}
