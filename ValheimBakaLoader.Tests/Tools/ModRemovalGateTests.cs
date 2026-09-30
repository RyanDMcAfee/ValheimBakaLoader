using System;
using System.Collections.Generic;
using System.IO;
using ValheimBakaLoader.Tools;
using Xunit;

namespace ValheimBakaLoader.Tests.Tools
{
    /// <summary>
    /// Which realm holds a folder that a removal is about to delete from.
    /// <para>
    /// WHY THIS EXISTS. The refusal behind mods.remove asked "is ANY server up" and named the
    /// first one it found. A realm is not an install: realms are given their own isolated
    /// install by default, and an isolated install COPIES its plugins folder, so a running
    /// realm's process has never opened a stopped realm's plugins and Windows has no objection
    /// to deleting from it. The owner leaves one world up all evening, so the old rule blocked
    /// every removal on every other realm for as long as it stayed up, and the sentence it
    /// showed named a realm the host was not looking at. It also disagreed with the page, which
    /// asked only about the realm on screen: the confirm offered a button the host side then
    /// refused.
    /// </para>
    /// <para>
    /// And the half that goes the other way. An isolated install JUNCTIONS its patchers folder
    /// to the base install, so that one is shared: a mod carrying a patcher part, removed on a
    /// stopped realm, would delete the folder a running realm has loaded. Narrowing the rule to
    /// the plugins folder alone would have opened exactly that, so the gate is asked about
    /// every folder the removal will touch and follows a junction before it compares.
    /// </para>
    /// </summary>
    public class ModRemovalGateTests : IDisposable
    {
        private readonly string Root =
            Path.Combine(Path.GetTempPath(), "vbl-gate-" + Guid.NewGuid().ToString("N"));

        private string InstallA => Path.Combine(Root, "a", "BepInEx");
        private string InstallB => Path.Combine(Root, "b", "BepInEx");

        public void Dispose()
        {
            try { Directory.Delete(Root, true); } catch { /* best-effort temp cleanup */ }
            GC.SuppressFinalize(this);
        }

        private static ModRemovalGate.Session Up(string realm, string bepInEx)
            => new(realm, true, Path.Combine(bepInEx, "plugins"), Path.Combine(bepInEx, "patchers"));

        private static ModRemovalGate.Session Down(string realm, string bepInEx)
            => new(realm, false, Path.Combine(bepInEx, "plugins"), Path.Combine(bepInEx, "patchers"));

        /// <summary>What a removal that has no patcher part touches: the plugins folder only.</summary>
        private static string[] PluginsOf(string bepInEx) => new[] { Path.Combine(bepInEx, "plugins") };

        /// <summary>And one that does: both.</summary>
        private static string[] PluginsAndPatchersOf(string bepInEx)
            => new[] { Path.Combine(bepInEx, "plugins"), Path.Combine(bepInEx, "patchers") };

        [Fact]
        public void Nothing_running_blocks_nothing()
        {
            Assert.Null(ModRemovalGate.BlockedBy(
                new[] { Down("Final Sunset", InstallA), Down("Second Realm", InstallB) },
                PluginsAndPatchersOf(InstallA)));
        }

        /// <summary>
        /// The case the refusal is FOR: the realm whose plugins these are is up, and Windows
        /// holds the loaded DLL open.
        /// </summary>
        [Fact]
        public void A_realm_up_on_this_install_blocks_and_says_which()
        {
            Assert.Equal("Final Sunset", ModRemovalGate.BlockedBy(
                new[] { Up("Final Sunset", InstallA) }, PluginsOf(InstallA)));
        }

        /// <summary>
        /// The case that was wrong: realm A is up on its own install, the host is looking at
        /// stopped realm B, and B's plugins are a folder A has never opened. This returned
        /// "Final Sunset" before, which refused a removal nothing was stopping and named a
        /// realm the host was not on.
        /// </summary>
        [Fact]
        public void A_realm_up_on_a_different_install_does_not_block()
        {
            Assert.Null(ModRemovalGate.BlockedBy(
                new[] { Up("Final Sunset", InstallA) }, PluginsOf(InstallB)));
        }

        /// <summary>
        /// Two realms sharing one install is the other default, and there the refusal stands:
        /// the folder is the same folder however many realms point at it.
        /// </summary>
        [Fact]
        public void Two_realms_on_one_install_still_block_each_other()
        {
            Assert.Equal("Shared Realm", ModRemovalGate.BlockedBy(
                new[] { Down("Quiet Realm", InstallA), Up("Shared Realm", InstallA) },
                PluginsOf(InstallA)));
        }

        /// <summary>
        /// The same folder written two ways is the same folder. A trailing separator, a
        /// relative step and a difference of case are all spellings, not installs.
        /// </summary>
        [Fact]
        public void The_same_folder_spelled_differently_still_blocks()
        {
            var spelled = Path.Combine(Root, "a", "saves", "..", "BepInEx", "PLUGINS")
                + Path.DirectorySeparatorChar;

            Assert.Equal("Final Sunset", ModRemovalGate.BlockedBy(
                new[] { Up("Final Sunset", InstallA) }, new[] { spelled }));
        }

        /// <summary>
        /// And a junction is a third spelling. An isolated realm's patchers folder is a link to
        /// the base install's, so a mod with a patcher part removed on the stopped realm would
        /// delete the folder the running one has loaded. The plugins folder beside it is a real
        /// copy, so it is still not in the way and a mod without a patcher part is not blocked.
        /// </summary>
        [Fact]
        public void A_patchers_junction_shared_with_a_running_realm_blocks()
        {
            var basePatchers = Path.Combine(InstallA, "patchers");
            Directory.CreateDirectory(basePatchers);
            Directory.CreateDirectory(Path.Combine(InstallA, "plugins"));
            Directory.CreateDirectory(Path.Combine(InstallB, "plugins"));

            // A JUNCTION, made the way InstallIsolationService makes one, because that is what
            // an isolated realm's patchers folder really is. Junctions need no privilege.
            var linked = Path.Combine(InstallB, "patchers");
            using (var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c mklink /J \"{linked}\" \"{basePatchers}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            }))
            {
                mklink.WaitForExit(20000);
            }

            Assert.True(Directory.Exists(linked)
                && new DirectoryInfo(linked).Attributes.HasFlag(FileAttributes.ReparsePoint),
                "the junction this rule is about was not made, so nothing here was tested");

            // Realm A is up on the base install. Removing a mod with a patcher part from the
            // stopped realm B would delete out of the folder A has loaded.
            Assert.Equal("Final Sunset", ModRemovalGate.BlockedBy(
                new[] { Up("Final Sunset", InstallA) }, PluginsAndPatchersOf(InstallB)));

            // A mod with no patcher part touches only B's own copied plugins, and goes.
            Assert.Null(ModRemovalGate.BlockedBy(
                new[] { Up("Final Sunset", InstallA) }, PluginsOf(InstallB)));
        }

        /// <summary>
        /// Unknown is held, in both directions. When the folders a removal will touch cannot be
        /// worked out, or a live session will not say which ones it opened, nothing here can
        /// prove the two are different installs, and a half removed folder is worse than a
        /// refusal the host can read.
        /// </summary>
        [Fact]
        public void An_unknown_folder_is_treated_as_held()
        {
            Assert.Equal("Final Sunset", ModRemovalGate.BlockedBy(
                new[] { Up("Final Sunset", InstallA) }, null));
            Assert.Equal("Final Sunset", ModRemovalGate.BlockedBy(
                new[] { Up("Final Sunset", InstallA) }, Array.Empty<string>()));
            Assert.Equal("Final Sunset", ModRemovalGate.BlockedBy(
                new[] { Up("Final Sunset", InstallA) }, new[] { "   ", (string)null }));
            Assert.Equal("Final Sunset", ModRemovalGate.BlockedBy(
                new[] { new ModRemovalGate.Session("Final Sunset", true, (string[])null) },
                PluginsOf(InstallB)));
        }

        [Fact]
        public void No_sessions_at_all_blocks_nothing()
        {
            Assert.Null(ModRemovalGate.BlockedBy(null, PluginsOf(InstallA)));
            Assert.Null(ModRemovalGate.BlockedBy(
                new List<ModRemovalGate.Session>(), PluginsOf(InstallA)));
        }
    }
}
