using System;
using System.IO;
using ValheimBakaLoader.Tools.Logging;

namespace ValheimBakaLoader.Tools
{
    public interface IMaxPlayersInstaller
    {
        /// <summary>True when the BakaLoaderMaxPlayers plugin is installed in the given plugins directory.</summary>
        bool IsInstalled(string pluginsDir);

        /// <summary>True when the legacy third-party Azumatt-MaxPlayerCount mod is still present.</summary>
        bool IsLegacyInstalled(string pluginsDir);

        /// <summary>
        /// Installs the bundled BakaLoaderMaxPlayers plugin into <c>BepInEx/plugins</c>.
        /// Unlike the always-on companions this one is only installed on demand (the user
        /// raised Max Players above vanilla's 10), so failures THROW for the caller to surface.
        /// </summary>
        void Install(string pluginsDir);

        /// <summary>
        /// Keeps an existing install current and migrates away from the legacy third-party
        /// Azumatt-MaxPlayerCount mod: adopts its configured count into our cfg, removes its
        /// folder and cfg, and installs the bundled plugin in its place. All failures are
        /// logged and non-fatal (the legacy mod keeps working until migration succeeds).
        /// Should only be called while the server is STOPPED, since BepInEx loads plugins at
        /// start and the legacy DLL is file-locked while the server runs.
        /// </summary>
        void EnsureCurrent(string pluginsDir);

        /// <summary>The configured max-player count from our plugin's cfg, or null when unset.</summary>
        int? ReadConfiguredCount(string configDir);

        /// <summary>The count from the LEGACY mod's cfg (pre-migration reads), or null when unset.</summary>
        int? ReadLegacyCount(string configDir);

        /// <summary>Writes the count into our plugin's cfg (rewrites the key line, or seeds a minimal cfg).</summary>
        void WriteConfiguredCount(string configDir, int count);
    }

    /// <summary>
    /// Manages the companion BepInEx plugin that raises Valheim's built-in 10-player cap.
    /// The plugin is bundled with BakaLoader under <c>Resources/MaxPlayers/</c> - same
    /// pattern as KillAllInstaller/CommanderInstaller. Replaces the third-party
    /// Azumatt-MaxPlayerCount dependency (0.9.26) so the feature no longer relies on a
    /// mod that might stop being maintained.
    /// </summary>
    public class MaxPlayersInstaller : IMaxPlayersInstaller
    {
        public const string PluginFolderName = "BakaLoaderMaxPlayers";
        public const string PluginDllName = "BakaLoaderMaxPlayers.dll";
        public const string ConfigFileName = "com.baka.maxplayers.cfg";
        private const string ConfigKey = "MaxPlayers";

        // The third-party mod this plugin replaces (installed by BakaLoader 0.9.26-0.9.33).
        public const string LegacyFolderName = "Azumatt-MaxPlayerCount";
        public const string LegacyConfigFileName = "Azumatt.MaxPlayerCount.cfg";
        private const string LegacyConfigKey = "MaxPlayerCount";
        private const int LegacyDefaultCount = 20; // Azumatt's surprise default when its cfg is missing

        private readonly IApplicationLogger Logger;

        public MaxPlayersInstaller(IApplicationLogger logger)
        {
            Logger = logger;
        }

        private static string BundledDir =>
            Path.Combine(AppContext.BaseDirectory, "Resources", "MaxPlayers");

        private static string BundledDll => Path.Combine(BundledDir, PluginDllName);

        public bool IsInstalled(string pluginsDir) =>
            !string.IsNullOrWhiteSpace(pluginsDir)
            && File.Exists(Path.Combine(pluginsDir, PluginFolderName, PluginDllName));

        public bool IsLegacyInstalled(string pluginsDir) =>
            !string.IsNullOrWhiteSpace(pluginsDir)
            && Directory.Exists(Path.Combine(pluginsDir, LegacyFolderName));

        public void Install(string pluginsDir)
        {
            if (string.IsNullOrWhiteSpace(pluginsDir) || !Directory.Exists(pluginsDir))
                throw new DirectoryNotFoundException(
                    "BepInEx plugins folder not found - set a valid server .exe path first.");

            if (!File.Exists(BundledDll))
                throw new FileNotFoundException(
                    "The bundled BakaLoaderMaxPlayers plugin is missing from this BakaLoader install.", BundledDll);

            var targetDir = Path.Combine(pluginsDir, PluginFolderName);
            var targetDll = Path.Combine(targetDir, PluginDllName);

            if (!Directory.Exists(targetDir)) Directory.CreateDirectory(targetDir);

            if (NeedsCopy(BundledDll, targetDll))
            {
                File.Copy(BundledDll, targetDll, overwrite: true);
                Logger.Information("Installed/updated max-players plugin in {dir}", targetDir);
            }
        }

        public void EnsureCurrent(string pluginsDir)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(pluginsDir) || !Directory.Exists(pluginsDir)) return;

                if (IsLegacyInstalled(pluginsDir))
                {
                    MigrateFromLegacy(pluginsDir);
                }
                else if (IsInstalled(pluginsDir))
                {
                    Install(pluginsDir); // refresh the DLL if the bundled copy is newer
                }
            }
            catch (Exception e)
            {
                Logger.Warning("Could not prepare the max-players plugin: {message}", e.Message);
            }
        }

        /// <summary>
        /// One-way migration off the legacy mod. Adopt its count first (so nothing is lost
        /// if a later step throws), then delete its folder - the step that fails with a
        /// file lock if the server is running, in which case we bail out with everything
        /// intact and retry on the next server start.
        /// </summary>
        private void MigrateFromLegacy(string pluginsDir)
        {
            var configDir = GetConfigDirectory(pluginsDir);
            var legacyDir = Path.Combine(pluginsDir, LegacyFolderName);

            if (configDir != null && ReadConfiguredCount(configDir) == null)
            {
                var adopted = ReadCfgValue(Path.Combine(configDir, LegacyConfigFileName), LegacyConfigKey)
                    ?? LegacyDefaultCount;
                WriteConfiguredCount(configDir, adopted);
            }

            Directory.Delete(legacyDir, recursive: true);

            var legacyCfg = configDir == null ? null : Path.Combine(configDir, LegacyConfigFileName);
            if (legacyCfg != null && File.Exists(legacyCfg)) File.Delete(legacyCfg);

            Install(pluginsDir);
            Logger.Information("Migrated Max Players from the third-party MaxPlayerCount mod to the bundled BakaLoader plugin.");
        }

        /// <summary>BepInEx/plugins -&gt; BepInEx/config (same derivation as CommanderInstaller).</summary>
        private static string GetConfigDirectory(string pluginsDir)
        {
            if (string.IsNullOrWhiteSpace(pluginsDir)) return null;
            var bepinexDir = Path.GetDirectoryName(
                pluginsDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            return string.IsNullOrEmpty(bepinexDir) ? null : Path.Combine(bepinexDir, "config");
        }

        public int? ReadConfiguredCount(string configDir) =>
            configDir == null ? null : ReadCfgValue(Path.Combine(configDir, ConfigFileName), ConfigKey);

        public int? ReadLegacyCount(string configDir) =>
            configDir == null ? null : ReadCfgValue(Path.Combine(configDir, LegacyConfigFileName), LegacyConfigKey);

        /// <summary>
        /// Writes the count into our plugin's cfg, rewriting only the key line when the
        /// file already exists (preserving everything else BepInEx wrote) and seeding a
        /// minimal cfg otherwise so the FIRST start uses the chosen value.
        /// </summary>
        public void WriteConfiguredCount(string configDir, int count)
        {
            if (configDir == null)
                throw new InvalidOperationException("Server exe path is not configured");

            Directory.CreateDirectory(configDir);
            var path = Path.Combine(configDir, ConfigFileName);

            if (File.Exists(path))
            {
                var lines = new System.Collections.Generic.List<string>(File.ReadAllLines(path));
                var idx = lines.FindIndex(l =>
                    l.TrimStart().StartsWith(ConfigKey, StringComparison.OrdinalIgnoreCase)
                    && l.Contains('='));
                if (idx >= 0) lines[idx] = $"{ConfigKey} = {count}";
                else lines.Add($"{ConfigKey} = {count}");
                File.WriteAllLines(path, lines);
            }
            else
            {
                File.WriteAllText(path,
                    "[General]\n\n" +
                    "## Maximum number of players allowed on the server. Vanilla cap is 10. Applied when the server starts.\n" +
                    "# Setting type: Int32\n" +
                    "# Default value: 10\n" +
                    $"{ConfigKey} = {count}\n");
            }
        }

        private static int? ReadCfgValue(string path, string key)
        {
            if (!File.Exists(path)) return null;

            foreach (var line in File.ReadAllLines(path))
            {
                var t = line.Trim();
                if (!t.StartsWith(key, StringComparison.OrdinalIgnoreCase)) continue;
                var eq = t.IndexOf('=');
                if (eq > 0 && int.TryParse(t[(eq + 1)..].Trim(), out var v)) return v;
            }
            return null;
        }

        private static bool NeedsCopy(string source, string target)
        {
            if (!File.Exists(target)) return true;

            try
            {
                var src = new FileInfo(source);
                var dst = new FileInfo(target);
                return src.Length != dst.Length || src.LastWriteTimeUtc > dst.LastWriteTimeUtc;
            }
            catch
            {
                return true;
            }
        }
    }
}
