using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using ValheimBakaLoader.Tools;
using Xunit;

namespace ValheimBakaLoader.Tests.Tools
{
    /// <summary>
    /// The launcher never changes again, and the version truth lives in the library.
    /// <para>
    /// On 2026-10-04 Windows Defender's local machine learning rule looked at a freshly
    /// self-updated 1.2.7 ValheimBakaLoader.exe on a host's PC, called it
    /// Trojan:Win32/Bearfoos.A!ml, quarantined it and took the app and the Valheim server it
    /// was running down with it, along with the Run entry and the taskbar pin. The exe is not
    /// the program: it is the SDK's generic apphost, 353,280 bytes of startup code carrying the
    /// name of the assembly to load, and the 1.2.6 and 1.2.7 copies of it differ in exactly
    /// fourteen bytes, all of them inside the version resource. So the verdict was about a brand
    /// new hash of an unsigned launcher, written by a script and relaunched, with a Run key
    /// pointing at it.
    /// </para>
    /// <para>
    /// From 1.2.8 the launcher is a committed artefact copied over whatever the SDK generates,
    /// so every release ships the same bytes and a self-update out of 1.2.8 writes no exe at all
    /// (the update INTO it runs the old watchdog, which copies it one last time). These are
    /// the gates that keep it that way: the bytes, the build step, and the fact that the app's
    /// own version is the DLL's, which is where the window, the log and the update check have
    /// always read it and is now the only place anything in the app may read it from.
    /// </para>
    /// </summary>
    public class FrozenAppHostTests
    {
        /// <summary>
        /// The 1.2.6 apphost, which has run on about fifty machines since 2026-09-30 with no
        /// detection anywhere. Changing this constant means shipping a launcher the world has
        /// never seen, which is the whole thing 1.2.8 exists to stop.
        /// </summary>
        public const string FrozenSha256 =
            "365a8dec30b302a0b37851250d2089e646449c6017f7a2e2e59f2fd5a10ffcc4";

        private static string Sha256Of(string path)
        {
            using var sha = SHA256.Create();
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
        }

        private static string Csproj() =>
            AppSourceTree.Read("ValheimBakaLoader", "ValheimBakaLoader.csproj");

        /// <summary>The version in the csproj, which is the one the DLL is stamped with.</summary>
        private static string ShippedVersion()
        {
            var match = Regex.Match(Csproj(), @"<Version>([^<]+)</Version>");
            Assert.True(match.Success, "the app csproj has no Version");
            return match.Groups[1].Value.Trim();
        }

        private static string FrozenSource() =>
            RepoScript.At("ValheimBakaLoader", "Resources", "host", "ValheimBakaLoader.exe");

        // -------------------------------------------------------------------------- the bytes

        /// <summary>
        /// The committed launcher is the 1.2.6 apphost, byte for byte. This one is always
        /// answerable: the file is in the tree whether anything has been built or not.
        /// </summary>
        [Fact]
        public void The_committed_launcher_is_the_frozen_apphost()
        {
            var source = FrozenSource();
            Assert.True(File.Exists(source), "the frozen launcher is missing: " + source);
            Assert.Equal(FrozenSha256, Sha256Of(source));
        }

        /// <summary>
        /// And the repository is told in writing that those bytes are bytes. Git's Windows
        /// default rewrites line endings in everything it thinks is text, and while its own
        /// heuristic calls an exe binary, a file whose entire value is its hash should not be
        /// resting on a heuristic. The rule is deliberately for this one path: a repository wide
        /// one would renormalise every file here in a single commit.
        /// </summary>
        [Fact]
        public void The_repository_marks_the_frozen_launcher_as_bytes()
        {
            var attributes = AppSourceTree.Read(".gitattributes");

            Assert.Contains(
                "ValheimBakaLoader/Resources/host/ValheimBakaLoader.exe binary", attributes);

            // And no rule over every path, which would renormalise the whole repository in one
            // commit. Read as rules rather than as text, because a comment in this very file
            // may well quote the pattern it is explaining.
            foreach (var line in attributes.Split('\n'))
            {
                var rule = line.Trim();
                if (rule.Length == 0 || rule.StartsWith("#", StringComparison.Ordinal)) continue;

                Assert.False(rule.StartsWith("*", StringComparison.Ordinal),
                    ".gitattributes carries a rule over every path, which renormalises every "
                    + "file in this repository: " + rule);
            }
        }

        /// <summary>
        /// And what a build actually produces is that same file. The exe beside this test is
        /// the app project's output, copied here by the project reference; a release build puts
        /// its own copy in BuildStaging128, and that one is checked too when it is there.
        /// <para>
        /// Nothing here skips. An exe this test cannot find at all is a failure that names
        /// every place it looked, because a gate that quietly stands down is the false green
        /// the whole thing exists to avoid.
        /// </para>
        /// </summary>
        [Fact]
        public void Every_built_launcher_is_the_frozen_apphost()
        {
            var beside = Path.Combine(AppContext.BaseDirectory, "ValheimBakaLoader.exe");
            var staged = RepoScript.At("BuildStaging128", "ValheimBakaLoader.exe");

            var looked = new[] { beside, staged };
            var found = 0;

            foreach (var exe in looked)
            {
                if (!File.Exists(exe)) continue;
                found++;
                Assert.Equal(FrozenSha256, Sha256Of(exe));
            }

            Assert.True(found > 0,
                "no built ValheimBakaLoader.exe was found, so nothing proved the build ships the "
                + "frozen launcher. Looked at: " + string.Join(", ", looked));
        }

        /// <summary>
        /// The launcher's own version resource still reads 1.2.6, and that is expected: it is
        /// the launcher's version and the only place it appears. A test that read the exe for
        /// the app's version would now be reading a number that never moves again.
        /// </summary>
        [Fact]
        public void The_frozen_launcher_still_names_the_library_it_loads()
        {
            var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(FrozenSource());

            // The one thing in these bytes that is not generic: the assembly to start.
            Assert.Equal("ValheimBakaLoader.dll", info.InternalName);
            Assert.StartsWith("1.2.6", info.FileVersion);
        }

        // ---------------------------------------------------------------------- the build step

        /// <summary>
        /// The copy runs after Build in every configuration, not only on Release and not only
        /// on Publish: a Debug build is what every release of this app has been cut from.
        /// <para>
        /// And it runs on the INTERMEDIATE apphost as well, which is the copy that matters. The
        /// SDK writes the apphost it generates into obj and every consumer takes it from there:
        /// the output folder, a publish, and any project that references this one. Freezing only
        /// the output folder left the test project's own copy of the exe unfrozen, which is how
        /// that was found, and a release cut with publish would have shipped the generated one.
        /// </para>
        /// </summary>
        [Fact]
        public void The_build_copies_the_frozen_launcher_over_the_generated_one()
        {
            var csproj = Csproj();

            Assert.Contains("<Target Name=\"FreezeAppHost\" AfterTargets=\"Build\"", csproj);
            Assert.Contains("<Target Name=\"FreezeAppHostIntermediate\" AfterTargets=\"_CreateAppHost\"", csproj);
            Assert.Contains("Resources\\host\\ValheimBakaLoader.exe", csproj);
            Assert.Contains("DestinationFiles=\"$(OutDir)ValheimBakaLoader.exe\"", csproj);
            Assert.Contains("DestinationFiles=\"$(AppHostIntermediatePath)\"", csproj);

            // No Configuration condition on either target: Debug has to be frozen too.
            foreach (var name in new[] { "<Target Name=\"FreezeAppHost\"", "<Target Name=\"FreezeAppHostIntermediate\"" })
            {
                var target = csproj.Substring(csproj.IndexOf(name, StringComparison.Ordinal));
                target = target.Substring(0, target.IndexOf("</Target>", StringComparison.Ordinal));
                Assert.DoesNotContain("'$(Configuration)'=='Release'", target);
            }
        }

        /// <summary>
        /// The frozen host is the SDK's generic apphost with the assembly name embedded, so it
        /// keeps working with every future DLL as long as that name and the framework hold.
        /// Both are build errors rather than comments, because a launcher that stops matching
        /// its library is an app that does not start, and the build must say so out loud.
        /// </summary>
        [Fact]
        public void The_build_stops_when_the_assembly_name_or_the_framework_moves()
        {
            var csproj = Csproj();

            Assert.Contains("<Error Condition=\"'$(AssemblyName)' != 'ValheimBakaLoader'\"", csproj);
            Assert.Contains("'$(TargetFramework)' != '' and '$(TargetFramework)' != 'net6.0-windows'", csproj);
            Assert.Contains("<Error Condition=\"!Exists('$(FrozenAppHost)')\"", csproj);

            // Both copies depend on the checks, so neither can run without them.
            Assert.Contains("<Target Name=\"CheckFrozenAppHost\">", csproj);
            Assert.Equal(2, Regex.Matches(csproj, @"DependsOnTargets=""CheckFrozenAppHost""").Count);
        }

        /// <summary>The folder says what it is and why its file properties read 1.2.6.</summary>
        [Fact]
        public void The_frozen_launcher_folder_explains_itself()
        {
            var readme = AppSourceTree.Read(
                "ValheimBakaLoader", "Resources", "host", "README.md");

            Assert.Contains(FrozenSha256, readme);
            Assert.Contains("1.2.6", readme);
            Assert.Contains("ValheimBakaLoader.dll", readme);
        }

        // ------------------------------------------------------------- where the version lives

        /// <summary>
        /// The version the app answers with is the DLL's, so it is checked there: the library
        /// this test is running against carries the csproj's Version in its informational
        /// version, which is the one string AssemblyHelper reads and every surface in the app is
        /// handed.
        /// </summary>
        [Fact]
        public void The_library_carries_the_version_the_csproj_names()
        {
            var informational = typeof(AssemblyHelper).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion;

            Assert.False(string.IsNullOrWhiteSpace(informational),
                "the app assembly has no informational version, so nothing in the app knows what it is");

            var shipped = ShippedVersion();
            Assert.StartsWith(shipped, informational);

            // And what the app hands every surface is that version with the build metadata off.
            Assert.Equal(shipped, AssemblyHelper.GetApplicationVersion());
        }

        // ------------------------------------------------- nothing reads a version off the exe

        /// <summary>A reading of a version resource: what shape it is and what it names.</summary>
        internal sealed class VersionInfoSite
        {
            /// <summary><see cref="Call"/>, <see cref="Property"/> or <see cref="TypeOnly"/>.</summary>
            public string Kind { get; init; }

            /// <summary>The argument as it is written, or the member for a property read.</summary>
            public string Argument { get; init; }

            /// <summary>Its line in the file it was found in, counted from one.</summary>
            public int Line { get; init; }

            public override string ToString() => Kind + " " + Argument + " (line " + Line + ")";
        }

        internal const string Call = "call";
        internal const string Property = "property";
        internal const string TypeOnly = "type";

        /// <summary>
        /// The readings of a file's version resource this app is allowed to make, keyed by the
        /// file's path from the repository root and the argument exactly as it is written, with
        /// the reason each one is not a reading of which BakaLoader is installed.
        /// <para>
        /// Anything not in here fails, and that is the point. The sweep used to ask whether ONE
        /// line held both FileVersionInfo and MainModule, which a reading split over two lines
        /// walks straight past, and it read the app project alone while ValheimBakaLoader.Tools,
        /// the ClientSecrets partial the csproj compiles in and the bundled item indexer all
        /// ship just the same.
        /// </para>
        /// </summary>
        internal static readonly Dictionary<string, string> AllowedVersionReads =
            new(StringComparer.OrdinalIgnoreCase)
        {
            ["ValheimBakaLoader/Forms/BlendWindow.Bridge.cs:library"] =
                "the DLL beside ANOTHER BakaLoader's exe, which is where that app's version is. "
                + "The method builds that path as Path.Combine(folder, \"ValheimBakaLoader.dll\").",
            ["ValheimBakaLoader/Tools/BepInExService.cs:path"] =
                "a mod's own assembly, handed in by the caller. Nothing to do with which "
                + "BakaLoader is installed.",
        };

        /// <summary>
        /// Nothing in ANY folder of C# that ships reads a version off ValheimBakaLoader.exe.
        /// The one place that did was the dialog naming another running BakaLoader, which would
        /// have told every host it was 1.2.6 whatever was really there.
        /// <para>
        /// Every call to FileVersionInfo.GetVersionInfo has to be allowlisted above with a
        /// reason, or write the library's own file name out in a literal. Every read of
        /// the .FileVersionInfo PROPERTY fails outright: that is what ProcessModule and FileInfo
        /// hand back for an exe, and it needs no GetVersionInfo call at all. Both halves are read
        /// off the source with the comments blanked, so a reading spread over as many lines as
        /// somebody likes is still one site with one argument.
        /// </para>
        /// </summary>
        [Fact]
        public void Nothing_in_the_app_reads_its_version_off_the_launcher()
        {
            var files = AppSourceTree.ShippedFiles();

            // Every folder that ships, or the sweep has a blind half.
            foreach (var project in AppSourceTree.ShippedProjects)
                Assert.Contains(files.Keys, key =>
                    key.StartsWith(project + "/", StringComparison.OrdinalIgnoreCase));

            var found = new List<string>();

            foreach (var file in files)
            {
                foreach (var site in VersionInfoSites(file.Value))
                {
                    var key = file.Key + ":" + site.Argument;
                    found.Add(key);

                    // A path written out as the library is allowed wherever it appears: that is
                    // the reading this whole pass exists to move everything onto.
                    if (site.Kind == Call && ReadsTheLibrary(site.Argument)) continue;

                    Assert.True(site.Kind == Call && AllowedVersionReads.ContainsKey(key),
                        file.Key + " line " + site.Line + " reads a file's version resource as "
                        + site + ", and nothing here may read a version off "
                        + "ValheimBakaLoader.exe: it is a frozen launcher whose version resource "
                        + "says 1.2.6 forever. Read ValheimBakaLoader.dll beside it, or add this "
                        + "site to FrozenAppHostTests.AllowedVersionReads with the reason it is "
                        + "not the app's own version.");
                }
            }

            // And every allowlisted site is still there, which is the other way this could go
            // green while saying nothing: a reading moved into a shape the sweep cannot see at
            // all, and a sweep that finds nothing passes.
            foreach (var allowed in AllowedVersionReads)
                Assert.True(found.Contains(allowed.Key),
                    "the sweep no longer sees " + allowed.Key + ", which it is supposed to find "
                    + "and allow (" + allowed.Value + "). Either the reading moved, in which case "
                    + "move the allowlist entry with it, or the sweep stopped reading that file, "
                    + "which makes every gate here a false green.");

            Assert.Equal(
                AllowedVersionReads.Count,
                found.Count(key => AllowedVersionReads.ContainsKey(key)));

            // The reading that replaced the old one, on the DLL, with no fall back to the exe.
            var bridge = AppSourceTree.Files()["BlendWindow.Bridge.cs"];
            Assert.Contains("Path.Combine(folder, \"ValheimBakaLoader.dll\")", bridge);
            Assert.Contains("FileVersionInfo.GetVersionInfo(library)", bridge);
        }

        /// <summary>
        /// The sweep itself, against the regression the old one could not see: the exe's path on
        /// one line and the version read off it on the next. The old gate asked whether a single
        /// line held both FileVersionInfo and MainModule, so two lines was enough to get past it.
        /// A detection gate that has never been shown failing is a gate nobody has tested.
        /// </summary>
        [Fact]
        public void The_sweep_catches_a_launcher_version_read_split_over_two_lines()
        {
            const string planted = @"
        private static string Mine()
        {
            var exe = Process.GetCurrentProcess().MainModule.FileName;
            return FileVersionInfo.GetVersionInfo(exe).ProductVersion;
        }";

            var site = Assert.Single(VersionInfoSites(planted));

            Assert.Equal(Call, site.Kind);
            Assert.Equal("exe", site.Argument);
            Assert.Equal(5, site.Line);
            Assert.False(ReadsTheLibrary(site.Argument));
            Assert.False(AllowedVersionReads.ContainsKey("ValheimBakaLoader/Forms/Anywhere.cs:exe"));

            // The property form, which needs no GetVersionInfo call at all and is never allowed.
            var property = Assert.Single(VersionInfoSites(
                "var mine = process.MainModule.FileVersionInfo.ProductVersion;"));
            Assert.Equal(Property, property.Kind);
            Assert.Equal("MainModule.FileVersionInfo", property.Argument);

            // Application.ExecutablePath is the same bug wearing a different coat.
            var executable = Assert.Single(VersionInfoSites(
                "var v = FileVersionInfo.GetVersionInfo(Application.ExecutablePath);"));
            Assert.Equal("Application.ExecutablePath", executable.Argument);
            Assert.False(ReadsTheLibrary(executable.Argument));

            // An argument spread over three lines is still one argument.
            var folded = Assert.Single(VersionInfoSites(
                "var v = FileVersionInfo\n    .GetVersionInfo(\n        exe);"));
            Assert.Equal("exe", folded.Argument);

            // A comment is not a call, in either spelling, and neither is a doc line.
            Assert.Empty(VersionInfoSites("// FileVersionInfo.GetVersionInfo(exe)"));
            Assert.Empty(VersionInfoSites("/// <see cref=\"FileVersionInfo\"/> off the exe"));
            Assert.Empty(VersionInfoSites("/* FileVersionInfo.GetVersionInfo(exe) */"));

            // A string literal is not a call either, and a verbatim or interpolated one does not
            // swallow the code after it: that would blank a real site and pass.
            Assert.Empty(VersionInfoSites("Log(\"FileVersionInfo.GetVersionInfo(exe)\");"));
            Assert.Single(VersionInfoSites(
                "var a = @\"C:\\x\"; var b = $\"{a}\"; var c = @$\"-name \"\"{a}\"\"\";"
                + " var v = FileVersionInfo.GetVersionInfo(exe);"));

            // And the readings that are allowed are seen and allowed.
            var library = Assert.Single(VersionInfoSites(
                "var v = FileVersionInfo.GetVersionInfo(library)?.ProductVersion;"));
            Assert.Equal("library", library.Argument);

            var written = Assert.Single(VersionInfoSites(
                "var v = FileVersionInfo.GetVersionInfo(Path.Combine(f, \"ValheimBakaLoader.dll\"));"));
            Assert.True(ReadsTheLibrary(written.Argument), written.Argument);

            // The type written out in full is the type, not a property called FileVersionInfo.
            var qualified = Assert.Single(VersionInfoSites(
                "var v = System.Diagnostics.FileVersionInfo.GetVersionInfo(exe);"));
            Assert.Equal(Call, qualified.Kind);
        }

        /// <summary>
        /// An argument that writes the app's own library out in a string literal, as
        /// Path.Combine(folder, "ValheimBakaLoader.dll") does. That is the reading everything
        /// here is supposed to move onto, so it needs no allowlist entry wherever it appears.
        /// </summary>
        private static bool ReadsTheLibrary(string argument) =>
            argument != null
            && Regex.IsMatch(
                argument, "\"[^\"]*ValheimBakaLoader\\.dll\"", RegexOptions.IgnoreCase);

        /// <summary>
        /// Every place a piece of source touches FileVersionInfo, with the comments blanked out
        /// first. String and character literals are left exactly where they were, because a path
        /// written out in one is the one argument this gate wants to read, and an offset in the
        /// answer is still an offset in the original so a line number means something.
        /// </summary>
        internal static List<VersionInfoSite> VersionInfoSites(string source)
        {
            const string Name = "FileVersionInfo";
            const string Getter = ".GetVersionInfo";

            var code = WithoutComments(AppSourceTree.Lf(source) ?? string.Empty, out var quoted);
            var sites = new List<VersionInfoSite>();

            for (var at = code.IndexOf(Name, StringComparison.Ordinal);
                 at >= 0;
                 at = code.IndexOf(Name, at + 1, StringComparison.Ordinal))
            {
                // The word inside a string literal is text, not a call. The literals are
                // walked rather than blanked, because a path written out in one is the one
                // argument this gate reads and allows.
                if (quoted[at]) continue;

                // A longer identifier that merely ends in this one is a different word. The
                // test either side of the name is on the character NEXT TO it, before any
                // whitespace is skipped: a site written as "return FileVersionInfo..." has a
                // word in front of it with a space between, and that is a reading like any other.
                var after = at + Name.Length;
                if (after < code.Length && IsWord(code[after])) continue;
                if (at > 0 && IsWord(code[at - 1])) continue;

                var before = at - 1;
                while (before >= 0 && char.IsWhiteSpace(code[before])) before--;

                var line = 1;
                for (var i = 0; i < at; i++) if (code[i] == '\n') line++;

                if (before >= 0 && code[before] == '.')
                {
                    // System.Diagnostics.FileVersionInfo is the type with its namespace on it.
                    // Anything else in front of that dot is a MEMBER called FileVersionInfo,
                    // which is how ProcessModule and FileInfo hand back the version resource of
                    // an EXE, and that is exactly what must not be read here.
                    var receiver = WordBefore(code, before);
                    if (!receiver.Equals("Diagnostics", StringComparison.Ordinal))
                    {
                        sites.Add(new VersionInfoSite
                        {
                            Kind = Property,
                            Argument = receiver + "." + Name,
                            Line = line,
                        });
                        continue;
                    }
                }

                var cursor = Skip(code, after);
                if (!At(code, cursor, Getter))
                {
                    sites.Add(new VersionInfoSite { Kind = TypeOnly, Argument = Name, Line = line });
                    continue;
                }

                cursor = Skip(code, cursor + Getter.Length);
                if (cursor >= code.Length || code[cursor] != '(')
                {
                    sites.Add(new VersionInfoSite { Kind = TypeOnly, Argument = Getter, Line = line });
                    continue;
                }

                var depth = 0;
                var end = -1;
                for (var k = cursor; k < code.Length; k++)
                {
                    if (code[k] == '(') depth++;
                    else if (code[k] == ')' && --depth == 0) { end = k; break; }
                }

                Assert.True(end > cursor,
                    "a FileVersionInfo.GetVersionInfo call at line " + line + " has no closing "
                    + "bracket the sweep can find, so its argument was never read. A sweep that "
                    + "cannot read a call must say so rather than pass it.");

                sites.Add(new VersionInfoSite
                {
                    Kind = Call,
                    Argument = Regex.Replace(
                        code.Substring(cursor + 1, end - cursor - 1), @"\s+", " ").Trim(),
                    Line = line,
                });
            }

            return sites;
        }

        private static bool IsWord(char c) => char.IsLetterOrDigit(c) || c == '_';

        private static int Skip(string code, int at)
        {
            while (at < code.Length && char.IsWhiteSpace(code[at])) at++;
            return at;
        }

        private static bool At(string code, int at, string what) =>
            at >= 0 && at + what.Length <= code.Length
            && string.CompareOrdinal(code, at, what, 0, what.Length) == 0;

        /// <summary>The identifier immediately in front of a dot, or an empty string.</summary>
        private static string WordBefore(string code, int dot)
        {
            var end = dot - 1;
            while (end >= 0 && char.IsWhiteSpace(code[end])) end--;

            var start = end;
            while (start >= 0 && IsWord(code[start])) start--;

            return end > start ? code.Substring(start + 1, end - start) : string.Empty;
        }

        /// <summary>
        /// The same source with every comment turned into spaces and everything else left where
        /// it was, so offsets and line numbers still line up, plus which characters turned out
        /// to be inside a string or character literal.
        /// <para>
        /// The literals are walked through rather than blanked, including verbatim and
        /// interpolated ones, because a double slash inside a path literal must not be read as
        /// the start of a comment: that would blank the code after it and hide a real reading.
        /// Where they are is answered by the same pass, because two passes over the same awkward
        /// rules is two chances to disagree about where a literal ended.
        /// </para>
        /// </summary>
        internal static string WithoutComments(string source, out bool[] quoted)
        {
            var text = source.ToCharArray();
            quoted = new bool[text.Length];
            var i = 0;

            while (i < text.Length)
            {
                var c = text[i];

                if (c == '"' || c == '\'')
                {
                    var verbatim = false;
                    if (c == '"')
                    {
                        var back = i - 1;
                        while (back >= 0 && (text[back] == '@' || text[back] == '$'))
                        {
                            if (text[back] == '@') verbatim = true;
                            back--;
                        }
                    }

                    quoted[i] = true;
                    i++;
                    while (i < text.Length)
                    {
                        if (verbatim)
                        {
                            if (text[i] != '"') { quoted[i++] = true; continue; }
                            if (i + 1 < text.Length && text[i + 1] == '"')
                            {
                                quoted[i++] = true;
                                quoted[i++] = true;
                                continue;
                            }
                            quoted[i++] = true;
                            break;
                        }

                        if (text[i] == '\\')
                        {
                            quoted[i++] = true;
                            if (i < text.Length) quoted[i++] = true;
                            continue;
                        }
                        if (text[i] == c) { quoted[i++] = true; break; }
                        // An unterminated literal is not something to guess at: stop at the line
                        // end and carry on reading code rather than swallowing the rest of the file.
                        if (text[i] == '\n') break;
                        quoted[i++] = true;
                    }
                    continue;
                }

                if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
                {
                    while (i < text.Length && text[i] != '\n') text[i++] = ' ';
                    continue;
                }

                if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
                {
                    text[i++] = ' ';
                    text[i++] = ' ';
                    while (i < text.Length)
                    {
                        if (text[i] == '*' && i + 1 < text.Length && text[i + 1] == '/')
                        {
                            text[i++] = ' ';
                            text[i++] = ' ';
                            break;
                        }
                        if (text[i] != '\n') text[i] = ' ';
                        i++;
                    }
                    continue;
                }

                i++;
            }

            return new string(text);
        }

        // --------------------------------------------------------------------------- the icon

        /// <summary>
        /// The sha256 of the icon the frozen launcher was generated with, and the same number
        /// the csproj pins. The icon is not in the DLL: the SDK writes it into the APPHOST's own
        /// resources, which is why it has to be guarded alongside the assembly name and the
        /// framework rather than assumed to follow the source.
        /// </summary>
        public const string IconSha256 =
            "193a8d1a11345b22ad213954ae5dc860c6bf381c3a7df4cbe3f21c1191ec2dac";

        private static string IconSource() =>
            RepoScript.At("ValheimBakaLoader", "Resources", "ApplicationIcon.ico");

        /// <summary>
        /// The build stops when the application icon moves, and it is a build error rather than
        /// a test alone because of how quiet this one would otherwise be. A new
        /// ApplicationIcon.ico would compile, pass every gate in this file and ship, while every
        /// host went on seeing the old picture: the launcher that carries the icon was frozen
        /// before it, and the copy puts those bytes back over the one the SDK just generated
        /// with the new picture in it. So the icon's hash is pinned beside the launcher's, and
        /// moving it means rebuilding and re-freezing the host deliberately.
        /// </summary>
        [Fact]
        public void The_build_stops_when_the_application_icon_moves()
        {
            var csproj = Csproj();

            Assert.Contains("<ApplicationIcon>Resources\\ApplicationIcon.ico</ApplicationIcon>", csproj);
            Assert.Contains(
                "<FrozenAppHostIconSha256>" + IconSha256 + "</FrozenAppHostIconSha256>", csproj);
            Assert.Contains(
                "<GetFileHash Files=\"$(FrozenAppHostIcon)\" Algorithm=\"SHA256\" HashEncoding=\"hex\"",
                csproj);
            Assert.Contains("'$(FrozenAppHostIconNow)' != '$(FrozenAppHostIconSha256)'", csproj);
            Assert.Contains("<Error Condition=\"!Exists('$(FrozenAppHostIcon)')\"", csproj);

            // And the error says what a reader has to do about it, which is not "change the pin".
            var text = csproj.Substring(csproj.IndexOf(
                "'$(FrozenAppHostIconNow)' != '$(FrozenAppHostIconSha256)'", StringComparison.Ordinal));
            text = text.Substring(0, text.IndexOf("/>", StringComparison.Ordinal));
            Assert.Contains("rebuilt and re-frozen deliberately", text);
            Assert.Contains("FrozenAppHostTests", text);

            // The pin names the file that is really there.
            Assert.Equal(IconSha256, Sha256Of(IconSource()));
        }

        /// <summary>
        /// And the pin guards something real. Every image in that .ico is inside the frozen
        /// launcher's bytes, byte for byte, which is what makes a changed icon with an unchanged
        /// launcher a release that shows the wrong picture rather than a harmless difference.
        /// </summary>
        [Fact]
        public void The_frozen_launcher_carries_the_icon_the_build_pins()
        {
            var icon = File.ReadAllBytes(IconSource());
            var host = File.ReadAllBytes(FrozenSource());

            var images = IconImages(icon);
            Assert.Equal(7, images.Count);

            foreach (var image in images)
                Assert.True(host.AsSpan().IndexOf(image.AsSpan()) >= 0,
                    "an image of " + image.Length + " bytes in ApplicationIcon.ico is not inside "
                    + "the frozen launcher, so what ships does not show the icon in this tree. "
                    + "The host has to be regenerated and re-frozen.");
        }

        /// <summary>Every image in a .ico, read out of its directory.</summary>
        private static List<byte[]> IconImages(byte[] icon)
        {
            Assert.True(icon.Length > 22, "ApplicationIcon.ico is too small to be an icon");

            var count = BitConverter.ToUInt16(icon, 4);
            var images = new List<byte[]>();

            for (var i = 0; i < count; i++)
            {
                var entry = 6 + i * 16;
                var size = BitConverter.ToInt32(icon, entry + 8);
                var offset = BitConverter.ToInt32(icon, entry + 12);

                Assert.InRange(offset, 0, icon.Length);
                Assert.InRange(size, 1, icon.Length - offset);

                images.Add(icon.AsSpan(offset, size).ToArray());
            }

            return images;
        }
    }
}
