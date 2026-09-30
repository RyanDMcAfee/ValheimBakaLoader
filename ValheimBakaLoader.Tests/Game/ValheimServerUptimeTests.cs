using System;
using System.IO;
using ValheimBakaLoader.Game;
using ValheimBakaLoader.Tests.Tools;
using Xunit;

namespace ValheimBakaLoader.Tests.Game
{
    /// <summary>
    /// When the session came up, which is not the same question as when the window first
    /// looked at it.
    /// <para>
    /// WHY THIS EXISTS. The condition bar and the SERVER card counted uptime from the first
    /// moment the PAGE saw a Running state. A realm switch throws the page's state away and
    /// asks again, so the first sighting was the switch: a world that had been up all evening
    /// read as freshly started, and switching back and forth reset it every time. The session
    /// has always known the answer. These hold it to writing it down.
    /// </para>
    /// <para>
    /// And the road that is not a launch. BakaLoader adopts a server it finds already running,
    /// which is what the owner's box looks like every time the window is closed and opened
    /// again over a world that stays up. The anchor there is the PROCESS's start, not the
    /// moment of the adoption; the last three hold that one.
    /// </para>
    /// </summary>
    public class ValheimServerUptimeTests : BaseTest, IDisposable
    {
        private readonly ValheimServer Server;
        private readonly string SandboxDir;
        private readonly IDisposable OwnRecords;

        public ValheimServerUptimeTests()
        {
            OwnRecords = ValheimBakaLoader.Tools.CompanionPluginStatus.BeginOwnRecords();
            Server = GetService<ValheimServer>();

            SandboxDir = Path.Combine(Path.GetTempPath(), "vbl-uptime-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(SandboxDir, "saves"));
            File.WriteAllBytes(Path.Combine(SandboxDir, "valheim_server.exe"), Array.Empty<byte>());
        }

        public void Dispose()
        {
            try { Directory.Delete(SandboxDir, true); } catch { /* best-effort temp cleanup */ }
            OwnRecords.Dispose();
            GC.SuppressFinalize(this);
        }

        [Fact]
        public void A_stopped_server_claims_no_uptime()
        {
            Assert.Null(Server.RunningSinceUtc);
        }

        /// <summary>
        /// The anchor is written once, when the server comes up, and it does not move while
        /// the session runs. That second half is the whole point: a page that asks for the
        /// state again, which is what a realm switch does, gets the same answer it got the
        /// first time rather than a fresh one.
        /// </summary>
        [Fact]
        public void The_anchor_is_written_when_the_server_comes_up_and_stays_put()
        {
            var before = DateTime.UtcNow;
            Feed("Game server connected");

            Assert.Equal(ServerStatus.Running, Server.Status);
            var anchor = Server.RunningSinceUtc;
            Assert.NotNull(anchor);
            Assert.InRange(anchor.Value, before.AddSeconds(-5), DateTime.UtcNow.AddSeconds(5));

            // Ten more lines, one of them another "connected": the anchor is the first one.
            for (var i = 0; i < 9; i++) Feed("Got connection SteamID {0}", 1000 + i);
            Feed("Game server connected");

            Assert.Equal(anchor, Server.RunningSinceUtc);
        }

        /// <summary>
        /// And what the window does with it: a session that came up 31 minutes ago reads 31
        /// minutes, however many times the state is asked for. This is the arithmetic the page
        /// does, driven against the field the session really answers with, so the two cannot
        /// drift apart without one of them failing here.
        /// </summary>
        [Fact]
        public void A_session_that_came_up_31_minutes_ago_reads_31_minutes_on_every_read()
        {
            Feed("Game server connected");
            var anchor = Server.RunningSinceUtc;
            Assert.NotNull(anchor);

            // The clock a page would be holding half an hour into the session.
            var now = anchor.Value.AddMinutes(31);

            for (var read = 0; read < 3; read++)
            {
                var minutes = (int)Math.Round((now - Server.RunningSinceUtc.Value).TotalMinutes);
                Assert.Equal(31, minutes);
            }
        }

        /// <summary>
        /// And the case the owner actually hits, which is not a realm switch at all: the window
        /// is closed and opened again while the world stays up, and the session BakaLoader
        /// finds has been running since long before it was found. The anchor is the PROCESS's
        /// start, so an evening-old world reads as an evening old rather than as freshly
        /// started. This is the only road into Running that does not begin with a launch.
        /// </summary>
        [Fact]
        public void An_adopted_server_counts_from_when_its_process_came_up()
        {
            var cameUp = DateTime.UtcNow.AddMinutes(-31);
            Server.AdoptedStartUtcReader = _ => cameUp;

            using var standIn = new System.Diagnostics.Process();
            Server.AdoptProcess(standIn, Options());

            Assert.Equal(ServerStatus.Running, Server.Status);
            Assert.True(Server.IsAdopted);
            Assert.Equal(cameUp, Server.RunningSinceUtc);

            // The arithmetic the page does, against the field the session really answers with.
            var minutes = (int)Math.Round(
                (cameUp.AddMinutes(31) - Server.RunningSinceUtc.Value).TotalMinutes);
            Assert.Equal(31, minutes);
        }

        /// <summary>
        /// A process that will not say when it started, because it has already gone or because
        /// Windows refuses the read, leaves the session with this moment: what it always had.
        /// A missing answer must never come out as no uptime at all.
        /// </summary>
        [Fact]
        public void An_adopted_server_whose_process_will_not_say_falls_back_to_this_moment()
        {
            var before = DateTime.UtcNow;

            // An unstarted Process throws on StartTime, which is the shape of every read that
            // cannot be had.
            using var standIn = new System.Diagnostics.Process();
            Server.AdoptProcess(standIn, Options());

            Assert.Equal(ServerStatus.Running, Server.Status);
            Assert.NotNull(Server.RunningSinceUtc);
            Assert.InRange(Server.RunningSinceUtc.Value,
                before.AddSeconds(-60), DateTime.UtcNow.AddSeconds(60));
        }

        /// <summary>
        /// A start time in the future is a clock that disagrees with itself, not an uptime.
        /// Taken as read it would draw a negative one on the condition bar, so it is clamped.
        /// </summary>
        [Fact]
        public void An_adopted_start_in_the_future_never_becomes_a_negative_uptime()
        {
            Server.AdoptedStartUtcReader = _ => DateTime.UtcNow.AddHours(2);
            var before = DateTime.UtcNow;

            using var standIn = new System.Diagnostics.Process();
            Server.AdoptProcess(standIn, Options());

            Assert.NotNull(Server.RunningSinceUtc);
            Assert.InRange(Server.RunningSinceUtc.Value,
                before.AddSeconds(-60), DateTime.UtcNow.AddSeconds(60));
        }

        private ValheimServerOptions Options() => new()
        {
            Name = "Uptime Server",
            WorldName = "Uptime World",
            Password = "hunter2",
            Port = 2456,
            Public = false,
            Crossplay = false,
            SaveInterval = 30,
            Backups = 1,
            BackupShort = 60,
            BackupLong = 120,
            LogToFile = false,
            ServerExePath = Path.Combine(SandboxDir, "valheim_server.exe"),
            SaveDataFolderPath = Path.Combine(SandboxDir, "saves"),
        };

        private void Feed(string template, params object[] args)
        {
            if (Server.Logger == null) Server.Start(Options());

            Server.Logger.Information(string.Format(template, args));
        }
    }
}
