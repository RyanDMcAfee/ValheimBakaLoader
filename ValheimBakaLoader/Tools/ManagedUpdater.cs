using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ValheimBakaLoader.Tools
{
    /// <summary>
    /// The part of a self update that runs after BakaLoader has closed: the same library, started a
    /// second time through dotnet with <c>--apply-update</c>, in a small window of its own.
    /// <para>
    /// WHY THIS EXISTS. Until 1.2.13 this was a PowerShell script the app wrote into a temp folder
    /// and started hidden. On 2026-10-11 Defender scored the self update as a dropper, and the
    /// verdict came from behaviour rather than bytes: powershell.exe fetching a zip, writing files
    /// beside an exe, starting that exe, with a Run key naming it. Every self update on every host
    /// repeated that shape. Now nothing in an update is a script. The app downloads and unpacks the
    /// release itself (AppUpdateService), and this, the app's own code under the dotnet host
    /// Microsoft signs, waits for it to close, copies the new files in, writes down what happened
    /// and starts the app again. No Run key is written by any of it; Start with Windows is only
    /// ever touched by the host's own switch.
    /// </para>
    /// <para>
    /// The updater is loaded from the very library it replaces. That works because Windows lets a
    /// file that is mapped as a running image be renamed even though it cannot be deleted or written
    /// over, so every file about to be replaced is moved aside first, into update-rollback in the
    /// install, and that move is also the copy a failed update is put back from.
    /// </para>
    /// </summary>
    public static class ManagedUpdater
    {
        /// <summary>The switch that turns a start of the library into the updater.</summary>
        public const string ApplyUpdateSwitch = "--apply-update";

        /// <summary>A work folder other than the default, for a test. The app never passes it.</summary>
        internal const string WorkSwitch = "--work";

        /// <summary>A shorter wait for the app to close, for a test. The app never passes it.</summary>
        internal const string WaitSwitch = "--wait-seconds";

        /// <summary>
        /// Copy the files and start nothing afterwards, for a test whose install holds a real copy
        /// of the library: a test must never start BakaLoader itself. The app never passes it.
        /// </summary>
        internal const string NoStartSwitch = "--no-start";

        /// <summary>
        /// Run without the progress window, and start nothing afterwards with a console of its own.
        /// The suite passes it on every run, because a test must never put a window in front of
        /// whoever is using the PC; the app never passes it, so a host always sees their update.
        /// </summary>
        public const string NoWindowSwitch = "--no-window";

        /// <summary>The same as <see cref="NoWindowSwitch"/>, from the environment, when it is "1".</summary>
        public const string NoWindowVariable = "BAKALOADER_UPDATER_NO_WINDOW";

        /// <summary>The folder in the install the release is unpacked into before it is copied over.</summary>
        public const string PendingFolderName = "update-pending";

        /// <summary>The folder in the install the files an update replaces are moved aside into.</summary>
        public const string RollbackFolderName = "update-rollback";

        /// <summary>What the updater writes in the work folder for the next start to read.</summary>
        public const string ResultFileName = "RESULT.txt";

        /// <summary>
        /// How long the updater waits for the app to close. The close can take as long as the
        /// longest save wait plus the grace the close gives a server to go, so the wait is that
        /// and a minute more; a shorter one drops the update of a host whose world saves slowly,
        /// because the app is still stopping the server when the wait runs out. The suite holds
        /// this against ValheimServer.CloseDeadlineFor rather than this class reading it, so the
        /// updater loads nothing of the server code.
        /// </summary>
        public static readonly TimeSpan CloseWait = TimeSpan.FromMinutes(4);

        /// <summary>
        /// How long a start through the launcher is watched. A security tool that takes the file
        /// out from under the process does it early, and a launcher taken inside this window is
        /// followed by a start through dotnet.
        /// </summary>
        internal static readonly TimeSpan LauncherWatch = TimeSpan.FromSeconds(5);

        /// <summary>
        /// How long a start through dotnet is watched. A host with no .NET 6 Desktop Runtime ends
        /// inside the first second and says why only on a console that is gone, so an early end
        /// is written down for the next start to say.
        /// </summary>
        internal static readonly TimeSpan DotnetWatch = TimeSpan.FromSeconds(8);

        /// <summary>
        /// Where the release zip is downloaded and where the updater leaves RESULT.txt:
        /// %LOCALAPPDATA%\BakaLoaderUpdate. Outside the install, so nothing in it is ever copied
        /// over the app, and the same folder for the app and the updater because both work it out
        /// the same way.
        /// </summary>
        public static string WorkFolder() => WorkFolderOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BakaLoaderUpdate");

        /// <summary>
        /// Somewhere else to work, for a test, so nothing a test does is ever read by a running copy
        /// of the app at its next start. Null in every shipped path.
        /// </summary>
        internal static string WorkFolderOverride { get; set; }

        /// <summary>Where staging unpacks a release for this install.</summary>
        public static string PendingFolder(string install) => Path.Combine(install, PendingFolderName);

        /// <summary>Whether this start of the library is the updater.</summary>
        public static bool IsApplyUpdate(string[] args) =>
            args != null && args.Any(a => string.Equals(a, ApplyUpdateSwitch, StringComparison.OrdinalIgnoreCase));

        // ------------------------------------------------------------------ the command line

        /// <summary>What one run of the updater was asked to do.</summary>
        internal sealed class Plan
        {
            public int Pid { get; init; }
            public string Pending { get; init; }
            public string Install { get; init; }
            public string Work { get; init; }
            public TimeSpan Wait { get; init; }
            public bool Start { get; init; } = true;
            public bool Window { get; init; } = true;
        }

        /// <summary>
        /// The arguments the app starts the updater with, after the library's own path:
        /// <c>--apply-update &lt;pid&gt; &lt;pending&gt; &lt;install&gt;</c>.
        /// </summary>
        public static IReadOnlyList<string> Arguments(int pid, string pending, string install) => new[]
        {
            ApplyUpdateSwitch,
            pid.ToString(CultureInfo.InvariantCulture),
            pending,
            install,
        };

        /// <summary>
        /// The plan, or null when the arguments do not make one. The pending folder has to be
        /// update-pending inside the install named beside it: the updater copies whatever it is
        /// pointed at over the install, and that is the only folder the app ever stages into.
        /// </summary>
        internal static Plan Parse(string[] args)
        {
            if (args == null) return null;

            var at = Array.FindIndex(args, a => string.Equals(a, ApplyUpdateSwitch, StringComparison.OrdinalIgnoreCase));
            if (at < 0 || args.Length < at + 4) return null;

            if (!int.TryParse(args[at + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid)) return null;
            if (string.IsNullOrWhiteSpace(args[at + 2]) || string.IsNullOrWhiteSpace(args[at + 3])) return null;

            string pending, install;
            try
            {
                pending = Path.TrimEndingDirectorySeparator(Path.GetFullPath(args[at + 2]));
                install = Path.TrimEndingDirectorySeparator(Path.GetFullPath(args[at + 3]));
            }
            catch
            {
                return null;
            }

            if (!string.Equals(pending, Path.Combine(install, PendingFolderName), StringComparison.OrdinalIgnoreCase))
                return null;

            var work = WorkFolder();
            var wait = CloseWait;
            var start = true;
            var window = !string.Equals(
                Environment.GetEnvironmentVariable(NoWindowVariable)?.Trim(), "1", StringComparison.Ordinal);

            for (var i = at + 4; i < args.Length; i++)
            {
                if (string.Equals(args[i], WorkSwitch, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    work = args[++i];
                }
                else if (string.Equals(args[i], WaitSwitch, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    if (int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) && seconds >= 0)
                        wait = TimeSpan.FromSeconds(seconds);
                }
                else if (string.Equals(args[i], NoStartSwitch, StringComparison.OrdinalIgnoreCase))
                {
                    start = false;
                }
                else if (string.Equals(args[i], NoWindowSwitch, StringComparison.OrdinalIgnoreCase))
                {
                    window = false;
                }
            }

            return new Plan { Pid = pid, Pending = pending, Install = install, Work = work, Wait = wait, Start = start, Window = window };
        }

        // ------------------------------------------------------------------ the run

        /// <summary>
        /// The updater, from Program.Main. A small window that says what it is doing while the work
        /// runs beside it, and the process ends when the work does. Answers 0 for an update that
        /// went in and 1 for every other ending; 2 for arguments that did not make a plan.
        /// <para>
        /// The window is only for a real update: arguments that make no plan, a staged folder with
        /// no app in it, or <see cref="NoWindowSwitch"/> run without one, so nothing ever appears
        /// on the desktop for a run that has nothing to show a host.
        /// </para>
        /// </summary>
        public static int Run(string[] args)
        {
            var plan = Parse(args);
            if (plan == null) return 2;

            if (!ShowsWindow(plan, HoldsTheApp(plan.Pending))) return Apply(plan, null).ExitCode;

            UpdateResult result = null;
            UpdaterWindow window = null;
            try
            {
                window = MakeWindow();
            }
            catch
            {
                // No desktop to put a window on. The update still runs: the window is there so a
                // host can see it, never so it can be hidden.
                window = null;
            }

            if (window == null)
            {
                result = Apply(plan, null);
                return result.ExitCode;
            }

            using (window)
            {
                window.Shown += (_, _) => Task.Run(() =>
                {
                    try { result = Apply(plan, window.Say); }
                    finally { window.Finish(); }
                });

                Application.Run(window);
            }

            return result?.ExitCode ?? 1;
        }

        /// <summary>
        /// Whether this run puts a window on the desktop: only a real update (a plan whose staged
        /// folder holds the app) that was not told to show nothing, by <see cref="NoWindowSwitch"/>
        /// or by <see cref="NoWindowVariable"/>. The one place Run decides it, so the suite can hold
        /// the rule without ever drawing a window.
        /// </summary>
        internal static bool ShowsWindow(Plan plan, bool holdsApp) => plan != null && plan.Window && holdsApp;

        /// <summary>
        /// How Run makes its window. Declared after Run on purpose, so the only construction of the
        /// window in this file sits below the guard that decides whether there is one. The suite
        /// swaps it for one that throws and records the call, so a guard that stopped working fails
        /// a test instead of putting a window in front of whoever is using the PC.
        /// </summary>
        internal static Func<UpdaterWindow> MakeWindow { get; set; } = () => new UpdaterWindow();

        /// <summary>
        /// The whole update, in order: wait for the app to close, copy the staged files over the
        /// install with everything replaced set aside, put it all back when any copy fails, write
        /// down what happened, start the app. Never throws. Every ending that is not "the app would
        /// not close" starts the app again, because by then the host has no BakaLoader running.
        /// </summary>
        internal static UpdateResult Apply(Plan plan, Action<string> say)
        {
            var result = new UpdateResult();

            Say(say, () => HostCatalog.T("host.updater.waiting"));
            if (!WaitForExit(plan.Pid, plan.Wait))
            {
                // The copy that would not close is still running, so nothing is written over it and
                // nothing is started beside it. The update is staged again the next time it looks.
                result.Outcome = UpdateResult.StillRunning;
                result.WaitedMinutes = (int)Math.Ceiling(plan.Wait.TotalMinutes);
                result.Start = UpdateResult.StartNone;
                TryWrite(plan.Work, result);
                return result;
            }

            try
            {
                if (!HoldsTheApp(plan.Pending))
                {
                    result.Outcome = UpdateResult.NothingStaged;
                }
                else
                {
                    Say(say, () => HostCatalog.T("host.updater.copying"));
                    CopyInto(plan, result, say);
                }
            }
            catch (Exception e)
            {
                // CopyInto puts back what it wrote before it lets anything out; this is only a
                // failure before the first file, so there is nothing to put back.
                result.Outcome = UpdateResult.RolledBack;
                result.Detail = OneLine(e.Message);
            }

            TryDeleteFolder(plan.Pending);

            // The files set aside are the only copy of what was there before. They go when the
            // update went in or every one of them went back; a rollback that could not put them
            // all back keeps them, marked so no later clean takes them either.
            if (result.Unrestored == 0) CleanLeftovers(plan.Install);

            Say(say, () => HostCatalog.T("host.updater.starting"));
            StartApp(plan, result);
            return result;
        }

        private static void Say(Action<string> say, Func<string> line)
        {
            if (say == null) return;
            try { say(line()); } catch { /* a status line is never worth an update */ }
        }

        /// <summary>
        /// Waits for the app's process to end, up to the bound, and then looks once more a second
        /// later. True when it has gone.
        /// </summary>
        internal static bool WaitForExit(int pid, TimeSpan bound)
        {
            if (pid <= 0 || pid == Environment.ProcessId) return true;

            try
            {
                using var app = Process.GetProcessById(pid);
                if (!app.WaitForExit((int)Math.Min(int.MaxValue, Math.Max(0, bound.TotalMilliseconds)))) return false;
            }
            catch (ArgumentException)
            {
                return true; // not running
            }
            catch (InvalidOperationException)
            {
                return true; // ended between the lookup and the wait
            }

            Thread.Sleep(1000);
            try
            {
                using var again = Process.GetProcessById(pid);
                return again.HasExited;
            }
            catch
            {
                return true;
            }
        }

        private static bool HoldsTheApp(string pending) =>
            Directory.Exists(pending)
            && (File.Exists(Path.Combine(pending, LauncherRoute.LibraryName))
                || File.Exists(Path.Combine(pending, LauncherRoute.LauncherName)));

        // ------------------------------------------------------------------ the copy

        private enum Aside { Created, Moved, Copied }

        private sealed class Step
        {
            public string Relative { get; init; }
            public string Target { get; init; }
            public string AsidePath { get; init; }
            public Aside How { get; set; }
        }

        /// <summary>
        /// Every staged file over the install, launcher last. A file already there is moved aside
        /// first, and copied aside when it cannot be moved. The first file that cannot be set aside
        /// or written puts every earlier one back. The launcher is the one exception: one that
        /// cannot be read in the download, or cannot be written into the install, is let go and
        /// the one already there is kept, because the app is the library and the update is whole
        /// without it.
        /// </summary>
        private static void CopyInto(Plan plan, UpdateResult result, Action<string> say)
        {
            var files = Directory.EnumerateFiles(plan.Pending, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(plan.Pending, f))
                .OrderBy(rel => IsLauncher(rel) ? 1 : 0)
                .ThenBy(rel => rel, StringComparer.OrdinalIgnoreCase)
                .ToList();

            // A folder of its own for this run, so a file a previous run could not delete (a
            // library still mapped by that run's updater) never stands in the way of this one.
            var asideRoot = Path.Combine(plan.Install, RollbackFolderName,
                DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + "-" + Environment.ProcessId);

            var done = new List<Step>();

            foreach (var rel in files)
            {
                var source = Path.Combine(plan.Pending, rel);
                var target = Path.Combine(plan.Install, rel);
                byte[] launcher = null;

                if (IsLauncher(rel))
                {
                    launcher = ReadWithRetries(source, out var problem);
                    if (launcher == null)
                    {
                        result.LauncherDownload = UpdateResult.LauncherHeld;
                        result.LauncherDetail = problem;
                        continue;
                    }

                    if (SameBytes(target, launcher))
                    {
                        result.LauncherDownload = UpdateResult.LauncherKept;
                        continue;
                    }
                }

                var step = new Step { Relative = rel, Target = target, AsidePath = Path.Combine(asideRoot, rel) };
                if (!SetAside(step, out var asideProblem))
                {
                    if (launcher != null)
                    {
                        result.LauncherDownload = UpdateResult.LauncherUnwritable;
                        result.LauncherDetail = asideProblem;
                        continue;
                    }

                    Fail(result, done, rel, asideProblem, asideRoot, say);
                    return;
                }

                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    if (launcher != null)
                    {
                        ClearReadOnly(target);
                        File.WriteAllBytes(target, launcher);
                        result.LauncherDownload = UpdateResult.LauncherWritten;
                    }
                    else
                    {
                        ClearReadOnly(target);
                        File.Copy(source, target, overwrite: true);
                    }

                    done.Add(step);
                }
                catch (Exception e)
                {
                    if (launcher != null)
                    {
                        // The old launcher goes back where it was and the update stays in.
                        Undo(step);
                        result.LauncherDownload = UpdateResult.LauncherUnwritable;
                        result.LauncherDetail = OneLine(e.Message);
                        continue;
                    }

                    done.Add(step);
                    Fail(result, done, rel, e.Message, asideRoot, say);
                    return;
                }
            }

            result.Outcome = UpdateResult.Updated;
        }

        private static void Fail(UpdateResult result, List<Step> done, string rel, string problem, string asideRoot, Action<string> say)
        {
            Say(say, () => HostCatalog.T("host.updater.restoring"));

            var unrestored = 0;
            for (var i = done.Count - 1; i >= 0; i--)
            {
                if (!Undo(done[i])) unrestored++;
            }

            result.Outcome = UpdateResult.RolledBack;
            result.File = rel;
            result.Detail = OneLine(problem);
            result.Unrestored = unrestored;

            if (unrestored > 0)
            {
                // What could not go back is still in this run's folder, and it is the only copy
                // there is. Marked, so neither this updater nor the next start nor the next update
                // cleans it away, and named in RESULT.txt so the host is told where it is.
                result.Kept = asideRoot;
                MarkKept(asideRoot);
            }
        }

        /// <summary>The file that marks a run's set aside files as the only copy left of them.</summary>
        public const string KeptMarkerName = "KEEP-the-old-files-are-here.txt";

        private static void MarkKept(string asideRoot)
        {
            try
            {
                Directory.CreateDirectory(asideRoot);
                File.WriteAllText(Path.Combine(asideRoot, KeptMarkerName),
                    "An update could not put every file in this folder back into the BakaLoader folder. "
                    + "These are the files that were there before it. BakaLoader leaves this folder alone; "
                    + "copy what you need back with BakaLoader closed, then delete it.\r\n",
                    new UTF8Encoding(false));
            }
            catch
            {
                // The updater itself does not clean a kept run either way; the mark is for later.
            }
        }

        private static bool IsKept(string runFolder)
        {
            try { return File.Exists(Path.Combine(runFolder, KeptMarkerName)); }
            catch { return true; }
        }

        private static bool SetAside(Step step, out string problem)
        {
            problem = null;
            if (!File.Exists(step.Target))
            {
                step.How = Aside.Created;
                return true;
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(step.AsidePath));
            }
            catch (Exception e)
            {
                problem = OneLine(e.Message);
                return false;
            }

            try
            {
                // A rename works on a file that is mapped as a running image, which is every
                // library the updater itself has loaded out of the install.
                File.Move(step.Target, step.AsidePath);
                step.How = Aside.Moved;
                return true;
            }
            catch
            {
                // A file held without delete sharing cannot be renamed. A copy is the next best
                // thing to put back, as long as it can be read.
            }

            try
            {
                File.Copy(step.Target, step.AsidePath, overwrite: true);
                step.How = Aside.Copied;
                return true;
            }
            catch (Exception e)
            {
                problem = OneLine(e.Message);
                return false;
            }
        }

        private static bool Undo(Step step)
        {
            try
            {
                switch (step.How)
                {
                    case Aside.Created:
                        if (File.Exists(step.Target)) { ClearReadOnly(step.Target); File.Delete(step.Target); }
                        return true;

                    case Aside.Moved:
                        if (File.Exists(step.Target)) { ClearReadOnly(step.Target); File.Delete(step.Target); }
                        File.Move(step.AsidePath, step.Target);
                        return true;

                    default:
                        ClearReadOnly(step.Target);
                        File.Copy(step.AsidePath, step.Target, overwrite: true);
                        return true;
                }
            }
            catch
            {
                return false;
            }
        }

        private static bool IsLauncher(string relative) =>
            string.Equals(relative, LauncherRoute.LauncherName, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// A file into memory, three tries a second apart, so an ordinary on-access scan can finish
        /// first. Null when it cannot be read, with the reason.
        /// </summary>
        private static byte[] ReadWithRetries(string path, out string problem)
        {
            problem = null;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                if (attempt > 0) Thread.Sleep(1000);
                try
                {
                    return File.ReadAllBytes(path);
                }
                catch (Exception e)
                {
                    problem = OneLine(e.Message);
                }
            }

            return null;
        }

        private static bool SameBytes(string path, byte[] bytes)
        {
            try
            {
                return File.Exists(path) && string.Equals(Sha256Of(File.ReadAllBytes(path)), Sha256Of(bytes), StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static void ClearReadOnly(string path)
        {
            try
            {
                if (File.Exists(path)) File.SetAttributes(path, FileAttributes.Normal);
            }
            catch
            {
                // The write that follows says what is wrong, if anything is.
            }
        }

        // ------------------------------------------------------------------ the start

        /// <summary>
        /// How the updater starts BakaLoader from a folder. Through the launcher only when it is
        /// there AND it is the frozen launcher BakaLoader ships, because a launcher with any other
        /// bytes is not one this version knows; otherwise through dotnet and the library, the same
        /// app under the host Microsoft signs; and not at all when neither is there.
        /// </summary>
        public static LaunchRoute ChooseStart(string install)
        {
            if (string.IsNullOrWhiteSpace(install)) return LaunchRoute.None;

            var launcher = Path.Combine(install, LauncherRoute.LauncherName);
            if (LauncherRoute.IsFrozenLauncher(launcher)) return LaunchRoute.Launcher;

            return File.Exists(Path.Combine(install, LauncherRoute.LibraryName)) ? LaunchRoute.Dotnet : LaunchRoute.None;
        }

        private static void StartApp(Plan plan, UpdateResult result)
        {
            result.Launcher = LauncherState(plan.Install);

            if (!plan.Start)
            {
                result.Start = UpdateResult.StartSkipped;
                TryWrite(plan.Work, result);
                return;
            }

            var route = ChooseStart(plan.Install);
            result.Start = route == LaunchRoute.Launcher ? UpdateResult.StartLauncher
                : route == LaunchRoute.Dotnet ? UpdateResult.StartDotnet
                : UpdateResult.StartNone;
            if (route == LaunchRoute.None) result.StartProblem = UpdateResult.ProblemNothing;

            // Written BEFORE the start, because the app that starts reads it on its way up. Only a
            // start that went wrong writes it again, and that app is not running to have read it.
            TryWrite(plan.Work, result);
            if (route == LaunchRoute.None) return;

            if (route == LaunchRoute.Launcher)
            {
                if (StartThroughLauncher(plan)) return;

                result.Launcher = LauncherState(plan.Install);
                result.Start = UpdateResult.StartDotnet;
            }

            StartThroughDotnet(plan, result);
            TryWrite(plan.Work, result);
        }

        private static string LauncherState(string install)
        {
            var launcher = Path.Combine(install, LauncherRoute.LauncherName);
            if (!File.Exists(launcher)) return UpdateResult.LauncherMissing;
            return LauncherRoute.IsFrozenLauncher(launcher) ? UpdateResult.LauncherFrozen : UpdateResult.LauncherOther;
        }

        /// <summary>
        /// The launcher, watched for a moment. False when it would not start or was taken out from
        /// under the process it started, which is when the start goes through dotnet instead.
        /// </summary>
        private static bool StartThroughLauncher(Plan plan)
        {
            var install = plan.Install;
            var launcher = Path.Combine(install, LauncherRoute.LauncherName);
            try
            {
                using var started = Process.Start(new ProcessStartInfo
                {
                    FileName = launcher,
                    WorkingDirectory = install,
                    UseShellExecute = false,
                    CreateNoWindow = !plan.Window,
                });
                if (started == null) return false;

                var until = DateTime.UtcNow + LauncherWatch;
                while (DateTime.UtcNow < until)
                {
                    if (!File.Exists(launcher)) return false;
                    if (started.WaitForExit(250))
                    {
                        Thread.Sleep(1000);
                        return File.Exists(launcher);
                    }
                }

                return File.Exists(launcher);
            }
            catch
            {
                return false;
            }
        }

        private static void StartThroughDotnet(Plan plan, UpdateResult result)
        {
            var install = plan.Install;
            var dotnet = FindDotnet();
            if (dotnet == null)
            {
                result.StartProblem = UpdateResult.ProblemNoDotnet;
                return;
            }

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = dotnet,
                    WorkingDirectory = install,
                    UseShellExecute = false,
                    // A run told to show nothing starts nothing with a console either. The app's own
                    // start never sets this: the console dotnet opens is let go by Program.Main.
                    CreateNoWindow = !plan.Window,
                };
                psi.ArgumentList.Add(Path.Combine(install, LauncherRoute.LibraryName));

                using var started = Process.Start(psi);
                if (started == null)
                {
                    result.StartProblem = UpdateResult.ProblemFailed;
                    return;
                }

                if (started.WaitForExit((int)DotnetWatch.TotalMilliseconds) && started.ExitCode != 0)
                {
                    result.StartProblem = UpdateResult.ProblemEnded;
                    result.StartDetail = started.ExitCode.ToString(CultureInfo.InvariantCulture);
                }
            }
            catch (Exception e)
            {
                result.StartProblem = UpdateResult.ProblemFailed;
                result.StartDetail = OneLine(e.Message);
            }
        }

        /// <summary>
        /// The dotnet host, or null. The one running this process first, when it is one; then the
        /// one that owns the runtime this code is running on, which is the install a framework
        /// dependent app is started by; then DOTNET_ROOT, Program Files and the path. The 64 bit
        /// Program Files copy comes before the path because the release is the x64 build and a 32
        /// bit dotnet earlier on the path would run it as a 32 bit process.
        /// </summary>
        public static string FindDotnet()
        {
            foreach (var candidate in DotnetCandidates())
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate)) return Path.GetFullPath(candidate);
                }
                catch
                {
                    // An odd entry on the path is not the end of the search.
                }
            }

            return null;
        }

        private static IEnumerable<string> DotnetCandidates()
        {
            var host = LauncherRoute.DotnetHostName;

            var self = Environment.ProcessPath;
            if (LauncherRoute.IsDotnetHost(self)) yield return self;

            // <root>\shared\Microsoft.NETCore.App\<version>\System.Private.CoreLib.dll
            var runtime = SafeDirectory(typeof(object).Assembly.Location);
            var root = SafeDirectory(SafeDirectory(SafeDirectory(runtime)));
            if (root != null) yield return Path.Combine(root, host);

            var dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
            if (!string.IsNullOrWhiteSpace(dotnetRoot)) yield return Path.Combine(dotnetRoot.Trim().Trim('"'), host);

            foreach (var programFiles in new[] { Environment.GetEnvironmentVariable("ProgramW6432"), Environment.GetEnvironmentVariable("ProgramFiles") })
            {
                if (!string.IsNullOrWhiteSpace(programFiles)) yield return Path.Combine(programFiles, "dotnet", host);
            }

            var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (var folder in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                yield return Path.Combine(folder.Trim().Trim('"'), host);
            }
        }

        private static string SafeDirectory(string path)
        {
            try
            {
                return string.IsNullOrWhiteSpace(path) ? null : Path.GetDirectoryName(path);
            }
            catch
            {
                return null;
            }
        }

        // ------------------------------------------------------------------ RESULT.txt

        private static void TryWrite(string work, UpdateResult result)
        {
            try
            {
                Directory.CreateDirectory(work);
                File.WriteAllText(Path.Combine(work, ResultFileName), result.ToText(), new UTF8Encoding(false));
            }
            catch
            {
                // Nowhere to write it is no reason to leave the host without their BakaLoader.
            }
        }

        /// <summary>
        /// What the last update wrote down, read once and taken away so the same lines are never
        /// said twice. Null when there is nothing.
        /// </summary>
        public static UpdateResult ReadAndClearResult(string work)
        {
            try
            {
                var path = Path.Combine(work, ResultFileName);
                if (!File.Exists(path)) return null;

                var text = File.ReadAllText(path);
                try { File.Delete(path); } catch { /* read once is what matters */ }
                return UpdateResult.Parse(text);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// The Saga lines for what the last update wrote down, in the order they happened, each
        /// with whether it is a warning. From the host catalog, with the version this start is
        /// running filled in.
        /// </summary>
        public static IReadOnlyList<(bool Warning, string Line)> SagaLines(UpdateResult result, string version)
        {
            var lines = new List<(bool, string)>();
            if (result == null) return lines;

            var script = LauncherRoute.ScriptName;
            var launcher = LauncherRoute.LauncherName;

            switch (result.Outcome)
            {
                case UpdateResult.Updated:
                    lines.Add((false, HostCatalog.T("host.saga.update.updated", ("version", version))));
                    break;
                case UpdateResult.RolledBack:
                    var file = string.IsNullOrWhiteSpace(result.File) ? LauncherRoute.LibraryName : result.File;
                    if (result.Unrestored > 0)
                    {
                        // Never "the files were put back" when some were not: the softer line, then
                        // how many and the folder the only copy of them is kept in.
                        lines.Add((true, HostCatalog.T("host.saga.update.rolled_back_partly",
                            ("file", file), ("detail", result.Detail ?? string.Empty), ("version", version))));
                        lines.Add((true, HostCatalog.T("host.saga.update.restore_incomplete",
                            ("count", result.Unrestored),
                            ("folder", string.IsNullOrWhiteSpace(result.Kept) ? RollbackFolderName : result.Kept))));
                    }
                    else
                    {
                        lines.Add((true, HostCatalog.T("host.saga.update.rolled_back",
                            ("file", file), ("detail", result.Detail ?? string.Empty), ("version", version))));
                    }
                    break;
                case UpdateResult.StillRunning:
                    lines.Add((true, HostCatalog.T("host.saga.update.still_running", ("minutes", result.WaitedMinutes))));
                    break;
                case UpdateResult.NothingStaged:
                    lines.Add((true, HostCatalog.T("host.saga.update.nothing_staged", ("version", version))));
                    break;
            }

            if (result.LauncherDownload == UpdateResult.LauncherHeld)
                lines.Add((true, HostCatalog.T("host.saga.update.launcher_held",
                    ("launcher", launcher), ("detail", result.LauncherDetail ?? string.Empty))));
            else if (result.LauncherDownload == UpdateResult.LauncherUnwritable)
                lines.Add((true, HostCatalog.T("host.saga.update.launcher_unwritable",
                    ("launcher", launcher), ("detail", result.LauncherDetail ?? string.Empty))));

            if (result.Start == UpdateResult.StartDotnet && result.Launcher == UpdateResult.LauncherMissing)
                lines.Add((true, HostCatalog.T("host.saga.update.launcher_missing", ("launcher", launcher), ("script", script))));

            switch (result.StartProblem)
            {
                case UpdateResult.ProblemNoDotnet:
                    lines.Add((true, HostCatalog.T("host.saga.update.start_no_dotnet", ("script", script))));
                    break;
                case UpdateResult.ProblemEnded:
                    int.TryParse(result.StartDetail, NumberStyles.Integer, CultureInfo.InvariantCulture, out var code);
                    lines.Add((true, HostCatalog.T("host.saga.update.start_ended", ("code", code), ("script", script))));
                    break;
                case UpdateResult.ProblemFailed:
                    lines.Add((true, HostCatalog.T("host.saga.update.start_failed",
                        ("detail", result.StartDetail ?? string.Empty), ("script", script))));
                    break;
                case UpdateResult.ProblemNothing:
                    lines.Add((true, HostCatalog.T("host.saga.update.start_nothing",
                        ("launcher", launcher), ("library", LauncherRoute.LibraryName))));
                    break;
            }

            return lines;
        }

        /// <summary>
        /// What an update leaves in the install that the next one does not need: the files it set
        /// aside, except a run marked with <see cref="KeptMarkerName"/>, whose files could not all be
        /// put back and are the only copy of them. Best effort, because a library the updater still had loaded when the app started
        /// cannot be deleted until that updater has gone; whatever is left goes next time.
        /// </summary>
        public static void CleanLeftovers(string install)
        {
            if (string.IsNullOrWhiteSpace(install)) return;

            var root = Path.Combine(install, RollbackFolderName);
            try
            {
                if (!Directory.Exists(root)) return;

                // One folder per run. A run whose restore was not complete is marked and kept,
                // because its files are the only copy of what was there before that update.
                foreach (var run in Directory.EnumerateDirectories(root).ToList())
                {
                    if (!IsKept(run)) TryDeleteFolder(run);
                }

                foreach (var file in Directory.EnumerateFiles(root).ToList())
                {
                    try { ClearReadOnly(file); File.Delete(file); } catch { /* next time */ }
                }

                if (!Directory.EnumerateFileSystemEntries(root).Any()) Directory.Delete(root);
            }
            catch
            {
                // Left for the next run.
            }
        }

        internal static void TryDeleteFolder(string folder)
        {
            try
            {
                if (!Directory.Exists(folder)) return;
                foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
                {
                    ClearReadOnly(file);
                }

                Directory.Delete(folder, recursive: true);
            }
            catch
            {
                // Then file by file, so one file that is still held leaves only itself behind.
                try
                {
                    foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
                    {
                        try { File.Delete(file); } catch { /* next time */ }
                    }

                    foreach (var dir in Directory.EnumerateDirectories(folder, "*", SearchOption.AllDirectories)
                        .OrderByDescending(d => d.Length))
                    {
                        try { Directory.Delete(dir); } catch { /* not empty yet */ }
                    }

                    Directory.Delete(folder);
                }
                catch
                {
                    // Left for the next run.
                }
            }
        }

        internal static string OneLine(string text) =>
            string.IsNullOrWhiteSpace(text)
                ? string.Empty
                : string.Join(" ", text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)).Trim();

        internal static string Sha256Of(byte[] bytes)
        {
            using var sha = SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(bytes));
        }

        // ------------------------------------------------------------------ the window

        /// <summary>
        /// The updater's window: a title and one line of status, the size of a small dialog, in the
        /// middle of the screen and in the taskbar. It cannot be closed by hand while files are
        /// being copied, because a copy stopped half way is the thing the whole updater exists to
        /// avoid; it closes itself when the work is done.
        /// </summary>
        internal sealed class UpdaterWindow : Form
        {
            private readonly Label Status;
            private volatile bool Done;

            public UpdaterWindow()
            {
                Text = HostCatalog.T("host.updater.title");
                FormBorderStyle = FormBorderStyle.FixedDialog;
                MaximizeBox = false;
                MinimizeBox = true;
                ShowInTaskbar = true;
                StartPosition = FormStartPosition.CenterScreen;
                AutoScaleMode = AutoScaleMode.Dpi;
                ClientSize = new Size(380, 64);

                Status = new Label
                {
                    Dock = DockStyle.Fill,
                    Padding = new Padding(14, 0, 14, 0),
                    TextAlign = ContentAlignment.MiddleLeft,
                    AutoEllipsis = true,
                    Text = HostCatalog.T("host.updater.waiting"),
                };
                Controls.Add(Status);

                FormClosing += (_, e) =>
                {
                    if (!Done && e.CloseReason == CloseReason.UserClosing) e.Cancel = true;
                };
            }

            public void Say(string line)
            {
                try
                {
                    if (IsHandleCreated && !IsDisposed) BeginInvoke(new Action(() => Status.Text = line));
                }
                catch
                {
                    // A window going away is not a reason to stop copying.
                }
            }

            public void Finish()
            {
                Done = true;
                try
                {
                    if (IsHandleCreated && !IsDisposed) BeginInvoke(new Action(Close));
                }
                catch
                {
                    // Already closed.
                }
            }
        }
    }

    /// <summary>
    /// What one run of the updater did, as it writes it into RESULT.txt: one <c>name: value</c>
    /// line each, readable by a host who opens the file and by the next start, which turns it into
    /// Saga lines and deletes it. Values are short words; the two detail fields carry the reason
    /// Windows gave, on one line.
    /// </summary>
    public sealed class UpdateResult
    {
        public const string Updated = "updated";
        public const string RolledBack = "rolled_back";
        public const string StillRunning = "still_running";
        public const string NothingStaged = "nothing_staged";

        public const string LauncherNone = "none";
        public const string LauncherWritten = "written";
        public const string LauncherKept = "kept";
        public const string LauncherHeld = "held";
        public const string LauncherUnwritable = "unwritable";

        public const string LauncherFrozen = "frozen";
        public const string LauncherOther = "other";
        public const string LauncherMissing = "missing";

        public const string StartLauncher = "launcher";
        public const string StartDotnet = "dotnet";
        public const string StartNone = "none";
        public const string StartSkipped = "skipped";

        public const string ProblemNoDotnet = "no_dotnet";
        public const string ProblemEnded = "ended";
        public const string ProblemFailed = "failed";
        public const string ProblemNothing = "nothing_to_start";

        /// <summary>updated, rolled_back, still_running or nothing_staged.</summary>
        public string Outcome { get; set; } = RolledBack;

        /// <summary>The file a failed copy stopped on, relative to the install.</summary>
        public string File { get; set; }

        /// <summary>The reason Windows gave for the failure, on one line.</summary>
        public string Detail { get; set; }

        /// <summary>How many files a rollback could not put back.</summary>
        public int Unrestored { get; set; }

        /// <summary>
        /// The folder the files a rollback could not put back are kept in, the only copy of them,
        /// when there are any.
        /// </summary>
        public string Kept { get; set; }

        /// <summary>How long the updater waited for the app to close, when it would not.</summary>
        public int WaitedMinutes { get; set; }

        /// <summary>What became of the launcher in the download.</summary>
        public string LauncherDownload { get; set; } = LauncherNone;

        /// <summary>Why the launcher in the download was let go, on one line.</summary>
        public string LauncherDetail { get; set; }

        /// <summary>The launcher in the install once the copy was done: frozen, other or missing.</summary>
        public string Launcher { get; set; } = string.Empty;

        /// <summary>How the app was started afterwards.</summary>
        public string Start { get; set; } = StartNone;

        /// <summary>What went wrong with that start, when anything did.</summary>
        public string StartProblem { get; set; } = string.Empty;

        /// <summary>The exit code or the reason for that, on one line.</summary>
        public string StartDetail { get; set; }

        /// <summary>When the updater wrote this, in UTC.</summary>
        public DateTime Finished { get; set; } = DateTime.UtcNow;

        /// <summary>0 for an update that went in, 1 for every other ending.</summary>
        public int ExitCode => Outcome == Updated ? 0 : 1;

        private static readonly string[] Order =
        {
            "result", "file", "detail", "unrestored", "kept", "waited_minutes", "launcher_download",
            "launcher_detail", "launcher", "start", "start_problem", "start_detail", "finished",
        };

        public string ToText()
        {
            var values = new Dictionary<string, string>
            {
                ["result"] = Outcome,
                ["file"] = File,
                ["detail"] = Detail,
                ["unrestored"] = Unrestored.ToString(CultureInfo.InvariantCulture),
                ["kept"] = Kept,
                ["waited_minutes"] = WaitedMinutes.ToString(CultureInfo.InvariantCulture),
                ["launcher_download"] = LauncherDownload,
                ["launcher_detail"] = LauncherDetail,
                ["launcher"] = Launcher,
                ["start"] = Start,
                ["start_problem"] = StartProblem,
                ["start_detail"] = StartDetail,
                ["finished"] = Finished.ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            };

            var text = new StringBuilder();
            text.Append("# BakaLoader update result. BakaLoader reads this on its next start, writes it to the Saga and deletes it.\r\n");
            foreach (var name in Order)
            {
                text.Append(name).Append(": ").Append(ManagedUpdater.OneLine(values[name])).Append("\r\n");
            }

            return text.ToString();
        }

        public static UpdateResult Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;

            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
                var colon = line.IndexOf(':');
                if (colon <= 0) continue;
                values[line.Substring(0, colon).Trim()] = line.Substring(colon + 1).Trim();
            }

            if (!values.TryGetValue("result", out var outcome) || string.IsNullOrWhiteSpace(outcome)) return null;

            string Get(string name) => values.TryGetValue(name, out var v) && v.Length > 0 ? v : null;
            int Number(string name) =>
                int.TryParse(Get(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;

            return new UpdateResult
            {
                Outcome = outcome,
                File = Get("file"),
                Detail = Get("detail"),
                Unrestored = Number("unrestored"),
                Kept = Get("kept"),
                WaitedMinutes = Number("waited_minutes"),
                LauncherDownload = Get("launcher_download") ?? LauncherNone,
                LauncherDetail = Get("launcher_detail"),
                Launcher = Get("launcher") ?? string.Empty,
                Start = Get("start") ?? StartNone,
                StartProblem = Get("start_problem") ?? string.Empty,
                StartDetail = Get("start_detail"),
                Finished = DateTime.TryParse(Get("finished"), CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var when) ? when : DateTime.MinValue,
            };
        }
    }
}
