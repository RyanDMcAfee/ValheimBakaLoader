using System;
using System.IO;
using System.Linq;
using ValheimBakaLoader.Tools.Logging;
using Xunit;

namespace ValheimBakaLoader.Tests.Tools
{
    /// <summary>
    /// Where a test run's log lines land.
    /// <para>
    /// WriteApplicationLogsToFile is on by default and an empty LogsFolderPath means the folder
    /// the installed app writes its real log to, so for as long as the fake preferences served the
    /// bare defaults every test that logged anything appended to the owner's live ApplicationLogs
    /// file. It did: that file carries test lines from 04:53 on 2026-09-20. A test run has nothing
    /// to say in a host's log.
    /// </para>
    /// <para>
    /// The real folder is not read here to prove it. The app that owns it is running on this
    /// machine and writes to it constantly, so any assertion about its contents would be a coin
    /// toss. What is asserted instead is the thing that makes the write impossible: the folder the
    /// sink resolves is a temp one, it is not the shipped default, and a line written through the
    /// app logger really does arrive in a file there.
    /// </para>
    /// </summary>
    public class TestLogFolderTests : BaseTest
    {
        [Fact]
        public void The_fake_preferences_send_every_log_file_to_a_temp_folder()
        {
            var folder = MockUserPreferencesProvider.LoadPreferences().LogsFolderPath;

            Assert.False(string.IsNullOrWhiteSpace(folder));
            Assert.StartsWith(
                Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(folder),
                StringComparison.OrdinalIgnoreCase);

            // The shipped default names the host's own logs folder. Whatever the temp path is, it
            // is not that one.
            var shipped = Environment.ExpandEnvironmentVariables(
                @"%USERPROFILE%\AppData\LocalLow\BakaLoader\ValheimBakaLoader\logs");
            Assert.NotEqual(
                Path.GetFullPath(shipped).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar),
                StringComparer.OrdinalIgnoreCase);
        }

        [Fact]
        public void A_line_written_through_the_app_logger_lands_in_that_temp_folder()
        {
            var marker = "log folder check " + Guid.NewGuid().ToString("N");

            GetService<IApplicationLogger>().Information(marker);

            var folder = MockUserPreferencesProvider.LoadPreferences().LogsFolderPath;
            Assert.True(Directory.Exists(folder), folder + " was never created");

            var written = Directory
                .EnumerateFiles(folder, "*.txt", SearchOption.TopDirectoryOnly)
                .Any(path => ReadShared(path).Contains(marker, StringComparison.Ordinal));

            Assert.True(written, "no file under " + folder + " carries the line that was written");
        }

        /// <summary>
        /// The floor under the fake preferences. They only redirect a logger that goes through
        /// them, and LoggerCore expands %USERPROFILE% itself when it builds the file sink, so a
        /// logger built any other way still landed in the owner's real LocalLow folder. The
        /// variable those defaults are built out of names a temp folder for the whole test
        /// process now, so there is no path left to the real one at all.
        /// </summary>
        [Fact]
        public void The_shipped_default_itself_expands_under_the_temp_folder()
        {
            var shipped = Environment.ExpandEnvironmentVariables(
                @"%USERPROFILE%\AppData\LocalLow\BakaLoader\ValheimBakaLoader\logs");

            Assert.StartsWith(
                Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(shipped),
                StringComparison.OrdinalIgnoreCase);

            Assert.StartsWith(
                Path.GetFullPath(TestLogRedirect.HomeFolder).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(shipped),
                StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>The sink holds the file open and shares it, so the read has to share too.</summary>
        private static string ReadShared(string path)
        {
            try
            {
                using var stream = new FileStream(
                    path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd();
            }
            catch (IOException)
            {
                return string.Empty;
            }
        }
    }
}
