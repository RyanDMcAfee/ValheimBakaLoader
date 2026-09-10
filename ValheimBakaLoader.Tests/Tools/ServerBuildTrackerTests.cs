using System;
using System.IO;

using ValheimBakaLoader.Tools;
using Xunit;

namespace ValheimBakaLoader.Tests.Tools
{
    /// <summary>
    /// Proves the launch guard's two inputs: what Steam's appmanifest says about the
    /// installed build, and what the binaries hash to when there is no manifest at all.
    /// Both .acf samples below are the real files off a live machine, trimmed to the keys
    /// that matter: one with the 1.0 update queued but not applied, one with it applied.
    /// </summary>
    public class ServerBuildTrackerTests : IDisposable
    {
        private readonly string TempDir =
            Path.Combine(Path.GetTempPath(), "vbl-buildtracker-tests-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try { Directory.Delete(TempDir, recursive: true); } catch { /* best effort */ }
        }

        // StateFlags 6 = installed + update required; the whole download is still outstanding.
        private const string QueuedManifest = @"""AppState""
{
	""appid""		""896660""
	""universe""		""1""
	""name""		""Valheim Dedicated Server""
	""StateFlags""		""6""
	""installdir""		""Valheim dedicated server""
	""LastUpdated""		""1780580515""
	""SizeOnDisk""		""1636649733""
	""buildid""		""21981590""
	""BytesToDownload""		""888960864""
	""BytesDownloaded""		""0""
	""TargetBuildID""		""25185644""
}";

        // StateFlags 4 = fully installed. BytesToDownload still carries the SIZE of the
        // download that finished, which is exactly why a bare "> 0" test would be wrong.
        private const string AppliedManifest = @"""AppState""
{
	""appid""		""896660""
	""Universe""		""1""
	""name""		""Valheim Dedicated Server""
	""StateFlags""		""4""
	""installdir""		""Valheim dedicated server""
	""LastUpdated""		""1788989466""
	""SizeOnDisk""		""2062079003""
	""buildid""		""25185644""
	""BytesToDownload""		""1842931424""
	""BytesDownloaded""		""1842931424""
	""TargetBuildID""		""25185644""
}";

        // ------------------------------------------------------------------ manifest parsing

        [Fact]
        public void Queued_update_is_reported_as_pending_with_the_bytes_still_to_fetch()
        {
            var info = ServerBuildTracker.ParseManifest(QueuedManifest, @"C:\steamapps\appmanifest_896660.acf");

            Assert.Equal("21981590", info.BuildId);
            Assert.True(info.UpdatePending);
            Assert.Equal(888960864L, info.PendingBytes);
            Assert.Equal(ServerBuildSource.Manifest, info.Source);
            Assert.Equal("21981590", info.Identity);
            Assert.NotNull(info.LastUpdatedUtc);
        }

        [Fact]
        public void Applied_update_is_not_pending_even_though_BytesToDownload_is_not_zero()
        {
            var info = ServerBuildTracker.ParseManifest(AppliedManifest);

            Assert.Equal("25185644", info.BuildId);
            Assert.False(info.UpdatePending);
            Assert.Equal(0L, info.PendingBytes);
        }

        [Fact]
        public void A_build_that_no_longer_matches_the_target_counts_as_pending()
        {
            // Steam has not set the update-required bit yet, and the download has not been
            // planned, but it is already pointing the install at a newer build.
            var text = AppliedManifest
                .Replace(@"""buildid""		""25185644""", @"""buildid""		""21981590""");

            var info = ServerBuildTracker.ParseManifest(text);

            Assert.Equal("21981590", info.BuildId);
            Assert.True(info.UpdatePending);
        }

        [Fact]
        public void Manifest_parsing_survives_missing_keys()
        {
            var info = ServerBuildTracker.ParseManifest(@"""AppState"" { ""appid"" ""896660"" }");

            Assert.Null(info.BuildId);
            Assert.False(info.UpdatePending);
            Assert.Equal(0L, info.PendingBytes);
            Assert.Null(info.LastUpdatedUtc);
        }

        // ------------------------------------------------------------------ manifest lookup

        [Fact]
        public void Manifest_is_found_in_a_Steam_library_layout()
        {
            // steamapps/appmanifest_896660.acf + steamapps/common/<install>/valheim_server.exe
            var steamapps = Path.Combine(TempDir, "steamapps");
            var install = Path.Combine(steamapps, "common", "Valheim dedicated server");
            Directory.CreateDirectory(install);
            var manifest = Path.Combine(steamapps, ServerBuildTracker.ManifestFileName);
            File.WriteAllText(manifest, QueuedManifest);
            var exe = Path.Combine(install, "valheim_server.exe");
            File.WriteAllBytes(exe, new byte[] { 1, 2, 3 });

            Assert.Equal(manifest, ServerBuildTracker.FindManifest(exe));

            var info = ServerBuildTracker.Probe(exe);
            Assert.Equal("21981590", info.BuildId);
            Assert.True(info.UpdatePending);
            Assert.Equal(manifest, info.ManifestPath);
        }

        [Fact]
        public void Manifest_is_found_in_a_steamcmd_layout()
        {
            // <install>/steamapps/appmanifest_896660.acf beside <install>/valheim_server.exe
            var install = Path.Combine(TempDir, "vds10");
            Directory.CreateDirectory(Path.Combine(install, "steamapps"));
            var manifest = Path.Combine(install, "steamapps", ServerBuildTracker.ManifestFileName);
            File.WriteAllText(manifest, AppliedManifest);
            var exe = Path.Combine(install, "valheim_server.exe");
            File.WriteAllBytes(exe, new byte[] { 4, 5, 6 });

            Assert.Equal(manifest, ServerBuildTracker.FindManifest(exe));

            var info = ServerBuildTracker.Probe(exe);
            Assert.Equal("25185644", info.Identity);
            Assert.False(info.UpdatePending);
            Assert.Equal(ServerBuildSource.Manifest, info.Source);
        }

        // ------------------------------------------------------------------ fingerprint

        [Fact]
        public void Without_a_manifest_the_binaries_are_the_identity()
        {
            var exe = MakeInstall("hand-made install", "assembly one");

            var info = ServerBuildTracker.Probe(exe);

            Assert.Null(info.BuildId);
            Assert.Null(info.ManifestPath);
            Assert.Equal(ServerBuildSource.Fingerprint, info.Source);
            Assert.NotNull(info.Fingerprint);
            Assert.Equal(info.Fingerprint, info.Identity);
            Assert.Equal(64, info.Fingerprint.Length); // sha256, lowercase hex
            Assert.False(info.UpdatePending);
        }

        [Fact]
        public void The_fingerprint_changes_when_either_binary_changes()
        {
            var exe = MakeInstall("build A", "assembly A");
            var first = ServerBuildTracker.Fingerprint(exe);

            File.WriteAllText(exe, "build B");
            var afterExe = ServerBuildTracker.Fingerprint(exe);

            File.WriteAllText(ManagedDllPath(exe), "assembly B");
            var afterDll = ServerBuildTracker.Fingerprint(exe);

            Assert.NotEqual(first, afterExe);
            Assert.NotEqual(afterExe, afterDll);

            // Same bytes back again = same fingerprint, so an unchanged install stays quiet.
            File.WriteAllText(exe, "build A");
            File.WriteAllText(ManagedDllPath(exe), "assembly A");
            Assert.Equal(first, ServerBuildTracker.Fingerprint(exe));
        }

        [Fact]
        public void An_install_that_is_not_there_yields_nothing_rather_than_throwing()
        {
            Assert.Null(ServerBuildTracker.Fingerprint(null));
            Assert.Null(ServerBuildTracker.Fingerprint(Path.Combine(TempDir, "nope", "valheim_server.exe")));
            Assert.Null(ServerBuildTracker.FindManifest("   "));

            var info = ServerBuildTracker.Probe(Path.Combine(TempDir, "nope", "valheim_server.exe"));
            Assert.Equal(ServerBuildSource.Unknown, info.Source);
            Assert.Null(info.Identity);
        }

        // ------------------------------------------------------------------ the decision

        [Fact]
        public void Nothing_changed_and_nothing_queued_starts_normally()
        {
            var current = Info(buildId: "25185644");

            Assert.Equal(LaunchGuardOutcome.Proceed, LaunchGuard.Decide(current, "25185644", hasWorlds: true));
            Assert.Equal("proceed", LaunchGuard.Token(LaunchGuardOutcome.Proceed));
        }

        [Fact]
        public void A_queued_update_outranks_an_unchanged_build()
        {
            var current = Info(buildId: "21981590", pending: true, pendingBytes: 888960864);

            Assert.Equal(LaunchGuardOutcome.UpdatePending, LaunchGuard.Decide(current, "21981590", hasWorlds: true));
            Assert.Equal(LaunchGuardOutcome.UpdatePending, LaunchGuard.Decide(current, null, hasWorlds: false));
        }

        [Fact]
        public void A_different_build_than_last_time_asks_first()
        {
            var current = Info(buildId: "25185644");

            Assert.Equal(LaunchGuardOutcome.BuildChanged, LaunchGuard.Decide(current, "21981590", hasWorlds: true));
            Assert.Equal(LaunchGuardOutcome.BuildChanged, LaunchGuard.Decide(current, "21981590", hasWorlds: false));
            Assert.Equal("buildChanged", LaunchGuard.Token(LaunchGuardOutcome.BuildChanged));
        }

        [Fact]
        public void A_first_launch_only_asks_when_the_profile_already_owns_worlds()
        {
            var current = Info(buildId: "25185644");

            // Brand new profile: nothing on disk to upgrade, so nothing to warn about.
            Assert.Equal(LaunchGuardOutcome.Proceed, LaunchGuard.Decide(current, null, hasWorlds: false));

            // A profile that predates the guard: its worlds may be about to be upgraded.
            Assert.Equal(LaunchGuardOutcome.BuildChanged, LaunchGuard.Decide(current, null, hasWorlds: true));
        }

        [Fact]
        public void An_unreadable_install_never_blocks_a_start()
        {
            Assert.Equal(LaunchGuardOutcome.Proceed, LaunchGuard.Decide(null, "21981590", hasWorlds: true));
            Assert.Equal(
                LaunchGuardOutcome.Proceed,
                LaunchGuard.Decide(Info(buildId: null), "21981590", hasWorlds: true));
        }

        [Fact]
        public void A_fingerprint_identity_is_compared_the_same_way_as_a_build_id()
        {
            var current = Info(buildId: null, fingerprint: "abc123");

            Assert.Equal(LaunchGuardOutcome.Proceed, LaunchGuard.Decide(current, "abc123", hasWorlds: true));
            Assert.Equal(LaunchGuardOutcome.BuildChanged, LaunchGuard.Decide(current, "def456", hasWorlds: true));
        }

        // ------------------------------------------------------------------ helpers

        private static ServerBuildInfo Info(
            string buildId, bool pending = false, long pendingBytes = 0, string fingerprint = null)
            => new()
            {
                BuildId = buildId,
                UpdatePending = pending,
                PendingBytes = pendingBytes,
                Fingerprint = fingerprint,
                Source = buildId != null ? ServerBuildSource.Manifest
                    : fingerprint != null ? ServerBuildSource.Fingerprint
                    : ServerBuildSource.Unknown,
            };

        /// <summary>A server folder with the two files the fingerprint reads. Returns the exe path.</summary>
        private string MakeInstall(string exeContent, string dllContent)
        {
            var install = Path.Combine(TempDir, "install-" + Guid.NewGuid().ToString("N")[..8]);
            var managed = Path.Combine(install, "valheim_server_Data", "Managed");
            Directory.CreateDirectory(managed);

            var exe = Path.Combine(install, "valheim_server.exe");
            File.WriteAllText(exe, exeContent);
            File.WriteAllText(Path.Combine(managed, "assembly_valheim.dll"), dllContent);
            return exe;
        }

        private static string ManagedDllPath(string exePath)
            => Path.Combine(
                Path.GetDirectoryName(exePath)!, "valheim_server_Data", "Managed", "assembly_valheim.dll");
    }
}
