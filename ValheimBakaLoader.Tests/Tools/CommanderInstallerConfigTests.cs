using BakaLoaderSpawn;
using Moq;
using System;
using System.IO;
using ValheimBakaLoader.Tools;
using ValheimBakaLoader.Tools.Logging;
using Xunit;

namespace ValheimBakaLoader.Tests.Tools
{
    /// <summary>
    /// com.baka.commander.cfg, which BakaLoader rewrites from the server profile on every
    /// single start.
    /// <para>
    /// That rewrite is right for the three things the app owns, the port, the password and
    /// whether the listener runs at all, and it was quietly wrong for everything else. It kept
    /// BindAddress and threw the rest away, and the rest had grown a section: [Spawning], with
    /// MarkSpawnedAsCheated in it. A host who set that to true had it deleted before BepInEx
    /// ever opened the file, so the setting could not be turned on at all for the plugin that
    /// is serving RCON, which is the normal arrangement and the only one BakaLoader sets up by
    /// itself. Writing it again after a start did not help either: the file had already been
    /// read, and the next start wiped it again.
    /// </para>
    /// <para>
    /// Everything here writes into a temporary folder. Nothing goes near a game install.
    /// </para>
    /// </summary>
    public class CommanderInstallerConfigTests : IDisposable
    {
        private readonly string Root =
            Path.Combine(Path.GetTempPath(), "bakaloader-commandercfg-" + Guid.NewGuid().ToString("N"));

        private string PluginsDir => Path.Combine(Root, "BepInEx", "plugins");

        private string ConfigPath => Path.Combine(Root, "BepInEx", "config", "com.baka.commander.cfg");

        public CommanderInstallerConfigTests()
        {
            // EnsureConfig only writes for an install that is actually there, so the plugin
            // has to look installed. The bytes are never read by anything under test.
            var installed = Path.Combine(PluginsDir, CommanderInstaller.PluginFolderName);
            Directory.CreateDirectory(installed);
            File.WriteAllText(Path.Combine(installed, CommanderInstaller.PluginDllName), "not a dll");
            Directory.CreateDirectory(Path.Combine(Root, "BepInEx", "config"));
        }

        public void Dispose()
        {
            try { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); } catch (Exception) { }
        }

        private static CommanderInstaller Installer() =>
            new CommanderInstaller(Mock.Of<IApplicationLogger>());

        private void Start(int port = 25575, string password = "hunter2") =>
            Installer().EnsureConfig(PluginsDir, rconEnabled: true, rconPort: port, rconPassword: password);

        /// <summary>A .cfg the way BepInEx writes one, with the spawn section the host edited.</summary>
        private static string WithSpawning(string value) =>
            "## Settings file for BakaLoader Commander (com.baka.commander).\r\n" +
            "\r\n" +
            "[Server]\r\n" +
            "\r\n" +
            "Enabled = true\r\n" +
            "Port = 11111\r\n" +
            "Password = whatever\r\n" +
            "BindAddress = 0.0.0.0\r\n" +
            "\r\n" +
            "[Spawning]\r\n" +
            "\r\n" +
            "## Mark everything this plugin spawns as summoned through cheating.\r\n" +
            "# Setting type: Boolean\r\n" +
            "# Default value: false\r\n" +
            "MarkSpawnedAsCheated = " + value + "\r\n";

        // ------------------------------------------------------------------ the section survives

        /// <summary>
        /// The one that matters. A host sets it to true while the server is stopped, the
        /// server starts, and the setting is still true when BepInEx reads the file.
        /// </summary>
        [Fact]
        public void A_hosts_true_survives_a_server_start()
        {
            File.WriteAllText(ConfigPath, WithSpawning("true"));

            Start();

            var written = File.ReadAllText(ConfigPath);

            Assert.Contains("[" + SpawnMark.ConfigSection + "]", written, StringComparison.Ordinal);
            Assert.Contains(SpawnMark.ConfigKey + " = true", written, StringComparison.Ordinal);

            // Carried across whole, not reduced to the one line that holds the value: the
            // comments above it are what tells a host the change needs a restart.
            Assert.Contains("## Mark everything this plugin spawns as summoned through cheating.",
                written, StringComparison.Ordinal);
            Assert.Contains("# Setting type: Boolean", written, StringComparison.Ordinal);
        }

        /// <summary>
        /// And it survives the start after that, and the one after that. A rewrite that
        /// preserved the section once and dropped it on the second pass would look fixed for
        /// exactly one server start, which is the shape of bug a single-run test never sees.
        /// </summary>
        [Fact]
        public void It_survives_every_start_after_that_too()
        {
            File.WriteAllText(ConfigPath, WithSpawning("true"));

            Start();
            var once = File.ReadAllText(ConfigPath);

            Start();
            var twice = File.ReadAllText(ConfigPath);

            Start(port: 25580, password: "changed");
            var thrice = File.ReadAllText(ConfigPath);

            Assert.Equal(once, twice);
            Assert.Contains(SpawnMark.ConfigKey + " = true", thrice, StringComparison.Ordinal);

            // The three things the app owns still change with the profile.
            Assert.Contains("Port = 25580", thrice, StringComparison.Ordinal);
            Assert.Contains("Password = changed", thrice, StringComparison.Ordinal);
        }

        /// <summary>A false the host set on purpose is a value too, and is not "missing".</summary>
        [Fact]
        public void A_hosts_false_survives_as_well()
        {
            File.WriteAllText(ConfigPath, WithSpawning("false"));

            Start();

            Assert.Contains(SpawnMark.ConfigKey + " = false", File.ReadAllText(ConfigPath), StringComparison.Ordinal);
        }

        // ------------------------------------------------------------------ and stays away

        /// <summary>
        /// A section that was never there stays away. The plugin binds its own default on the
        /// next start and writes the entry out itself, which is the file every host who has
        /// never touched this setting already has.
        /// </summary>
        [Fact]
        public void A_missing_section_stays_missing()
        {
            File.WriteAllText(
                ConfigPath,
                "[Server]\r\nEnabled = true\r\nPort = 11111\r\nBindAddress = 127.0.0.1\r\n");

            Start();

            Assert.DoesNotContain(Environment.NewLine + "[" + SpawnMark.ConfigSection + "]",
                File.ReadAllText(ConfigPath), StringComparison.Ordinal);
            Assert.DoesNotContain(SpawnMark.ConfigKey, File.ReadAllText(ConfigPath), StringComparison.Ordinal);
        }

        /// <summary>And so does a first start, where there is no file to read anything out of.</summary>
        [Fact]
        public void A_first_start_writes_no_spawn_section()
        {
            Start();

            var written = File.ReadAllText(ConfigPath);

            Assert.DoesNotContain(Environment.NewLine + "[" + SpawnMark.ConfigSection + "]",
                written, StringComparison.Ordinal);
            Assert.DoesNotContain(SpawnMark.ConfigKey, written, StringComparison.Ordinal);
            Assert.Contains("[Server]", written, StringComparison.Ordinal);
            Assert.Contains("BindAddress = 127.0.0.1", written, StringComparison.Ordinal);
        }

        // ------------------------------------------------------------------ the bind address still

        /// <summary>
        /// The thing this method already did. Preserving a second thing must not cost the
        /// first one, and the two are read out of the same file in the same pass.
        /// </summary>
        [Fact]
        public void The_bind_address_is_still_preserved_beside_it()
        {
            File.WriteAllText(ConfigPath, WithSpawning("true"));

            Start();

            var written = File.ReadAllText(ConfigPath);

            Assert.Contains("BindAddress = 0.0.0.0", written, StringComparison.Ordinal);
            Assert.Contains(SpawnMark.ConfigKey + " = true", written, StringComparison.Ordinal);
        }

        // ------------------------------------------------------------------ the reader itself

        [Fact]
        public void A_section_at_the_end_of_a_file_is_read_to_the_end()
        {
            var read = CommanderInstaller.ExtractSection(WithSpawning("true"), "Spawning");

            Assert.NotNull(read);
            Assert.StartsWith("[Spawning]", read, StringComparison.Ordinal);
            Assert.EndsWith("MarkSpawnedAsCheated = true", read, StringComparison.Ordinal);
        }

        [Fact]
        public void A_section_stops_at_the_next_one()
        {
            const string cfg =
                "[Spawning]\nMarkSpawnedAsCheated = true\n\n[Server]\nPort = 1\nBindAddress = 0.0.0.0\n";

            var read = CommanderInstaller.ExtractSection(cfg, "Spawning");

            Assert.Equal("[Spawning]\nMarkSpawnedAsCheated = true", read);
        }

        [Theory]
        [InlineData("[Server]\nPort = 1\n")]
        [InlineData("")]
        [InlineData(null)]
        public void A_file_without_the_section_reads_as_nothing(string cfg)
        {
            Assert.Null(CommanderInstaller.ExtractSection(cfg, "Spawning"));
        }

        /// <summary>
        /// A section with nothing under it yet is still a section the host wrote. Answering
        /// null for it would delete a header somebody typed, which is a small thing to get
        /// wrong and an annoying one to notice.
        /// </summary>
        [Fact]
        public void An_empty_section_is_still_a_section()
        {
            Assert.Equal("[Spawning]", CommanderInstaller.ExtractSection("[Spawning]\n\n\n", "Spawning"));
        }

        /// <summary>
        /// A header a host typed with spaces around it, or in another case, is the same
        /// header. BepInEx writes it one way and people edit files the other.
        /// </summary>
        [Theory]
        [InlineData("  [Spawning]  \nMarkSpawnedAsCheated = true\n")]
        [InlineData("[spawning]\nMarkSpawnedAsCheated = true\n")]
        public void A_header_is_matched_whatever_the_spacing_and_the_case(string cfg)
        {
            Assert.Equal("[Spawning]\nMarkSpawnedAsCheated = true", CommanderInstaller.ExtractSection(cfg, "Spawning"));
        }

        /// <summary>
        /// Whatever line endings the file arrived with, the section comes back as lines, and
        /// the rewrite writes them out the way this machine writes lines.
        /// </summary>
        [Fact]
        public void The_lines_of_the_written_file_all_end_the_same_way()
        {
            File.WriteAllText(ConfigPath, WithSpawning("true").Replace("\r\n", "\n"));

            Start();

            var bytes = File.ReadAllText(ConfigPath);

            Assert.Contains(SpawnMark.ConfigKey + " = true", bytes, StringComparison.Ordinal);
            Assert.DoesNotContain("\r\r", bytes, StringComparison.Ordinal);
            Assert.Equal(
                bytes.Split('\n').Length - 1,
                bytes.Split(new[] { Environment.NewLine }, StringSplitOptions.None).Length - 1);
        }
    }
}
