// BakaLoader Commander: the one line a finished sweep writes to the server's own output.
//
// WHY IT EXISTS
// A sweep too big to finish inside its RCON answer (cleardrops, killall, cleanse) used to put
// its result line in the BepInEx log and nowhere else. BakaLoader reads the server's stdout,
// not that file, and BepInEx ships with its console logging off, so the result of a long
// sweep never reached the window or the Saga. A host cleared 5245 items off a big world and
// was told nothing at all. So the last line of every sweep is ALSO written through Unity's
// own log, which a dedicated server prints on stdout, behind a tag nothing else prints.
//
// WHY THIS FILE IS PLAIN C#
// The same reason BakaClearDropsPlan.cs is: it names no Unity, BepInEx or Valheim type, so it
// compiles into BakaLoaderCommander.dll through Resources\build-plugins.ps1 AND into BakaLoader
// itself through the app project's ordinary source glob. The plugin writes the line with
// Line() and the app's log reader reads it back with TryRead(), out of this one file, so the
// two sides cannot drift into spelling the tag differently.
using System;

namespace BakaLoaderSweepEcho
{
    /// <summary>The tag, the writer and the reader of a sweep's result line on stdout.</summary>
    internal static class SweepEcho
    {
        /// <summary>What every such line carries. Nothing else the plugin prints opens with it.</summary>
        internal const string Tag = "[BakaLoader Commander]";

        /// <summary>The three sliced sweeps, by the word their status line opens with.</summary>
        internal static readonly string[] Verbs = { "cleardrops", "killall", "cleanse" };

        /// <summary>
        /// The stdout line for a sweep's result. A result that already opens with the verb and
        /// a colon (every cleardrops line does) is written as it is; any other is given the verb
        /// in front, so "KillAll complete: ..." goes out as
        /// "[BakaLoader Commander] killall: KillAll complete: ...".
        /// </summary>
        internal static string Line(string verb, string result)
        {
            var said = (result ?? "").Trim();
            var lead = (verb ?? "").Trim().ToLowerInvariant();
            if (lead.Length == 0) return Tag + " " + said;

            if (said.StartsWith(lead + ":", StringComparison.OrdinalIgnoreCase))
                return Tag + " " + said;

            return Tag + " " + lead + ": " + said;
        }

        /// <summary>
        /// Reads one line of server output back into the verb and the plugin's own result line.
        /// The tag may sit anywhere on the line, because a server can put a timestamp in front
        /// of what Unity prints. False for any line that is not one of these.
        /// </summary>
        internal static bool TryRead(string logLine, out string verb, out string result)
        {
            verb = null;
            result = null;
            if (string.IsNullOrEmpty(logLine)) return false;

            var at = logLine.IndexOf(Tag, StringComparison.Ordinal);
            if (at < 0) return false;

            var rest = logLine.Substring(at + Tag.Length).Trim();
            var colon = rest.IndexOf(':');
            if (colon <= 0) return false;

            var word = rest.Substring(0, colon).Trim().ToLowerInvariant();
            if (Array.IndexOf(Verbs, word) < 0) return false;

            var tail = rest.Substring(colon + 1).Trim();
            if (tail.Length == 0) return false;

            verb = word;
            // Every cleardrops line opens with its own verb, which Line() did not repeat, so it
            // is put back. The other two open with words of their own ("KillAll complete",
            // "Cleanse stopped", an "Error:"), which Line() kept whole after the verb.
            result = word == "cleardrops" ? "cleardrops: " + tail : tail;
            return true;
        }
    }
}
