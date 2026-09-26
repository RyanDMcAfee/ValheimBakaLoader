using System;
using System.Linq;

namespace ValheimBakaLoader.Game
{
    /// <summary>
    /// Command-line arguments the app was launched with. The first bare
    /// (non "--flag") argument names a server profile to load immediately.
    /// </summary>
    public interface IStartupArgsProvider
    {
        string ServerProfileName { get; }

        /// <summary>
        /// True when the exe was started with <c>--verbose</c>. It opens the application log
        /// up for the whole session and the window cannot close it again, which is what a
        /// host is told to use when the thing that will not work is the window itself.
        /// Matched without regard to case, because a host types what they are given.
        /// </summary>
        bool VerboseRequested { get; }
    }

    public class StartupArgsProvider : IStartupArgsProvider
    {
        public StartupArgsProvider(string[] args)
        {
            ServerProfileName = args?.FirstOrDefault(a => !a.StartsWith("--"));
            VerboseRequested = args?.Any(a =>
                string.Equals(a?.Trim(), "--verbose", StringComparison.OrdinalIgnoreCase)) ?? false;
        }

        public string ServerProfileName { get; }

        public bool VerboseRequested { get; }
    }
}
