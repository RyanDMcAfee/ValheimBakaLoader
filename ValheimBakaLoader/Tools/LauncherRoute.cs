using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace ValheimBakaLoader.Tools
{
    /// <summary>
    /// The three ways an install folder can be found, as far as starting BakaLoader goes.
    /// </summary>
    public enum LaunchRoute
    {
        /// <summary>ValheimBakaLoader.exe is there, so it is started the way it always was.</summary>
        Launcher,

        /// <summary>
        /// The launcher is gone and ValheimBakaLoader.dll is still there: the launcher was taken
        /// and the app is intact, so it is started through the dotnet host instead.
        /// </summary>
        Dotnet,

        /// <summary>Neither file is there, so there is nothing to start.</summary>
        None,
    }

    /// <summary>
    /// How BakaLoader gets started when the launcher may not be there.
    /// <para>
    /// WHY THIS EXISTS. On 2026-10-11 Windows Defender's cloud heuristic called the frozen
    /// launcher Trojan:Win32/Bearfoos.B!ml during a self-update, quarantined it out of a live
    /// install, took it again every time it was put back, and the update read the missing exe
    /// as a failed update and rolled the install back. The launcher is the SDK's generic
    /// apphost and nothing more: the app is ValheimBakaLoader.dll, it is framework dependent,
    /// and it finds its own folder through AppContext.BaseDirectory. So
    /// <c>dotnet ValheimBakaLoader.dll</c> from the install folder is the same app, started by
    /// the host Microsoft signs, with no launcher involved at all.
    /// </para>
    /// <para>
    /// The same rule is written twice: here, for the app, and as Get-LaunchRoute in the
    /// update watchdog, which runs after the app has closed and cannot call into it. The suite
    /// runs the script's copy against real folders and holds the two to the same answers.
    /// </para>
    /// </summary>
    public static class LauncherRoute
    {
        /// <summary>The launcher the SDK generates, frozen since 1.2.8.</summary>
        public const string LauncherName = "ValheimBakaLoader.exe";

        /// <summary>The app itself.</summary>
        public const string LibraryName = "ValheimBakaLoader.dll";

        /// <summary>The script that ships beside both and starts the app through dotnet.</summary>
        public const string ScriptName = "BakaLoader.cmd";

        /// <summary>The file name of the dotnet host.</summary>
        public const string DotnetHostName = "dotnet.exe";

        /// <summary>
        /// The rule, on the two facts it turns on. The launcher wins whenever it is there,
        /// because that is how every host has always started the app. A missing launcher
        /// beside a library that is still there is "launcher taken, app intact" and never a
        /// failed install.
        /// </summary>
        public static LaunchRoute For(bool launcherThere, bool libraryThere) =>
            launcherThere ? LaunchRoute.Launcher
            : libraryThere ? LaunchRoute.Dotnet
            : LaunchRoute.None;

        /// <summary>The rule, asked of a folder on disk.</summary>
        public static LaunchRoute In(string folder)
        {
            if (string.IsNullOrWhiteSpace(folder)) return LaunchRoute.None;

            try
            {
                return For(
                    File.Exists(Path.Combine(folder, LauncherName)),
                    File.Exists(Path.Combine(folder, LibraryName)));
            }
            catch
            {
                return LaunchRoute.None;
            }
        }

        /// <summary>
        /// The Saga line for a run that started without its launcher, or null when the launcher
        /// is there. One line, from the catalog, naming the script a host can start next time.
        /// </summary>
        public static string MissingLauncherLine(string folder) =>
            In(folder) == LaunchRoute.Dotnet
                ? HostCatalog.T("host.saga.launcher_missing", ("script", ScriptName))
                : null;

        /// <summary>The folder this copy of BakaLoader runs from, without a trailing separator.</summary>
        public static string InstallFolder() =>
            Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);

        /// <summary>
        /// Whether a path names the dotnet host. Application.ExecutablePath is the host that
        /// started the process, so a run through dotnet answers dotnet.exe there rather than
        /// the launcher.
        /// </summary>
        public static bool IsDotnetHost(string hostPath)
        {
            if (string.IsNullOrWhiteSpace(hostPath)) return false;

            try
            {
                return string.Equals(
                    Path.GetFileName(hostPath.Trim().Trim('"')), DotnetHostName,
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Whether a process is a copy of BakaLoader: one started by its launcher, or a dotnet
        /// host that has loaded the app's library. The name alone is not enough for the second
        /// kind, because every build tool on the machine runs as dotnet too.
        /// </summary>
        public static bool IsBakaLoader(string processName, IEnumerable<string> modulePaths)
        {
            if (string.Equals(processName, Path.GetFileNameWithoutExtension(LauncherName),
                    StringComparison.OrdinalIgnoreCase)) return true;

            if (!string.Equals(processName, Path.GetFileNameWithoutExtension(DotnetHostName),
                    StringComparison.OrdinalIgnoreCase)) return false;

            return LibraryIn(modulePaths) != null;
        }

        /// <summary>The app's library among a process's modules, or null.</summary>
        public static string LibraryIn(IEnumerable<string> modulePaths) =>
            modulePaths?.FirstOrDefault(path =>
                !string.IsNullOrWhiteSpace(path)
                && string.Equals(Path.GetFileName(path), LibraryName, StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// The paths of a live process's modules, or nothing when Windows will not say. Another
        /// account's process, or one that has just ended, answers nothing rather than throwing.
        /// </summary>
        public static IReadOnlyList<string> ModulePaths(Process process)
        {
            try
            {
                return process.Modules.Cast<ProcessModule>()
                    .Select(m => m.FileName)
                    .Where(f => !string.IsNullOrWhiteSpace(f))
                    .ToList();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }
    }
}
