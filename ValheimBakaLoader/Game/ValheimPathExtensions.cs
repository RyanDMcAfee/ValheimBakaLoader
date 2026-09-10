using System.IO;
using ValheimBakaLoader.Tools;

namespace ValheimBakaLoader.Game
{
    /// <summary>
    /// Filesystem lookups specific to a Valheim install: the server exe and the
    /// save-data folder. Everything about the WORLDS inside that folder lives in
    /// <see cref="WorldStore"/>, which knows both save formats; this file used to
    /// carry a .fwl-only world lister that no longer had any callers.
    /// </summary>
    public static class ValheimPathExtensions
    {
        public static FileInfo GetValidatedServerExe(this IValheimServerOptions options)
            => PathExtensions.GetFileInfo(options.ServerExePath, ".exe");

        public static DirectoryInfo GetValidatedSaveDataFolder(this IValheimServerOptions options)
            => PathExtensions.GetDirectoryInfo(options.SaveDataFolderPath, true);
    }
}
