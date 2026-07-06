using System;
using System.IO;
using ValheimBakaLoader.Tools.Logging;

namespace ValheimBakaLoader.Tools
{
    public interface IKillAllInstaller
    {
        /// <summary>
        /// Ensures the BakaLoaderKillAll companion plugin is present and current inside the
        /// given <c>BepInEx/plugins</c> directory. This plugin provides the <c>baka_killall</c>
        /// console command which kills all non-player creatures in loaded zones, dispatched on
        /// the Unity main thread so it is safe to trigger from the RCON socket thread on
        /// headless dedicated servers.
        /// Should only be called while the server is STOPPED, since BepInEx loads plugins at start.
        /// </summary>
        void EnsureInstalled(string pluginsDir);
    }

    /// <summary>
    /// Manages the companion BepInEx plugin that provides the kill-all-creatures command via RCON.
    /// The plugin is bundled with BakaLoader under <c>Resources/KillAll/</c> and copied into the
    /// server's plugins folder on demand - identical pattern to SpawnHelperInstaller.
    /// </summary>
    public class KillAllInstaller : IKillAllInstaller
    {
        private const string PluginFolderName = "BakaLoaderKillAll";
        private const string PluginDllName = "BakaKillAll.dll";

        private readonly IApplicationLogger Logger;

        public KillAllInstaller(IApplicationLogger logger)
        {
            Logger = logger;
        }

        private static string BundledDir =>
            Path.Combine(AppContext.BaseDirectory, "Resources", "KillAll");

        private static string BundledDll => Path.Combine(BundledDir, PluginDllName);

        public void EnsureInstalled(string pluginsDir)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(pluginsDir)) return;

                if (!File.Exists(BundledDll))
                {
                    Logger.Debug("Kill-all plugin not bundled ({path}); baka_killall will be unavailable unless installed manually.", BundledDll);
                    return;
                }

                if (!Directory.Exists(pluginsDir))
                {
                    Logger.Debug("Plugins directory {dir} does not exist; skipping kill-all plugin install.", pluginsDir);
                    return;
                }

                var targetDir = Path.Combine(pluginsDir, PluginFolderName);
                var targetDll = Path.Combine(targetDir, PluginDllName);

                if (!Directory.Exists(targetDir)) Directory.CreateDirectory(targetDir);

                if (NeedsCopy(BundledDll, targetDll))
                {
                    File.Copy(BundledDll, targetDll, overwrite: true);
                    Logger.Information("Installed/updated kill-all plugin in {dir}", targetDir);
                }
            }
            catch (Exception e)
            {
                Logger.Warning("Could not install kill-all plugin: {message}", e.Message);
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
