using System;
using System.IO;
using ValheimBakaLoader.Tools.Logging;

namespace ValheimBakaLoader.Tools
{
    public interface ISpawnHelperInstaller
    {
        /// <summary>
        /// Ensures the BakaLoaderSpawnHelper companion plugin is present and current inside the
        /// given <c>BepInEx/plugins</c> directory. This plugin provides the <c>baka_spawn</c>
        /// console command which dispatches Object.Instantiate() to the Unity main thread,
        /// avoiding the "Graphics device is null" crash that WEC's spawn_object causes when
        /// called from the RCON socket thread on headless dedicated servers.
        /// Should only be called while the server is STOPPED, since BepInEx loads plugins at start.
        /// </summary>
        void EnsureInstalled(string pluginsDir);
    }

    /// <summary>
    /// Manages the companion BepInEx plugin that provides headless-safe spawning via RCON.
    /// The plugin is bundled with BakaLoader under <c>Resources/SpawnHelper/</c> and copied
    /// into the server's plugins folder on demand - identical pattern to ItemIndexerInstaller.
    /// </summary>
    public class SpawnHelperInstaller : ISpawnHelperInstaller
    {
        private const string PluginFolderName = "BakaLoaderSpawnHelper";
        private const string PluginDllName = "BakaLoaderSpawnHelper.dll";

        private readonly IApplicationLogger Logger;

        public SpawnHelperInstaller(IApplicationLogger logger)
        {
            Logger = logger;
        }

        private static string BundledDir =>
            Path.Combine(AppContext.BaseDirectory, "Resources", "SpawnHelper");

        private static string BundledDll => Path.Combine(BundledDir, PluginDllName);

        public void EnsureInstalled(string pluginsDir)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(pluginsDir)) return;

                if (!File.Exists(BundledDll))
                {
                    Logger.Debug("Spawn helper plugin not bundled ({path}); spawn-at-player will use fallback.", BundledDll);
                    return;
                }

                if (!Directory.Exists(pluginsDir))
                {
                    Logger.Debug("Plugins directory {dir} does not exist; skipping spawn helper install.", pluginsDir);
                    return;
                }

                var targetDir = Path.Combine(pluginsDir, PluginFolderName);
                var targetDll = Path.Combine(targetDir, PluginDllName);

                if (!Directory.Exists(targetDir)) Directory.CreateDirectory(targetDir);

                if (NeedsCopy(BundledDll, targetDll))
                {
                    File.Copy(BundledDll, targetDll, overwrite: true);
                    Logger.Information("Installed/updated spawn helper plugin in {dir}", targetDir);
                }
            }
            catch (Exception e)
            {
                Logger.Warning("Could not install spawn helper plugin: {message}", e.Message);
            }
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
