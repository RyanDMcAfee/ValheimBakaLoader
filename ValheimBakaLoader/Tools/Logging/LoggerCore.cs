using Serilog;
using Serilog.Events;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using ValheimBakaLoader.Properties;

namespace ValheimBakaLoader.Tools.Logging
{
    /// <summary>
    /// One stage of a log pipeline: returns the (possibly rewritten) line,
    /// or null to drop the line entirely.
    /// </summary>
    public delegate string LogStep(LogEvent logEvent, string line);

    /// <summary>The pipeline stages the app's loggers compose.</summary>
    public static class LogSteps
    {
        /// <summary>Drops any line matching the pattern.</summary>
        public static LogStep DropMatching(string pattern)
        {
            var regex = new Regex(pattern);
            return (_, line) => regex.IsMatch(line) ? null : line;
        }

        /// <summary>Deletes the matching portion of a line, keeping the rest.</summary>
        public static LogStep StripMatching(string pattern)
        {
            var regex = new Regex(pattern);
            return (_, line) => regex.Replace(line, string.Empty);
        }

        /// <summary>Prefixes a severity tag; info lines stay bare.</summary>
        public static LogStep TagSeverity()
        {
            return (logEvent, line) => logEvent.Level switch
            {
                LogEventLevel.Verbose => $"[VER] {line}",
                LogEventLevel.Debug => $"[DBG] {line}",
                LogEventLevel.Warning => $"[WRN] {line}",
                LogEventLevel.Error => $"[ERR] {line}",
                LogEventLevel.Fatal => $"[FAT] {line}",
                _ => line,
            };
        }

        /// <summary>Prefixes the event's wall-clock time.</summary>
        public static LogStep Timestamp()
        {
            return (logEvent, line) => $"{logEvent.Timestamp:[HH:mm:ss.fff]} {line}";
        }
    }

    /// <summary>
    /// A logger that keeps a tail of recent lines in memory and announces
    /// each line to subscribers (the UI streams these live).
    /// </summary>
    public interface IBufferedLogger : ILogger
    {
        event Action<string> LogReceived;

        IEnumerable<string> LogBuffer { get; }
    }

    /// <summary>
    /// Serilog-backed logger that pushes every event through an ordered list
    /// of <see cref="LogStep"/>s, then fans the finished line out to the
    /// in-memory tail, live subscribers, and (optionally) a daily rolling
    /// file under the app's logs folder.
    /// </summary>
    public abstract class PipelineLogger : IBufferedLogger
    {
        private readonly RingBuffer<string> History = new(1000);
        private readonly List<LogStep> Steps = new();
        private ILogger Sink;

        public event Action<string> LogReceived;

        public IEnumerable<string> LogBuffer => History;

        /// <summary>Appends a stage to the pipeline. Stages run in the order added.</summary>
        protected void Use(LogStep step)
        {
            Steps.Add(step);
        }

        /// <summary>
        /// Names the rolling log file to write to, or null for no file output.
        /// Re-evaluated whenever <see cref="Rebuild"/> is called.
        /// </summary>
        protected abstract string ResolveLogFile();

        /// <summary>Tears down the sink so the next write reflects new settings.</summary>
        protected void Rebuild()
        {
            Sink = null;
        }

        public void Write(LogEvent logEvent)
        {
            // Built lazily on first write so DI has finished wiring by then.
            Sink ??= BuildSink();

            var line = logEvent.RenderMessage();

            foreach (var step in Steps)
            {
                line = step(logEvent, line);
                if (line == null) return;
            }

            Sink.Write(logEvent.Level, line);
            History.Add(line);
            LogReceived?.Invoke(line);
        }

        private ILogger BuildSink()
        {
            var config = new LoggerConfiguration();

#if DEBUG
            config.MinimumLevel.Verbose();
#else
            config.MinimumLevel.Debug();
#endif

            var fileName = ResolveLogFile();
            if (!string.IsNullOrWhiteSpace(fileName))
            {
                var folder = Environment.ExpandEnvironmentVariables(Resources.LogsFolderPath);
                Directory.CreateDirectory(folder);
                var path = Path.Join(folder, $"{PathExtensions.GetValidFileName(fileName)}_.txt");

                config.WriteTo.File(path,
                    rollingInterval: RollingInterval.Day,
                    retainedFileTimeLimit: TimeSpan.FromDays(30),
                    outputTemplate: "{Message:lj}{NewLine}",
                    shared: true); // The app and a server can share one file.
            }

            return config.CreateLogger();
        }
    }
}
