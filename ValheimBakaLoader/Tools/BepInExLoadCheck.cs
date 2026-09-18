using System;

namespace ValheimBakaLoader.Tools
{
    /// <summary>
    /// What the load check can say about one start.
    /// </summary>
    public enum BepInExLoadVerdict
    {
        /// <summary>BepInEx is not installed, so there is nothing to have loaded.</summary>
        NotInstalled,

        /// <summary>The server has not been up long enough for the absence to mean anything.</summary>
        TooEarly,

        /// <summary>The preloader wrote its log after this process started, so it ran.</summary>
        Loaded,

        /// <summary>The grace has passed and no log was written for this start.</summary>
        NotLoaded,
    }

    /// <summary>
    /// Whether BepInEx actually loaded on this start, decided from files rather than from
    /// anything the server prints.
    /// <para>
    /// BakaLoader never passes the server a loader flag: on Windows the whole mechanism is
    /// <c>winhttp.dll</c> sitting beside the executable and being picked up by the DLL search
    /// order. So there is no launch argument to get wrong and, correspondingly, no answer
    /// coming back. The one thing BepInEx does leave behind is
    /// <c>BepInEx/LogOutput.log</c>, which its preloader rewrites at every launch. A log
    /// whose last write is older than the process that should have written it means the
    /// preloader never ran, and the host has a modded server running vanilla with no sign of
    /// it anywhere.
    /// </para>
    /// <para>
    /// The grace exists because the log is written a moment into startup, not at the instant
    /// the process is created. Ninety seconds is far longer than that takes and far shorter
    /// than a host would tolerate wondering, and it is a parameter so the table test can walk
    /// both sides of it.
    /// </para>
    /// </summary>
    public static class BepInExLoadCheck
    {
        /// <summary>The name of the file the preloader rewrites at every launch.</summary>
        public const string LogFileName = "LogOutput.log";

        /// <summary>How long after a start the absence of a log means nothing yet.</summary>
        public const int DefaultGraceSeconds = 90;

        /// <summary>
        /// The verdict for one start. Pure: every fact it needs is an argument.
        /// </summary>
        /// <param name="installed">Whether BepInEx is installed at all.</param>
        /// <param name="logMtimeUtc">Last write of BepInEx/LogOutput.log, or null when absent.</param>
        /// <param name="processStartUtc">When this server process was started.</param>
        /// <param name="nowUtc">The moment the question is asked.</param>
        /// <param name="graceSeconds">Seconds after the start during which silence means nothing.</param>
        public static BepInExLoadVerdict Verdict(
            bool installed,
            DateTime? logMtimeUtc,
            DateTime processStartUtc,
            DateTime nowUtc,
            int graceSeconds = DefaultGraceSeconds)
        {
            if (!installed) return BepInExLoadVerdict.NotInstalled;

            // A log written at or after the start is this start's log. Equality counts as
            // loaded: a filesystem that stamps to the second can land both on the same tick,
            // and calling that "did not load" would raise the row on a healthy server.
            if (logMtimeUtc.HasValue && logMtimeUtc.Value >= processStartUtc)
                return BepInExLoadVerdict.Loaded;

            var grace = graceSeconds < 0 ? 0 : graceSeconds;
            if ((nowUtc - processStartUtc).TotalSeconds < grace)
                return BepInExLoadVerdict.TooEarly;

            return BepInExLoadVerdict.NotLoaded;
        }

        /// <summary>
        /// The verdict as the condition row reads it: true only for the one state worth
        /// saying out loud.
        /// </summary>
        public static bool DidNotLoad(
            bool installed,
            DateTime? logMtimeUtc,
            DateTime processStartUtc,
            DateTime nowUtc,
            int graceSeconds = DefaultGraceSeconds)
            => Verdict(installed, logMtimeUtc, processStartUtc, nowUtc, graceSeconds)
               == BepInExLoadVerdict.NotLoaded;
    }
}
