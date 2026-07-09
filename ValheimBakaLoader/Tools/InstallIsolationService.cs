using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using ValheimBakaLoader.Tools.Logging;

namespace ValheimBakaLoader.Tools
{
    /// <summary>
    /// Outcome of provisioning an isolated server install.
    /// </summary>
    public class IsolatedInstallResult
    {
        /// <summary>Root directory of the new isolated install.</summary>
        public string InstallDirectory { get; init; }

        /// <summary>Absolute path to the server .exe inside the isolated install (set this as the profile's ServerExePath).</summary>
        public string ServerExePath { get; init; }

        /// <summary>True when loose game files were hard-linked (same volume) rather than copied.</summary>
        public bool UsedHardLinks { get; init; }
    }

    public interface IInstallIsolationService
    {
        /// <summary>
        /// Provisions a lightweight isolated server install for a profile. The large, read-only
        /// game folders (valheim_server_Data, MonoBleedingEdge, BepInEx/core, BepInEx/patchers, …)
        /// are shared via directory JUNCTIONS, loose root files are hard-linked (same volume) or
        /// copied, and BepInEx/plugins + config + cache are REAL per-server copies so each server's
        /// mod set is fully independent. Returns the path to the isolated server .exe.
        /// </summary>
        /// <param name="baseExePath">Path to the base (Steam) server .exe to derive from.</param>
        /// <param name="profileName">Profile name; sanitized into the install folder name.</param>
        /// <param name="seedPluginsFromBase">When true, copies the base install's mods into the new
        /// install so it starts as a clone; when false, the new install starts with no mods.</param>
        IsolatedInstallResult ProvisionInstall(string baseExePath, string profileName, bool seedPluginsFromBase);

        /// <summary>
        /// True when <paramref name="installDir"/> lives under a BakaLoader-managed instances root
        /// (and is therefore safe to reclaim). Base/Steam installs and save folders return false.
        /// </summary>
        bool IsManagedInstall(string installDir);

        /// <summary>
        /// Junction-safe recursive delete of a BakaLoader-provisioned install. Reparse points
        /// (junctions to shared game data) are UNLINKED, never followed, so this can never delete
        /// the shared 1.5&#160;GB game files or the base BepInEx. Throws if the directory is not a
        /// managed install.
        /// </summary>
        void DeleteInstall(string installDir);
    }

    /// <summary>
    /// Builds and tears down per-server "isolated installs": a real directory that looks like a
    /// full Valheim dedicated-server install but shares the bulky read-only game files with the
    /// base install through Windows directory junctions, keeping only each server's mods
    /// (BepInEx/plugins + config + cache) as independent copies. This gives every server a
    /// distinct mod set without duplicating the multi-gigabyte game install.
    /// </summary>
    public class InstallIsolationService : IInstallIsolationService
    {
        /// <summary>Folder that holds all BakaLoader-provisioned isolated installs.</summary>
        public const string InstancesRootName = ".bakaloader-instances";

        // BepInEx subfolders that are read-only at runtime and safe to share via junction.
        private static readonly string[] JunctionableBepInExDirs = { "core", "patchers" };

        private readonly IApplicationLogger Logger;

        public InstallIsolationService(IApplicationLogger logger)
        {
            Logger = logger;
        }

        public IsolatedInstallResult ProvisionInstall(string baseExePath, string profileName, bool seedPluginsFromBase)
        {
            if (string.IsNullOrWhiteSpace(baseExePath) || !File.Exists(baseExePath))
                throw new InvalidOperationException($"Base server .exe not found: {baseExePath ?? "<null>"}");

            var baseDir = Path.GetDirectoryName(baseExePath)
                ?? throw new InvalidOperationException("Could not resolve the base install directory.");
            var exeName = Path.GetFileName(baseExePath);

            var instancesRoot = GetInstancesRoot(baseDir);
            var installDir = MakeUniqueInstallDirectory(instancesRoot, profileName);
            Directory.CreateDirectory(installDir);

            var sameVolume = SameVolume(baseDir, installDir);
            Logger.Information("Provisioning isolated install for '{0}' at {1} (sameVolume={2}, seedPlugins={3}).",
                profileName, installDir, sameVolume, seedPluginsFromBase);

            try
            {
                foreach (var dir in Directory.EnumerateDirectories(baseDir))
                {
                    var name = new DirectoryInfo(dir).Name;
                    if (string.Equals(name, "BepInEx", StringComparison.OrdinalIgnoreCase))
                    {
                        ProvisionBepInEx(dir, Path.Combine(installDir, name), seedPluginsFromBase, sameVolume);
                    }
                    else
                    {
                        // Bulky read-only game runtime (valheim_server_Data, MonoBleedingEdge, D3D12, …): share via junction.
                        CreateJunctionOrThrow(Path.Combine(installDir, name), dir);
                    }
                }

                foreach (var file in Directory.EnumerateFiles(baseDir))
                {
                    LinkOrCopyFile(file, Path.Combine(installDir, Path.GetFileName(file)), sameVolume);
                }

                var newExe = Path.Combine(installDir, exeName);
                if (!File.Exists(newExe))
                    throw new InvalidOperationException($"Provisioning finished but the server .exe is missing: {newExe}");

                Logger.Information("Isolated install ready: {0}", newExe);
                return new IsolatedInstallResult
                {
                    InstallDirectory = installDir,
                    ServerExePath = newExe,
                    UsedHardLinks = sameVolume,
                };
            }
            catch
            {
                // Roll back a partial install so a failed provision never leaves half-junctioned junk.
                TryRollback(installDir);
                throw;
            }
        }

        private void ProvisionBepInEx(string srcBep, string dstBep, bool seedPlugins, bool sameVolume)
        {
            Directory.CreateDirectory(dstBep);

            foreach (var dir in Directory.EnumerateDirectories(srcBep))
            {
                var name = new DirectoryInfo(dir).Name;

                if (JunctionableBepInExDirs.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    // core + patchers are read-only BepInEx runtime: share via junction.
                    CreateJunctionOrThrow(Path.Combine(dstBep, name), dir);
                }
                else if (string.Equals(name, "plugins", StringComparison.OrdinalIgnoreCase))
                {
                    var dst = Path.Combine(dstBep, "plugins");
                    if (seedPlugins) CopyDirectory(dir, dst);
                    else Directory.CreateDirectory(dst);
                }
                else if (string.Equals(name, "cache", StringComparison.OrdinalIgnoreCase))
                {
                    // Assembly cache is keyed to the (per-server) plugin set: start fresh, never share.
                    Directory.CreateDirectory(Path.Combine(dstBep, "cache"));
                }
                else
                {
                    // config and any other subdir: real per-server copy.
                    CopyDirectory(dir, Path.Combine(dstBep, name));
                }
            }

            foreach (var file in Directory.EnumerateFiles(srcBep))
            {
                // Loose BepInEx files (BepInEx.cfg, doorstop logs): copy so per-server edits don't bleed.
                File.Copy(file, Path.Combine(dstBep, Path.GetFileName(file)), overwrite: true);
            }
        }

        public bool IsManagedInstall(string installDir)
        {
            if (string.IsNullOrWhiteSpace(installDir)) return false;

            try
            {
                var full = Path.GetFullPath(installDir).TrimEnd(Path.DirectorySeparatorChar);
                var parent = Directory.GetParent(full)?.Name;
                return string.Equals(parent, InstancesRootName, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        public void DeleteInstall(string installDir)
        {
            if (!IsManagedInstall(installDir))
                throw new InvalidOperationException(
                    $"Refusing to delete '{installDir}': not a BakaLoader-managed isolated install.");

            if (!Directory.Exists(installDir))
            {
                Logger.Warning("Isolated install already gone, nothing to delete: {0}", installDir);
                return;
            }

            Logger.Information("Deleting isolated install (junction-safe): {0}", installDir);
            DeleteTreeJunctionSafe(installDir);
        }

        /// <summary>
        /// Recursively deletes a directory tree WITHOUT ever following a reparse point. Any
        /// junction/symlink is unlinked (its link removed) rather than descended into, so shared
        /// game data behind a junction is never touched. This is the critical safety guarantee.
        /// </summary>
        private static void DeleteTreeJunctionSafe(string dir)
        {
            var di = new DirectoryInfo(dir);
            if (!di.Exists) return;

            // If the directory itself is a junction/symlink, just remove the link.
            if (di.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                Directory.Delete(dir); // non-recursive: unlinks only, never follows the target
                return;
            }

            foreach (var sub in di.GetDirectories())
            {
                if (sub.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    Directory.Delete(sub.FullName); // unlink junction; do NOT recurse into shared data
                }
                else
                {
                    DeleteTreeJunctionSafe(sub.FullName);
                }
            }

            foreach (var file in di.GetFiles())
            {
                try { file.Attributes = FileAttributes.Normal; } catch { /* best effort */ }
                file.Delete();
            }

            Directory.Delete(dir); // now empty
        }

        private void TryRollback(string installDir)
        {
            try
            {
                if (Directory.Exists(installDir)) DeleteTreeJunctionSafe(installDir);
            }
            catch (Exception e)
            {
                Logger.Warning("Rollback of partial install '{0}' failed: {1}", installDir, e.Message);
            }
        }

        /// <summary>
        /// Resolves the instances root next to the base install (same volume, so hard-links work),
        /// falling back to LocalApplicationData if that location is not writable.
        /// </summary>
        private string GetInstancesRoot(string baseDir)
        {
            var parent = Directory.GetParent(baseDir)?.FullName;
            if (!string.IsNullOrWhiteSpace(parent))
            {
                var candidate = Path.Combine(parent, InstancesRootName);
                if (TryEnsureWritableDirectory(candidate)) return candidate;
            }

            var fallback = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BakaLoader", InstancesRootName);
            Directory.CreateDirectory(fallback);
            Logger.Information("Using fallback instances root (base parent not writable): {0}", fallback);
            return fallback;
        }

        private static bool TryEnsureWritableDirectory(string dir)
        {
            try
            {
                Directory.CreateDirectory(dir);
                var probe = Path.Combine(dir, ".writetest-" + Guid.NewGuid().ToString("N"));
                File.WriteAllText(probe, string.Empty);
                File.Delete(probe);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string MakeUniqueInstallDirectory(string instancesRoot, string profileName)
        {
            var safe = MakeSafeName(profileName);
            var candidate = Path.Combine(instancesRoot, safe);
            var n = 2;
            while (Directory.Exists(candidate) && Directory.EnumerateFileSystemEntries(candidate).Any())
            {
                candidate = Path.Combine(instancesRoot, $"{safe}-{n++}");
            }
            return candidate;
        }

        /// <summary>Sanitizes a profile name into a safe folder token (invalid chars → '_').</summary>
        public static string MakeSafeName(string profileName)
        {
            if (string.IsNullOrWhiteSpace(profileName)) return "server";
            var invalid = Path.GetInvalidFileNameChars();
            var cleaned = new string(profileName.Trim().Select(c => invalid.Contains(c) ? '_' : c).ToArray());
            cleaned = cleaned.Trim('.', ' ');
            return string.IsNullOrWhiteSpace(cleaned) ? "server" : cleaned;
        }

        private static bool SameVolume(string a, string b)
        {
            try
            {
                var ra = Path.GetPathRoot(Path.GetFullPath(a));
                var rb = Path.GetPathRoot(Path.GetFullPath(b));
                return string.Equals(ra, rb, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private void LinkOrCopyFile(string source, string dest, bool sameVolume)
        {
            if (sameVolume && CreateHardLink(dest, source, IntPtr.Zero)) return;

            // Cross-volume, or hard-link failed (e.g. read-only source): fall back to a plain copy.
            File.Copy(source, dest, overwrite: true);
        }

        private void CreateJunctionOrThrow(string linkPath, string targetPath)
        {
            if (Directory.Exists(linkPath))
            {
                // A stale link from a previous attempt: unlink (never follow) before recreating.
                try { Directory.Delete(linkPath); } catch { /* best effort */ }
            }

            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c mklink /J \"{linkPath}\" \"{targetPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start cmd.exe for junction creation.");
            proc.WaitForExit(20000);

            // A junction is a directory carrying a reparse point; confirm both to be sure it took.
            var ok = proc.HasExited && proc.ExitCode == 0 && Directory.Exists(linkPath)
                     && new DirectoryInfo(linkPath).Attributes.HasFlag(FileAttributes.ReparsePoint);
            if (!ok)
            {
                var err = proc.HasExited ? proc.StandardError.ReadToEnd() : "timed out";
                throw new IOException($"Could not create junction '{linkPath}' -> '{targetPath}': {err}");
            }
        }

        private static void CopyDirectory(string sourceDir, string destinationDir)
        {
            Directory.CreateDirectory(destinationDir);

            foreach (var file in Directory.GetFiles(sourceDir))
            {
                File.Copy(file, Path.Combine(destinationDir, Path.GetFileName(file)), overwrite: true);
            }

            foreach (var dir in Directory.GetDirectories(sourceDir))
            {
                CopyDirectory(dir, Path.Combine(destinationDir, Path.GetFileName(dir)));
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreateHardLink(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);
    }
}
