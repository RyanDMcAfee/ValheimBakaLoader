using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using ValheimBakaLoader.Tools;

namespace ValheimBakaLoader.Game
{
    /// <summary>
    /// Filesystem lookups specific to a Valheim install: the server exe, the
    /// save-data folder, and the world files inside it.
    /// </summary>
    public static class ValheimPathExtensions
    {
        // Valheim's own worlds/worlds_local migration (June 2022) left behind
        // files named like "MyWorld_backup_20220620-101500.fwl"; those are
        // backups, not selectable worlds.
        private static readonly Regex MigrationBackupName = new(@"^.*?_backup_\d+?-\d+?");

        // Both spellings of the save layout, oldest first.
        private static readonly string[] WorldSubfolders = { "worlds", "worlds_local" };

        public static FileInfo GetValidatedServerExe(this IValheimServerOptions options)
            => PathExtensions.GetFileInfo(options.ServerExePath, ".exe");

        public static DirectoryInfo GetValidatedSaveDataFolder(this IValheimServerOptions options)
            => PathExtensions.GetDirectoryInfo(options.SaveDataFolderPath, true);

        /// <summary>
        /// Names of every world (.fwl) under the save-data folder. Returns an
        /// empty list when the folder can't be read.
        /// </summary>
        public static List<string> GetWorldNames(this DirectoryInfo saveDataFolder)
        {
            try
            {
                return WorldSubfolders
                    .Select(sub => new DirectoryInfo(Path.Join(saveDataFolder.FullName, sub)))
                    .Where(dir => dir.Exists)
                    .SelectMany(dir => dir.GetFiles("*.fwl"))
                    .Where(f => !MigrationBackupName.IsMatch(f.Name))
                    .Select(f => Path.GetFileNameWithoutExtension(f.FullName))
                    .ToList();
            }
            catch
            {
                return new List<string>();
            }
        }
    }
}
