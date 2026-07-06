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
    }

    public class StartupArgsProvider : IStartupArgsProvider
    {
        public StartupArgsProvider(string[] args)
        {
            ServerProfileName = args?.FirstOrDefault(a => !a.StartsWith("--"));
        }

        public string ServerProfileName { get; }
    }
}
