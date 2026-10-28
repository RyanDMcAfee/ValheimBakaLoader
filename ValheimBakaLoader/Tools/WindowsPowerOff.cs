using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace ValheimBakaLoader.Tools
{
    /// <summary>
    /// The machine's own power switch, as far as BakaLoader ever reaches for it: a timed
    /// shutdown that Windows announces itself, and the way to call one off.
    /// <para>
    /// An interface so that nothing but the window ever holds the real one. The server uses
    /// whichever it is handed, and every test hands it a fake that writes the calls down: a test
    /// run that reached the real one would switch off the machine it runs on.
    /// </para>
    /// </summary>
    public interface IWindowsPowerOff
    {
        /// <summary>Asks Windows to shut down after <paramref name="seconds"/>, showing <paramref name="comment"/> in its notice.</summary>
        void Schedule(int seconds, string comment);

        /// <summary>Calls off a shutdown that is counting down.</summary>
        void Cancel();
    }

    /// <summary>
    /// The real one: <c>shutdown.exe</c>, started with no window, answered by its exit code.
    /// <para>
    /// A timed shutdown rather than an immediate one, because the minute is the host's chance to
    /// change their mind: Windows shows its own notice with the comment in it for the whole of
    /// the wait, and <c>shutdown /a</c> in any command prompt calls it off. When the minute is
    /// out Windows closes the programs still open without asking them to save, which is what a
    /// timed shutdown always does, and the wiki says so beside the switch.
    /// </para>
    /// </summary>
    public sealed class WindowsPowerOff : IWindowsPowerOff
    {
        /// <summary>How long the notice stands before Windows goes down.</summary>
        public const int NoticeSeconds = 60;

        /// <summary>The longest comment shutdown.exe takes.</summary>
        public const int CommentLimit = 512;

        /// <summary>The longest wait shutdown.exe takes: ten years, in seconds.</summary>
        public const int MaxSeconds = 315360000;

        /// <summary>The sentence in the notice Windows shows after a stop when empty.</summary>
        public const string EmptyStopComment =
            "BakaLoader: the Valheim server stopped after sitting empty. Windows shuts down in one minute; run shutdown /a to cancel.";

        /// <summary>The arguments that call a pending shutdown off.</summary>
        public const string CancelArguments = "/a";

        /// <summary>
        /// The arguments for a timed shutdown, as one string: <c>/s /t seconds /c "comment"</c>.
        /// <para>
        /// The comment is cut to the 512 characters shutdown.exe takes, its line breaks and other
        /// control characters become spaces, and it is quoted the way a Windows program reads its
        /// own command line: a quotation mark inside it is escaped with a backslash, and a run of
        /// backslashes in front of a quotation mark (or in front of the closing one) is doubled,
        /// so nothing in the comment can end the argument early and add one of its own.
        /// </para>
        /// </summary>
        public static string Arguments(int seconds, string comment)
        {
            var wait = Math.Clamp(seconds, 0, MaxSeconds);
            return "/s /t " + wait.ToString(System.Globalization.CultureInfo.InvariantCulture)
                   + " /c " + Quote(Cut(Clean(comment ?? "")));
        }

        public void Schedule(int seconds, string comment) => Run(Arguments(seconds, comment));

        public void Cancel() => Run(CancelArguments);

        private static string Clean(string text)
        {
            var sb = new StringBuilder(text.Length);
            foreach (var c in text) sb.Append(char.IsControl(c) ? ' ' : c);
            return sb.ToString();
        }

        private static string Cut(string text)
        {
            if (text.Length <= CommentLimit) return text;
            // Never half a surrogate pair at the cut.
            var length = char.IsHighSurrogate(text[CommentLimit - 1]) ? CommentLimit - 1 : CommentLimit;
            return text.Substring(0, length);
        }

        private static string Quote(string text)
        {
            var sb = new StringBuilder(text.Length + 2);
            sb.Append('"');
            var slashes = 0;
            foreach (var c in text)
            {
                if (c == '\\')
                {
                    slashes++;
                    continue;
                }

                if (c == '"')
                {
                    sb.Append('\\', slashes * 2 + 1);
                    sb.Append('"');
                }
                else
                {
                    sb.Append('\\', slashes);
                    sb.Append(c);
                }

                slashes = 0;
            }

            sb.Append('\\', slashes * 2);
            sb.Append('"');
            return sb.ToString();
        }

        private static void Run(string arguments)
        {
            var psi = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "shutdown.exe"),
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException("shutdown.exe did not start.");
            var said = proc.StandardError.ReadToEndAsync();
            _ = proc.StandardOutput.ReadToEndAsync();

            // A refusal (a shutdown already pending, no right to shut down) comes back as an exit
            // code and nothing else, so it is read rather than taken for a yes.
            if (!proc.WaitForExit(10000))
                throw new TimeoutException("shutdown.exe did not answer within 10 seconds.");
            if (proc.ExitCode != 0)
                throw new InvalidOperationException(
                    "shutdown.exe answered " + proc.ExitCode + ": " + (said.Result ?? "").Trim());
        }
    }
}
