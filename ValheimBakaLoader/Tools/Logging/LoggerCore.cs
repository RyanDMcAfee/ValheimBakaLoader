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

        /// <summary>
        /// The folder log files land in. Overridable so the user's
        /// "logs folder" preference can redirect output; may contain
        /// environment variables. Re-evaluated on <see cref="Rebuild"/>.
        /// </summary>
        protected virtual string ResolveLogFolder() => Resources.LogsFolderPath;

        /// <summary>
        /// How the file sink rolls. Day = one file per calendar day (the app log);
        /// Infinite = one fixed file (per-session server logs bake their own stamp
        /// into the file name instead).
        /// </summary>
        protected virtual RollingInterval Rolling => RollingInterval.Day;

        /// <summary>
        /// Hook for cleaning up old log files when the sink is (re)built.
        /// Serilog's retention only tracks its own rolling set, so per-session
        /// files from previous runs prune themselves here.
        /// </summary>
        protected virtual void PruneOldLogs(string folder) { }

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

            // The exception, when there is one, goes to the file with the line. RenderMessage
            // does not carry it and the old write handed the sink a bare string, so every
            // Logger.Warning(ex, ...) in the app wrote its sentence and threw the reason away:
            // the host whose machine could not reach Thunderstore had a log full of "did not
            // answer" and nothing saying whether that was a proxy, a name lookup or a timeout.
            // The line is passed as a VALUE rather than as the template it used to be, so a
            // sentence carrying braces cannot be read as a property token.
            if (logEvent.Exception != null) Sink.Write(logEvent.Level, logEvent.Exception, "{Line}", line);
            else Sink.Write(logEvent.Level, "{Line}", line);

            // What the window and the in-memory tail show gains the exception's own type and
            // innermost message, because that is the copy a host reads and pastes into a
            // report. The file gets the whole stack through the template below.
            if (logEvent.Exception != null) line += " [" + Summarise(logEvent.Exception) + "]";

            History.Add(line);
            LogReceived?.Invoke(line);
        }

        /// <summary>The exception in one bracket: its type, and the message at the bottom of it.</summary>
        private static string Summarise(Exception problem)
        {
            var innermost = problem;
            while (innermost.InnerException != null) innermost = innermost.InnerException;

            return innermost == problem
                ? problem.GetType().Name + ": " + problem.Message
                : problem.GetType().Name + ": " + problem.Message
                    + " <- " + innermost.GetType().Name + ": " + innermost.Message;
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
                // A bad custom logs folder must never take the logger down with it -
                // fall back to in-memory/live-stream only and keep running.
                try
                {
                    var folder = Environment.ExpandEnvironmentVariables(ResolveLogFolder());
                    Directory.CreateDirectory(folder);
                    try { PruneOldLogs(folder); } catch { /* best-effort cleanup */ }

                    // Day-rolling files carry Serilog's date suffix after the "_";
                    // fixed (per-session) files are named in full already.
                    var suffix = Rolling == RollingInterval.Infinite ? "" : "_";
                    var path = Path.Join(folder, $"{PathExtensions.GetValidFileName(fileName)}{suffix}.txt");

                    config.WriteTo.File(path,
                        rollingInterval: Rolling,
                        retainedFileTimeLimit: TimeSpan.FromDays(30),
                        outputTemplate: "{Message:lj}{NewLine}{Exception}",
                        shared: true); // The app and a server can share one file.
                }
                catch
                {
                    // Folder unusable (bad path, no permission) - skip file output.
                }
            }

            return config.CreateLogger();
        }
    }
}
