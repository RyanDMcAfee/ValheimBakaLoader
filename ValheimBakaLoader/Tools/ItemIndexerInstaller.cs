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
