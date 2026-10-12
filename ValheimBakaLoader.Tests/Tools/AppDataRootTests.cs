using Serilog;
using Serilog.Events;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using ValheimBakaLoader.Properties;
using ValheimBakaLoader.Tools;
using Xunit;

namespace ValheimBakaLoader.Tests.Tools
{
    /// <summary>
    /// The tests that set one of the BAKALOADER_* variables on the process. Any other test
    /// that reads a data path while one is set would read the moved path, so these run alone.
    /// </summary>
    [CollectionDefinition(Name, DisableParallelization = true)]
    public sealed class ProcessEnvironmentCollection
    {
        public const string Name = "Process environment";
    }

    /// <summary>
    /// <see cref="AppDataRoot"/>: the BakaLoader data folder, the default Valheim save folder and
    /// the self-update work folder, each movable with an environment variable for an isolated
    /// test run and each exactly the shipped path when the variable is unset or unusable.
    /// </summary>
    public class AppDataRootTests : IDisposable
    {
        private readonly string Scratch = Path.Combine(
            Path.GetTempPath(), "vbl-appdataroot-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try { if (Directory.Exists(Scratch)) Directory.Delete(Scratch, true); } catch { }
        }

        // ------------------------------------------------------------- unset: shipped paths

        [Fact]
        public void Unset_every_path_is_the_shipped_string_byte_for_byte()
        {
            Assert.Null(Environment.GetEnvironmentVariable(AppDataRoot.DataVariable));
            Assert.Null(Environment.GetEnvironmentVariable(AppDataRoot.ValheimSaveVariable));
            Assert.Null(Environment.GetEnvironmentVariable(AppDataRoot.UpdateVariable));

            Assert.Equal(@"%USERPROFILE%\AppData\LocalLow\BakaLoader\ValheimBakaLoader", AppDataRoot.BakaLoaderRoot);
            Assert.Equal(@"%USERPROFILE%\AppData\LocalLow\IronGate\Valheim", AppDataRoot.ValheimSaveRoot);
            Assert.Equal(@"%USERPROFILE%\AppData\LocalLow\IronGate\Valheim", Resources.DefaultValheimSaveFolder);
            Assert.Equal(@"%USERPROFILE%\AppData\LocalLow\BakaLoader\ValheimBakaLoader\languages", Resources.LanguagesFolderPath);
            Assert.Equal(@"%USERPROFILE%\AppData\LocalLow\BakaLoader\ValheimBakaLoader\logs", Resources.LogsFolderPath);
            Assert.Equal(@"%USERPROFILE%\AppData\LocalLow\BakaLoader\ValheimBakaLoader\players-cache.json", Resources.PlayerListFilePath);
            Assert.Equal(@"%USERPROFILE%\AppData\LocalLow\BakaLoader\ValheimBakaLoader\userprefs.json", Resources.UserPrefsFilePathV2);

            Assert.Equal(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "AppData", "LocalLow", "IronGate", "Valheim"),
                AppDataRoot.ValheimSaveRootFull);
            Assert.Equal(AppDataRoot.ValheimSaveRootFull, IsolatedSaveFolder.ValheimDefault());
            Assert.Equal(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BakaLoaderUpdate"),
                AppDataRoot.UpdateWorkRoot);
            Assert.False(AppDataRoot.DataOverrideActive);

            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            Assert.Equal(local, AppDataRoot.LocalRoot);
            Assert.Equal(Path.Combine(local, "ValheimBakaLoader", "WebView2"), ValheimBakaLoader.Forms.BlendWindow.WebViewUserDataFolder());
            Assert.Equal(Path.Combine(local, "BakaLoader", InstallIsolationService.InstancesRootName), InstallIsolationService.FallbackInstancesRoot);
            Assert.Equal(Path.Combine(local, "ValheimBakaLoader", "steamcmd", "steamcmd.exe"), NewSteamCmdRunner().SteamCmdPath);
        }

        internal static SteamCmdRunner NewSteamCmdRunner() => new SteamCmdRunner(
            new Moq.Mock<ValheimBakaLoader.Tools.Http.IHttpClientProvider>().Object,
            new Moq.Mock<ValheimBakaLoader.Tools.Processes.IProcessProvider>().Object,
            new Moq.Mock<ValheimBakaLoader.Tools.Logging.IApplicationLogger>().Object);

        // ------------------------------------------------------------ the acceptance rule

        [Fact]
        public void An_absolute_folder_that_does_not_exist_yet_is_made_and_accepted()
        {
            var wanted = Path.Combine(Scratch, "data");
            Assert.False(Directory.Exists(wanted));

            Assert.Equal(wanted, AppDataRoot.Accept(wanted));
            Assert.True(Directory.Exists(wanted));
            Assert.Empty(Directory.EnumerateFileSystemEntries(wanted));
        }

        [Fact]
        public void A_trailing_separator_and_surrounding_quotes_name_the_same_folder()
        {
            var wanted = Path.Combine(Scratch, "quoted");
            Assert.Equal(wanted, AppDataRoot.Accept("  \"" + wanted + "\\\"  "));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(@"relative\data")]
        [InlineData(@".\data")]
        [InlineData(@"\rooted-but-no-drive")]
        [InlineData(@"C:drive-relative")]
        public void A_blank_or_relative_value_is_refused(string raw)
        {
            Assert.Null(AppDataRoot.Accept(raw));
        }

        [Fact]
        public void A_relative_value_makes_nothing_in_the_working_directory()
        {
            var name = "vbl-relative-" + Guid.NewGuid().ToString("N");
            Assert.Null(AppDataRoot.Accept(name));
            Assert.False(Directory.Exists(Path.Combine(Environment.CurrentDirectory, name)));
        }

        [Fact]
        public void A_folder_that_cannot_be_made_is_refused()
        {
            Directory.CreateDirectory(Scratch);
            var file = Path.Combine(Scratch, "a-file");
            File.WriteAllText(file, "x");

            Assert.Null(AppDataRoot.Accept(Path.Combine(file, "under-a-file")));
        }

        [Fact]
        public void A_folder_that_exists_but_cannot_be_written_is_refused()
        {
            var locked = Path.Combine(Scratch, "locked");
            var dir = Directory.CreateDirectory(locked);
            var me = WindowsIdentity.GetCurrent().User;
            var deny = new FileSystemAccessRule(
                me, FileSystemRights.CreateFiles | FileSystemRights.WriteData, AccessControlType.Deny);

            var acl = dir.GetAccessControl();
            acl.AddAccessRule(deny);
            dir.SetAccessControl(acl);
            try
            {
                Assert.Null(AppDataRoot.Accept(locked));
                Assert.Empty(Directory.EnumerateFileSystemEntries(locked));
            }
            finally
            {
                var back = dir.GetAccessControl();
                back.RemoveAccessRule(deny);
                dir.SetAccessControl(back);
            }
        }

        // ------------------------------------------------- start with Windows stays off

        [Fact]
        public void An_isolated_run_never_reaches_the_Run_key_road_and_says_so_once()
        {
            StartupHelper.ResetIsolationLog();
            var logger = new Remembering();
            var roads = 0;

            var first = StartupHelper.UnlessIsolated(true, logger, () => { roads++; return new StartupOutcome { Changed = true }; });
            var second = StartupHelper.UnlessIsolated(true, logger, () => { roads++; return new StartupOutcome { Changed = true }; });

            Assert.Equal(0, roads);
            Assert.False(first.Changed);
            Assert.False(second.Changed);
            var line = Assert.Single(logger.Events);
            Assert.Equal(LogEventLevel.Information, line.Level);
            Assert.Contains("BAKALOADER_DATA_DIR", line.RenderMessage());
            StartupHelper.ResetIsolationLog();
        }

        [Fact]
        public void An_ordinary_run_takes_the_Run_key_road_unchanged()
        {
            var logger = new Remembering();
            var expected = new StartupOutcome { Changed = true };

            Assert.Same(expected, StartupHelper.UnlessIsolated(false, logger, () => expected));
            Assert.Empty(logger.Events);
        }

        [Fact]
        public void Both_roads_that_write_the_Run_key_go_through_the_isolation_guard()
        {
            var source = AppSourceTree.Read("ValheimBakaLoader", "Tools", "StartupHelper.cs");
            foreach (var road in new[]
            {
                "ApplyStartupSetting(bool userPreference, ILogger logger) =>",
                "RepointMovedEntry(bool userPreference, ILogger logger) =>",
            })
            {
                var at = source.IndexOf(road, StringComparison.Ordinal);
                Assert.True(at >= 0, road);
                var body = source.Substring(at + road.Length, 120);
                Assert.Contains("UnlessIsolated(AppDataRoot.DataOverrideActive", body);
            }
        }

        // ----------------------------------------------- no hard-coded copy left behind

        /// <summary>
        /// Every place that named one of these folders now asks <see cref="AppDataRoot"/>. A
        /// new line of code spelling out LocalLow, BakaLoaderUpdate or
        /// SpecialFolder.LocalApplicationData is a site the variables would not reach, so the whole app source tree is read for one. Comments may still
        /// name the shipped folders, because they describe them.
        /// </summary>
        [Fact]
        public void No_code_outside_the_helper_spells_out_the_shipped_folders()
        {
            var root = AppSourceTree.RepoRoot();
            var offenders = new List<string>();
            var scanned = 0;
            foreach (var project in new[] { "ValheimBakaLoader", "ValheimBakaLoader.Tools", "BakaLoaderItemIndexer" })
            {
                var dir = Path.Combine(root, project);
                if (!Directory.Exists(dir)) continue;
                foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
                {
                    var rel = Path.GetRelativePath(root, file);
                    var parts = rel.Split(Path.DirectorySeparatorChar);
                    if (parts.Contains("obj") || parts.Contains("bin")) continue;
                    if (string.Equals(Path.GetFileName(file), "AppDataRoot.cs", StringComparison.OrdinalIgnoreCase)) continue;

                    scanned++;
                    var lines = File.ReadAllLines(file);
                    for (var i = 0; i < lines.Length; i++)
                    {
                        var t = lines[i].TrimStart();
                        if (t.StartsWith("//", StringComparison.Ordinal) || t.StartsWith("*", StringComparison.Ordinal)
                            || t.StartsWith("/*", StringComparison.Ordinal)) continue;
                        if (t.Contains("LocalLow", StringComparison.OrdinalIgnoreCase)
                            || t.Contains("BakaLoaderUpdate", StringComparison.OrdinalIgnoreCase)
                            || t.Contains("SpecialFolder.LocalApplicationData", StringComparison.Ordinal))
                            offenders.Add(rel + ":" + (i + 1) + ": " + t);
                    }
                }
            }

            Assert.True(scanned > 100, "the app source tree was not found: " + scanned + " files");
            Assert.True(offenders.Count == 0, string.Join(Environment.NewLine, offenders));
        }

        // --------------------------------------------------------- a Serilog that remembers

        private sealed class Remembering : ILogger
        {
            public List<LogEvent> Events { get; } = new();

            public void Write(LogEvent logEvent)
            {
                if (logEvent != null) Events.Add(logEvent);
            }
        }
    }

    /// <summary>
    /// The variables really set on the process, read through the same properties the app
    /// reads, and put back afterwards.
    /// </summary>
    [Collection(ProcessEnvironmentCollection.Name)]
    public class AppDataRootVariableTests : IDisposable
    {
        private readonly string Scratch = Path.Combine(
            Path.GetTempPath(), "vbl-appdataroot-env-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(AppDataRoot.DataVariable, null);
            Environment.SetEnvironmentVariable(AppDataRoot.ValheimSaveVariable, null);
            Environment.SetEnvironmentVariable(AppDataRoot.UpdateVariable, null);
            try { if (Directory.Exists(Scratch)) Directory.Delete(Scratch, true); } catch { }
        }

        [Fact]
        public void A_set_data_folder_moves_every_BakaLoader_file_with_it()
        {
            var data = Path.Combine(Scratch, "data");
            Environment.SetEnvironmentVariable(AppDataRoot.DataVariable, data);

            Assert.True(AppDataRoot.DataOverrideActive);
            Assert.Equal(data, AppDataRoot.BakaLoaderRoot);
            Assert.Equal(Path.Combine(data, "userprefs.json"), Resources.UserPrefsFilePathV2);
            Assert.Equal(Path.Combine(data, "logs"), Resources.LogsFolderPath);
            Assert.Equal(Path.Combine(data, "languages"), Resources.LanguagesFolderPath);
            Assert.Equal(Path.Combine(data, "players-cache.json"), Resources.PlayerListFilePath);
            Assert.Equal(Path.Combine(data, "userprefs.json"), PathCheck.Resolve(Resources.UserPrefsFilePathV2));

            // Every LocalAppData folder follows it, under a "local" folder inside the root.
            var local = Path.Combine(data, "local");
            Assert.Equal(local, AppDataRoot.LocalRoot);
            Assert.Equal(Path.Combine(local, "ValheimBakaLoader", "WebView2"), ValheimBakaLoader.Forms.BlendWindow.WebViewUserDataFolder());
            Assert.Equal(Path.Combine(local, "ValheimBakaLoader", "AtlasCache"), ValheimBakaLoader.Forms.BlendWindow.GetAtlasCacheDir());
            Assert.True(Directory.Exists(Path.Combine(local, "ValheimBakaLoader", "AtlasCache")));
            Assert.Equal(Path.Combine(local, "BakaLoader", InstallIsolationService.InstancesRootName), InstallIsolationService.FallbackInstancesRoot);
            Assert.True(InstallIsolationService.IsFallbackRoot(Path.Combine(local, "BakaLoader", InstallIsolationService.InstancesRootName)));
            Assert.Equal(Path.Combine(local, "ValheimBakaLoader", "steamcmd", "steamcmd.exe"), AppDataRootTests.NewSteamCmdRunner().SteamCmdPath);
            Assert.Equal(Path.Combine(local, "BakaLoaderUpdate"), AppDataRoot.UpdateWorkRoot);
            var saved = AppUpdateService.ReportPathOverride;
            try
            {
                AppUpdateService.ReportPathOverride = null;
                Assert.Equal(Path.Combine(local, "ValheimBakaLoader", "update-report.txt"), AppUpdateService.UpdateReportPath());
            }
            finally { AppUpdateService.ReportPathOverride = saved; }

            // The other two are untouched by this one.
            Assert.Equal(@"%USERPROFILE%\AppData\LocalLow\IronGate\Valheim", Resources.DefaultValheimSaveFolder);
        }

        // The update report override is shared with SelfUpdateIntegrityTests, so it is read
        // here, in the collection that runs alone, rather than beside the other unset paths.
        [Fact]
        public void Unset_the_update_report_is_in_the_real_LocalAppData()
        {
            var saved = AppUpdateService.ReportPathOverride;
            try
            {
                AppUpdateService.ReportPathOverride = null;
                Assert.Equal(
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ValheimBakaLoader", "update-report.txt"),
                    AppUpdateService.UpdateReportPath());
            }
            finally { AppUpdateService.ReportPathOverride = saved; }
        }

        [Fact]
        public void A_set_save_folder_moves_both_forms_of_the_Valheim_default()
        {
            var saves = Path.Combine(Scratch, "saves");
            Environment.SetEnvironmentVariable(AppDataRoot.ValheimSaveVariable, saves);

            Assert.Equal(saves, Resources.DefaultValheimSaveFolder);
            Assert.Equal(saves, AppDataRoot.ValheimSaveRootFull);
            Assert.Equal(saves, IsolatedSaveFolder.ValheimDefault());
            Assert.False(AppDataRoot.DataOverrideActive);
        }

        [Fact]
        public void A_set_update_folder_moves_the_updater_work_folder()
        {
            var work = Path.Combine(Scratch, "update");
            Environment.SetEnvironmentVariable(AppDataRoot.UpdateVariable, work);

            Assert.Equal(work, AppDataRoot.UpdateWorkRoot);
            Assert.Equal(work, ManagedUpdater.WorkFolder());
        }

        [Fact]
        public void A_relative_or_unusable_value_falls_back_to_the_shipped_paths()
        {
            Directory.CreateDirectory(Scratch);
            var file = Path.Combine(Scratch, "a-file");
            File.WriteAllText(file, "x");

            Environment.SetEnvironmentVariable(AppDataRoot.DataVariable, @"relative\data");
            Environment.SetEnvironmentVariable(AppDataRoot.ValheimSaveVariable, Path.Combine(file, "saves"));
            Environment.SetEnvironmentVariable(AppDataRoot.UpdateVariable, "   ");

            Assert.False(AppDataRoot.DataOverrideActive);
            Assert.Equal(@"%USERPROFILE%\AppData\LocalLow\BakaLoader\ValheimBakaLoader\userprefs.json", Resources.UserPrefsFilePathV2);
            Assert.Equal(@"%USERPROFILE%\AppData\LocalLow\IronGate\Valheim", Resources.DefaultValheimSaveFolder);
            Assert.Equal(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BakaLoaderUpdate"),
                ManagedUpdater.WorkFolder());
        }
    }
}
