using Serilog;
using System;
using System.IO;
using ValheimBakaLoader.Game;

namespace ValheimBakaLoader.Tools.Logging
{
    public interface IValheimServerLogger : IBufferedLogger
    {
    }

    /// <summary>
    /// Per-server logger fed by the dedicated server's console output.
    /// Cleans up the game's own formatting quirks before display. A fresh
    /// instance is created for every server start, so each session writes
    /// its own file (stamped with the start time) instead of one endless
    /// daily-rolling stream.
    /// </summary>
    public class ValheimServerLogger : PipelineLogger, IValheimServerLogger
    {
        private readonly IValheimServerOptions Options;

        // Captured once at construction = once per server session.
        private readonly string SessionStamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");

        public ValheimServerLogger(IValheimServerOptions options)
        {
            Options = options;

            // The game stamps some lines with its own date prefix; strip it
            // so every line carries exactly one (ours).
            Use(LogSteps.StripMatching(@"^\d+\/\d+\/\d+ \d+:\d+:\d+:\s+"));

            if (!options.LogFilteringDisabled)
            {
                Use(LogSteps.DropMatching(@"^\(Filename:")); // Unity source-location spam
                Use(LogSteps.DropMatching(@"^Console: "));
                Use(LogSteps.DropMatching(@"^\s*?$"));       // blank lines
            }

            Use(LogSteps.Timestamp());
        }

        protected override string ResolveLogFile()
        {
            return Options.LogToFile ? $"ServerLogs-{Options.Name}-{SessionStamp}" : null;
        }

        // Session files are fixed-name (the stamp does the splitting).
        protected override RollingInterval Rolling => RollingInterval.Infinite;

        protected override string ResolveLogFolder()
        {
            return string.IsNullOrWhiteSpace(Options.LogFolderPath)
                ? base.ResolveLogFolder()
                : Options.LogFolderPath;
        }

        /// <summary>
        /// Serilog's retention never sees older sessions' files (each sink only
        /// tracks its own), so stale ServerLogs-* files prune themselves here.
        /// Mirrors the 30-day retention the day-rolling app log uses.
        /// </summary>
        protected override void PruneOldLogs(string folder)
        {
            var cutoff = DateTime.Now.AddDays(-30);
            foreach (var file in Directory.GetFiles(folder, "ServerLogs-*.txt"))
            {
                try
                {
                    if (File.GetLastWriteTime(file) < cutoff) File.Delete(file);
                }
                catch { /* locked or already gone - leave it */ }
            }
        }
    }
}
