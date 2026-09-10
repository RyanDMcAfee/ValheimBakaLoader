using System;
using System.IO;
using System.Threading.Tasks;
using ValheimBakaLoader.Game;
using Xunit;

namespace ValheimBakaLoader.Tests.Game
{
    /// <summary>
    /// When the host answers the launch guard with "copy the worlds aside first", that copy is
    /// the whole reason the start is allowed: this launch converts every world one way. A copy
    /// that does not happen has to abandon the start, and it has to say which world and what
    /// stopped it. A generic sentence sends the host looking through a log for the half of the
    /// answer the snapshot already worked out.
    /// </summary>
    public class ValheimServerPreLaunchBackupTests : BaseTest, IDisposable
    {
        private readonly ValheimServer Server;
        private readonly string SandboxDir;
        private readonly string SaveDir;

        public ValheimServerPreLaunchBackupTests()
        {
            Server = GetService<ValheimServer>();

            SandboxDir = Path.Combine(Path.GetTempPath(), "vbl-presnap-" + Guid.NewGuid().ToString("N"));
            SaveDir = Path.Combine(SandboxDir, "saves");
            Directory.CreateDirectory(Path.Combine(SaveDir, "worlds_local"));
            File.WriteAllBytes(Path.Combine(SandboxDir, "valheim_server.exe"), Array.Empty<byte>());

            // One world on disk, so there really is something the snapshot was meant to protect.
            File.WriteAllText(Path.Combine(SaveDir, "worlds_local", "Midgard.fwl"), "world file");
            File.WriteAllText(Path.Combine(SaveDir, "worlds_local", "Midgard.db"), "database bytes");
        }

        public void Dispose()
        {
            try { Server.Dispose(); } catch { /* best effort */ }
            try { Directory.Delete(SandboxDir, true); } catch { /* best-effort temp cleanup */ }
            GC.SuppressFinalize(this);
        }

        [Fact]
        public async Task A_copy_that_could_not_happen_abandons_the_start_and_says_which_world_and_why()
        {
            var settled = new TaskCompletionSource<LaunchSettledEventArgs>();
            Server.LaunchSettled += (s, e) => settled.TrySetResult(e);

            Server.ConfirmLaunchAsync = _ => Task.FromResult(LaunchDecision.BackUpThenGo("the host asked for a backup"));

            // The same veto the bridge wires: a world another profile's server is writing to
            // cannot be captured whole, so the snapshot refuses rather than copying half of it.
            Server.WorldSnapshotVeto = _ => "'Keeper' is running it right now. Stop that server first.";

            Server.Start(Options());

            var outcome = await settled.Task.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.False(outcome.Held);
            Assert.Equal(ServerStatus.Stopped, Server.Status);

            // The snapshot's own sentence, as written: the world it stopped on and the reason.
            Assert.Contains("Midgard", outcome.Error);
            Assert.Contains("Keeper", outcome.Error);
            Assert.Contains("cannot be copied aside", outcome.Error);
        }

        [Fact]
        public async Task A_copy_that_worked_lets_the_start_go_ahead()
        {
            Server.ConfirmLaunchAsync = _ => Task.FromResult(LaunchDecision.BackUpThenGo("the host asked for a backup"));

            Server.Start(Options());

            await WaitUntil(() => Server.Status == ServerStatus.Starting);
            Assert.Equal(ServerStatus.Starting, Server.Status);
        }

        private static async Task WaitUntil(Func<bool> condition)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                if (condition()) return;
                await Task.Delay(25);
            }

            Assert.True(condition(), "the condition was never met");
        }

        private ValheimServerOptions Options() => new()
        {
            Name = "Presnap Test Server",
            WorldName = "Midgard",
            Password = "hunter2",
            Port = 2456,
            SaveInterval = 30,
            Backups = 1,
            BackupShort = 60,
            BackupLong = 120,
            LogToFile = false,
            ServerExePath = Path.Combine(SandboxDir, "valheim_server.exe"),
            SaveDataFolderPath = SaveDir,
        };
    }
}
