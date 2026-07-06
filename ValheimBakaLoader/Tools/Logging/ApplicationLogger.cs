using Microsoft.Extensions.DependencyInjection;
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

        public ApplicationLogger(IServiceProvider services)
        {
            // The preferences provider is resolved lazily because it also
            // logs, which would otherwise make a circular DI dependency.
            Services = services;

            Use(LogSteps.TagSeverity());
            Use(LogSteps.Timestamp());
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
    }
}
