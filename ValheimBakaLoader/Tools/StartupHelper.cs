using Microsoft.Win32;
using Serilog;
using System;
using System.Windows.Forms;

namespace ValheimBakaLoader.Tools
{
    /// <summary>
    /// Keeps the Windows "run at startup" registry entry in sync with the
    /// user's preference. Machine-wide registration is preferred (HKLM) but
    /// requires elevation, so a per-user entry (HKCU) is the usual outcome.
    /// </summary>
    public static class StartupHelper
    {
        private const string RunKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";

        /// <summary>Returns true when the registry was actually changed.</summary>
        public static bool ApplyStartupSetting(bool userPreference, ILogger logger)
        {
            var entryName = Application.ProductName;
            var exePath = Application.ExecutablePath;
            var registeredPath = ReadEntry(entryName, logger);

            if (!userPreference)
            {
                if (registeredPath == null) return false;

                if (WithRunKey(key => key.DeleteValue(entryName), "remove the startup entry", logger))
                {
                    logger.Information("ValheimBakaLoader will no longer run on Windows startup");
                    return true;
                }

                return false;
            }

            if (registeredPath != null)
            {
                if (registeredPath.Equals(exePath, StringComparison.OrdinalIgnoreCase))
                {
                    return false; // Already registered and pointing at this exe.
                }

                // The app has moved since it was registered; re-point the entry.
                logger.Information("ValheimBakaLoader executable path has changed, refreshing the startup entry...");
                if (!WithRunKey(key => key.DeleteValue(entryName), "remove the stale startup entry", logger))
                {
                    return false;
                }
            }

            if (WithRunKey(key => key.SetValue(entryName, exePath), "add the startup entry", logger))
            {
                logger.Information("ValheimBakaLoader will now run on Windows startup");
                return true;
            }

            return false;
        }

        private static string ReadEntry(string entryName, ILogger logger)
        {
            string result = null;
            WithRunKey(key => result = key.GetValue(entryName)?.ToString(), "read the startup entry", logger);
            return result;
        }

        /// <summary>
        /// Runs an action against the Run key, trying the machine-wide hive
        /// first and falling back to the current user's hive when access is
        /// denied (the app is rarely elevated).
        /// </summary>
        private static bool WithRunKey(Action<RegistryKey> action, string description, ILogger logger)
        {
            foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
            {
                try
                {
                    using var key = hive.OpenSubKey(RunKeyPath, writable: true);
                    action(key);
                    return true;
                }
                catch (Exception e)
                {
                    logger.Debug("Could not {description} under {hive}: {message}", description, hive.Name, e.Message);
                }
            }

            logger.Warning("Failed to {description} in the Windows registry", description);
            return false;
        }
    }
}
