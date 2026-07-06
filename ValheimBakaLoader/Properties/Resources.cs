using System.Drawing;
using System.Resources;

namespace ValheimBakaLoader.Properties
{
    /// <summary>
    /// App-wide fixed values: branding, dedicated-server defaults, file
    /// locations, and external URLs. The window icon is the only entry left
    /// in Resources.resx; everything else lives here as plain constants.
    /// </summary>
    internal static class Resources
    {
        private static readonly ResourceManager Embedded =
            new("ValheimBakaLoader.Properties.Resources", typeof(Resources).Assembly);

        internal static Icon ApplicationIcon => (Icon)Embedded.GetObject("ApplicationIcon");

        internal const string ApplicationTitle = "Valheim BakaLoader";

        // Dedicated-server defaults, per the Valheim Dedicated Server Manual
        // and start_headless_server.bat.
        internal const string DefaultBackupCount = "4";
        internal const string DefaultBackupIntervalLong = "43200";
        internal const string DefaultBackupIntervalShort = "7200";
        internal const string DefaultSaveInterval = "1800";
        internal const string DefaultServerPath = @"%ProgramFiles(x86)%\Steam\steamapps\common\Valheim dedicated server\valheim_server.exe";
        internal const string DefaultServerPort = "2456";
        internal const string DefaultServerProfileName = "Default";
        internal const string DefaultValheimSaveFolder = @"%USERPROFILE%\AppData\LocalLow\IronGate\Valheim";
        internal const string ValheimSteamAppId = "892970";

        // Where BakaLoader keeps its own data on disk.
        internal const string LogsFolderPath = @"%USERPROFILE%\AppData\LocalLow\BakaLoader\ValheimBakaLoader\logs";
        internal const string PlayerListFilePath = @"%USERPROFILE%\AppData\LocalLow\BakaLoader\ValheimBakaLoader\players-cache.json";
        internal const string UserPrefsFilePathV2 = @"%USERPROFILE%\AppData\LocalLow\BakaLoader\ValheimBakaLoader\userprefs.json";

        // Remote endpoints and update cadence.
        internal const string UpdateCheckInterval = "1.00:00:00";
        internal const string UrlDotnetDownload = "https://dotnet.microsoft.com/download/dotnet/6.0";
        internal const string UrlExternalIpLookup = "https://api.ipify.org?format=json";
        internal const string UrlGithubApi = "https://api.github.com/repos/RyanDMcAfee/ValheimBakaLoader";
        internal const string UrlRemoteApi = "";
    }
}
