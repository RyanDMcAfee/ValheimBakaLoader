using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Newtonsoft.Json;
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
        ///
        /// Provisioning works at DIRECTORY level off a deny-list, so a game update that adds a
        /// top-level folder or new Managed assemblies is included with no code change.
        /// </summary>
        /// <param name="baseExePath">Path to the base (Steam) server .exe to derive from.</param>
        /// <param name="profileName">Profile name; sanitized into the install folder name.</param>
        /// <param name="seedPluginsFromBase">When true, copies mods into the new install so it
        /// starts as a clone; when false, the new install starts with no mods.</param>
        /// <param name="seedSourceBepInExDir">Optional BepInEx directory to seed mods/configs FROM
        /// (e.g. the active server's, which may itself be an isolated install). Null seeds from the
        /// base install's own BepInEx.</param>
        IsolatedInstallResult ProvisionInstall(string baseExePath, string profileName, bool seedPluginsFromBase,
            string seedSourceBepInExDir = null);

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

        /// <summary>
        /// Every isolated install BakaLoader has provisioned from this base, whether or not it
        /// is still attached to a profile. Empty when there is no instances root, which is the
        /// ordinary single-install case.
        /// </summary>
        IReadOnlyList<string> ManagedInstallDirectories(string baseExePath);

        /// <summary>
        /// Gives one already-provisioned isolated install the BepInEx sharing it does not have
        /// yet, and answers whether anything was created.
        /// <para>
        /// <see cref="ProvisionInstall"/> only ever walks the folders the base install already
        /// carries, so a profile provisioned before BepInEx existed has no BepInEx folder at
        /// all: no core junction, no patchers junction, no loader files beside the exe. It
        /// would run vanilla forever with nothing saying so. This is the step that closes
        /// that, run after BepInEx lands in the base.
        /// </para>
        /// <para>
        /// It only ever CREATES what is missing. A folder that is already there, junction or
        /// real, is left exactly as it is: an install that diverged on purpose is not
        /// something to silently undo, and a real core folder holds files this has no business
        /// deleting.
        /// </para>
        /// </summary>
        bool EnsureSharedBepInEx(string baseExePath, string installDir);
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

        /// <summary>
        /// The note an isolated install carries naming the base it was made from.
        /// <para>
        /// It exists because <see cref="FallbackInstancesRoot"/> is ONE folder shared by every
        /// base whose own parent could not be written to, and a path there says nothing about
        /// which base an instance under it came from. The executable's file name is no help
        /// either: every Valheim dedicated server on the machine is called valheim_server.exe.
        /// </para>
        /// </summary>
        public const string InstanceMarkerName = ".bakaloader-instance.json";

        /// <summary>
        /// The instances root used when the folder beside the base install cannot be written,
        /// which a server under Program Files without administrator rights cannot be. ONE
        /// definition, because <see cref="GetInstancesRoot"/>, CandidateInstancesRoots and the
        /// BepInEx sharing rule all have to name the same folder; two copies of this path
        /// drifting apart is exactly how an install escapes a refusal that names it.
        /// </summary>
        public static string FallbackInstancesRoot => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BakaLoader", InstancesRootName);

        /// <summary>
        /// BakaLoader's own bookkeeping beside a server executable. These are never shared into
        /// an isolated install: the BepInEx note describes the BASE install's loader, and an
        /// instance carrying a hard link to it would answer questions about an install it is
        /// not, while its own stamp names the base it came from and is per install by
        /// definition.
        /// </summary>
        private static readonly string[] BookkeepingFileNames =
        {
            BepInExMarkerFile.FileName,
            InstanceMarkerName,
        };

        private static bool IsBookkeepingFile(string path) =>
            BookkeepingFileNames.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase);

        /// <summary>The note one isolated install carries about where it came from.</summary>
        private sealed class InstanceMarker
        {
            [JsonProperty("schema")]
            public int Schema { get; set; }

            /// <summary>The base server executable this install was provisioned from.</summary>
            [JsonProperty("baseExe")]
            public string BaseExe { get; set; }

            [JsonProperty("stampedUtc")]
            public DateTime StampedUtc { get; set; }
        }

        /// <summary>Writes the note naming the base an isolated install was made from.</summary>
        private void StampInstance(string installDir, string baseExePath)
        {
            try
            {
                File.WriteAllText(Path.Combine(installDir, InstanceMarkerName),
                    JsonConvert.SerializeObject(new InstanceMarker
                    {
                        Schema = 1,
                        BaseExe = Path.GetFullPath(baseExePath),
                        StampedUtc = DateTime.UtcNow,
                    }, Formatting.Indented));
            }
            catch (Exception e)
            {
                // A stamp that could not be written costs precision in the shared fallback
                // root and nothing else, so it never fails a provision.
                Logger.Debug("Could not stamp the isolated install {0}: {1}", installDir, e.Message);
            }
        }

        /// <summary>
        /// The base an isolated install says it was made from, or null when it carries no note.
        /// Null is not "no base": an install provisioned before the note existed has none, and
        /// is treated exactly as it was before.
        /// </summary>
        public static string RecordedBaseExe(string installDir)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(installDir)) return null;

                var path = Path.Combine(installDir, InstanceMarkerName);
                if (!File.Exists(path)) return null;

                var marker = JsonConvert.DeserializeObject<InstanceMarker>(File.ReadAllText(path));
                return string.IsNullOrWhiteSpace(marker?.BaseExe) ? null : marker.BaseExe;
            }
            catch
            {
                return null;
            }
        }

        // BepInEx subfolders that are read-only at runtime and safe to share via junction.
        private static readonly string[] JunctionableBepInExDirs = { "core", "patchers" };

        /// <summary>
        /// The ONLY top-level entries an isolated install skips. Everything else the base
        /// install carries is provisioned automatically, by design: this is a DENY-list, not
        /// an allow-list, so a game update that adds a folder (Valheim 1.0 added
        /// "Valheim_BurstDebugInformation_DoNotShip") or new Managed assemblies is picked up
        /// with no code change. Never turn this into an allow-list.
        /// </summary>
        private static readonly string[] SkippedTopLevelDirs =
        {
            InstancesRootName,      // never nest one instances root inside another
            ".git",
        };

        private readonly IApplicationLogger Logger;

        public InstallIsolationService(IApplicationLogger logger)
        {
            Logger = logger;
        }

        public IsolatedInstallResult ProvisionInstall(string baseExePath, string profileName, bool seedPluginsFromBase,
            string seedSourceBepInExDir = null)
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
                    if (SkippedTopLevelDirs.Contains(name, StringComparer.OrdinalIgnoreCase))
                    {
                        Logger.Debug("Skipping top-level folder '{0}' while provisioning '{1}'.", name, profileName);
                        continue;
                    }

                    if (string.Equals(name, "BepInEx", StringComparison.OrdinalIgnoreCase))
                    {
                        ProvisionBepInEx(dir, Path.Combine(installDir, name), seedPluginsFromBase, sameVolume,
                            seedSourceBepInExDir);
                    }
                    else
                    {
                        // Everything else is bulky read-only game runtime and gets shared through a
                        // single directory junction: valheim_server_Data (Managed and all its
                        // assemblies included), MonoBleedingEdge, D3D12,
                        // Valheim_BurstDebugInformation_DoNotShip, and whatever a future update
                        // adds. Junctioning at DIRECTORY level is what makes that automatic.
                        CreateJunctionOrThrow(Path.Combine(installDir, name), dir);
                    }
                }

                // Every loose root file: the exe, its config, the doorstop files, and anything a
                // game update drops beside them. The one exception is BakaLoader's own
                // bookkeeping, which describes the install it sits in and belongs to no other.
                foreach (var file in Directory.EnumerateFiles(baseDir))
                {
                    if (IsBookkeepingFile(file)) continue;
                    LinkOrCopyFile(file, Path.Combine(installDir, Path.GetFileName(file)), sameVolume);
                }

                // Which base this came from, written down while the answer is certain. Nothing
                // in the path records it, and in the shared fallback root nothing else can.
                StampInstance(installDir, baseExePath);

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

        private void ProvisionBepInEx(string srcBep, string dstBep, bool seedPlugins, bool sameVolume,
            string seedSourceBepInExDir = null)
        {
            Directory.CreateDirectory(dstBep);

            // Mods/configs seed from the requested source (the ACTIVE server's BepInEx, which may
            // itself be an isolated install) when one is given; the read-only runtime dirs always
            // junction to the canonical base so no junction ever points at another junction.
            var seedBep = !string.IsNullOrWhiteSpace(seedSourceBepInExDir) && Directory.Exists(seedSourceBepInExDir)
                ? seedSourceBepInExDir
                : srcBep;

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
                    var seedSrc = Path.Combine(seedBep, "plugins");
                    if (seedPlugins && Directory.Exists(seedSrc)) CopyDirectory(seedSrc, dst);
                    else Directory.CreateDirectory(dst);
                }
                else if (string.Equals(name, "cache", StringComparison.OrdinalIgnoreCase))
                {
                    // Assembly cache is keyed to the (per-server) plugin set: start fresh, never share.
                    Directory.CreateDirectory(Path.Combine(dstBep, "cache"));
                }
                else
                {
                    // config and any other subdir: real per-server copy, from the seed source
                    // when it carries the same subdir (mod configs travel with the mods).
                    var seedSrc = Path.Combine(seedBep, name);
                    CopyDirectory(Directory.Exists(seedSrc) ? seedSrc : dir, Path.Combine(dstBep, name));
                }
            }

            foreach (var file in Directory.EnumerateFiles(seedBep))
            {
                // Loose BepInEx files (doorstop logs, stray cfgs): copy so per-server edits don't bleed.
                File.Copy(file, Path.Combine(dstBep, Path.GetFileName(file)), overwrite: true);
            }
        }

        public IReadOnlyList<string> ManagedInstallDirectories(string baseExePath)
        {
            var found = new List<string>();
            if (string.IsNullOrWhiteSpace(baseExePath)) return found;

            try
            {
                var baseDir = Path.GetDirectoryName(Path.GetFullPath(baseExePath));
                if (string.IsNullOrWhiteSpace(baseDir)) return found;

                var baseFull = Path.GetFullPath(baseExePath);

                foreach (var root in CandidateInstancesRoots(baseDir))
                {
                    if (!Directory.Exists(root)) continue;

                    // The fallback root is shared by every base whose own parent was not
                    // writable, so an install under it has to SAY which base it came from.
                    var shared = IsFallbackRoot(root);

                    foreach (var dir in Directory.EnumerateDirectories(root))
                    {
                        // Only an install that carries the same executable came from this base.
                        var exe = Path.Combine(dir, Path.GetFileName(baseExePath));
                        if (!File.Exists(exe)) continue;

                        // In the shared root the file name proves nothing: every Valheim
                        // dedicated server is called valheim_server.exe. A stamped install
                        // that names ANOTHER base is that base's to link, never this one's.
                        // An unstamped one predates the stamp and keeps the older answer,
                        // because dropping it would leave it running vanilla with nothing
                        // saying so.
                        if (shared)
                        {
                            var stamped = RecordedBaseExe(dir);
                            if (!string.IsNullOrWhiteSpace(stamped)
                                && !string.Equals(stamped, baseFull, StringComparison.OrdinalIgnoreCase))
                                continue;
                        }

                        found.Add(dir);
                    }
                }
            }
            catch (Exception e)
            {
                Logger.Debug("Could not list isolated installs for {0}: {1}", baseExePath, e.Message);
            }

            return found;
        }

        /// <summary>
        /// Where an instances root may live: beside the base install, and the fallback under
        /// LocalApplicationData that <see cref="GetInstancesRoot"/> falls back to when the
        /// first is not writable. Both are looked at, because either may hold real installs.
        /// </summary>
        private static IEnumerable<string> CandidateInstancesRoots(string baseDir)
        {
            var parent = Directory.GetParent(baseDir)?.FullName;
            if (!string.IsNullOrWhiteSpace(parent))
            {
                yield return Path.Combine(parent, InstancesRootName);
            }

            yield return FallbackInstancesRoot;
        }

        /// <summary>True when a path names the shared LocalApplicationData instances root.</summary>
        public static bool IsFallbackRoot(string root)
        {
            if (string.IsNullOrWhiteSpace(root)) return false;

            try
            {
                return string.Equals(
                    Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar),
                    Path.GetFullPath(FallbackInstancesRoot).TrimEnd(Path.DirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        public bool EnsureSharedBepInEx(string baseExePath, string installDir)
        {
            if (string.IsNullOrWhiteSpace(baseExePath) || string.IsNullOrWhiteSpace(installDir)) return false;
            if (!Directory.Exists(installDir)) return false;

            var baseDir = Path.GetDirectoryName(Path.GetFullPath(baseExePath));
            if (string.IsNullOrWhiteSpace(baseDir)) return false;

            var srcBep = Path.Combine(baseDir, "BepInEx");
            if (!Directory.Exists(srcBep)) return false;

            var dstBep = Path.Combine(installDir, "BepInEx");
            var sameVolume = SameVolume(baseDir, installDir);
            var changed = false;

            Directory.CreateDirectory(dstBep);

            foreach (var name in JunctionableBepInExDirs)
            {
                var source = Path.Combine(srcBep, name);
                var destination = Path.Combine(dstBep, name);
                if (!Directory.Exists(source)) continue;
                if (Directory.Exists(destination)) continue;   // junction or real: never replaced

                CreateJunctionOrThrow(destination, source);
                changed = true;
            }

            foreach (var name in new[] { "plugins", "config", "cache" })
            {
                var destination = Path.Combine(dstBep, name);
                if (Directory.Exists(destination)) continue;

                // config seeds from the base so the loader's own cfg is there; plugins and the
                // assembly cache are per server and start empty.
                var source = Path.Combine(srcBep, name);
                if (string.Equals(name, "config", StringComparison.OrdinalIgnoreCase) && Directory.Exists(source))
                    CopyDirectory(source, destination);
                else
                    Directory.CreateDirectory(destination);

                changed = true;
            }

            // The loose loader files beside the executable: winhttp.dll is the whole mechanism
            // on Windows, and an install without it starts vanilla no matter what BepInEx folder
            // it can see.
            foreach (var file in Directory.EnumerateFiles(baseDir))
            {
                // BakaLoader's own bookkeeping stays where it was written. The BepInEx note
                // describes the BASE install, and an instance hard-linked to it would answer
                // "is this install looked after" on behalf of a folder it is not.
                if (IsBookkeepingFile(file)) continue;

                var destination = Path.Combine(installDir, Path.GetFileName(file));
                if (File.Exists(destination)) continue;

                LinkOrCopyFile(file, destination, sameVolume);
                changed = true;
            }

            if (changed)
                Logger.Information("Shared the base install's BepInEx into {0}.", installDir);

            return changed;
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
                // Belt-and-braces: a managed instance already lives at
                // "<root>/.bakaloader-instances/<name>". Provisioning from it must reuse
                // that root - never nest a second instances dir inside the first.
                var parentName = new DirectoryInfo(parent).Name;
                if (string.Equals(parentName, InstancesRootName, StringComparison.OrdinalIgnoreCase)
                    && TryEnsureWritableDirectory(parent))
                    return parent;

                var candidate = Path.Combine(parent, InstancesRootName);
                if (TryEnsureWritableDirectory(candidate)) return candidate;
            }

            var fallback = FallbackInstancesRoot;
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
