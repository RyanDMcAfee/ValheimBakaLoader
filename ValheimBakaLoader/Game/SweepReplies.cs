using System;
using BakaLoaderClearDrops;
using BakaLoaderKillAll;

namespace ValheimBakaLoader.Game
{
    /// <summary>
    /// What a sliced sweep (baka_cleardrops, baka_killall, baka_cleanse) or its status verb
    /// answered, read on the host side.
    /// <para>
    /// The page polls the status verb after a "started" answer and toasts the result, but a
    /// page line is gone the moment the window closes. The result is the one line a host comes
    /// back to the Saga for, so the bridge writes it into the application log as well, and this
    /// is how it knows which lines those are. Every opening it reads is the plugin's own
    /// constant, out of the plan files the app compiles too, so a reworded plugin line cannot
    /// leave this reading a sentence nobody writes any more.
    /// </para>
    /// </summary>
    public static class SweepReplies
    {
        /// <summary>
        /// Which sweep a command line is about, by its first word: "cleardrops", "killall" or
        /// "cleanse" for the verb and for its status verb, and null for anything else.
        /// </summary>
        public static string VerbOf(string command)
        {
            var text = (command ?? "").Trim();
            if (text.Length == 0) return null;

            var space = text.IndexOf(' ');
            var first = (space < 0 ? text : text.Substring(0, space)).ToLowerInvariant();

            switch (first)
            {
                case ClearDropsPlan.Verb:
                case ClearDropsPlan.StatusVerb:
                    return "cleardrops";
                case "baka_killall":
                case KillAllPlan.StatusVerb:
                    return "killall";
                case "baka_cleanse":
                case "baka_cleanse_status":
                    return "cleanse";
                default:
                    return null;
            }
        }

        /// <summary>
        /// Whether a console line names a realm other than the one on screen, and so must not
        /// be sent. The page's sweep wait names the realm it is waiting on with every status
        /// poll, because server.command goes to whichever realm's server is on screen: after a
        /// realm switch the poll would ask the other server, and a result it kept from an
        /// earlier sweep would read as this one's. A line that names no realm (the free-typed
        /// console, the command picker, a page from an older build) goes where it always did.
        /// </summary>
        public static bool IsForAnotherRealm(string asked, string onScreen)
        {
            if (string.IsNullOrWhiteSpace(asked)) return false;
            return !string.Equals(asked.Trim(), (onScreen ?? "").Trim(), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Whether a status line says the sweep is still walking, in the shape all three share
        /// from Commander 1.9.2 or the one an older plugin's cleanse answered with.
        /// </summary>
        public static bool IsRunning(string reply)
        {
            var said = (reply ?? "").TrimStart();
            return said.StartsWith(ClearDropsPlan.RunningPrefix, StringComparison.OrdinalIgnoreCase)
                   || said.StartsWith(KillAllPlan.RunningPrefix, StringComparison.OrdinalIgnoreCase)
                   || CleansePlan.IsRunningStatus(said);
        }

        /// <summary>
        /// Whether a reply is the result line of the named sweep: the counts of one that
        /// finished, or of one that stopped part way. A "started" line, a status line, a refusal
        /// and an idle answer are not.
        /// </summary>
        public static bool IsResult(string verb, string reply)
        {
            var said = (reply ?? "").Trim();
            if (said.Length == 0) return false;

            switch (verb)
            {
                case "cleardrops":
                    return said.StartsWith(ClearDropsPlan.ReplyPrefix + " removed ", StringComparison.OrdinalIgnoreCase)
                           || said.StartsWith(ClearDropsPlan.ReplyPrefix + " stopped early", StringComparison.OrdinalIgnoreCase);
                case "killall":
                    return said.StartsWith("KillAll complete:", StringComparison.OrdinalIgnoreCase)
                           || said.StartsWith("KillAll stopped early:", StringComparison.OrdinalIgnoreCase);
                case "cleanse":
                    return said.StartsWith(CleansePlan.CompletePrefix, StringComparison.OrdinalIgnoreCase)
                           || said.StartsWith(CleansePlan.StoppedPrefix, StringComparison.OrdinalIgnoreCase);
                default:
                    return false;
            }
        }
    }
}
