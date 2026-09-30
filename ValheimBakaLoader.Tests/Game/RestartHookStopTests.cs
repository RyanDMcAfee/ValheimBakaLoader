using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using ValheimBakaLoader.Game;
using ValheimBakaLoader.Tests.Tools;
using ValheimBakaLoader.Tools;
using ValheimBakaLoader.Tools.Logging;
using ValheimBakaLoader.Tools.Models;
using Xunit;

namespace ValheimBakaLoader.Tests.Game
{
    /// <summary>
    /// What happens to a restart hook that runs past its clock, and what the relaunch does
    /// about it.
    /// <para>
    /// The clock on the two restart hooks stops the WAIT, which is what a host whose world is
    /// down needs. It does not stop the WORK: the hooks are handed no token, and the abandoned
    /// task carries on. Both hooks write into the install. The mod one walks its list one mod
    /// at a time and per mod it backs the folder up, CLEARS it and copies the new package in;
    /// the loader one replaces BepInEx\core. So the moment the clock ran out, the server was
    /// started over the very folders that work was still replacing, and Windows locks a DLL the
    /// instant the server loads it: a clear half-succeeds and the mod is left broken. Before
    /// the clock the world stayed down. That was bad. A corrupted install is worse.
    /// </para>
    /// <para>
    /// These drive the real resume step. Nothing here touches a server install: the sandbox is
    /// a temporary folder and the process launch is the mock provider's.
    /// </para>
    /// </summary>
    [Collection(WallClockCollection.Name)]
    public class RestartHookStopTests : BaseTest, IDisposable
    {
        private readonly ValheimServer Server;
        private readonly string SandboxDir;
        private readonly TimeSpan OriginalBudget = ValheimServer.RestartHookBudget;
        private readonly IDisposable OwnRecords;

        public RestartHookStopTests()
        {
            OwnRecords = CompanionPluginStatus.BeginOwnRecords();
            Server = GetService<ValheimServer>();

            // The production relaunch waits half a second for the exiting process to let go of
            // its port, and these tests read the ORDER of what ran, so that wait is dead time.
            Server.RelaunchDelay = _ => Task.CompletedTask;

            SandboxDir = Path.Combine(Path.GetTempPath(), "vbl-hookstop-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(SandboxDir, "saves"));
            File.WriteAllBytes(Path.Combine(SandboxDir, "valheim_server.exe"), Array.Empty<byte>());
        }

        public void Dispose()
        {
            ValheimServer.RestartHookBudget = OriginalBudget;
            try { Server.Dispose(); } catch { /* best effort */ }
            try { Directory.Delete(SandboxDir, true); } catch { /* best-effort temp cleanup */ }
            OwnRecords.Dispose();
            GC.SuppressFinalize(this);
        }

        // ------------------------------------------- the relaunch and the writer cannot overlap

        /// <summary>
        /// The one that was broken. The hook is still inside its write when the clock runs out,
        /// and the server must not be started until it has let go.
        /// </summary>
        [Fact(Timeout = 60000)]
        public async Task The_relaunch_waits_for_a_loader_hook_that_was_told_to_stop()
        {
            ValheimServer.RestartHookBudget = TimeSpan.FromMilliseconds(250);

            var stillWriting = true;

            Server.ApplyLoaderUpdate = async stop =>
            {
                // The stall: an address that answered its headers and then stopped sending.
                try { await Task.Delay(Timeout.Infinite, stop); }
                catch (OperationCanceledException) { /* the stop we were asked for */ }

                // And the tail nothing can interrupt: one folder still being refilled. Short,
                // because that is what it really is, and the relaunch has to outwait it.
                await Task.Delay(200, CancellationToken.None);
                stillWriting = false;
            };

            await RunningServer();
            Server.Restart();
            await Relaunch();

            Assert.False(stillWriting,
                "the server was started while the restart hook was still writing into the install");
        }

        /// <summary>
        /// The same for the mod hook, which is the one that clears plugin folders. It only runs
        /// when the restart was raised because mod updates were pending, so the flag is set the
        /// way the countdown path sets it.
        /// </summary>
        [Fact(Timeout = 60000)]
        public async Task The_relaunch_waits_for_a_mod_hook_that_was_told_to_stop()
        {
            ValheimServer.RestartHookBudget = TimeSpan.FromMilliseconds(250);

            var stillWriting = true;

            Server.GetPendingModUpdateCount = () => Task.FromResult(3);
            Server.ApplyModUpdates = async stop =>
            {
                try { await Task.Delay(Timeout.Infinite, stop); }
                catch (OperationCanceledException) { /* the stop we were asked for */ }

                await Task.Delay(200, CancellationToken.None);
                stillWriting = false;
            };

            await RunningServer();

            // No RCON in these options, so the countdown restarts at once, which is the branch
            // a host with RCON off always takes. This is the path that sets the mod-update flag.
            await Server.RestartWithCountdown(applyModUpdates: true);
            await Relaunch();

            Assert.False(stillWriting,
                "the server was started while the mod walk was still replacing plugin folders");
        }

        /// <summary>
        /// A hook that runs over is really told to stop, rather than only being stopped waiting
        /// for. A token nobody signals is a token nobody can honour, which is the shape that
        /// makes a fix like this a no-op.
        /// </summary>
        [Fact(Timeout = 60000)]
        public async Task A_hook_that_runs_over_is_told_to_stop()
        {
            ValheimServer.RestartHookBudget = TimeSpan.FromMilliseconds(250);

            var asked = false;

            Server.ApplyLoaderUpdate = async stop =>
            {
                try { await Task.Delay(Timeout.Infinite, stop); }
                catch (OperationCanceledException) { asked = stop.IsCancellationRequested; }
            };

            await RunningServer();
            Server.Restart();
            await Relaunch();

            Assert.True(asked, "the hook was abandoned rather than told to stop");
        }

        /// <summary>
        /// And a hook that answers inside its clock is not cancelled at all, so the ordinary
        /// restart window still does its whole job.
        /// </summary>
        [Fact(Timeout = 60000)]
        public async Task A_hook_that_finishes_in_time_is_never_told_to_stop()
        {
            ValheimServer.RestartHookBudget = TimeSpan.FromSeconds(30);

            var ran = 0;
            var toldToStop = true;

            Server.ApplyLoaderUpdate = stop =>
            {
                ran++;
                toldToStop = stop.IsCancellationRequested;
                return Task.CompletedTask;
            };

            await RunningServer();
            Server.Restart();
            var second = await Relaunch();

            Assert.Equal(1, ran);
            Assert.False(toldToStop);
            Assert.Contains(@"-world ""Hook Stop World""", second);
        }


        // ------------------------------------------------ and the walk really honours the stop

        /// <summary>
        /// A token nobody reads is a fix that does nothing. The mod walk is the writer the
        /// relaunch waits for, so it has to stop where nothing is half written: the mod being
        /// worked on is left as it was, and the ones after it are not even asked about.
        /// </summary>
        [Fact(Timeout = 60000)]
        public async Task The_mod_walk_stops_where_nothing_is_half_written()
        {
            var plugins = Path.Combine(SandboxDir, "BepInEx", "plugins");
            Directory.CreateDirectory(plugins);

            var alpha = WriteMod(plugins, "Alpha", "1.0.0");
            var beta = WriteMod(plugins, "Beta", "1.0.0");
            var gamma = WriteMod(plugins, "Gamma", "1.0.0");

            var mods = new ModScanner(Mock.Of<IApplicationLogger>()).ScanPlugins(plugins)
                .OrderBy(m => m.ModName, StringComparer.Ordinal).ToList();
            Assert.Equal(3, mods.Count);

            using var stop = new CancellationTokenSource();

            var site = new Mock<IThunderstoreClient>();
            foreach (var name in new[] { "Alpha", "Beta", "Gamma" })
            {
                var listing = Listing(name);
                var mod = name;

                site.Setup(c => c.LookupLiveAsync("Author", mod)).Returns(() =>
                {
                    // The clock runs out while the second mod is being looked up, which is the
                    // ordinary shape of it: one slow address and a budget that ran out.
                    if (mod == "Beta") stop.Cancel();
                    return Task.FromResult(new ThunderstoreLiveLookup { Answered = true, Package = listing });
                });

                site.Setup(c => c.GetLatestAsync("Author", mod)).ReturnsAsync(listing);
            }

            var http = new RecordingHttpClientProvider(request =>
            {
                var url = request.RequestUri?.ToString() ?? "";
                var name = url.Contains("/Alpha/") ? "Alpha" : url.Contains("/Beta/") ? "Beta" : "Gamma";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(ModZip(name, "2.0.0")),
                };
            });

            var results = await new ModUpdateService(site.Object, http, Mock.Of<IApplicationLogger>())
                .UpdateModsAsync(mods, null, stop.Token);

            // Alpha was already in place when the clock ran out, and it stays in place.
            Assert.Equal("Alpha 2.0.0", File.ReadAllText(Path.Combine(alpha, "Alpha.dll")));

            // Beta was the one in hand: nothing of it was written.
            Assert.Equal("Beta 1.0.0", File.ReadAllText(Path.Combine(beta, "Beta.dll")));

            // And Gamma was never reached at all, so the site was not even asked about it.
            Assert.Equal("Gamma 1.0.0", File.ReadAllText(Path.Combine(gamma, "Gamma.dll")));
            site.Verify(c => c.LookupLiveAsync("Author", "Gamma"), Times.Never);

            Assert.Equal(2, results.Count);
            Assert.True(results[0].Updated);
            Assert.False(results[1].Updated);
        }

        /// <summary>What the site says about one of these mods: a 2.0.0 of the weight it is.</summary>
        private static ThunderstorePackage Listing(string name) => new()
        {
            Namespace = "Author",
            Name = name,
            Latest = new ThunderstorePackageVersion
            {
                VersionNumber = "2.0.0",
                FileSize = ModZip(name, "2.0.0").LongLength,
            },
        };

        /// <summary>A walk told to stop before it began writes nothing and asks nothing.</summary>
        [Fact(Timeout = 60000)]
        public async Task A_walk_told_to_stop_up_front_touches_nothing()
        {
            var plugins = Path.Combine(SandboxDir, "BepInEx", "plugins");
            Directory.CreateDirectory(plugins);
            var dir = WriteMod(plugins, "Alpha", "1.0.0");

            var mods = new ModScanner(Mock.Of<IApplicationLogger>()).ScanPlugins(plugins).ToList();

            var site = new Mock<IThunderstoreClient>(MockBehavior.Strict);
            var http = new RecordingHttpClientProvider(
                _ => new HttpResponseMessage(HttpStatusCode.NotFound));

            using var stop = new CancellationTokenSource();
            stop.Cancel();

            var results = await new ModUpdateService(site.Object, http, Mock.Of<IApplicationLogger>())
                .UpdateModsAsync(mods, null, stop.Token);

            Assert.Empty(results);
            Assert.Empty(http.Handler.Requests);
            Assert.Equal("Alpha 1.0.0", File.ReadAllText(Path.Combine(dir, "Alpha.dll")));
        }

        /// <summary>One mod folder with a plausible manifest and one file to check afterwards.</summary>
        private static string WriteMod(string plugins, string name, string version)
        {
            var dir = Path.Combine(plugins, "Author-" + name);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "manifest.json"),
                "{\"name\":\"" + name + "\",\"version_number\":\"" + version + "\"}");
            File.WriteAllText(Path.Combine(dir, name + ".dll"), name + " " + version);
            return dir;
        }

        /// <summary>The package a mod update lands: the manifest and the one file.</summary>
        private static byte[] ModZip(string name, string version)
        {
            using var buffer = new MemoryStream();
            using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            {
                using (var manifest = new StreamWriter(zip.CreateEntry("manifest.json").Open()))
                    manifest.Write("{\"name\":\"" + name + "\",\"version_number\":\"" + version + "\"}");
                using var dll = new StreamWriter(zip.CreateEntry(name + ".dll").Open());
                dll.Write(name + " " + version);
            }

            return buffer.ToArray();
        }

        // ------------------------------------------------------------------------- plumbing

        private async Task<string> RunningServer()
        {
            Server.Start(Options());
            await WaitUntil(() => Server.GetTrackedProcess() != null);

            var args = Server.GetTrackedProcess().StartInfo.Arguments;
            Server.Logger.Information("Game server connected");
            await WaitUntil(() => Server.Status == ServerStatus.Running);
            return args;
        }

        /// <summary>
        /// Finishes the restart that is in flight: the old process exits (nothing kills it in a
        /// test, so the exit is raised here), the resume step runs, and the command line the
        /// relaunch built comes back.
        /// </summary>
        private async Task<string> Relaunch()
        {
            await WaitUntil(() => Server.Status == ServerStatus.Stopping);

            var old = Server.GetTrackedProcess();
            Assert.NotNull(old);
            RaiseExited(old);

            await WaitUntil(() => Server.Status == ServerStatus.Starting);
            var process = Server.GetTrackedProcess();
            Assert.NotNull(process);
            return process.StartInfo.Arguments;
        }

        private static void RaiseExited(Process process)
        {
            var onExited = typeof(Process).GetMethod(
                "OnExited", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(onExited);
            onExited.Invoke(process, null);
        }

        private ValheimServerOptions Options() => new()
        {
            Name = "Hook Stop Server",
            WorldName = "Hook Stop World",
            Password = "hunter2",
            Port = 2456,
            SaveInterval = 30,
            Backups = 1,
            BackupShort = 60,
            BackupLong = 120,
            LogToFile = false,
            ServerExePath = Path.Combine(SandboxDir, "valheim_server.exe"),
            SaveDataFolderPath = Path.Combine(SandboxDir, "saves"),
        };

        private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 30000)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(10);
            Assert.True(condition(), "The condition never came true.");
        }
    }
}
