using System;
using System.IO;
using ValheimBakaLoader.Tools.Logging;

namespace ValheimBakaLoader.Tools
{
    public interface IItemIndexerInstaller
    {
        /// <summary>
        /// Ensures the BakaLoaderItemIndexer companion plugin is present and current inside the
        /// given <c>BepInEx/plugins</c> directory. No-ops gracefully if the bundled plugin DLL
        /// has not been built/shipped yet (the app then runs on the bundled vanilla catalog).
        /// Should only be called while the server is STOPPED, since BepInEx loads plugins at start.
        /// </summary>
        void EnsureInstalled(string pluginsDir);
    }

    /// <summary>
    /// Manages the small companion BepInEx plugin that dumps the live, mod-aware ObjectDB to
    /// <c>BepInEx/items.json</c>. The plugin is bundled with BakaLoader under
    /// <c>Resources/ItemIndexer/</c> and copied into the server's plugins folder on demand.
    /// </summary>
    public class ItemIndexerInstaller : IItemIndexerInstaller
    {
        private const string PluginFolderName = "BakaLoaderItemIndexer";
        private const string PluginDllName = "BakaLoaderItemIndexer.dll";

        private readonly IApplicationLogger Logger;

        public ItemIndexerInstaller(IApplicationLogger logger)
        {
            Logger = logger;
        }

        private static string BundledDir =>
            Path.Combine(AppContext.BaseDirectory, "Resources", "ItemIndexer");

        private static string BundledDll => Path.Combine(BundledDir, PluginDllName);

        public void EnsureInstalled(string pluginsDir)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(pluginsDir)) return;

                if (!File.Exists(BundledDll))
                {
                    // The indexer DLL is built separately (needs the game's assemblies). Until it
                    // is shipped, fall back to the bundled vanilla catalog silently-but-logged.
                    Logger.Debug("Item indexer plugin not bundled ({path}); using vanilla catalog.", BundledDll);
                    return;
                }

                if (!Directory.Exists(pluginsDir))
                {
                    Logger.Debug("Plugins directory {dir} does not exist; skipping indexer install.", pluginsDir);
                    return;
                }

                var targetDir = Path.Combine(pluginsDir, PluginFolderName);
                var targetDll = Path.Combine(targetDir, PluginDllName);

                if (!Directory.Exists(targetDir)) Directory.CreateDirectory(targetDir);

                if (NeedsCopy(BundledDll, targetDll))
                {
                    File.Copy(BundledDll, targetDll, overwrite: true);
                    Logger.Information("Installed/updated item indexer plugin in {dir}", targetDir);
                }
            }
            catch (Exception e)
            {
                // Never block a server start because of the indexer; the vanilla catalog still works.
                Logger.Warning("Could not install item indexer plugin: {message}", e.Message);
            }
        }

        private static bool NeedsCopy(string source, string target)
        {
            if (!File.Exists(target)) return true;

            try
            {
                var src = new FileInfo(source);
                var dst = new FileInfo(target);
                // Copy when the bundled DLL is a different size or newer than the installed one.
                return src.Length != dst.Length || src.LastWriteTimeUtc > dst.LastWriteTimeUtc;
            }
            catch
            {
                return true;
            }
        }
    }
}
