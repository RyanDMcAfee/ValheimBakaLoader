using System;
using System.Linq;
using ValheimBakaLoader.Tools;
using Xunit;

namespace ValheimBakaLoader.Tests.Tools
{
    /// <summary>
    /// The real power switch, asked for the one thing a test may ask it: the string it would hand
    /// shutdown.exe. Nothing in this project ever builds the real one or calls Schedule or Cancel
    /// on it, because a test that did would switch off the machine running the tests; the server
    /// tests hand a fake that writes the calls down instead, and the last test here holds that.
    /// </summary>
    public class WindowsPowerOffArgumentsTests
    {
        [Fact]
        public void A_plain_comment_is_quoted_after_the_wait()
        {
            Assert.Equal("/s /t 60 /c \"BakaLoader: the Valheim server stopped after sitting empty. Windows shuts down in one minute; run shutdown /a to cancel.\"",
                WindowsPowerOff.Arguments(60, WindowsPowerOff.EmptyStopComment));
        }

        // (i) a quotation mark cannot end the argument early
        [Fact]
        public void A_quotation_mark_inside_the_comment_is_escaped()
        {
            Assert.Equal("/s /t 60 /c \"a \\\"quoted\\\" comment\"", WindowsPowerOff.Arguments(60, "a \"quoted\" comment"));
        }

        [Theory]
        // a backslash in the middle stays one backslash
        [InlineData("C:\\saves", "\"C:\\saves\"")]
        // backslashes in front of a quotation mark are doubled, and the mark escaped
        [InlineData("a\\\"b", "\"a\\\\\\\"b\"")]
        // backslashes in front of the closing mark are doubled, so the mark still closes
        [InlineData("end\\", "\"end\\\\\"")]
        // a comment that tries to add a switch of its own stays inside the one argument
        [InlineData("x\" /r /t 0 \"", "\"x\\\" /r /t 0 \\\"\"")]
        public void Backslashes_are_written_the_way_a_windows_program_reads_them(string comment, string quoted)
        {
            Assert.Equal("/s /t 60 /c " + quoted, WindowsPowerOff.Arguments(60, comment));
        }

        // (i) the 512 character limit is shutdown.exe's own
        [Fact]
        public void A_long_comment_is_cut_to_512_characters()
        {
            var args = WindowsPowerOff.Arguments(60, new string('k', 600));
            var inside = args.Substring(args.IndexOf('"') + 1).TrimEnd('"');
            Assert.Equal(512, inside.Length);
            Assert.Equal(512, WindowsPowerOff.CommentLimit);
        }

        [Fact]
        public void The_cut_never_splits_a_surrogate_pair_and_line_breaks_become_spaces()
        {
            var args = WindowsPowerOff.Arguments(60, new string('k', 511) + "\U0001F525" + "tail");
            var inside = args.Substring(args.IndexOf('"') + 1).TrimEnd('"');
            Assert.Equal(511, inside.Length);

            Assert.Equal("/s /t 60 /c \"one two  three\"", WindowsPowerOff.Arguments(60, "one\ntwo\r\nthree"));
            Assert.Equal("/s /t 60 /c \"\"", WindowsPowerOff.Arguments(60, null));
        }

        [Theory]
        [InlineData(-5, "/s /t 0 /c \"x\"")]
        [InlineData(int.MaxValue, "/s /t 315360000 /c \"x\"")]
        public void The_wait_is_held_to_what_shutdown_takes(int seconds, string expected)
        {
            Assert.Equal(expected, WindowsPowerOff.Arguments(seconds, "x"));
        }

        [Fact]
        public void The_cancel_is_the_abort_switch_and_nothing_else()
        {
            Assert.Equal("/a", WindowsPowerOff.CancelArguments);
        }

        /// <summary>
        /// The real one is built in exactly one place in the app, the window that hands it to
        /// every session, and nowhere at all in the tests. The search strings are put together
        /// here so this file is not itself the match it is looking for.
        /// </summary>
        [Fact]
        public void The_real_power_switch_is_built_only_by_the_window_and_never_in_a_test()
        {
            var built = "new " + "WindowsPowerOff(";

            var app = AppSourceTree.ShippedFiles().Where(f => f.Value.Contains(built, StringComparison.Ordinal)).Select(f => f.Key).ToList();
            Assert.Single(app);
            Assert.EndsWith("BlendWindow.Bridge.cs", app[0].Replace('\\', '/'));

            var root = AppSourceTree.RepoRoot();
            var tests = System.IO.Directory.EnumerateFiles(System.IO.Path.Combine(root, "ValheimBakaLoader.Tests"), "*.cs", System.IO.SearchOption.AllDirectories)
                .Where(p => !p.Replace('\\', '/').Contains("/obj/") && !p.Replace('\\', '/').Contains("/bin/"))
                .Where(p => System.IO.File.ReadAllText(p).Contains(built, StringComparison.Ordinal))
                .ToList();
            Assert.Empty(tests);
        }
    }
}
