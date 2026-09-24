using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Moq;
using ValheimBakaLoader.Tools;
using ValheimBakaLoader.Tools.Http;
using ValheimBakaLoader.Tools.Logging;
using ValheimBakaLoader.Tools.Models;
using Xunit;

namespace ValheimBakaLoader.Tests.Tools
{
    /// <summary>
    /// Issue 18, the half a host actually loses a server to: the loader resolve at a start.
    /// <para>
    /// A start runs PrepareBepInExForStart, which asks Thunderstore which pack to fetch. On a
    /// machine that cannot reach the site that question used to be two requests at two minutes
    /// each, inside the launch, so Start never came back and the host had no server. The
    /// resolve is on a clock now: when it runs out the start goes ahead with the loader that is
    /// already there, and the reason is recorded the way every other failed resolve is, through
    /// the bepinex.offline refusal the companion-plugin pass turns into a row on the condition
    /// bar.
    /// </para>
    /// <para>
    /// The clock is only for an UNATTENDED write, which is the restart window and the
    /// before-start step. A host who pressed Install is watching it work and keeps the whole of
    /// the client's own patience.
    /// </para>
    /// </summary>
    public class BepInExStartNotBlockedTests : IDisposable
    {
        private readonly string Root =
            Path.Combine(Path.GetTempPath(), "vbl-bepinex-start-" + Guid.NewGuid().ToString("N"));
        private readonly string BaseExe;

        public BepInExStartNotBlockedTests()
        {
            var baseDir = Path.Combine(Root, "common", "Valheim dedicated server");
            Directory.CreateDirectory(baseDir);
            BaseExe = Path.Combine(baseDir, "valheim_server.exe");
            File.WriteAllText(BaseExe, "not really an executable");
        }

        public void Dispose()
        {
            try { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); } catch { }
        }

        /// <summary>A client that accepts the question and never answers it.</summary>
        private static IThunderstoreClient NeverAnswers()
        {
            var client = new Mock<IThunderstoreClient>();
            client.Setup(c => c.LookupLiveAsync(It.IsAny<string>(), It.IsAny<string>()))
                .Returns(new TaskCompletionSource<ThunderstoreLiveLookup>().Task);
            client.Setup(c => c.GetLatestAsync(It.IsAny<string>(), It.IsAny<string>()))
                .Returns(new TaskCompletionSource<ThunderstorePackage>().Task);
            return client.Object;
        }

        private BepInExService Service(IThunderstoreClient thunderstore, TimeSpan budget)
        {
            var http = new RecordingHttpClientProvider(
                _ => new HttpResponseMessage(HttpStatusCode.NotImplemented));

            return new BepInExService(
                thunderstore, http, new InstallIsolationService(Mock.Of<IApplicationLogger>()),
                Mock.Of<IApplicationLogger>())
            {
                UnattendedResolveTimeout = budget,
            };
        }

        private static IEnumerable<BepInExProfileInstall> Nobody() => Array.Empty<BepInExProfileInstall>();

        [Fact(Timeout = 60000)]
        public async Task A_start_whose_loader_resolve_never_answers_gives_up_in_seconds()
        {
            var service = Service(NeverAnswers(), TimeSpan.FromMilliseconds(250));

            var watch = Stopwatch.StartNew();
            var refused = await Assert.ThrowsAsync<HostFacingException>(() =>
                service.InstallAsync(BaseExe, null, Nobody(), options: BepInExWriteOptions.Window));
            watch.Stop();

            // The existing refusal, which is what the companion-plugin pass around the start
            // records against the BepInEx entry before letting the launch carry on.
            Assert.Equal("bepinex.offline", refused.MessageId);

            // And it is over in a moment rather than in four minutes.
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10),
                "the resolve held the start for " + watch.Elapsed);

            // Nothing was written, so the loader that is there is what the server starts with.
            Assert.False(Directory.Exists(Path.Combine(Path.GetDirectoryName(BaseExe), "BepInEx")));
        }

        /// <summary>
        /// The other half, and it is asserted by watching a write rather than by reading back
        /// the number this test set itself. A MANUAL write over the same never-answering client
        /// is still going long after the unattended clock would have given up: nothing shortens
        /// a write a host pressed a button for, because there is nobody waiting on a server for
        /// it and the client's own deadlines are the right ones there.
        /// </summary>
        [Fact(Timeout = 60000)]
        public async Task A_manual_write_keeps_the_whole_of_the_clients_own_patience()
        {
            var service = Service(NeverAnswers(), TimeSpan.FromMilliseconds(200));

            var manual = service.InstallAsync(BaseExe, null, Nobody(), options: BepInExWriteOptions.Manual);

            // Twenty times the unattended budget, and it has not given up, because that budget
            // is not its budget.
            Assert.NotSame(manual, await Task.WhenAny(manual, Task.Delay(TimeSpan.FromSeconds(4))));

            // The unattended write over the very same client is over in a moment, which is what
            // makes the line above a difference in behaviour rather than a slow machine.
            var watch = Stopwatch.StartNew();
            await Assert.ThrowsAsync<HostFacingException>(() =>
                service.InstallAsync(BaseExe, null, Nobody(), options: BepInExWriteOptions.Window));
            watch.Stop();
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(4),
                "the unattended write took " + watch.Elapsed);

            Assert.True(BepInExWriteOptions.Window.Unattended);
            Assert.False(BepInExWriteOptions.Manual.Unattended);
        }

        // ------------------------------------------------------------------ the restart window

        /// <summary>
        /// The OTHER step with a server waiting on it, and until now the unbudgeted one: the
        /// restart window's loader step. ResumeAfterStopAsync awaits it before it relaunches and
        /// it reaches LatestVersionAsync, which took no budget and made two serial requests on
        /// the client's own deadlines. On a machine that cannot reach Thunderstore that held a
        /// host's server DOWN, on every restart, for as long as those deadlines allowed.
        /// <para>
        /// Both halves are driven here off one never-answering client. Without a budget the
        /// question is still unanswered seconds later, which is the 1.2.2 behaviour exactly,
        /// because a null budget IS the old path. With the window's clock it comes back with an
        /// unknown version, and an unknown version is already "skip the loader", so the plan
        /// says Skip and the relaunch carries on.
        /// </para>
        /// </summary>
        [Fact(Timeout = 60000)]
        public async Task The_windows_loader_step_is_bounded_and_the_relaunch_goes_on()
        {
            var service = Service(NeverAnswers(), TimeSpan.FromSeconds(10));

            // 1.2.2: no budget, and no end to it.
            var unbounded = service.LatestVersionAsync(BepInExService.DefaultPackage);
            Assert.NotSame(unbounded, await Task.WhenAny(unbounded, Task.Delay(TimeSpan.FromSeconds(2))));

            // The window, with the clock it now hands in, and the whole step rather than the
            // one request inside it.
            var watch = Stopwatch.StartNew();
            var plan = await BepInExUnattended.PlanAsync(
                BepInExConsent.Effective(maintained: true, asked: true),
                () => new BepInExStatus
                {
                    BaseFolder = Path.GetDirectoryName(BaseExe),
                    Installed = true,
                    MaintainedByBakaLoader = true,
                    PackVersion = "5.4.2333",
                    Package = BepInExService.DefaultPackage,
                },
                package => service.LatestVersionAsync(package, TimeSpan.FromMilliseconds(250)),
                () => false);
            watch.Stop();

            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10),
                "the restart window held the relaunch for " + watch.Elapsed);
            Assert.Null(plan.Latest);
            Assert.Equal(BepInExUnattendedAction.Skip, plan.Action);
        }

        /// <summary>
        /// And the window really does hand that clock in. A gate on the source, because the
        /// window itself cannot be built here, and because what it guards against is the budget
        /// quietly going away again and the await going back to being unbounded.
        /// </summary>
        [Fact(Timeout = 60000)]
        public void The_window_hands_the_unattended_clock_to_its_version_lookup()
        {
            var bridge = AppSourceTree.Files()["BlendWindow.Bridge.cs"];
            var at = bridge.IndexOf(
                "private async Task ApplyBepInExUpdateAsync(string profile)", StringComparison.Ordinal);
            Assert.True(at > 0, "the unattended BepInEx step is gone");

            var body = bridge.Substring(at, Math.Min(3600, bridge.Length - at));
            var call = body.IndexOf("LatestVersionAsync(", StringComparison.Ordinal);
            Assert.True(call > 0, "the window no longer asks the site at all");

            var arguments = body.Substring(call, Math.Min(140, body.Length - call));
            Assert.Contains("UnattendedResolveTimeout", arguments, StringComparison.Ordinal);
        }

        /// <summary>
        /// A budget that is already spent buys the one thing a budget is for: no request. The
        /// deadline used to be laid over a task the caller had ALREADY started, so a spent
        /// budget refused an answer the machine had gone out and fetched anyway.
        /// </summary>
        [Fact(Timeout = 60000)]
        public async Task A_budget_that_is_already_gone_sends_nothing()
        {
            var provider = new RecordingHttpClientProvider(
                _ => new HttpResponseMessage(HttpStatusCode.NotImplemented));
            var thunderstore = new ThunderstoreClient(
                new RestClientContext(new Serilog.LoggerConfiguration().CreateLogger(), provider));

            var service = new BepInExService(
                thunderstore, provider, new InstallIsolationService(Mock.Of<IApplicationLogger>()),
                Mock.Of<IApplicationLogger>())
            {
                UnattendedResolveTimeout = TimeSpan.Zero,
            };

            await Assert.ThrowsAsync<HostFacingException>(() =>
                service.InstallAsync(BaseExe, null, Nobody(), options: BepInExWriteOptions.Window));

            Assert.Equal(0, provider.Handler.Count);
            Assert.Empty(provider.Handler.Hosts);
        }
    }
}
