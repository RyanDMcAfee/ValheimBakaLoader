using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using ValheimBakaLoader.Tools.Processes;
using Xunit;

namespace ValheimBakaLoader.Tests.Tools
{
    /// <summary>
    /// WHERE THE NAME WAS BROKEN. The server writes its console output as UTF-8, and the app
    /// learns every player from that output: the connect lines, the character spawn line, the
    /// player id line, the playerlist capture. The registry redirected both streams and set no
    /// encoding on either, so .NET decoded them with the machine's own code page and a name in
    /// Greek, Cyrillic or Japanese arrived as one Latin-1 character per byte. Everything keyed
    /// on the name inherited it: the roster, the kick, the ban, the permit, the teleport, the
    /// heal, the smite, the spawn at, the statistics.
    /// <para>
    /// The first test drives a REAL child process that writes those exact bytes, because the
    /// bug was in the decoding and not in the settings. The second pins the settings themselves
    /// for the streams nobody can conveniently drive, and runs on a machine with no shell.
    /// </para>
    /// </summary>
    public class ProcessOutputEncodingTests
    {
        [Fact]
        public void A_child_that_writes_utf8_is_read_as_utf8()
        {
            var shell = Environment.GetEnvironmentVariable("ComSpec");
            if (string.IsNullOrWhiteSpace(shell) || !File.Exists(shell))
            {
                // No cmd.exe means no child to drive here. The settings test below still holds
                // the rule; this one has nothing to say and says so rather than failing.
                return;
            }

            // The reported name, written to a file as the bytes the server would write: CE A9
            // 20 CE A3. "type" copies a file's bytes to its output, so what the child emits is
            // exactly what a Unity server emits, with nothing in between to fix it up.
            var folder = Path.Combine(Path.GetTempPath(), "vbl-encoding-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            var file = Path.Combine(folder, "name.txt");

            try
            {
                File.WriteAllBytes(file, new UTF8Encoding(false).GetBytes(Mojibake.Greek + "\r\n"));

                var lines = new ConcurrentQueue<string>();
                var read = new ManualResetEventSlim();

                var processes = new ProcessProvider();
                var process = processes.AddBackgroundProcess(
                    "encoding-test", shell, "/c type \"" + file + "\"");

                process.OutputDataReceived += (_, e) =>
                {
                    if (e.Data == null) { read.Set(); return; }
                    if (e.Data.Trim().Length > 0) lines.Enqueue(e.Data.Trim());
                };

                processes.StartIO(process);
                Assert.True(process.WaitForExit(30000), "the child never exited");
                Assert.True(read.Wait(5000), "the output stream never ended");

                Assert.True(lines.TryDequeue(out var line), "the child printed nothing");
                Assert.Equal(Mojibake.Greek, line);

                // Said the other way round as well, because equality on a name nobody can read
                // in a failure message is worth very little: the four characters the bug
                // produced must not be what came back.
                Assert.NotEqual(Mojibake.GreekBroken, line);
            }
            finally
            {
                try { Directory.Delete(folder, true); } catch { /* best effort */ }
            }
        }

        [Fact]
        public void Both_redirected_streams_are_built_to_be_read_as_utf8()
        {
            var processes = new ProcessProvider();
            var process = processes.AddBackgroundProcess("settings-test", "whatever.exe", "--args");

            var info = process.StartInfo;
            Assert.True(info.RedirectStandardOutput);
            Assert.True(info.RedirectStandardError);

            // Error as well as output: the server writes its own warnings there, and a name in
            // one of them is a name the log shows the host.
            Assert.NotNull(info.StandardOutputEncoding);
            Assert.NotNull(info.StandardErrorEncoding);
            Assert.Equal(Encoding.UTF8.CodePage, info.StandardOutputEncoding.CodePage);
            Assert.Equal(Encoding.UTF8.CodePage, info.StandardErrorEncoding.CodePage);

            // No byte order mark: a mark would be written into the first line the app parsed.
            Assert.Empty(info.StandardOutputEncoding.GetPreamble());
            Assert.Empty(info.StandardErrorEncoding.GetPreamble());

            process.Dispose();
        }
    }
}
