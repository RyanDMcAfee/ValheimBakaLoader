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
        // Valheim's default save folder in its stored form; see Tools.AppDataRoot.
        internal static string DefaultValheimSaveFolder => Tools.AppDataRoot.ValheimSaveRoot;
        internal const string ValheimSteamAppId = "892970";

        // Where BakaLoader keeps its own data on disk.
        // Installed language packs, one folder per language and app version. They live here
        // rather than in the install folder because the install folder may be read-only and a
        // manual re-extract carries nothing across.
        // All of them sit under Tools.AppDataRoot.BakaLoaderRoot: the shipped %USERPROFILE%
        // folder, unless BAKALOADER_DATA_DIR moves it for an isolated test run.
        internal static string LanguagesFolderPath => Tools.AppDataRoot.BakaLoaderRoot + @"\languages";
        internal static string LogsFolderPath => Tools.AppDataRoot.BakaLoaderRoot + @"\logs";
        internal static string PlayerListFilePath => Tools.AppDataRoot.BakaLoaderRoot + @"\players-cache.json";
        internal static string UserPrefsFilePathV2 => Tools.AppDataRoot.BakaLoaderRoot + @"\userprefs.json";

        // Remote endpoints and update cadence.
        // How long an automatic check waits before it asks GitHub again. The app now keeps
        // looking while it runs rather than only at launch, so this is the floor that keeps a
        // machine left on for a week to four requests a day instead of one per timer tick.
        internal const string UpdateCheckInterval = "06:00:00";
        internal const string UrlDotnetDownload = "https://dotnet.microsoft.com/download/dotnet/6.0";
        internal const string UrlExternalIpLookup = "https://api.ipify.org?format=json";
        internal const string UrlGithubApi = "https://api.github.com/repos/RyanDMcAfee/ValheimBakaLoader";
        internal const string UrlRemoteApi = "";
    }
}
