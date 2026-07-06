using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using ValheimBakaLoader.Tools.Logging;

namespace ValheimBakaLoader.Tools
{
    public interface ICommanderInstaller
    {
        /// <summary>
        /// Ensures the BakaLoaderCommander companion plugin is present and current inside the
        /// given <c>BepInEx/plugins</c> directory. Commander hosts BakaLoader's own Source-RCON
        /// listener and implements the full command suite natively (broadcast, playerlist, dmg,
        /// tp, kick, baka_spawn, baka_killall), replacing the AviiNL-RCON +
        /// Server_devcommands + Rcon_Commands third-party mod trio.
        /// Should only be called while the server is STOPPED, since BepInEx loads plugins at start.
        /// </summary>
        void EnsureInstalled(string pluginsDir);

        /// <summary>
        /// Writes/updates <c>BepInEx/config/com.baka.commander.cfg</c> so the plugin binds the
        /// port and password configured in BakaLoader. Preserves a user-customized BindAddress.
        /// Should only be called while the server is STOPPED.
        /// </summary>
        void EnsureConfig(string pluginsDir, bool rconEnabled, int rconPort, string rconPassword);
    }

    /// <summary>
    /// Manages the companion BepInEx plugin that provides BakaLoader's native RCON server.
    /// The plugin is bundled with BakaLoader under <c>Resources/Commander/</c> and copied
    /// into the server's plugins folder on demand - identical pattern to SpawnHelperInstaller.
    /// </summary>
    public class CommanderInstaller : ICommanderInstaller
    {
        public const string PluginFolderName = "BakaLoaderCommander";
        public const string PluginDllName = "BakaLoaderCommander.dll";
        private const string ConfigFileName = "com.baka.commander.cfg";

        private readonly IApplicationLogger Logger;

        public CommanderInstaller(IApplicationLogger logger)
        {
            Logger = logger;
        }

        private static string BundledDir =>
            Path.Combine(AppContext.BaseDirectory, "Resources", "Commander");

        private static string BundledDll => Path.Combine(BundledDir, PluginDllName);

        /// <summary>True when the Commander plugin is installed in the given plugins directory.</summary>
        public static bool IsInstalled(string pluginsDir)
        {
            if (string.IsNullOrWhiteSpace(pluginsDir)) return false;
            return File.Exists(Path.Combine(pluginsDir, PluginFolderName, PluginDllName));
        }

        public void EnsureInstalled(string pluginsDir)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(pluginsDir)) return;

                if (!File.Exists(BundledDll))
                {
                    Logger.Debug("Commander plugin not bundled ({path}); RCON features rely on third-party mods.", BundledDll);
                    return;
                }

                if (!Directory.Exists(pluginsDir))
                {
                    Logger.Debug("Plugins directory {dir} does not exist; skipping Commander install.", pluginsDir);
                    return;
                }

                var targetDir = Path.Combine(pluginsDir, PluginFolderName);
                var targetDll = Path.Combine(targetDir, PluginDllName);

                if (!Directory.Exists(targetDir)) Directory.CreateDirectory(targetDir);

                if (NeedsCopy(BundledDll, targetDll))
                {
                    File.Copy(BundledDll, targetDll, overwrite: true);
                    Logger.Information("Installed/updated Commander plugin in {dir}", targetDir);
                }
            }
            catch (Exception e)
            {
                Logger.Warning("Could not install Commander plugin: {message}", e.Message);
            }
        }

        public void EnsureConfig(string pluginsDir, bool rconEnabled, int rconPort, string rconPassword)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(pluginsDir)) return;
                if (!IsInstalled(pluginsDir)) return;

                // BepInEx/plugins -> BepInEx/config
                var bepinexDir = Path.GetDirectoryName(pluginsDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (string.IsNullOrEmpty(bepinexDir)) return;

                var configDir = Path.Combine(bepinexDir, "config");
                if (!Directory.Exists(configDir)) Directory.CreateDirectory(configDir);

                var cfgPath = Path.Combine(configDir, ConfigFileName);

                // Preserve a user-customized bind address; everything else is BakaLoader-owned.
                var bindAddress = "127.0.0.1";
                if (File.Exists(cfgPath))
                {
                    var existing = File.ReadAllText(cfgPath);
                    var m = Regex.Match(existing, @"^\s*BindAddress\s*=\s*(\S+)\s*$", RegexOptions.Multiline);
                    if (m.Success) bindAddress = m.Groups[1].Value;
                }

                var sb = new StringBuilder();
                sb.AppendLine("## Settings file for BakaLoader Commander (com.baka.commander).");
                sb.AppendLine("## Port/Password/Enabled are managed by BakaLoader from the server profile -");
                sb.AppendLine("## they are rewritten on every server start. BindAddress is preserved.");
                sb.AppendLine();
                sb.AppendLine("[Server]");
                sb.AppendLine();
                sb.AppendLine("## Enable the built-in RCON server.");
                sb.AppendLine("# Setting type: Boolean");
                sb.AppendLine("# Default value: true");
                sb.AppendLine($"Enabled = {(rconEnabled ? "true" : "false")}");
                sb.AppendLine();
                sb.AppendLine("## TCP port the RCON server listens on. Must not be the game port.");
                sb.AppendLine("# Setting type: Int32");
                sb.AppendLine("# Default value: 25575");
                sb.AppendLine($"Port = {rconPort}");
                sb.AppendLine();
                sb.AppendLine("## RCON password. Only gates the AUTH handshake; keep the bind address on loopback.");
                sb.AppendLine("# Setting type: String");
                sb.AppendLine("# Default value: ");
                sb.AppendLine($"Password = {rconPassword ?? string.Empty}");
                sb.AppendLine();
                sb.AppendLine("## Address to listen on. 127.0.0.1 (default) = local only; 0.0.0.0 = all interfaces (NOT recommended).");
                sb.AppendLine("# Setting type: String");
                sb.AppendLine("# Default value: 127.0.0.1");
                sb.AppendLine($"BindAddress = {bindAddress}");

                File.WriteAllText(cfgPath, sb.ToString());
                Logger.Debug("Wrote Commander config: {path} (enabled={enabled}, port={port})", cfgPath, rconEnabled, rconPort);
            }
            catch (Exception e)
            {
                Logger.Warning("Could not write Commander config: {message}", e.Message);
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
