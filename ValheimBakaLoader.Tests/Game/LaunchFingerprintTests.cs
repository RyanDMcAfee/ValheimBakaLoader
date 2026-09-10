using System;
using System.IO;
using Newtonsoft.Json;
using ValheimBakaLoader.Forms;
using ValheimBakaLoader.Game;
using ValheimBakaLoader.Tools;
using Xunit;

namespace ValheimBakaLoader.Tests.Game
{
    /// <summary>
    /// A profile records two things about the server it last started: what Steam called the
    /// build, and what the binaries themselves hashed to. They answer different questions. The
    /// build id is only readable while the manifest is, and when it is not, a guard holding
    /// only a build id has nothing left to compare and has to wave the start through. That is
    /// the exact moment a game update lands and every world gets converted unprotected.
    /// </summary>
    public class LaunchFingerprintTests : IDisposable
    {
        private const string StoredBuild = "25185644";
        private const string StoredFingerprint =
            "1111111111111111111111111111111111111111111111111111111111111111";
        private const string DifferentFingerprint =
            "2222222222222222222222222222222222222222222222222222222222222222";

        private readonly string SandboxDir;

        public LaunchFingerprintTests()
        {
            SandboxDir = Path.Combine(Path.GetTempPath(), "vbl-fingerprint-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(SandboxDir);
        }

        public void Dispose()
        {
            try { Directory.Delete(SandboxDir, true); } catch { /* best-effort temp cleanup */ }
            GC.SuppressFinalize(this);
        }

        // ---------------------------------------------------------------- the guard

        /// <summary>
        /// The install as it reads when the manifest is there but cannot be opened: no build
        /// id, so the identity is the binaries' own hash.
        /// </summary>
        private static ServerBuildInfo ManifestUnreadable(string fingerprintNow) => new()
        {
            BuildId = null,
            Fingerprint = fingerprintNow,
            ManifestPath = @"C:\steamapps\appmanifest_896660.acf",
            Source = ServerBuildSource.Fingerprint,
        };

        [Fact]
        public void A_build_that_changed_while_the_manifest_was_unreadable_used_to_go_unnoticed()
        {
            var current = ManifestUnreadable(DifferentFingerprint);

            // The old three argument decision has nothing comparable: a build id was stored and
            // there is no build id to read now. It proceeds, which is the gap.
            Assert.Equal(
                LaunchGuardOutcome.Proceed,
                LaunchGuard.Decide(current, StoredBuild, hasWorlds: true));

            // With the fingerprint recorded beside that build id, the same start is caught.
            Assert.Equal(
                LaunchGuardOutcome.BuildChanged,
                LaunchGuard.Decide(current, StoredBuild, StoredFingerprint, hasWorlds: true));
        }

        [Fact]
        public void The_same_binaries_under_an_unreadable_manifest_are_not_a_build_change()
        {
            var current = ManifestUnreadable(StoredFingerprint);

            Assert.Equal(
                LaunchGuardOutcome.Proceed,
                LaunchGuard.Decide(current, StoredBuild, StoredFingerprint, hasWorlds: true));
        }

        [Fact]
        public void A_profile_last_launched_before_the_fingerprint_existed_decides_exactly_as_before()
        {
            var current = ManifestUnreadable(DifferentFingerprint);

            Assert.Equal(
                LaunchGuard.Decide(current, StoredBuild, hasWorlds: true),
                LaunchGuard.Decide(current, StoredBuild, null, hasWorlds: true));
        }

        [Fact]
        public void A_readable_manifest_that_agrees_never_pays_for_a_hash()
        {
            // Identity matches on the build id, so the guard answers before it ever looks at a
            // fingerprint. Passing one that disagrees must not change that.
            var current = new ServerBuildInfo
            {
                BuildId = StoredBuild,
                ManifestPath = @"C:\steamapps\appmanifest_896660.acf",
                Source = ServerBuildSource.Manifest,
            };

            Assert.Equal(
                LaunchGuardOutcome.Proceed,
                LaunchGuard.Decide(current, StoredBuild, DifferentFingerprint, hasWorlds: true));
        }

        [Fact]
        public void A_queued_update_still_outranks_everything()
        {
            var current = new ServerBuildInfo
            {
                BuildId = StoredBuild,
                UpdatePending = true,
                ManifestPath = @"C:\steamapps\appmanifest_896660.acf",
                Source = ServerBuildSource.Manifest,
            };

            Assert.Equal(
                LaunchGuardOutcome.UpdatePending,
                LaunchGuard.Decide(current, StoredBuild, StoredFingerprint, hasWorlds: true));
        }

        // ---------------------------------------------------------------- the round trip

        [Fact]
        public void The_fingerprint_survives_the_whole_trip_from_disk_to_the_guard_context()
        {
            var prefs = new ServerPreferences
            {
                ProfileName = "Default",
                Name = "Fingerprint Test Server",
                WorldName = "Fingerprint Test World",
                Password = "hunter2",
                Port = 2456,
                SaveInterval = 30,
                BackupCount = 1,
                BackupIntervalShort = 60,
                BackupIntervalLong = 120,
                ServerExePath = Path.Combine(SandboxDir, "valheim_server.exe"),
                SaveDataFolderPath = SandboxDir,
                LastLaunchedServerBuild = StoredBuild,
                LastLaunchedServerFingerprint = StoredFingerprint,
                LastLaunchedGameVersion = "1.0.7",
            };

            // Through the file shape userprefs.json is actually written in and back.
            var onDisk = JsonConvert.DeserializeObject<ServerPreferencesFile>(
                JsonConvert.SerializeObject(prefs.ToFile()));
            var reloaded = ServerPreferences.FromFile(onDisk);

            Assert.Equal(StoredFingerprint, reloaded.LastLaunchedServerFingerprint);
            Assert.Equal(StoredBuild, reloaded.LastLaunchedServerBuild);

            // Through the options the server is launched with.
            var options = ValheimServerOptions.FromPreferences(reloaded, new UserPreferences(), null);
            Assert.Equal(StoredFingerprint, options.LastLaunchedServerFingerprint);

            // And into the context the guard is handed.
            var context = new LaunchContext
            {
                Current = ManifestUnreadable(DifferentFingerprint),
                LastLaunchedBuild = options.LastLaunchedServerBuild,
                LastLaunchedFingerprint = options.LastLaunchedServerFingerprint,
                HasWorlds = true,
            };

            Assert.Equal(
                LaunchGuardOutcome.BuildChanged,
                LaunchGuard.Decide(
                    context.Current, context.LastLaunchedBuild, context.LastLaunchedFingerprint, context.HasWorlds));
        }

        [Fact]
        public void The_stored_fingerprint_beats_a_payload_that_never_carried_one()
        {
            // The WebUI builds its prefs payload out of form fields, which do not include the
            // launch history, so the merge is what puts it back.
            var fromBrowser = new ServerPreferences { ProfileName = "Default" };
            var onDisk = new ServerPreferences
            {
                ProfileName = "Default",
                LastLaunchedServerBuild = StoredBuild,
                LastLaunchedServerFingerprint = StoredFingerprint,
            };

            var merged = BlendWindow.MergeLaunchHistory(fromBrowser, onDisk);

            Assert.Equal(StoredFingerprint, merged.LastLaunchedServerFingerprint);
        }

        [Fact]
        public void What_gets_recorded_is_the_binaries_own_hash_even_when_a_build_id_was_readable()
        {
            var exe = Path.Combine(SandboxDir, "valheim_server.exe");
            File.WriteAllBytes(exe, new byte[] { 1, 2, 3, 4 });

            var first = BlendWindow.LaunchedFingerprint(exe);
            Assert.False(string.IsNullOrWhiteSpace(first));
            Assert.Equal(64, first.Length);

            // A changed binary hashes differently, which is the whole point of storing it.
            File.WriteAllBytes(exe, new byte[] { 1, 2, 3, 4, 5 });
            Assert.NotEqual(first, BlendWindow.LaunchedFingerprint(exe));

            // Nothing to hash decides nothing, and says so quietly.
            Assert.Null(BlendWindow.LaunchedFingerprint(null));
            Assert.Null(BlendWindow.LaunchedFingerprint(Path.Combine(SandboxDir, "not-here.exe")));
        }
    }
}
