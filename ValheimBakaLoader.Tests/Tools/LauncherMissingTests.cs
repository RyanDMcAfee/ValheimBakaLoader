using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using ValheimBakaLoader.Tools;
using Xunit;

namespace ValheimBakaLoader.Tests.Tools
{
    /// <summary>
    /// BakaLoader with no launcher.
    /// <para>
    /// WHY THIS EXISTS. On 2026-10-11 Windows Defender's cloud heuristic called the frozen
    /// launcher Trojan:Win32/Bearfoos.B!ml during the 1.2.11 self-update, quarantined it out of
    /// the live install, took it again every time it was put back, and the watchdog read the
    /// missing exe as a failed update and rolled the install back to 1.2.10. The app and the
    /// server stayed down. The launcher is the SDK's generic apphost and nothing more: the app
    /// is ValheimBakaLoader.dll, so <c>dotnet ValheimBakaLoader.dll</c> runs it with no
    /// launcher at all. These hold the rule itself, the folder an update is written into, the
    /// script that ships for a host to start, and the line a run without its launcher writes.
    /// What an update does when the launcher is taken is driven end to end against the updater
    /// itself, in ManagedUpdaterTests, since 1.2.13 replaced the PowerShell watchdog with it.
    /// </para>
    /// </summary>
    public class LauncherMissingTests
    {
        // ----------------------------------------------------------------------- the rule

        [Theory]
        [InlineData(true, true, LaunchRoute.Launcher)]
        [InlineData(true, false, LaunchRoute.Launcher)]
        [InlineData(false, true, LaunchRoute.Dotnet)]
        [InlineData(false, false, LaunchRoute.None)]
        public void The_launcher_wins_when_it_is_there_and_the_library_is_the_way_in_when_it_is_not(
            bool launcher, bool library, LaunchRoute expected)
        {
            Assert.Equal(expected, LauncherRoute.For(launcher, library));
        }

        /// <summary>
        /// The folder the update is written into is the app's own folder. It used to be the
        /// folder of the process image, which through dotnet is the .NET install: the update
        /// would have been robocopied over Program Files\dotnet.
        /// </summary>
        [Fact]
        public void The_update_is_written_into_the_apps_own_folder_not_the_hosts()
        {
            var service = AppSourceTree.Files()["AppUpdateService.cs"];

            Assert.Contains("var installDir = InstallDirectory ?? LauncherRoute.InstallFolder();", service);
            Assert.DoesNotContain("MainModule", service);
            Assert.Equal(
                Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory),
                LauncherRoute.InstallFolder());
        }

        // ------------------------------------------------------------ the script that ships

        /// <summary>
        /// BakaLoader.cmd is content of the app project, copied to the output and to a publish,
        /// so it lands beside the exe in the folder the release zip is made from. This test
        /// project references the app, and content flows to a referencing project the same way,
        /// so the file beside this assembly is the build's own copy of it.
        /// </summary>
        [Fact]
        public void BakaLoader_cmd_is_in_the_published_content_and_lands_beside_the_launcher()
        {
            var csproj = XDocument.Parse(File.ReadAllText(
                RepoScript.At("ValheimBakaLoader", "ValheimBakaLoader.csproj")));

            var item = csproj.Descendants("Content")
                .SingleOrDefault(e => (string)e.Attribute("Include") == LauncherRoute.ScriptName);
            Assert.NotNull(item);
            Assert.Equal("PreserveNewest", (string)item.Element("CopyToOutputDirectory"));
            Assert.Equal("PreserveNewest", (string)item.Element("CopyToPublishDirectory"));
            Assert.Null(item.Attribute("Condition"));

            // Except a single file publish, which has no library beside the exe for it to start.
            var singleFile = item.Elements("CopyToPublishDirectory")
                .SingleOrDefault(e => e.Attribute("Condition") != null);
            Assert.NotNull(singleFile);
            Assert.Equal("Never", (string)singleFile);
            Assert.Contains("$(PublishSingleFile)", (string)singleFile.Attribute("Condition"));

            var built = Path.Combine(AppContext.BaseDirectory, LauncherRoute.ScriptName);
            Assert.True(File.Exists(built), "the build did not copy " + LauncherRoute.ScriptName + " beside the app");
            Assert.Equal(
                File.ReadAllBytes(RepoScript.At("ValheimBakaLoader", LauncherRoute.ScriptName)),
                File.ReadAllBytes(built));
        }

        /// <summary>
        /// What the script says and how it is written. CRLF, because cmd reads a batch file with
        /// bare line feeds badly; plain ASCII with no byte order mark, because cmd runs the bytes
        /// it is given; the app through dotnet with nothing in front of it, because conhost
        /// --headless is a hidden-console pattern security heuristics look for and the app closes
        /// the dotnet console itself; the Desktop Runtime 6 checked, because a dotnet that cannot run
        /// the app would end on a console nobody can see; and one line naming the runtime when it
        /// is missing.
        /// </summary>
        [Fact]
        public void BakaLoader_cmd_starts_the_library_through_dotnet_with_no_window_and_names_the_runtime()
        {
            var bytes = File.ReadAllBytes(RepoScript.At("ValheimBakaLoader", LauncherRoute.ScriptName));
            Assert.All(bytes, b => Assert.True(b < 0x80, "BakaLoader.cmd is not plain ASCII"));

            var text = Encoding.ASCII.GetString(bytes);
            Assert.DoesNotContain("\n", text.Replace("\r\n", ""));
            Assert.EndsWith("\r\n", text);

            Assert.Contains("start \"\" /d \"%HERE%.\" \"%DOTNET%\" \"%HERE%ValheimBakaLoader.dll\"", text);
            Assert.DoesNotContain("conhost", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("--headless", text);
            Assert.Contains("findstr /b /c:\"Microsoft.WindowsDesktop.App 6.\"", text);
            Assert.Contains("https://dotnet.microsoft.com/download/dotnet/6.0", text);
            Assert.DoesNotContain("ValheimBakaLoader.exe\" ", text);

            // And the README sends a host there with the same runtime link.
            var readme = File.ReadAllText(RepoScript.At("README.md"));
            Assert.Contains(LauncherRoute.ScriptName, readme);
            Assert.Contains("https://dotnet.microsoft.com/download/dotnet/6.0", readme);

            // And the repository keeps it CRLF whatever the checkout does.
            Assert.Contains("ValheimBakaLoader/BakaLoader.cmd text eol=crlf",
                File.ReadAllText(RepoScript.At(".gitattributes")));
        }

        // -------------------------------------------------------------- the line at startup

        /// <summary>
        /// The one line the Saga gets when this run started without its launcher: from the
        /// catalog, naming the script, and only on the Dotnet route.
        /// </summary>
        [Fact]
        public void A_run_without_its_launcher_says_so_once_and_names_the_script()
        {
            var line = HostCatalog.EmbeddedEnglish.Say(
                "host.saga.launcher_missing", ("script", LauncherRoute.ScriptName));

            Assert.Contains("BakaLoader.cmd", line);
            Assert.Contains("dotnet", line);
            Assert.DoesNotContain("{script}", line);
            Assert.DoesNotContain("host.saga", line);

            var program = AppSourceTree.Read("ValheimBakaLoader", "Program.cs");
            Assert.Contains("SayWhenTheLauncherIsMissing(container);", program);
            Assert.Contains("LauncherRoute.MissingLauncherLine(LauncherRoute.InstallFolder())", program);

            // Only on the Dotnet route, against real folders.
            var root = TempRoot();
            try
            {
                Assert.Null(LauncherRoute.MissingLauncherLine(
                    Folder(root, "both", LauncherRoute.LauncherName, LauncherRoute.LibraryName)));
                Assert.Null(LauncherRoute.MissingLauncherLine(Folder(root, "neither")));
                Assert.Equal(HostCatalog.T("host.saga.launcher_missing", ("script", LauncherRoute.ScriptName)),
                    LauncherRoute.MissingLauncherLine(Folder(root, "taken", LauncherRoute.LibraryName)));
            }
            finally
            {
                TryDelete(root);
            }
        }

        // ----------------------------------------------------------------------- fixtures

        internal static void MakeZip(string zipPath, params (string Name, string Text)[] entries)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(zipPath));
            using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
            foreach (var (name, text) in entries)
            {
                var entry = zip.CreateEntry(name);
                if (text == null) continue; // a directory entry
                using var writer = new StreamWriter(entry.Open());
                writer.Write(text);
            }
        }

        private static string Folder(string root, string name, params string[] files)
        {
            var folder = Path.Combine(root, name);
            Directory.CreateDirectory(folder);
            foreach (var file in files) File.WriteAllText(Path.Combine(folder, file), "x");
            return folder;
        }

        /// <summary>
        /// A temp folder with a space, a quote and a non-ASCII letter in its name, because every
        /// path the updater is handed is a host's folder and hosts name folders.
        /// </summary>
        internal static string TempRoot()
        {
            var root = Path.Combine(Path.GetTempPath(),
                "baka launcher's " + "\u00e4" + " " + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(root);
            return root;
        }

        internal static void TryDelete(string folder)
        {
            try { Directory.Delete(folder, true); } catch { /* best effort */ }
        }
    }
}
