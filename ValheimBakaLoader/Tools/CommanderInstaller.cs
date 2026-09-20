using BakaLoaderSpawn;
using System;
using System.Collections.Generic;
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
        /// port and password configured in BakaLoader. Preserves a user-customized BindAddress
        /// and the whole <c>[Spawning]</c> section, which is the host's and not BakaLoader's.
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

                // Preserve a user-customized bind address; the rest of [Server] is BakaLoader-owned.
                var bindAddress = "127.0.0.1";

                // And the spawn section in full. It is the host's setting, not BakaLoader's:
                // nothing in the app writes it and nothing in the app reads it, the plugin
                // binds it once while the server is starting, and this rewrite happens on
                // every start. Keeping only BindAddress meant a host who set
                // MarkSpawnedAsCheated by hand had it deleted before BepInEx ever parsed the
                // file, so the setting could not be turned on at all for the plugin serving
                // RCON. Every line inside it is carried across as it stands, comments and
                // all, because what a host wrote in their own config file is not this
                // method's to reword. The header line itself is written in this app's
                // spelling and the endings are this machine's, which is what ExtractSection
                // says on itself and neither of which can carry a setting.
                string spawning = null;

                if (File.Exists(cfgPath))
                {
                    var existing = File.ReadAllText(cfgPath);
                    var m = Regex.Match(existing, @"^\s*BindAddress\s*=\s*(\S+)\s*$", RegexOptions.Multiline);
                    if (m.Success) bindAddress = m.Groups[1].Value;

                    spawning = ExtractSection(existing, SpawnMark.ConfigSection);
                }

                var sb = new StringBuilder();
                sb.AppendLine("## Settings file for BakaLoader Commander (com.baka.commander).");
                sb.AppendLine("## Port/Password/Enabled are managed by BakaLoader from the server profile -");
                sb.AppendLine("## they are rewritten on every server start. BindAddress and the whole");
                sb.AppendLine("## spawning section are preserved as the host left them.");
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

                // A section that was never there stays away. The plugin binds its own default
                // on the next start and writes the entry out itself, which is how every host
                // who has never touched this setting gets the file they get today.
                if (spawning != null)
                {
                    sb.AppendLine();
                    foreach (var line in spawning.Split('\n')) sb.AppendLine(line);
                }

                File.WriteAllText(cfgPath, sb.ToString());
                Logger.Debug("Wrote Commander config: {path} (enabled={enabled}, port={port})", cfgPath, rconEnabled, rconPort);
            }
            catch (Exception e)
            {
                Logger.Warning("Could not write Commander config: {message}", e.Message);
            }
        }

        /// <summary>
        /// One section of a BepInEx .cfg with the host's own lines in it, header included,
        /// or null when the file has no such section.
        /// <para>
        /// A plain function with no I/O in it, because this is the whole of what makes the
        /// rewrite safe and the suite drives it directly. Every line INSIDE the section is
        /// kept as it was, character for character, comments and blank lines and all.
        /// </para>
        /// <para>
        /// Two things are deliberately not kept as they were, and neither can carry a
        /// setting. The header line is matched with the whitespace AROUND it ignored and
        /// with no regard for case, so "  [spawning]  " is this section: BepInEx writes the
        /// header on its own line without spaces and a host editing by hand may leave some
        /// either side of it. Spaces INSIDE the brackets are not ignored, so "[ Spawning ]"
        /// is not this section and its lines are not carried; BepInEx never writes it that
        /// way and normalises the header on its next write. The line written back is this
        /// app's spelling of it rather than the host's, whichever way it was matched. And
        /// the lines come back joined with "\n" whatever the file
        /// used, because the whole file is read that way; the caller splits them again and
        /// writes every line of the new file with this machine's endings, so the section
        /// ends up with the same endings as everything around it.
        /// </para>
        /// <para>
        /// The section ends at the next header or at the end of the file, and the blank
        /// lines that separate it from the next section belong to the layout rather than
        /// to the section, so they are dropped and written again by the caller.
        /// </para>
        /// </summary>
        internal static string ExtractSection(string cfg, string section)
        {
            if (string.IsNullOrEmpty(cfg) || string.IsNullOrWhiteSpace(section)) return null;

            var header = "[" + section.Trim() + "]";
            var kept = new List<string>();
            var found = false;
            var inside = false;

            // Read as lines whatever the file's endings are, so a .cfg written on one machine
            // and read on another is the same section either way.
            foreach (var line in cfg.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                var trimmed = line.Trim();
                var isHeader = trimmed.Length > 1 &&
                               trimmed[0] == '[' &&
                               trimmed[trimmed.Length - 1] == ']';

                if (isHeader)
                {
                    if (inside) break;

                    inside = string.Equals(trimmed, header, StringComparison.OrdinalIgnoreCase);
                    if (!inside) continue;

                    found = true;
                    kept.Add(header);
                    continue;
                }

                if (inside) kept.Add(line);
            }

            if (!found) return null;

            while (kept.Count > 1 && string.IsNullOrWhiteSpace(kept[kept.Count - 1]))
                kept.RemoveAt(kept.Count - 1);

            return string.Join("\n", kept);
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
