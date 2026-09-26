using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;
using Serilog.Core;
using Serilog.Events;
using System;
using ValheimBakaLoader.Game;

namespace ValheimBakaLoader.Tools.Logging
{
    /// <summary>
    /// How much detail the application log carries, and where that answer came from.
    /// <para>
    /// Two things can open the log up. The command line switch <c>--verbose</c> opens it for
    /// the whole session and cannot be closed again from the window, which is what a host is
    /// told to use when the window itself is what will not start. The Detailed log preference
    /// on the Upkeep card opens it the moment it is moved, with no restart, because a host
    /// chasing a problem should not have to reproduce it twice.
    /// </para>
    /// <para>
    /// Release has always written at Debug and still does, and a DEBUG build has always
    /// written at Verbose and still does: both of those are
    /// <see cref="LogLevelControl.DefaultLevel"/>, which is where the dial starts and where
    /// a switched-off Detailed log puts it back. Nothing in the product wrote at Verbose
    /// until the wire trace, so a release log with this switched off carries exactly what it
    /// carried before.
    /// </para>
    /// <para>
    /// The two providers it reads are resolved lazily, the same way every other service that
    /// the logger sits under does it: preferences log, the logger reads this, and asking for
    /// either in the constructor would close that ring while the container is still being built.
    /// </para>
    /// </summary>
    public interface ILogLevelControl
    {
        /// <summary>The dial itself. The application logger and its file sink both read it.</summary>
        LoggingLevelSwitch Switch { get; }

        /// <summary>True when the exe was started with <c>--verbose</c>.</summary>
        bool ForcedByCommandLine { get; }

        /// <summary>Where the dial is standing now.</summary>
        LogEventLevel Current { get; }

        /// <summary>
        /// Puts the dial where the command line and the saved preference say, and hands back
        /// the sentence that says which of them decided it. Called once at startup.
        /// </summary>
        string Apply();

        /// <summary>
        /// The same, for a preference value that has just been handed in rather than read off
        /// disk. Used by the save that carries the switch.
        /// </summary>
        string Apply(bool detailedLog);

        /// <summary>
        /// The one line written when the switch is moved by hand, after the dial has followed it.
        /// </summary>
        string Toggled(bool detailedLog);
    }

    /// <inheritdoc cref="ILogLevelControl"/>
    public sealed class LogLevelControl : ILogLevelControl
    {
        /// <summary>The word on the command line, matched without regard to case.</summary>
        public const string VerboseArgument = "--verbose";

        private readonly IServiceProvider Services;
        private IStartupArgsProvider Args;
        private IUserPreferencesProvider Prefs;

        public LogLevelControl(IServiceProvider services)
        {
            Services = services;
        }

        /// <summary>
        /// Where the dial stands with nothing turned on.
        /// <para>
        /// A release has always written at Debug and still does. A DEBUG build has always
        /// written at Verbose and still does too, which is the half that needs saying: the
        /// sink used to be given that level by an <c>#if DEBUG</c> inside
        /// <see cref="LoggerCore"/>, and once the dial stood in front of it the branch that
        /// read the dial shadowed the branch that read the <c>#if</c>. So the floor lives
        /// here now, and both the dial's starting point and the level a switched-off
        /// Detailed log puts it back to are this.
        /// </para>
        /// </summary>
        public static readonly LogEventLevel DefaultLevel =
#if DEBUG
            LogEventLevel.Verbose;
#else
            LogEventLevel.Debug;
#endif

        /// <summary>
        /// What the first line says when neither the command line nor the preference has
        /// opened the log up. On a release that is the sentence it always was; on a DEBUG
        /// build it names the build, because "Log level Verbose" with no reason beside it
        /// would leave a reader looking for a switch nobody moved.
        /// </summary>
        public static readonly string DefaultSentence =
#if DEBUG
            "Log level Verbose (debug build)";
#else
            "Log level Debug";
#endif

        /// <summary>Where a build with nothing switched on writes, which a release has always had at Debug.</summary>
        public LoggingLevelSwitch Switch { get; } = new(DefaultLevel);

        public bool ForcedByCommandLine
        {
            get
            {
                try
                {
                    Args ??= Services?.GetService<IStartupArgsProvider>();
                    return Args?.VerboseRequested ?? false;
                }
                catch
                {
                    // A container that cannot answer is not a reason to refuse to log.
                    return false;
                }
            }
        }

        public LogEventLevel Current => Switch.MinimumLevel;

        public string Apply() => Apply(SavedPreference());

        public string Apply(bool detailedLog)
        {
            var forced = ForcedByCommandLine;
            Switch.MinimumLevel = forced || detailedLog ? LogEventLevel.Verbose : DefaultLevel;

            if (forced) return "Log level Verbose (--verbose)";
            return detailedLog ? "Log level Verbose (Detailed log is on)" : DefaultSentence;
        }

        public string Toggled(bool detailedLog)
        {
            var forced = ForcedByCommandLine;
            Apply(detailedLog);

            // The command line wins for the whole session, so a switch moved under it changes
            // the preference and nothing else. Saying otherwise would be the one lie a line
            // about the log level must not tell.
            if (forced) return "Log level Verbose (--verbose)";

            return detailedLog
                ? "Log level Verbose (Detailed log turned on)"
                : "Log level " + DefaultLevel + " (Detailed log turned off)";
        }

        /// <summary>
        /// Puts the Detailed log value a save carried onto the preferences, and answers
        /// whether that save actually MOVED the switch.
        /// <para>
        /// Every switch on the Upkeep card posts the whole card together, so this key
        /// arrives on a save that came from a different switch entirely. A host who flips
        /// "Start with Windows" must not read "Log level Debug (Detailed log turned off)"
        /// afterwards: that is a sentence about something that did not happen, sitting in
        /// the one file this switch exists to make worth reading.
        /// </para>
        /// </summary>
        public static bool ApplySavedValue(UserPreferences prefs, JToken carried)
        {
            if (prefs == null || carried == null) return false;

            var wanted = carried.Value<bool>();
            var moved = wanted != prefs.DetailedLog;
            prefs.DetailedLog = wanted;
            return moved;
        }

        /// <summary>
        /// What the saved preference says, or false when preferences cannot be read. Off is
        /// the safe answer: a log nobody asked for must never be the one that grows.
        /// </summary>
        private bool SavedPreference()
        {
            try
            {
                Prefs ??= Services?.GetService<IUserPreferencesProvider>();
                return Prefs?.LoadPreferences()?.DetailedLog ?? false;
            }
            catch
            {
                return false;
            }
        }
    }
}
