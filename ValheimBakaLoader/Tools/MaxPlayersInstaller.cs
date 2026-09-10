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
        /// Azumatt-MaxPlayerCount mod: adopts its configured count into our cfg, installs the
        /// bundled plugin, and only then removes the legacy folder and cfg. All failures are
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
        /// <summary>Label used when a failure is surfaced to the operator.</summary>
        public const string CompanionPluginName = "Max Players";

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
                else
                {
                    // Nothing is installed and there is nothing to migrate, which is the ordinary
                    // state of a server that never raised the cap. Nothing was checked, so this
                    // pass has no news: an earlier failure is left standing rather than cleared by
                    // a pass that did no work. Same shape as the item indexer's early returns.
                    return;
                }

                CompanionPluginStatus.ReportSuccess(CompanionPluginName);
            }
            catch (Exception e)
            {
                Logger.Warning("Could not prepare the max-players plugin: {message}", e.Message);
                CompanionPluginStatus.ReportFailure(CompanionPluginName, e.Message);
            }
        }

        /// <summary>
        /// One-way migration off the legacy mod, in the order that never leaves the server
        /// with no plugin at all. Adopt the count first (so nothing is lost if a later step
        /// throws), then put the replacement in place, and only then take the old mod away.
        /// The delete is the step that fails with a file lock if the server is running, in
        /// which case we bail out with everything intact and retry on the next server start.
        /// The two plugins sitting side by side in between is safe: the bundled one names the
        /// legacy mod as an incompatibility and bows out for as long as it is there.
        /// </summary>
        private void MigrateFromLegacy(string pluginsDir)
        {
            var configDir = GetConfigDirectory(pluginsDir);
            var legacyDir = Path.Combine(pluginsDir, LegacyFolderName);

            AdoptLegacyCount(configDir);

            // Before anything is taken away. A copy that cannot happen (the bundled DLL missing
            // from this install, a plugins folder that will not take a write) throws from here,
            // and the working mod is still exactly where it was when the throw reaches
            // EnsureCurrent. Doing this after the delete is how a server ended up back at the
            // vanilla cap of 10 with the operator's chosen count sitting in a cfg no installed
            // plugin reads.
            Install(pluginsDir);

            try
            {
                Directory.Delete(legacyDir, recursive: true);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                // The old mod's DLL is held open for as long as the server runs, so its folder
                // cannot go yet. Nothing has been lost: the count is already carried across and
                // the next server start finishes the switch. This is a normal wait, not a
                // failure, so it never reaches the companion-plugin failure list.
                Logger.Information(
                    "The old MaxPlayerCount mod is still in use, so the switch to the bundled plugin finishes at the next server start: {message}",
                    e.Message);
                return;
            }

            var legacyCfg = configDir == null ? null : Path.Combine(configDir, LegacyConfigFileName);
            if (legacyCfg != null && File.Exists(legacyCfg)) File.Delete(legacyCfg);

            Logger.Information("Migrated Max Players from the third-party MaxPlayerCount mod to the bundled BakaLoader plugin.");
        }

        /// <summary>
        /// Brings the legacy mod's configured count across into our own cfg. Two cases matter.
        /// On the first pass our key is still unset and the legacy value is all there is. On a
        /// deferred pass an earlier attempt already seeded our key, and the operator may since
        /// have saved a new count that could only reach the legacy cfg, because the running
        /// server blocked the switch and the bundled plugin was not installed yet. Whichever
        /// file was written last is the one the operator touched last, so that value wins; a
        /// tie keeps ours, so a newer explicit choice of our own is never overwritten.
        /// </summary>
        private void AdoptLegacyCount(string configDir)
        {
            if (configDir == null) return;

            var legacyCfg = Path.Combine(configDir, LegacyConfigFileName);
            var legacyCount = ReadCfgValue(legacyCfg, LegacyConfigKey);
            var ours = ReadConfiguredCount(configDir);

            if (ours == null)
            {
                WriteConfiguredCount(configDir, legacyCount ?? LegacyDefaultCount);
                return;
            }

            if (legacyCount == null || legacyCount.Value == ours.Value) return;
            if (!IsNewerThan(legacyCfg, Path.Combine(configDir, ConfigFileName))) return;

            Logger.Information(
                "Keeping the max-player count of {count} that was saved while the migration was waiting.",
                legacyCount.Value);
            WriteConfiguredCount(configDir, legacyCount.Value);
        }

        /// <summary>True when both files exist and the first one was written strictly later.</summary>
        private static bool IsNewerThan(string candidate, string reference)
        {
            try
            {
                if (!File.Exists(candidate) || !File.Exists(reference)) return false;
                return File.GetLastWriteTimeUtc(candidate) > File.GetLastWriteTimeUtc(reference);
            }
            catch
            {
                return false;
            }
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

        /// <summary>
        /// True when the installed plugin is not the bundled one. Size settles it in nearly
        /// every case; when the sizes match the bytes are compared. Neither size nor date on
        /// its own is dependable: a rebuilt plugin can land on the same length, and a file
        /// copy carries the source's timestamp forward, so a reinstall of an older BakaLoader
        /// can leave a stale DLL looking current.
        /// </summary>
        private static bool NeedsCopy(string source, string target)
        {
            if (!File.Exists(target)) return true;

            try
            {
                var src = new FileInfo(source);
                var dst = new FileInfo(target);
                if (src.Length != dst.Length) return true;
                return !SameContent(source, target);
            }
            catch
            {
                // Unreadable for any reason (locked, permissions) - assume it needs replacing.
                // The copy itself is what defers when the server is running and holds the DLL.
                return true;
            }
        }

        /// <summary>Byte-for-byte comparison, opened shared so a running server cannot break it.</summary>
        private static bool SameContent(string a, string b)
        {
            var bytesA = ReadAllShared(a);
            var bytesB = ReadAllShared(b);
            if (bytesA.Length != bytesB.Length) return false;

            for (var i = 0; i < bytesA.Length; i++)
                if (bytesA[i] != bytesB[i]) return false;

            return true;
        }

        private static byte[] ReadAllShared(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            return memory.ToArray();
        }
    }
}
