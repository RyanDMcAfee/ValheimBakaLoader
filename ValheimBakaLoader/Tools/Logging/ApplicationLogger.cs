using Microsoft.Extensions.DependencyInjection;
using Serilog.Core;
using System;
using ValheimBakaLoader.Game;

namespace ValheimBakaLoader.Tools.Logging
{
    public interface IApplicationLogger : IBufferedLogger
    {
    }

    /// <summary>
    /// The app-wide logger. File output follows the user's
    /// "write application logs to file" preference and re-applies whenever
    /// preferences are saved.
    /// </summary>
    public class ApplicationLogger : PipelineLogger, IApplicationLogger
    {
        private readonly IServiceProvider Services;
        private IUserPreferencesProvider Prefs;
        private ILogLevelControl Level;
        private bool LevelAsked;

        public ApplicationLogger(IServiceProvider services)
        {
            // The preferences provider is resolved lazily because it also
            // logs, which would otherwise make a circular DI dependency.
            Services = services;

            Use(LogSteps.TagSeverity());
            Use(LogSteps.Timestamp());
        }

        /// <summary>
        /// The Detailed log dial. Asked of the container once and then held, because this is
        /// read on every single write: the control's own constructor takes nothing but the
        /// container and logs nothing, so resolving it here cannot come back round into here.
        /// </summary>
        protected override LoggingLevelSwitch LevelSwitch
        {
            get
            {
                if (!LevelAsked)
                {
                    LevelAsked = true;
                    try { Level = Services?.GetService<ILogLevelControl>(); }
                    catch { Level = null; }
                }

                return Level?.Switch;
            }
        }

        protected override string ResolveLogFile()
        {
            if (Prefs == null)
            {
                Prefs = Services.GetRequiredService<IUserPreferencesProvider>();
                Prefs.PreferencesSaved += (_, _) => Rebuild();
            }

            return Prefs.LoadPreferences().WriteApplicationLogsToFile ? "ApplicationLogs" : null;
        }

        protected override string ResolveLogFolder()
        {
            // ResolveLogFile runs first in BuildSink, so Prefs is wired by now -
            // but stay safe if the call order ever changes.
            var custom = Prefs?.LoadPreferences().LogsFolderPath;
            return string.IsNullOrWhiteSpace(custom) ? base.ResolveLogFolder() : custom;
        }
    }
}
