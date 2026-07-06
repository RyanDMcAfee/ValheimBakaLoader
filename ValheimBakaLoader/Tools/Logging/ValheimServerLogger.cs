using ValheimBakaLoader.Game;

namespace ValheimBakaLoader.Tools.Logging
{
    public interface IValheimServerLogger : IBufferedLogger
    {
    }

    /// <summary>
    /// Per-server logger fed by the dedicated server's console output.
    /// Cleans up the game's own formatting quirks before display.
    /// </summary>
    public class ValheimServerLogger : PipelineLogger, IValheimServerLogger
    {
        private readonly IValheimServerOptions Options;

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
            return Options.LogToFile ? $"ServerLogs-{Options.Name}" : null;
        }
    }
}
