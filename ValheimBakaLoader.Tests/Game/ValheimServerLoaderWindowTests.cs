using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
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
    /// The loader step inside a restart window, driven rather than read.
    /// <para>
    /// This was the bug: the BepInEx step was the first line of the mod-update hook, and that
    /// hook only runs when the restart was raised BECAUSE mod updates were pending. Mod auto
    /// update off, or nothing to update, meant the loader was never once looked at, restart
    /// after restart, with no sign of it anywhere. A gate on the source would have gone green
    /// on either shape, so these tests take a real server through a real restart with mod auto
    /// update off and ask what actually ran. The last one writes a real pack into a real
    /// folder through the real service.
    /// </para>
    /// <para>
    /// Nothing here touches a server install: the sandbox is a temporary folder, the process
    /// launch is the mock provider's, and every download is served from memory.
    /// </para>
    /// </summary>
    public class ValheimServerLoaderWindowTests : BaseTest, IDisposable
    {
        private readonly ValheimServer Server;
        private readonly string SandboxDir;
        private readonly IDisposable OwnRecords;

        public ValheimServerLoaderWindowTests()
        {
            OwnRecords = CompanionPluginStatus.BeginOwnRecords();
            Server = GetService<ValheimServer>();

            // The production relaunch waits half a second for the exiting process to let go of
            // its port. These tests drive the whole restart and then read what ran, so that
            // wait is dead time, and dead time on a loaded build machine is what turns a
            // passing test into a flaky one.
            Server.RelaunchDelay = _ => Task.CompletedTask;

            SandboxDir = Path.Combine(Path.GetTempPath(), "vbl-loaderwindow-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(SandboxDir, "saves"));
            File.WriteAllBytes(Path.Combine(SandboxDir, "valheim_server.exe"), Array.Empty<byte>());
        }

        public void Dispose()
        {
            try { Server.Dispose(); } catch { /* best effort */ }
            try { Directory.Delete(SandboxDir, true); } catch { /* best-effort temp cleanup */ }
            OwnRecords.Dispose();
            GC.SuppressFinalize(this);
        }

        // ------------------------------------------------- the window runs it, whatever else it does

        /// <summary>
        /// The whole of section G in one run: mod auto update off, nothing pending, a plain
        /// restart, and the loader step still runs. The mod step does not, which is the half
        /// that proves the loader step is no longer riding on it.
        /// </summary>
        [Fact]
        public async Task A_plain_restart_runs_the_loader_step_with_mod_auto_update_off()
        {
            var loader = 0;
            var mods = 0;

            // Mod auto update off is exactly this: the hook that counts pending updates
            // answers zero, which is what the bridge's own hook does when the preference is
            // off. Nothing then sets the flag the mod step is gated on.
            Server.GetPendingModUpdateCount = () => Task.FromResult(0);
            Server.ApplyModUpdates = () => { mods++; return Task.CompletedTask; };
            Server.ApplyLoaderUpdate = () => { loader++; return Task.CompletedTask; };

            await RunningServer();
            Server.Restart();
            await Relaunch();

            Assert.Equal(1, loader);
            Assert.Equal(0, mods);
        }

        /// <summary>
        /// A scheduled restart is the same resume step with another reason on it, and a crash
        /// relaunch is the same one again. Both are named here rather than assumed, because
        /// "every restart that has a window" is the rule and a reason that quietly skipped it
        /// would be the same bug back.
        /// </summary>
        [Theory]
        [InlineData(LaunchReasons.Scheduled)]
        [InlineData(LaunchReasons.Crash)]
        public async Task Every_restart_reason_runs_it(string reason)
        {
            var loader = 0;
            Server.ApplyLoaderUpdate = () => { loader++; return Task.CompletedTask; };

            await RunningServer();
            Server.Restart(reason: reason);
            await Relaunch();

            Assert.Equal(1, loader);
        }

        /// <summary>
        /// The countdown path, with the pending count answering zero. This is the path that
        /// sets the mod-update flag from that count, so it is the one where the old shape left
        /// the loader behind: zero pending mods meant no window work at all.
        /// </summary>
        [Fact]
        public async Task A_countdown_restart_with_nothing_pending_runs_it_too()
        {
            var loader = 0;
            var mods = 0;

            Server.GetPendingModUpdateCount = () => Task.FromResult(0);
            Server.ApplyModUpdates = () => { mods++; return Task.CompletedTask; };
            Server.ApplyLoaderUpdate = () => { loader++; return Task.CompletedTask; };

            await RunningServer();

            // No RCON in these options, so the countdown has nobody to announce to and
            // restarts immediately, which is the branch a host with RCON off always takes.
            await Server.RestartWithCountdown(applyModUpdates: true);
            await Relaunch();

            Assert.Equal(1, loader);
            Assert.Equal(0, mods);
        }

        /// <summary>
        /// The loader goes first. A newer core has to be in before the plugins that will load
        /// under it, or the restart in between runs new mods on the old framework.
        /// </summary>
        [Fact]
        public async Task The_loader_goes_in_before_the_mods()
        {
            var order = new List<string>();

            Server.GetPendingModUpdateCount = () => Task.FromResult(3);
            Server.ApplyModUpdates = () => { order.Add("mods"); return Task.CompletedTask; };
            Server.ApplyLoaderUpdate = () => { order.Add("loader"); return Task.CompletedTask; };

            await RunningServer();
            await Server.RestartWithCountdown(applyModUpdates: true);
            await Relaunch();

            Assert.Equal(new[] { "loader", "mods" }, order);
        }

        /// <summary>
        /// A loader step that falls over does not cost the host their world. The restart is
        /// what the players are waiting for, and a loader a pack behind is not worth holding
        /// it up; the row says what did not land.
        /// </summary>
        [Fact]
        public async Task A_loader_step_that_throws_still_lets_the_server_come_back()
        {
            Server.ApplyLoaderUpdate = () => throw new InvalidOperationException("Thunderstore did not answer");

            await RunningServer();
            Server.Restart();
            var second = await Relaunch();

            Assert.Equal(ServerStatus.Starting, Server.Status);
            Assert.Contains(@"-world ""Loader Window World""", second);
        }

        /// <summary>With no hook wired at all, a restart behaves exactly as it always did.</summary>
        [Fact]
        public async Task With_no_loader_hook_a_restart_is_unchanged()
        {
            await RunningServer();
            Server.Restart();
            var second = await Relaunch();

            Assert.Contains(@"-world ""Loader Window World""", second);
        }

        // ------------------------------------------- and the step really moves the loader

        /// <summary>
        /// The same window, with the real service on the end of the hook and a real folder
        /// under it: mod auto update off, no mod update pending, and BepInEx is a pack further
        /// on when the server comes back. Nothing about this test reads the source.
        /// </summary>
        [Fact]
        public async Task The_window_really_moves_bepinex_with_mod_auto_update_off()
        {
            var install = Path.Combine(SandboxDir, "common", "Valheim dedicated server");
            Directory.CreateDirectory(install);
            var exe = Path.Combine(install, "valheim_server.exe");
            File.WriteAllText(exe, "not really an executable");

            var core = Path.Combine(install, "BepInEx", "core");
            Directory.CreateDirectory(core);
            File.WriteAllText(Path.Combine(core, "BepInEx.dll"), "the core that was here");
            File.WriteAllText(Path.Combine(install, "winhttp.dll"), "the loader file that was here");

            var service = LoaderService();
            BepInExInstallResult result = null;

            Server.GetPendingModUpdateCount = () => Task.FromResult(0);
            Server.ApplyLoaderUpdate = async () =>
                result = await service.UpdateAsync(exe, Array.Empty<BepInExProfileInstall>(),
                    options: BepInExWriteOptions.Window);

            await RunningServer();
            Server.Restart();
            await Relaunch();

            Assert.NotNull(result);
            Assert.False(result.Skipped);
            Assert.True(result.Adopted);
            Assert.Equal("core 5.4.2350", File.ReadAllText(Path.Combine(core, "BepInEx.dll")));

            // And the note now names the pack, so the next window has a version to compare.
            Assert.Equal("5.4.2350", BepInExMarkerFile.Read(install)?.Version);
        }

        // ------------------------------------------------------------------------- plumbing

        /// <summary>
        /// The real BepInEx service with a recorded pack behind it. The archive is served from
        /// memory and the listing carries the size of those very bytes, because an unattended
        /// write is refused when neither Thunderstore answer says what the archive weighs.
        /// </summary>
        private static BepInExService LoaderService()
        {
            var bytes = Pack();

            var thunderstore = new Mock<IThunderstoreClient>();
            var listing = new ThunderstorePackage
            {
                Namespace = BepInExService.DefaultPackageOwner,
                Name = BepInExService.DefaultPackageName,
                Latest = new ThunderstorePackageVersion
                {
                    VersionNumber = "5.4.2350",
                    FileSize = bytes.LongLength,
                },
            };

            thunderstore
                .Setup(c => c.LookupLiveAsync(It.IsAny<string>(), It.IsAny<string>()))
                .ReturnsAsync(new ThunderstoreLiveLookup { Answered = true, Package = listing });
            thunderstore
                .Setup(c => c.GetLatestAsync(It.IsAny<string>(), It.IsAny<string>()))
                .ReturnsAsync(listing);

            var http = new RecordingHttpClientProvider(_ =>
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });

            return new BepInExService(thunderstore.Object, http,
                new InstallIsolationService(Mock.Of<IApplicationLogger>()), Mock.Of<IApplicationLogger>());
        }

        /// <summary>The pack's shape: one top level folder, the loader file and a core.</summary>
        private static byte[] Pack()
        {
            using var buffer = new MemoryStream();
            using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            {
                const string root = "BepInExPack_Valheim/";
                Write(zip, root + "winhttp.dll", "the doorstop proxy 5.4.2350");
                Write(zip, root + "doorstop_config.ini",
                    "[General]\nenabled = true\ntarget_assembly = BepInEx\\core\\BepInEx.Preloader.dll\n");
                Write(zip, root + ".doorstop_version", "4.4.0");
                Write(zip, root + "BepInEx/core/BepInEx.dll", "core 5.4.2350");
                Write(zip, root + "BepInEx/core/BepInEx.Preloader.dll", "preloader 5.4.2350");
                Write(zip, "manifest.json", "{\"name\":\"BepInExPack_Valheim\"}");
            }
            return buffer.ToArray();
        }

        private static void Write(ZipArchive zip, string path, string content)
        {
            var bytes = Encoding.UTF8.GetBytes(content);
            using var stream = zip.CreateEntry(path).Open();
            stream.Write(bytes, 0, bytes.Length);
        }

        /// <summary>
        /// Starts the server and takes it to Running the way the game does, through the log
        /// line the status rule watches for. Hands back the command line it launched with.
        /// </summary>
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
        /// Finishes the restart that is in flight: the old process exits (nothing kills it in
        /// a test, so the exit is raised here), the resume step runs, and the command line the
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

        /// <summary>
        /// Fires a process's Exited event without a process ever having run. The mock provider
        /// never starts (or kills) anything, so this is the only way to drive the exit the
        /// restart machinery waits on.
        /// </summary>
        private static void RaiseExited(Process process)
        {
            var onExited = typeof(Process).GetMethod(
                "OnExited", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(onExited);
            onExited.Invoke(process, null);
        }

        private ValheimServerOptions Options() => new()
        {
            Name = "Loader Window Server",
            WorldName = "Loader Window World",
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

        /// <summary>
        /// Waits for something a background step does. The ceiling is generous on purpose: a
        /// run that is going to pass gets here in milliseconds, so the only thing the deadline
        /// decides is how long a genuinely stalled build agent is given before it is called a
        /// failure.
        /// </summary>
        private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 30000)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(10);
            Assert.True(condition(), "The condition never came true.");
        }
    }
}
