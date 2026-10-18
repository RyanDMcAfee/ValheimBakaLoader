using System;
using System.IO;
using System.Linq;
using Moq;
using ValheimBakaLoader.Tools;
using ValheimBakaLoader.Tools.Logging;
using ValheimBakaLoader.Tools.Models;
using Xunit;

namespace ValheimBakaLoader.Tests.Tools
{
    /// <summary>
    /// Switching a mod off and on without removing it (mods.disable / mods.enable). The
    /// convention is r2modman's and Gale's: every <c>*.dll</c> becomes <c>*.dll.old</c>, which
    /// BepInEx does not load, and back. Everything here works on a temporary BepInEx tree.
    /// </summary>
    public class ModSwitchTests : IDisposable
    {
        private readonly string Root = Path.Combine(Path.GetTempPath(), "bakaloader-switch-" + Guid.NewGuid().ToString("N"));
        private readonly string Bep;
        private readonly string Plugins;
        private readonly IApplicationLogger Log = Mock.Of<IApplicationLogger>();

        public ModSwitchTests()
        {
            Bep = Path.Combine(Root, "BepInEx");
            Plugins = Path.Combine(Bep, "plugins");
            Directory.CreateDirectory(Plugins);
        }

        public void Dispose()
        {
            try { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); } catch { /* best effort */ }
        }

        private string Write(string relative, string body = "x")
        {
            var path = Path.Combine(Bep, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, body);
            return path;
        }

        private void Mod(string folder = "Some-Thing")
        {
            Write($"plugins/{folder}/manifest.json", "{\"name\":\"Thing\",\"version_number\":\"1.0.0\"}");
            Write($"plugins/{folder}/Thing.dll", "plugin");
            Write($"plugins/{folder}/Sub/Helper.dll", "helper");
            Write($"patchers/{folder}/ThingPatcher.dll", "patcher");
            Write($"config/some.thing.cfg", "[General]\nkept = yes");
        }

        private InstalledMod Scan(string fullName = "Some-Thing") =>
            new ModScanner(Log).ScanPlugins(Plugins).Single(m => m.FullName == fullName);

        private string At(string relative) => Path.Combine(Bep, relative.Replace('/', Path.DirectorySeparatorChar));

        [Fact]
        public void Off_then_on_renames_every_plugin_and_patcher_file_and_back()
        {
            Mod();
            Assert.False(Scan().Disabled);

            var off = ModSwitch.Disable(Scan(), Log);

            Assert.True(off.Ok, off.Error);
            Assert.Equal(3, off.Count);
            Assert.True(File.Exists(At("plugins/Some-Thing/Thing.dll.old")));
            Assert.True(File.Exists(At("plugins/Some-Thing/Sub/Helper.dll.old")));
            Assert.True(File.Exists(At("patchers/Some-Thing/ThingPatcher.dll.old")));
            Assert.False(File.Exists(At("plugins/Some-Thing/Thing.dll")));

            var row = Scan();
            Assert.True(row.Disabled);
            Assert.Equal("1.0.0", row.InstalledVersion);
            Assert.Equal("[General]\nkept = yes", File.ReadAllText(At("config/some.thing.cfg")));

            var on = ModSwitch.Enable(row, Log);

            Assert.True(on.Ok, on.Error);
            Assert.Equal(3, on.Count);
            Assert.Equal("plugin", File.ReadAllText(At("plugins/Some-Thing/Thing.dll")));
            Assert.Equal("patcher", File.ReadAllText(At("patchers/Some-Thing/ThingPatcher.dll")));
            Assert.False(Scan().Disabled);
            Assert.Equal("[General]\nkept = yes", File.ReadAllText(At("config/some.thing.cfg")));
        }

        [Fact]
        public void A_mod_with_one_plugin_file_still_loading_is_not_reported_off()
        {
            Mod();
            File.Move(At("plugins/Some-Thing/Thing.dll"), At("plugins/Some-Thing/Thing.dll.old"));

            Assert.False(Scan().Disabled);
        }

        [Fact]
        public void Switching_on_refuses_when_the_plain_name_is_already_there_and_touches_nothing()
        {
            Mod();
            Assert.True(ModSwitch.Disable(Scan(), Log).Ok);
            Write("plugins/Some-Thing/Sub/Helper.dll", "somebody put a copy back by hand");

            var on = ModSwitch.Enable(Scan(), Log);

            Assert.False(on.Ok);
            Assert.Equal("nameTaken", on.Reason);
            Assert.Contains("Helper.dll", on.Error);
            Assert.True(File.Exists(At("plugins/Some-Thing/Thing.dll.old")), "a refused switch renamed something");
        }

        [Fact]
        public void Switching_off_twice_says_it_is_already_off()
        {
            Mod();
            Assert.True(ModSwitch.Disable(Scan(), Log).Ok);

            var again = ModSwitch.Disable(Scan(), Log);

            Assert.False(again.Ok);
            Assert.Equal("alreadyOff", again.Reason);
        }

        [Fact]
        public void A_mod_with_no_plugin_file_has_nothing_to_switch()
        {
            Write("plugins/Some-Assets/manifest.json", "{\"name\":\"Assets\",\"version_number\":\"1.0.0\"}");
            Write("plugins/Some-Assets/data.json", "{}");

            var off = ModSwitch.Disable(Scan("Some-Assets"), Log);

            Assert.False(off.Ok);
            Assert.Equal("nothingToSwitch", off.Reason);
            Assert.False(Scan("Some-Assets").Disabled);
        }

        [Theory]
        [InlineData("BakaLoaderCommander")]
        [InlineData("ValheimModding-HookGenPatcher")]
        public void BakaLoaders_own_plugins_and_the_loaders_own_patchers_are_refused(string folder)
        {
            var mod = new InstalledMod
            {
                Author = "",
                ModName = folder,
                PluginDirectory = Path.GetDirectoryName(Write($"plugins/{folder}/{folder}.dll")),
            };

            var off = ModSwitch.Disable(mod, Log);

            Assert.False(off.Ok);
            Assert.Equal("protected", off.Reason);
            Assert.True(File.Exists(At($"plugins/{folder}/{folder}.dll")));
        }

        [Fact]
        public void The_monomod_part_of_a_mod_is_switched_with_it()
        {
            Mod();
            Write("monomod/Some-Thing/Assembly-CSharp.Thing.mm.dll", "monomod");

            Assert.True(ModSwitch.Disable(Scan(), Log).Ok);
            Assert.True(File.Exists(At("monomod/Some-Thing/Assembly-CSharp.Thing.mm.dll.old")));

            Assert.True(ModSwitch.Enable(Scan(), Log).Ok);
            Assert.True(File.Exists(At("monomod/Some-Thing/Assembly-CSharp.Thing.mm.dll")));
        }
    }
}
