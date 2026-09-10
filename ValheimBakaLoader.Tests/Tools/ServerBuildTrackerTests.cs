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

        [Fact]
        public void A_server_nested_deeper_under_the_same_library_does_not_borrow_its_manifest()
        {
            // What an isolated instance looks like on disk: steamapps/common/.bakaloader-
            // instances/<profile>/valheim_server.exe, which is one level too deep to be the
            // install the library's manifest describes.
            var steamapps = Path.Combine(TempDir, "steamapps");
            var baseInstall = Path.Combine(steamapps, "common", "Valheim dedicated server");
            var isolated = Path.Combine(steamapps, "common", ".bakaloader-instances", "Final Sunset");
            Directory.CreateDirectory(baseInstall);
            Directory.CreateDirectory(isolated);

            var manifest = Path.Combine(steamapps, ServerBuildTracker.ManifestFileName);
            File.WriteAllText(manifest, AppliedManifest);

            var baseExe = Path.Combine(baseInstall, "valheim_server.exe");
            var isolatedExe = Path.Combine(isolated, "valheim_server.exe");
            File.WriteAllText(baseExe, "base");
            File.WriteAllText(isolatedExe, "isolated");

            // The install the manifest actually names still finds it.
            Assert.Equal(manifest, ServerBuildTracker.FindManifest(baseExe));

            // The nested one does not, so it identifies itself by its own bytes instead.
            Assert.Null(ServerBuildTracker.FindManifest(isolatedExe));
            var info = ServerBuildTracker.Probe(isolatedExe);
            Assert.Equal(ServerBuildSource.Fingerprint, info.Source);
            Assert.NotNull(info.Identity);
            Assert.NotEqual("25185644", info.Identity);
        }

        [Fact]
        public void A_second_server_folder_under_common_does_not_borrow_the_real_installs_manifest()
        {
            // A copy of the install kept beside the real one is the commonest way to run a
            // second server. It sits exactly where Steam's own install sits, one level under
            // "common", so being there proves nothing: the manifest has to NAME the folder.
            // Without that, the copy reports the real install's build and its pending update,
            // and "Update and start" sends Steam to verify the OTHER folder, then starts this
            // one on its own unchanged files.
            var steamapps = Path.Combine(TempDir, "steamapps");
            var real = Path.Combine(steamapps, "common", "Valheim dedicated server");
            var copy = Path.Combine(steamapps, "common", "vds-modded");
            Directory.CreateDirectory(real);
            Directory.CreateDirectory(copy);

            var manifest = Path.Combine(steamapps, ServerBuildTracker.ManifestFileName);
            File.WriteAllText(manifest, QueuedManifest);
            File.WriteAllText(Path.Combine(steamapps, "libraryfolders.vdf"), "\"libraryfolders\"\n{\n}");

            var realExe = Path.Combine(real, "valheim_server.exe");
            var copyExe = Path.Combine(copy, "valheim_server.exe");
            File.WriteAllText(realExe, "the install Steam owns");
            File.WriteAllText(copyExe, "a copy of it");

            // The install the manifest names still finds it, update and all.
            Assert.Equal(manifest, ServerBuildTracker.FindManifest(realExe));
            Assert.True(ServerBuildTracker.Probe(realExe).UpdatePending);

            // The copy identifies itself by its own bytes, and is nobody's to update.
            Assert.Null(ServerBuildTracker.FindManifest(copyExe));

            var copied = ServerBuildTracker.Probe(copyExe);
            Assert.Equal(ServerBuildSource.Fingerprint, copied.Source);
            Assert.False(copied.UpdatePending);
            Assert.NotEqual("21981590", copied.Identity);

            Assert.Equal(ServerInstallKind.Unknown, ServerBuildTracker.ClassifyInstall(copyExe).Kind);
        }

        [Fact]
        public void An_isolated_instance_is_told_which_install_to_update_rather_than_blamed()
        {
            // BakaLoader made this folder itself, so telling the host they copied it here by
            // hand is wrong twice over. The install that CAN be updated is the one it was
            // provisioned from, so the sentence names it.
            var root = Path.Combine(TempDir, "servers");
            var baseInstall = Path.Combine(root, "Valheim dedicated server");
            var instance = Path.Combine(root, ".bakaloader-instances", "Final Sunset");
            Directory.CreateDirectory(baseInstall);
            Directory.CreateDirectory(instance);

            File.WriteAllText(Path.Combine(baseInstall, "valheim_server.exe"), "base");
            var exe = Path.Combine(instance, "valheim_server.exe");
            File.WriteAllText(exe, "instance");

            var kind = ServerBuildTracker.ClassifyInstall(exe);

            Assert.Equal(ServerInstallKind.Unknown, kind.Kind);
            Assert.Contains("isolated copy", kind.Reason);
            Assert.Contains("Valheim dedicated server", kind.Reason);
            Assert.DoesNotContain("by hand", kind.Reason);

            // A plain folder with no manifest still gets the hand copy sentence.
            var plain = MakeInstall("hand-made install", "assembly one");
            Assert.Contains("by hand", ServerBuildTracker.ClassifyInstall(plain).Reason);
        }

        [Fact]
        public void The_staging_counters_are_read_so_a_staging_Steam_is_not_read_as_a_stalled_one()
        {
            // BytesDownloaded stops moving for the whole staging pass after the last byte
            // lands. The update poll watches these two as well, so it can tell the difference.
            var text = QueuedManifest.Replace(
                @"""BytesDownloaded""		""0""",
                "\"BytesDownloaded\"\t\t\"888960864\"\n\t\"BytesToStage\"\t\t\"888960864\"\n\t\"BytesStaged\"\t\t\"412000000\"");

            var info = ServerBuildTracker.ParseManifest(text);

            Assert.Equal(888960864L, info.BytesToStage);
            Assert.Equal(412000000L, info.BytesStaged);

            // A manifest without them answers zero rather than throwing.
            Assert.Equal(0L, ServerBuildTracker.ParseManifest(AppliedManifest).BytesStaged);
        }

        [Fact]
        public void A_manifest_that_names_the_folder_the_server_sits_in_is_still_accepted()
        {
            // A library laid out by hand: the manifest is not two levels up, but it does say
            // which folder it describes, and that folder is the one the exe is in.
            var steamapps = Path.Combine(TempDir, "steamapps");
            var install = Path.Combine(steamapps, "common", "extra", "vds10");
            Directory.CreateDirectory(install);

            var manifest = Path.Combine(steamapps, ServerBuildTracker.ManifestFileName);
            File.WriteAllText(
                manifest,
                AppliedManifest.Replace(
                    @"""installdir""		""Valheim dedicated server""",
                    @"""installdir""		""vds10"""));

            var exe = Path.Combine(install, "valheim_server.exe");
            File.WriteAllText(exe, "nested but named");

            Assert.Equal(manifest, ServerBuildTracker.FindManifest(exe));
        }

        [Fact]
        public void A_manifest_that_cannot_be_read_still_identifies_the_install()
        {
            // Steam rewrites the .acf in place while it downloads, and holds it exclusively
            // while it does. Reading nothing out of it must never read as "nothing changed".
            var install = Path.Combine(TempDir, "vds10");
            var managed = Path.Combine(install, "valheim_server_Data", "Managed");
            Directory.CreateDirectory(Path.Combine(install, "steamapps"));
            Directory.CreateDirectory(managed);

            var manifest = Path.Combine(install, "steamapps", ServerBuildTracker.ManifestFileName);
            File.WriteAllText(manifest, AppliedManifest);
            var exe = Path.Combine(install, "valheim_server.exe");
            File.WriteAllText(exe, "build A");
            File.WriteAllText(Path.Combine(managed, "assembly_valheim.dll"), "assembly A");

            using (var locked = new FileStream(manifest, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var info = ServerBuildTracker.Probe(exe);

                Assert.Equal(ServerBuildSource.Fingerprint, info.Source);
                Assert.NotNull(info.Identity);
                Assert.Equal(64, info.Identity.Length);
                Assert.Equal(manifest, info.ManifestPath);

                // The guard still has something to compare, so a real change is still caught.
                Assert.Equal(
                    LaunchGuardOutcome.Proceed,
                    LaunchGuard.Decide(info, info.Identity, hasWorlds: true));
                Assert.Equal(
                    LaunchGuardOutcome.BuildChanged,
                    LaunchGuard.Decide(info, new string('b', 64), hasWorlds: true));
            }

            // Once Steam lets go of the file the manifest answers again.
            Assert.Equal("25185644", ServerBuildTracker.Probe(exe).Identity);
        }

        [Fact]
        public void The_three_manifest_states_read_the_way_the_update_flow_expects()
        {
            var install = Path.Combine(TempDir, "states");
            Directory.CreateDirectory(Path.Combine(install, "steamapps"));
            var manifest = Path.Combine(install, "steamapps", ServerBuildTracker.ManifestFileName);
            var exe = Path.Combine(install, "valheim_server.exe");
            File.WriteAllText(exe, "server");

            // Queued: Steam has work to do, and says how much.
            File.WriteAllText(manifest, QueuedManifest);
            var pending = ServerBuildTracker.Probe(exe);
            Assert.True(pending.UpdatePending);
            Assert.Equal(6L, pending.StateFlags);
            Assert.Equal("25185644", pending.TargetBuildId);
            Assert.Equal(888960864L, pending.BytesToDownload);
            Assert.Equal(0L, pending.BytesDownloaded);

            // Applied: the only state the update flow may call finished.
            File.WriteAllText(manifest, AppliedManifest);
            var done = ServerBuildTracker.Probe(exe);
            Assert.False(done.UpdatePending);
            Assert.Equal(4L, done.StateFlags);
            Assert.Equal(done.BuildId, done.TargetBuildId);

            // Locked mid-write: no manifest answer at all, which is not an answer of "done".
            using var held = new FileStream(manifest, FileMode.Open, FileAccess.Read, FileShare.None);
            var locked = ServerBuildTracker.Probe(exe);
            Assert.NotEqual(ServerBuildSource.Manifest, locked.Source);
            Assert.Equal(0L, locked.StateFlags);
        }

        // ------------------------------------------------------------------ install kinds

        [Fact]
        public void A_Steam_library_install_is_the_Steam_clients_to_update()
        {
            var library = Path.Combine(TempDir, "steamlibrary");
            var steamapps = Path.Combine(library, "steamapps");
            var install = Path.Combine(steamapps, "common", "Valheim dedicated server");
            Directory.CreateDirectory(install);
            File.WriteAllText(Path.Combine(steamapps, ServerBuildTracker.ManifestFileName), AppliedManifest);
            File.WriteAllText(Path.Combine(steamapps, "libraryfolders.vdf"), "\"libraryfolders\"\n{\n}");

            var exe = Path.Combine(install, "valheim_server.exe");
            File.WriteAllText(exe, "server");

            var kind = ServerBuildTracker.ClassifyInstall(exe);

            Assert.Equal(ServerInstallKind.SteamLibrary, kind.Kind);
            Assert.Equal(install, kind.InstallDir);
            Assert.Equal(library, kind.LibraryRoot);
            Assert.Null(kind.Reason);
        }

        [Fact]
        public void A_steamcmd_install_is_BakaLoaders_to_update()
        {
            var install = Path.Combine(TempDir, "vds10");
            Directory.CreateDirectory(Path.Combine(install, "steamapps"));
            var manifest = Path.Combine(install, "steamapps", ServerBuildTracker.ManifestFileName);
            File.WriteAllText(manifest, AppliedManifest);
            var exe = Path.Combine(install, "valheim_server.exe");
            File.WriteAllText(exe, "server");

            var kind = ServerBuildTracker.ClassifyInstall(exe);

            Assert.Equal(ServerInstallKind.Standalone, kind.Kind);
            Assert.Equal(install, kind.InstallDir);
            Assert.Equal(manifest, kind.ManifestPath);
            Assert.Null(kind.LibraryRoot);
        }

        [Fact]
        public void A_hand_copied_install_is_nobodys_to_update_and_says_so()
        {
            var exe = MakeInstall("hand-made install", "assembly one");

            var kind = ServerBuildTracker.ClassifyInstall(exe);

            Assert.Equal(ServerInstallKind.Unknown, kind.Kind);
            Assert.Equal(Path.GetDirectoryName(exe), kind.InstallDir);
            Assert.Null(kind.ManifestPath);
            Assert.False(string.IsNullOrWhiteSpace(kind.Reason));

            // Nothing set up at all answers the same way rather than throwing.
            Assert.Equal(ServerInstallKind.Unknown, ServerBuildTracker.ClassifyInstall(null).Kind);
            Assert.Equal(ServerInstallKind.Unknown, ServerBuildTracker.ClassifyInstall("   ").Kind);
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

        [Fact]
        public void An_identity_that_only_changed_shape_is_not_a_new_build()
        {
            // The profile last started while the manifest was unreadable, so what was stored
            // is a fingerprint. The manifest reads fine now, so the identity is a build id.
            // The binaries never changed, and the host must not be asked about an update that
            // did not happen.
            var install = Path.Combine(TempDir, "vds10");
            var managed = Path.Combine(install, "valheim_server_Data", "Managed");
            Directory.CreateDirectory(Path.Combine(install, "steamapps"));
            Directory.CreateDirectory(managed);
            File.WriteAllText(Path.Combine(install, "steamapps", ServerBuildTracker.ManifestFileName), AppliedManifest);

            var exe = Path.Combine(install, "valheim_server.exe");
            File.WriteAllText(exe, "build A");
            var dll = Path.Combine(managed, "assembly_valheim.dll");
            File.WriteAllText(dll, "assembly A");

            var storedFingerprint = ServerBuildTracker.Fingerprint(exe);
            var current = ServerBuildTracker.Probe(exe);

            Assert.Equal(ServerBuildSource.Manifest, current.Source);
            Assert.Equal("25185644", current.Identity);
            Assert.Equal(LaunchGuardOutcome.Proceed, LaunchGuard.Decide(current, storedFingerprint, hasWorlds: true));

            // The same shape change over binaries that really did move is still caught.
            File.WriteAllText(dll, "assembly B");
            var updated = ServerBuildTracker.Probe(exe);
            Assert.Equal(LaunchGuardOutcome.BuildChanged, LaunchGuard.Decide(updated, storedFingerprint, hasWorlds: true));

            // And the other direction, a stored build id against an install that can only be
            // fingerprinted now, says nothing rather than inventing a change.
            var fingerprintOnly = ServerBuildTracker.Probe(MakeInstall("build A", "assembly A"));
            Assert.Equal(LaunchGuardOutcome.Proceed, LaunchGuard.Decide(fingerprintOnly, "25185644", hasWorlds: true));
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
