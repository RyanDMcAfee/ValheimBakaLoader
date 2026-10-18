using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Moq;
using ValheimBakaLoader.Tools;
using ValheimBakaLoader.Tools.Logging;
using Xunit;

namespace ValheimBakaLoader.Tests.Tools
{
    /// <summary>
    /// Patchers are per realm since 1.2.15. Every isolated realm used to get BepInEx/patchers as
    /// a junction to the base install, so a patcher installed into one realm loaded in every
    /// realm and a removal in one took it from all of them. A new realm now gets a real copy,
    /// and an old realm's junction is turned into its own copy the moment anything writes there.
    /// <para>
    /// These use real junctions, made through the service's own helper (cmd.exe /c mklink /J,
    /// started with no window), in a temporary tree.
    /// </para>
    /// </summary>
    public class PatchersPerRealmTests : IDisposable
    {
        private readonly string Root = Path.Combine(Path.GetTempPath(), "bakaloader-patchers-" + Guid.NewGuid().ToString("N"));
        private readonly IApplicationLogger Log = Mock.Of<IApplicationLogger>();

        public PatchersPerRealmTests() => Directory.CreateDirectory(Root);

        public void Dispose()
        {
            try
            {
                foreach (var dir in Directory.EnumerateDirectories(Root, "*", SearchOption.AllDirectories).ToList())
                    if (BepInExService.IsReparsePoint(dir)) Directory.Delete(dir);
            }
            catch { /* best effort */ }

            try { Directory.Delete(Root, recursive: true); } catch { /* best effort */ }
        }

        private string BaseBepInEx()
        {
            var bep = Path.Combine(Root, "base", "BepInEx");
            Directory.CreateDirectory(Path.Combine(bep, "patchers", "Someone-Early"));
            Directory.CreateDirectory(Path.Combine(bep, "core"));
            Directory.CreateDirectory(Path.Combine(bep, "plugins"));
            Directory.CreateDirectory(Path.Combine(bep, "config"));
            File.WriteAllText(Path.Combine(bep, "patchers", "Someone-Early", "Early.dll"), "base patcher");
            File.WriteAllText(Path.Combine(bep, "core", "BepInEx.dll"), "loader");
            return bep;
        }

        [Fact]
        public void An_old_realms_patchers_junction_becomes_its_own_copy_and_the_base_keeps_every_file()
        {
            var baseBep = BaseBepInEx();
            var realmBep = Path.Combine(Root, "realm", "BepInEx");
            Directory.CreateDirectory(realmBep);
            InstallIsolationService.CreateJunctionOrThrow(Path.Combine(realmBep, "patchers"), Path.Combine(baseBep, "patchers"));
            Assert.True(BepInExService.IsReparsePoint(Path.Combine(realmBep, "patchers")));

            var changed = InstallIsolationService.EnsureOwnPatchersFolder(realmBep, Log);

            Assert.True(changed);
            var realmPatchers = Path.Combine(realmBep, "patchers");
            Assert.False(BepInExService.IsReparsePoint(realmPatchers));
            Assert.Equal("base patcher", File.ReadAllText(Path.Combine(realmPatchers, "Someone-Early", "Early.dll")));

            // The link was removed, never followed: the base still has everything.
            Assert.Equal("base patcher", File.ReadAllText(Path.Combine(baseBep, "patchers", "Someone-Early", "Early.dll")));

            // The two are separate now: a change in the realm stays in the realm.
            File.Delete(Path.Combine(realmPatchers, "Someone-Early", "Early.dll"));
            Assert.True(File.Exists(Path.Combine(baseBep, "patchers", "Someone-Early", "Early.dll")));

            // No staging folder is left behind.
            Assert.DoesNotContain(Directory.EnumerateDirectories(realmBep).Select(Path.GetFileName),
                n => n.StartsWith(".bakaloader-patchers-", StringComparison.Ordinal));

            Assert.False(InstallIsolationService.EnsureOwnPatchersFolder(realmBep, Log), "a real folder was converted again");
        }

        [Fact]
        public void The_base_installs_own_patchers_folder_is_left_alone()
        {
            var baseBep = BaseBepInEx();

            Assert.False(InstallIsolationService.EnsureOwnPatchersFolder(baseBep, Log));
            Assert.True(File.Exists(Path.Combine(baseBep, "patchers", "Someone-Early", "Early.dll")));
        }

        [Fact]
        public void A_new_realm_gets_a_real_patchers_folder_and_a_core_junction()
        {
            var baseBep = BaseBepInEx();
            var dst = Path.Combine(Root, "instance", "BepInEx");

            var method = typeof(InstallIsolationService).GetMethod("ProvisionBepInEx", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(method);
            method.Invoke(new InstallIsolationService(Log), new object[] { baseBep, dst, false, true, null });

            Assert.True(Directory.Exists(Path.Combine(dst, "patchers")));
            Assert.False(BepInExService.IsReparsePoint(Path.Combine(dst, "patchers")), "patchers is still shared by junction");
            Assert.True(File.Exists(Path.Combine(dst, "patchers", "Someone-Early", "Early.dll")));
            Assert.True(BepInExService.IsReparsePoint(Path.Combine(dst, "core")), "core stopped being shared");
        }

        [Fact]
        public void Giving_an_old_realm_its_missing_BepInEx_makes_patchers_a_real_folder()
        {
            var baseBep = BaseBepInEx();
            var baseExe = Path.Combine(Root, "base", "valheim_server.exe");
            File.WriteAllText(baseExe, "exe");

            var install = Path.Combine(Root, ".bakaloader-instances", "Old");
            Directory.CreateDirectory(install);

            Assert.True(new InstallIsolationService(Log).EnsureSharedBepInEx(baseExe, install));

            var patchers = Path.Combine(install, "BepInEx", "patchers");
            Assert.False(BepInExService.IsReparsePoint(patchers));
            Assert.True(File.Exists(Path.Combine(patchers, "Someone-Early", "Early.dll")));
            Assert.True(BepInExService.IsReparsePoint(Path.Combine(install, "BepInEx", "core")));
            Assert.True(File.Exists(Path.Combine(baseBep, "patchers", "Someone-Early", "Early.dll")));
        }

        [Fact]
        public void A_removal_in_an_old_realm_takes_the_patcher_from_that_realm_only()
        {
            var baseBep = BaseBepInEx();
            var realmBep = Path.Combine(Root, "realm", "BepInEx");
            Directory.CreateDirectory(Path.Combine(realmBep, "plugins", "Someone-Early"));
            File.WriteAllText(Path.Combine(realmBep, "plugins", "Someone-Early", "manifest.json"),
                "{\"name\":\"Early\",\"version_number\":\"1.0.0\"}");
            InstallIsolationService.CreateJunctionOrThrow(Path.Combine(realmBep, "patchers"), Path.Combine(baseBep, "patchers"));

            var mod = new ModScanner(Log).ScanPlugins(Path.Combine(realmBep, "plugins")).Single(m => m.FullName == "Someone-Early");
            var result = new ModRemovalService(Log).RemoveMod(mod, includeConfig: false);

            Assert.True(result.Removed, result.Error);
            Assert.False(Directory.Exists(Path.Combine(realmBep, "patchers", "Someone-Early")));
            Assert.True(File.Exists(Path.Combine(baseBep, "patchers", "Someone-Early", "Early.dll")),
                "removing the mod from one realm took its patcher from the base install");
        }

        [Fact]
        public void Switching_off_in_an_old_realm_never_renames_the_base_installs_patcher()
        {
            var baseBep = BaseBepInEx();
            var realmBep = Path.Combine(Root, "realm", "BepInEx");
            Directory.CreateDirectory(Path.Combine(realmBep, "plugins", "Someone-Early"));
            File.WriteAllText(Path.Combine(realmBep, "plugins", "Someone-Early", "manifest.json"),
                "{\"name\":\"Early\",\"version_number\":\"1.0.0\"}");
            InstallIsolationService.CreateJunctionOrThrow(Path.Combine(realmBep, "patchers"), Path.Combine(baseBep, "patchers"));

            var mod = new ModScanner(Log).ScanPlugins(Path.Combine(realmBep, "plugins")).Single(m => m.FullName == "Someone-Early");
            var off = ModSwitch.Disable(mod, Log);

            Assert.True(off.Ok, off.Error);
            Assert.True(File.Exists(Path.Combine(realmBep, "patchers", "Someone-Early", "Early.dll.old")));
            Assert.True(File.Exists(Path.Combine(baseBep, "patchers", "Someone-Early", "Early.dll")),
                "switching the mod off in one realm switched it off in the base install");
        }
    }
}
