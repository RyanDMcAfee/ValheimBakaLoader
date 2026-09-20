using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ValheimBakaLoader.Game;
using ValheimBakaLoader.Tools;
using Xunit;

namespace ValheimBakaLoader.Tests.Game
{
    /// <summary>
    /// The world SWITCHES on the command line, and the walk that brings a world's own settings
    /// in before the start that would otherwise wipe them.
    /// <para>
    /// The game reads -resetmodifiers, -preset, -modifier and -setkey in ONE pass over the
    /// command line in the order they appear. -resetmodifiers clears the world's whole starting
    /// key list and -preset clears it again before applying, so a -setkey that landed ahead of
    /// either would be wiped by it and the switch would silently never take. These pin the
    /// order both ways round.
    /// </para>
    /// <para>
    /// The other half is a key that carries a VALUE. The game takes the single argument after
    /// -setkey and adds that whole string to the world, so "carryweightrate 150" has to arrive
    /// as one argument: written bare, Windows would split it and the game would be handed the
    /// key "carryweightrate" with no value while "150" sat on the line as a flag of its own.
    /// 1.2.0 wrote every key bare.
    /// </para>
    /// </summary>
    public class ValheimServerWorldKeyArgumentTests : BaseTest, IDisposable
    {
        private readonly ValheimServer Server;
        private readonly string SandboxDir;
        private readonly IDisposable OwnRecords;

        public ValheimServerWorldKeyArgumentTests()
        {
            // A record book of its own: starting a server clears the shared companion-plugin
            // record for the realm it starts. See CompanionPluginStatusTests.
            OwnRecords = CompanionPluginStatus.BeginOwnRecords();
            Server = GetService<ValheimServer>();

            SandboxDir = Path.Combine(Path.GetTempPath(), "vbl-worldkeys-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(SaveFolder);
            File.WriteAllBytes(Path.Combine(SandboxDir, "valheim_server.exe"), Array.Empty<byte>());
        }

        private string SaveFolder => Path.Combine(SandboxDir, "saves");

        public void Dispose()
        {
            try { Directory.Delete(SandboxDir, true); } catch { /* best-effort temp cleanup */ }
            OwnRecords.Dispose();
            GC.SuppressFinalize(this);
        }

        // ------------------------------------------------------------------ the order

        [Fact]
        public void Every_switch_is_written_after_the_reset_that_would_clear_it()
        {
            var args = Launch(options => options.WorldKeys =
                new HashSet<string> { "nobuildcost", "playerevents", "passivemobs", "nomap", "fire" });

            var reset = args.IndexOf("-resetmodifiers", StringComparison.Ordinal);
            Assert.True(reset >= 0, "the reset was not emitted: " + args);

            foreach (var key in WorldGen.Switches)
            {
                var at = args.IndexOf("-setkey " + key, StringComparison.Ordinal);
                Assert.True(at > reset, $"'{key}' is missing or does not follow the reset: {args}");
            }
        }

        /// <summary>
        /// A preset clears the starting keys a SECOND time, inside the same pass, before it
        /// applies its own. A switch written ahead of it would be wiped by it, so the switches
        /// have to come after the preset as well as after the reset.
        /// </summary>
        [Fact]
        public void Every_switch_is_written_after_a_preset_that_would_clear_it_again()
        {
            var args = Launch(options =>
            {
                options.WorldPreset = "hard";
                options.WorldKeys = new HashSet<string> { "nobuildcost", "nomap" };
            });

            var preset = args.IndexOf("-preset hard", StringComparison.Ordinal);
            Assert.True(preset >= 0, "the preset was not emitted: " + args);

            foreach (var key in new[] { "nobuildcost", "nomap" })
            {
                var at = args.IndexOf("-setkey " + key, StringComparison.Ordinal);
                Assert.True(at > preset, $"'{key}' must follow the preset that clears for it: {args}");
            }
        }

        [Fact]
        public void Every_switch_is_written_after_every_dial_too()
        {
            var args = Launch(options =>
            {
                options.WorldModifiers = new Dictionary<string, string>
                {
                    ["combat"] = "hard",
                    ["portals"] = "veryhard",
                };
                options.WorldKeys = new HashSet<string> { "nobuildcost" };
            });

            Assert.True(
                args.IndexOf("-setkey nobuildcost", StringComparison.Ordinal)
                > args.IndexOf("-modifier portals veryhard", StringComparison.Ordinal),
                "the switches must follow the dials: " + args);
        }

        // ------------------------------------------------------------------ the quoting

        /// <summary>
        /// The pass-through case. This is the exact string a host carrying a console-set key
        /// has in their world, and it has to reach the game as ONE argument.
        /// </summary>
        [Fact]
        public void A_key_that_carries_a_value_is_quoted_so_it_arrives_as_one_argument()
        {
            var args = Launch(options => options.WorldKeys =
                new HashSet<string> { "carryweightrate 150" });

            Assert.Contains("-setkey \"carryweightrate 150\"", args);
            // and never as the two arguments an unquoted one would split into
            Assert.DoesNotContain("-setkey carryweightrate 150", args);
        }

        [Fact]
        public void A_bare_switch_is_still_written_without_quotes()
        {
            var args = Launch(options => options.WorldKeys = new HashSet<string> { "nomap" });

            Assert.Contains("-setkey nomap", args);
            Assert.DoesNotContain("-setkey \"nomap\"", args);
        }

        /// <summary>
        /// There is no way to put a double quote inside a Windows command line argument that
        /// both survives the split and means what it said, and a half-escaped one would swallow
        /// the rest of the line. Such a key is left out rather than guessed at.
        /// </summary>
        [Fact]
        public void A_key_carrying_a_quote_is_left_out_rather_than_breaking_the_line()
        {
            var args = Launch(options => options.WorldKeys =
                new HashSet<string> { "nomap", "silly\"key 1" });

            Assert.Contains("-setkey nomap", args);
            Assert.DoesNotContain("silly", args);
            Assert.Equal(1, Occurrences(args, "-setkey "));
        }

        /// <summary>
        /// The whole line, exactly, with a dial, a switch and a value key on it. The literal is
        /// the point: it is what the game receives.
        /// </summary>
        [Fact]
        public void The_whole_command_line_reads_in_the_documented_order()
        {
            var args = Launch(options =>
            {
                options.WorldModifiers = new Dictionary<string, string> { ["combat"] = "hard" };
                // A list rather than a set so the order in the line is the order written here.
                options.WorldKeys = new HashSet<string> { "nobuildcost" };
            });

            Assert.Equal(
                "-nographics -batchmode -name \"Test Server\" -port 2456 -world \"Test World\" -public 0 " +
                $"-savedir \"{SaveFolder}\" " +
                "-saveinterval 30 -backups 1 -backupshort 60 -backuplong 120 " +
                "-resetmodifiers -modifier combat hard -setkey nobuildcost",
                args);
        }

        // ------------------------------------------------------------------ restart pending

        /// <summary>
        /// A switch has to behave like a dial everywhere, and "restart pending" is the one that
        /// is easiest to get wrong, because the comparison is built out of the command line.
        /// Proved rather than assumed.
        /// </summary>
        [Fact]
        public void Turning_a_switch_on_while_the_world_is_up_raises_a_restart()
        {
            var running = Options();
            var saved = Options();
            saved.WorldKeys = new HashSet<string> { "nobuildcost" };

            Assert.True(ValheimServerOptions.RelaunchWouldDiffer(running, saved));
            Assert.NotEqual(
                ValheimServerOptions.RelaunchSignature(running),
                ValheimServerOptions.RelaunchSignature(saved));
        }

        [Fact]
        public void The_same_switches_in_another_order_are_the_same_launch()
        {
            var running = Options();
            running.WorldKeys = new HashSet<string> { "nobuildcost", "nomap" };
            var saved = Options();
            saved.WorldKeys = new HashSet<string> { "nomap", "nobuildcost" };

            Assert.False(ValheimServerOptions.RelaunchWouldDiffer(running, saved));
        }

        [Fact]
        public void A_pass_through_key_counts_in_the_comparison_like_any_other()
        {
            var running = Options();
            running.WorldKeys = new HashSet<string> { "carryweightrate 150" };
            var saved = Options();
            saved.WorldKeys = new HashSet<string> { "carryweightrate 200" };

            Assert.True(ValheimServerOptions.RelaunchWouldDiffer(running, saved));
        }

        // ------------------------------------------------------------------ the upgrade walk

        /// <summary>
        /// The whole defect, end to end. A world on disk carries No build cost and a Hard combat
        /// set, the profile has never heard of it, and a start happens. On 1.2.0 that start
        /// emitted -resetmodifiers and nothing else, and the world's settings were gone the
        /// moment it next saved.
        /// </summary>
        [Fact]
        public void A_world_that_carries_its_own_settings_keeps_them_across_its_first_start()
        {
            WriteWorldHeader("Test World", new[]
            {
                "nobuildcost",
                "playerdamage 85", "enemydamage 150", "enemyspeedsize 110", "enemyleveluprate 120",
            });

            var worlds = new WorldKeyImportTests.InMemoryWorlds();
            Assert.Null(worlds.LoadPreferences("Test World"));     // the profile has never heard of it

            var outcome = WorldKeyImportStep.Run(
                worlds,
                () => FwlReader.TryReadWorldStartingKeys(SaveFolder, "Test World"),
                "Test World");
            Assert.Equal(WorldKeyImportKind.Imported, outcome.Kind);

            // The prefs now hold what the world had.
            var stored = worlds.LoadPreferences("Test World");
            Assert.Equal("hard", stored.Modifiers["combat"]);
            Assert.Equal(new[] { "nobuildcost" }, stored.Keys.ToArray());

            // And the start that follows carries them, after the reset that clears for them.
            var args = LaunchFrom(stored);
            var reset = args.IndexOf("-resetmodifiers", StringComparison.Ordinal);
            Assert.True(reset >= 0, args);
            Assert.True(args.IndexOf("-modifier combat hard", StringComparison.Ordinal) > reset, args);
            Assert.True(args.IndexOf("-setkey nobuildcost", StringComparison.Ordinal) > reset, args);
        }

        /// <summary>
        /// The second start. The import happens once: by then the stored settings are the host's
        /// own, and a switch they turned OFF here must not be read back in from the world.
        /// </summary>
        [Fact]
        public void A_second_start_brings_nothing_in_and_changes_nothing()
        {
            WriteWorldHeader("Test World", new[] { "nobuildcost", "resourcerate 150" });

            var worlds = new WorldKeyImportTests.InMemoryWorlds();
            WorldKeyImportStep.Run(
                worlds, () => FwlReader.TryReadWorldStartingKeys(SaveFolder, "Test World"), "Test World");

            var firstArgs = LaunchFrom(worlds.LoadPreferences("Test World"));

            // The host turns the switch off between the two starts.
            var edited = worlds.LoadPreferences("Test World");
            edited.Keys = new HashSet<string>();
            worlds.SavePreferences(edited);

            var second = WorldKeyImportStep.Run(
                worlds, () => FwlReader.TryReadWorldStartingKeys(SaveFolder, "Test World"), "Test World");

            Assert.Equal(WorldKeyImportKind.AlreadyKnown, second.Kind);
            Assert.Empty(worlds.LoadPreferences("Test World").Keys);

            var secondArgs = LaunchFrom(worlds.LoadPreferences("Test World"));
            Assert.Contains("-setkey nobuildcost", firstArgs);
            Assert.DoesNotContain("-setkey nobuildcost", secondArgs);
            Assert.Contains("-modifier resources more", secondArgs);   // the dial the host kept
        }

        /// <summary>
        /// A realm forged over a world that is already on disk goes through the very same first
        /// meeting, because servers.create is a start path like any other as far as the world is
        /// concerned: what the host chooses in the wizard is written ON TOP of what came in.
        /// </summary>
        [Fact]
        public void A_realm_forged_over_an_existing_world_imports_it_before_the_wizards_choices_land()
        {
            WriteWorldHeader("Test World", new[] { "nomap", "carryweightrate 150" });

            var worlds = new WorldKeyImportTests.InMemoryWorlds();

            // servers.create: the import first ...
            WorldKeyImportStep.Run(
                worlds, () => FwlReader.TryReadWorldStartingKeys(SaveFolder, "Test World"), "Test World");

            // ... then the wizard's own dials and switches on top of it. The dials go OVER what
            // came in rather than in place of it: the forge sends only the dials the host moved,
            // so silence about one is not a statement about it.
            var prefs = worlds.LoadPreferences("Test World");
            foreach (var pair in new Dictionary<string, string> { ["combat"] = "hard" })
                prefs.Modifiers[pair.Key] = pair.Value;
            prefs.Keys = ValheimBakaLoader.Forms.BlendWindow.MergeWorldKeys(
                prefs.Keys, new HashSet<string> { "nobuildcost" });
            worlds.SavePreferences(prefs);

            var stored = worlds.LoadPreferences("Test World");
            Assert.Equal("hard", stored.Modifiers["combat"]);
            Assert.Contains("nobuildcost", stored.Keys);
            Assert.DoesNotContain("nomap", stored.Keys);            // the wizard replaced the switches
            Assert.Contains("carryweightrate 150", stored.Keys);    // and never touched the rest
        }

        /// <summary>
        /// And the dial half of that, which is where the forge threw away what it had just read.
        /// A world carrying four dials of its own, one dial moved in the wizard, and the line
        /// that goes to the game has to carry all four. Replacing the map instead of writing
        /// over it left the line with the one dial the host touched and nothing else, while the
        /// dialog they were looking at still showed Normal for the other four.
        /// </summary>
        [Fact]
        public void A_realm_forged_over_a_world_launches_with_the_dials_that_world_had()
        {
            WriteWorldHeader("Test World", new[]
            {
                // Combat very hard, Resources more, Raids less, Portals hard
                "playerdamage 70", "enemydamage 200", "enemyspeedsize 120", "enemyleveluprate 140",
                "resourcerate 150", "eventrate 150", "nobossportals",
            });

            var worlds = new WorldKeyImportTests.InMemoryWorlds();
            WorldKeyImportStep.Run(
                worlds, () => FwlReader.TryReadWorldStartingKeys(SaveFolder, "Test World"), "Test World");

            var imported = worlds.LoadPreferences("Test World");
            Assert.Equal("veryhard", imported.Modifiers["combat"]);
            Assert.Equal("more", imported.Modifiers["resources"]);

            // servers.create with ONE dial moved in the forge.
            foreach (var pair in new Dictionary<string, string> { ["combat"] = "hard" })
                imported.Modifiers[pair.Key] = pair.Value;
            worlds.SavePreferences(imported);

            var args = LaunchFrom(worlds.LoadPreferences("Test World"));
            Assert.Contains("-modifier combat hard", args, StringComparison.Ordinal);
            Assert.Contains("-modifier resources more", args, StringComparison.Ordinal);
            Assert.Contains("-modifier raids less", args, StringComparison.Ordinal);
            Assert.Contains("-modifier portals hard", args, StringComparison.Ordinal);
            Assert.DoesNotContain("-modifier combat veryhard", args, StringComparison.Ordinal);
        }

        [Fact]
        public void An_unreadable_world_header_never_holds_a_start_back()
        {
            var worldsDir = Path.Combine(SaveFolder, "worlds_local");
            Directory.CreateDirectory(worldsDir);
            File.WriteAllBytes(Path.Combine(worldsDir, "Test World.fwl"), new byte[] { 9, 9, 9, 9 });

            var worlds = new WorldKeyImportTests.InMemoryWorlds();
            var outcome = WorldKeyImportStep.Run(
                worlds, () => FwlReader.TryReadWorldStartingKeys(SaveFolder, "Test World"), "Test World");

            Assert.Equal(WorldKeyImportKind.NoHeader, outcome.Kind);
            Assert.Null(worlds.LoadPreferences("Test World"));

            // and the start goes ahead exactly as it always did
            var args = Launch();
            Assert.Contains("-resetmodifiers", args);
        }

        #region Plumbing

        private ValheimServerOptions Options() => new()
        {
            Name = "Test Server",
            WorldName = "Test World",
            Port = 2456,
            Public = false,
            SaveInterval = 30,
            Backups = 1,
            BackupShort = 60,
            BackupLong = 120,
            LogToFile = false,
            ServerExePath = Path.Combine(SandboxDir, "valheim_server.exe"),
            SaveDataFolderPath = SaveFolder,
        };

        /// <summary>
        /// Starts the server against the sandbox and hands back the command line it built. No
        /// launch guard is wired in a fresh container, so Start() runs the launch inline and the
        /// tracked process is there to read the moment it returns.
        /// </summary>
        private string Launch(Action<ValheimServerOptions> tweak = null)
        {
            var options = Options();
            tweak?.Invoke(options);
            Server.Start(options);

            var process = Server.GetTrackedProcess();
            Assert.NotNull(process);
            return process.StartInfo.Arguments;
        }

        /// <summary>
        /// The same launch, with the world half of the options built the way every start path
        /// builds it: out of the stored world preferences.
        /// </summary>
        private string LaunchFrom(WorldPreferences worldPrefs)
        {
            var serverPrefs = new ServerPreferences
            {
                ProfileName = "Test",
                Name = "Test Server",
                WorldName = "Test World",
                Port = 2456,
                SaveInterval = 30,
                BackupCount = 1,
                BackupIntervalShort = 60,
                BackupIntervalLong = 120,
                WriteServerLogsToFile = false,
                ServerExePath = Path.Combine(SandboxDir, "valheim_server.exe"),
                SaveDataFolderPath = SaveFolder,
            };

            var options = ValheimServerOptions.FromPreferences(
                serverPrefs, UserPreferences.GetDefault(), worldPrefs);

            // A server of its own for each start. ValheimServer is transient in the container,
            // and Stop() only SIGNALS a shutdown, so reusing one instance across two launches
            // would read the first launch's process again and call it the second's.
            var server = GetService<ValheimServer>();
            server.Start(options);

            var process = server.GetTrackedProcess();
            Assert.NotNull(process);
            return process.StartInfo.Arguments;
        }

        /// <summary>
        /// A legacy .fwl for the sandbox world, carrying the given starting keys. Written by
        /// hand from the layout the game writes, so no real world file is ever copied.
        /// </summary>
        private void WriteWorldHeader(string world, IReadOnlyList<string> keys)
        {
            var dir = Path.Combine(SaveFolder, "worlds_local");
            Directory.CreateDirectory(dir);

            using var payload = new MemoryStream();
            using (var bw = new BinaryWriter(payload, Encoding.UTF8, leaveOpen: true))
            {
                bw.Write(37);                 // worldVersion
                bw.Write(world);
                bw.Write("SeedSeed99");
                bw.Write(123456789);
                bw.Write(987654321L);
                bw.Write(2);                  // worldGenVersion
                bw.Write(false);              // needsDB
                bw.Write(keys.Count);
                foreach (var key in keys) bw.Write(key);
            }

            var bytes = payload.ToArray();
            using var file = new MemoryStream();
            using (var bw = new BinaryWriter(file, Encoding.UTF8, leaveOpen: true))
            {
                bw.Write(bytes.Length);
                bw.Write(bytes);
            }

            File.WriteAllBytes(Path.Combine(dir, world + ".fwl"), file.ToArray());
        }

        private static int Occurrences(string haystack, string needle)
        {
            var count = 0;
            for (var at = haystack.IndexOf(needle, StringComparison.Ordinal);
                 at >= 0;
                 at = haystack.IndexOf(needle, at + needle.Length, StringComparison.Ordinal))
            {
                count++;
            }

            return count;
        }

        #endregion
    }
}
