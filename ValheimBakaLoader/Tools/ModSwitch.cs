using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ValheimBakaLoader.Tools.Logging;
using ValheimBakaLoader.Tools.Models;

namespace ValheimBakaLoader.Tools
{
    /// <summary>What switching a mod off or on came to.</summary>
    public sealed class ModSwitchResult
    {
        public bool Ok { get; init; }

        /// <summary>How many plugin files were renamed.</summary>
        public int Count { get; init; }

        /// <summary>The sentence the host reads when it did not work. Null on success.</summary>
        public string Error { get; init; }

        /// <summary>A short name for why it did not work, so a page can word it itself. Null on success.</summary>
        public string Reason { get; init; }

        public static ModSwitchResult Done(int count) => new() { Ok = true, Count = count };

        public static ModSwitchResult Refused(string reason, string error) => new() { Ok = false, Reason = reason, Error = error };
    }

    /// <summary>
    /// Switches a mod off and on again without removing it, the way r2modman and Gale do:
    /// every <c>*.dll</c> under the mod's plugin folder and its patcher folder (and its monomod
    /// folder, which Gale toggles too, since a MonoMod patch left loading is a mod still half
    /// on) is renamed to <c>*.dll.old</c>, and back. BepInEx loads <c>*.dll</c> only, so a switched off mod keeps
    /// its folder, its manifest, its version and its config, and nothing else changes. Nothing
    /// under <c>BepInEx/config</c> is touched either way.
    /// <para>
    /// A switch is all or nothing. Every rename is checked for a name that is already taken
    /// before the first one happens, and a rename that fails half way puts back the ones done
    /// before it. The server holding the folder open is refused one step out, by the caller.
    /// </para>
    /// </summary>
    public static class ModSwitch
    {
        /// <summary>What a switched off plugin file ends in.</summary>
        public const string OffSuffix = ".old";

        private const string PluginExtension = ".dll";

        /// <summary>
        /// True when a mod is switched off: its folders hold at least one <c>*.dll.old</c> and
        /// no <c>*.dll</c>. A folder that is missing or null counts for nothing. Never throws.
        /// </summary>
        public static bool IsDisabled(string pluginDirectory, string patcherDirectory)
        {
            try
            {
                var files = FilesIn(pluginDirectory, patcherDirectory).ToList();
                return files.Any(IsOffFile) && !files.Any(IsPluginFile);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Renames every plugin file of the mod to <c>*.dll.old</c>.</summary>
        public static ModSwitchResult Disable(InstalledMod mod, IApplicationLogger logger) => Switch(mod, off: true, logger);

        /// <summary>Renames every <c>*.dll.old</c> of the mod back to <c>*.dll</c>.</summary>
        public static ModSwitchResult Enable(InstalledMod mod, IApplicationLogger logger) => Switch(mod, off: false, logger);

        /// <summary>
        /// Switches off every plugin file under the given folders without the checks
        /// <see cref="Disable"/> makes about the mod, for the update path: a mod that was off
        /// before an update is off after it. Answers how many files were renamed. Throws on a
        /// rename that fails, so the update's own rollback runs.
        /// </summary>
        internal static int SwitchOffFiles(IEnumerable<string> folders)
        {
            var count = 0;
            foreach (var file in FilesIn(folders.ToArray()).Where(IsPluginFile).ToList())
            {
                var target = file + OffSuffix;
                if (File.Exists(target)) File.Delete(target);
                File.Move(file, target);
                count++;
            }
            return count;
        }

        private static ModSwitchResult Switch(InstalledMod mod, bool off, IApplicationLogger logger)
        {
            if (mod == null) return ModSwitchResult.Refused("notFound", "No mod was named.");

            var pluginDir = Existing(mod.PluginDirectory);
            var patcherDir = Existing(ModRemovalService.ResolvePatcherDirectory(mod));
            if (pluginDir == null && patcherDir == null)
                return ModSwitchResult.Refused("notFound", $"The folder of {mod.FullName} was not found, so nothing was switched.");

            // The same names a removal refuses: BakaLoader's own plugins and the loader's own
            // patchers. Switching one of those off breaks a server in a way the Mods page has
            // no row to undo.
            var folderName = new DirectoryInfo(pluginDir ?? patcherDir).Name;
            if (ModScanner.IsCompanionFolder(folderName) || ModScanner.IsProtectedPatcher(folderName))
                return ModSwitchResult.Refused("protected",
                    $"{folderName} is managed by BakaLoader or BepInEx and cannot be switched off here.");

            var anchor = pluginDir ?? patcherDir;
            var bepInExRoot = Directory.GetParent(Directory.GetParent(anchor)!.FullName)?.FullName;
            var monomodDir = bepInExRoot == null || BepInExService.IsReparsePoint(Path.Combine(bepInExRoot, "monomod"))
                ? null
                : Existing(Path.Combine(bepInExRoot, "monomod", folderName));
            var folders = new[] { pluginDir, patcherDir, monomodDir }.Where(d => d != null).ToArray();
            var wanted = FilesIn(folders).Where(off ? IsPluginFile : IsOffFile).ToList();

            if (wanted.Count == 0)
            {
                var already = FilesIn(folders).Any(off ? IsOffFile : IsPluginFile);
                if (already)
                    return ModSwitchResult.Refused(off ? "alreadyOff" : "alreadyOn",
                        off ? $"{mod.FullName} is already switched off." : $"{mod.FullName} is already switched on.");

                return ModSwitchResult.Refused("nothingToSwitch",
                    $"{mod.FullName} has no plugin file to switch, so nothing was changed.");
            }

            var plan = wanted.Select(f => (From: f, To: off ? f + OffSuffix : f.Substring(0, f.Length - OffSuffix.Length))).ToList();
            var taken = plan.FirstOrDefault(p => File.Exists(p.To));
            if (taken.To != null)
                return ModSwitchResult.Refused("nameTaken",
                    $"{Path.GetFileName(taken.To)} is already there beside {Path.GetFileName(taken.From)}, so nothing was switched.");

            // A realm whose patchers folder is still the old link to the base install gets its
            // own copy first, so renaming a patcher here never switches it off on every realm.
            if (patcherDir != null)
            {
                var patcherBepInEx = Directory.GetParent(Directory.GetParent(patcherDir)!.FullName)?.FullName;
                InstallIsolationService.EnsureOwnPatchersFolder(patcherBepInEx, logger);
            }

            var done = new List<(string From, string To)>();
            try
            {
                foreach (var step in plan)
                {
                    File.Move(step.From, step.To);
                    done.Add(step);
                }
            }
            catch (Exception e)
            {
                for (var i = done.Count - 1; i >= 0; i--)
                {
                    try { File.Move(done[i].To, done[i].From); }
                    catch (Exception undo) { logger?.Warning("Could not put back {0}: {1}", done[i].From, undo.Message); }
                }

                logger?.Error(e, "Could not switch {0} {1}.", mod.FullName, off ? "off" : "on");
                return ModSwitchResult.Refused("failed",
                    $"{mod.FullName} could not be switched {(off ? "off" : "on")} ({e.Message}), so it was left as it was.");
            }

            logger?.Information("Switched {0} {1}: {2} plugin file(s) in {3} renamed {4}.",
                mod.FullName, off ? "off" : "on", done.Count, string.Join(" and ", folders),
                off ? "to .dll.old" : "back to .dll");
            return ModSwitchResult.Done(done.Count);
        }

        private static string Existing(string directory) =>
            !string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory) ? directory : null;

        private static IEnumerable<string> FilesIn(params string[] folders)
        {
            foreach (var folder in folders)
            {
                if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) continue;

                // Every file, filtered by its real ending: a "*.dll" search pattern is a
                // Windows pattern, and that family of matching has surprised enough code that
                // the ending is checked here by hand.
                foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
                    yield return file;
            }
        }

        private static bool IsPluginFile(string path) =>
            path.EndsWith(PluginExtension, StringComparison.OrdinalIgnoreCase);

        private static bool IsOffFile(string path) =>
            path.EndsWith(PluginExtension + OffSuffix, StringComparison.OrdinalIgnoreCase);
    }
}
