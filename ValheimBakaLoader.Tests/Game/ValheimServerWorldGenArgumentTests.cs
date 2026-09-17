using System;
using System.Collections.Generic;
using System.IO;
using ValheimBakaLoader.Game;
using ValheimBakaLoader.Tools;
using Xunit;

namespace ValheimBakaLoader.Tests.Game
{
    /// <summary>
    /// The world-generation half of the server command line.
    /// <para>
    /// BakaLoader owns a world's difficulty. The game stores those settings as keys in the
    /// world's .fwl and never clears a category on its own, so every start empties that list
    /// with -resetmodifiers and then writes the whole intended set back. The game applies
    /// -resetmodifiers, -preset, -modifier and -setkey in one pass over the command line in
    /// the order they appear, which makes the position of the reset load bearing: after those
    /// flags it would wipe exactly what they had just set and the world would come up vanilla.
    /// These tests pin that order, and pin that the reset is emitted even when nothing is
    /// configured, which is the case that needs it most (an all-Normal profile is a request
    /// for a world with no modifiers on it).
    /// </para>
    /// </summary>
    public class ValheimServerWorldGenArgumentTests : BaseTest, IDisposable
    {
        private readonly ValheimServer Server;

        // Throwaway on-disk sandbox (dummy exe + save folder) so Start()'s path validation
        // passes anywhere without a real Valheim install. The process is never launched;
        // BaseTest swaps in MockProcessProvider, which keeps the generated arguments on the
        // Process it hands back.
        private readonly string SandboxDir;

        // A record book of its own. Starting a server runs the companion plugin install pass,
        // and that pass clears the record for the realm it is starting, which walks over what a
        // class reading the shared record had written. See
        // CompanionPluginStatusTests.Every_test_class_that_touches_the_record_keeps_a_book_of_its_own.
        private readonly IDisposable OwnRecords;

        public ValheimServerWorldGenArgumentTests()
        {
            OwnRecords = CompanionPluginStatus.BeginOwnRecords();
            Server = GetService<ValheimServer>();

            SandboxDir = Path.Combine(Path.GetTempPath(), "vbl-worldgen-" + Guid.NewGuid().ToString("N"));
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
        public void The_reset_comes_before_the_modifiers_and_the_keys_it_clears_for()
        {
            var args = Launch(options =>
            {
                options.WorldModifiers = new Dictionary<string, string> { ["combat"] = "hard" };
                options.WorldKeys = new HashSet<string> { "nomap" };
            });

            var reset = args.IndexOf("-resetmodifiers", StringComparison.Ordinal);
            var modifier = args.IndexOf("-modifier combat hard", StringComparison.Ordinal);
            var key = args.IndexOf("-setkey nomap", StringComparison.Ordinal);

            Assert.True(reset >= 0, "the reset was not emitted: " + args);
            Assert.True(modifier > reset, "the modifier must follow the reset: " + args);
            Assert.True(key > reset, "the key must follow the reset: " + args);
        }

        [Fact]
        public void Every_modifier_and_key_still_follows_the_reset()
        {
            // The reset is only half the job: what the host chose has to be written back
            // immediately afterwards, or the world launches with nothing on it at all.
            var args = Launch(options =>
            {
                options.WorldModifiers = new Dictionary<string, string>
                {
                    ["combat"] = "hard",
                    ["deathpenalty"] = "casual",
                    ["portals"] = "veryhard",
                };
                options.WorldKeys = new HashSet<string> { "nomap", "passivemobs" };
            });

            var reset = args.IndexOf("-resetmodifiers", StringComparison.Ordinal);
            Assert.True(reset >= 0, "the reset was not emitted: " + args);

            foreach (var flag in new[]
            {
                "-modifier combat hard",
                "-modifier deathpenalty casual",
                "-modifier portals veryhard",
                "-setkey nomap",
                "-setkey passivemobs",
            })
            {
                var at = args.IndexOf(flag, StringComparison.Ordinal);
                Assert.True(at > reset, $"'{flag}' is missing or does not follow the reset: {args}");
            }
        }

        [Fact]
        public void The_reset_comes_before_a_preset_too()
        {
            var args = Launch(options => options.WorldPreset = "hard");

            var reset = args.IndexOf("-resetmodifiers", StringComparison.Ordinal);
            var preset = args.IndexOf("-preset hard", StringComparison.Ordinal);

            Assert.True(reset >= 0, "the reset was not emitted: " + args);
            Assert.True(preset > reset, "the preset must follow the reset: " + args);
        }

        [Fact]
        public void A_world_with_nothing_configured_is_still_reset()
        {
            // All-Normal is not "leave the world alone", it is "this world has no modifiers".
            // Without the reset, a world that used to be Hard would keep every leftover key.
            var args = Launch();

            Assert.Contains("-resetmodifiers", args);
            Assert.DoesNotContain("-modifier ", args);
            Assert.DoesNotContain("-setkey ", args);
            Assert.DoesNotContain("-preset ", args);
        }

        [Fact]
        public void The_reset_is_emitted_exactly_once()
        {
            // Guards a second emission path being added by accident later: a repeat further
            // down the line would clear everything the first pass had already written.
            var args = Launch(options =>
            {
                options.WorldModifiers = new Dictionary<string, string> { ["resources"] = "more" };
                options.WorldKeys = new HashSet<string> { "nobuildcost" };
                options.AdditionalArgs = "-console";
            });

            Assert.Equal(1, Occurrences(args, "-resetmodifiers"));
        }

        [Fact]
        public void A_host_typed_reset_is_dropped_and_ours_stays_first_and_alone()
        {
            // The extra arguments go on the end of the command line, so a reset typed in
            // there would be read after the modifiers and clear the keys they had just
            // written: the world would launch with nothing on it. The guard takes it out
            // and leaves BakaLoader's own, once, ahead of everything it clears for.
            var args = Launch(options =>
            {
                options.WorldModifiers = new Dictionary<string, string> { ["combat"] = "hard" };
                options.WorldKeys = new HashSet<string> { "nomap" };
                options.AdditionalArgs = "-console -resetmodifiers -crossplay";
            });

            Assert.Equal(1, Occurrences(args, "-resetmodifiers"));

            var reset = args.IndexOf("-resetmodifiers", StringComparison.Ordinal);
            Assert.True(reset >= 0, "the reset was not emitted: " + args);
            Assert.True(
                reset < args.IndexOf("-modifier combat hard", StringComparison.Ordinal),
                "the surviving reset must be ours, ahead of the modifiers: " + args);
            Assert.True(
                reset < args.IndexOf("-setkey nomap", StringComparison.Ordinal),
                "the surviving reset must be ours, ahead of the keys: " + args);

            // Everything else the host typed is still their business.
            Assert.EndsWith("-console -crossplay", args);
        }

        [Fact]
        public void The_host_typed_extra_arguments_still_come_last()
        {
            var args = Launch(options =>
            {
                options.WorldModifiers = new Dictionary<string, string> { ["raids"] = "none" };
                options.AdditionalArgs = "-console";
            });

            Assert.EndsWith("-console", args);
            Assert.True(
                args.IndexOf("-console", StringComparison.Ordinal)
                > args.IndexOf("-modifier raids none", StringComparison.Ordinal),
                "the extra arguments must stay at the end: " + args);
        }

        [Fact]
        public void The_whole_command_line_reads_in_the_documented_order()
        {
            // One modifier and one key on purpose: the exact string is the point here, and a
            // second entry in either collection would leave the order up to the collection.
            var args = Launch(options =>
            {
                options.WorldModifiers = new Dictionary<string, string> { ["combat"] = "hard" };
                options.WorldKeys = new HashSet<string> { "nomap" };
                options.AdditionalArgs = "-console";
            });

            Assert.Equal(
                "-nographics -batchmode -name \"Test Server\" -port 2456 -world \"Test World\" -public 0 " +
                $"-savedir \"{Path.Combine(SandboxDir, "saves")}\" " +
                "-saveinterval 30 -backups 1 -backupshort 60 -backuplong 120 " +
                "-password \"hunter2\" -crossplay " +
                "-resetmodifiers -modifier combat hard -setkey nomap -console",
                args);
        }

        #region Plumbing

        /// <summary>
        /// Starts the server against the sandbox and hands back the command line it built.
        /// No launch guard is wired in a fresh container, so Start() runs the launch inline
        /// and the tracked process is there to read the moment it returns.
        /// </summary>
        private string Launch(Action<ValheimServerOptions> tweak = null)
        {
            var options = new ValheimServerOptions
            {
                Name = "Test Server",
                WorldName = "Test World",
                Password = "hunter2",
                Port = 2456,
                Public = false,
                Crossplay = true,
                SaveInterval = 30,
                Backups = 1,
                BackupShort = 60,
                BackupLong = 120,
                LogToFile = false,
                ServerExePath = Path.Combine(SandboxDir, "valheim_server.exe"),
                SaveDataFolderPath = Path.Combine(SandboxDir, "saves"),
            };

            tweak?.Invoke(options);
            Server.Start(options);

            var process = Server.GetTrackedProcess();
            Assert.NotNull(process);
            return process.StartInfo.Arguments;
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
