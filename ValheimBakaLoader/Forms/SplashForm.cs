using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using ValheimBakaLoader.Game;
using ValheimBakaLoader.Properties;
using ValheimBakaLoader.Tools;
using ValheimBakaLoader.Tools.Logging;
using ValheimBakaLoader.Tools.Theming;

namespace ValheimBakaLoader.Forms
{
    /// <summary>
    /// The small progress window shown at launch, and also the application's main
    /// form: it decides which server-profile windows to open (every auto-start
    /// profile, else the most recently saved one), runs the launch work in
    /// parallel with a progress bar, then keeps running hidden so the process
    /// exits only after every profile window has closed.
    /// </summary>
    public partial class SplashForm : Form
    {
        /// <summary>One named unit of launch work, timed and logged when it runs.</summary>
        private sealed record LaunchStep(string Name, Func<Task> Run);

        private readonly List<LaunchStep> LaunchSteps = new();
        private int FinishedSteps;

        // Raised (marshaled onto the UI thread) each time one launch step ends.
        private event EventHandler<Task> StepCompleted;

        // Slots hold BlendWindow instances; a closed window leaves a null slot so
        // the SplashIndex of the remaining windows stays valid.
        private readonly List<Form> MainWindows = new();

        private bool HasShownOnce;
        private bool QuitWhenExceptionDismissed;

        // Set when the launch-time self-update check staged a newer version; the app
        // closes instead of opening any windows so the watchdog can swap files safely
        // (no server has been auto-started/adopted yet, so nothing gets killed).
        private volatile bool AppUpdateStaged;

        /// <summary>
        /// Set the moment the main windows go up. Read by the self-update step, which is allowed
        /// to be given up on: once a window is open, staging an update is no longer a safe thing
        /// to arm, because the swap works by closing the app and a window that is open may be
        /// holding a live server through the Job Object.
        /// </summary>
        private volatile bool WindowsOpened;

        // Set when a self-update staged while the app was already open has asked every window
        // to close. The watchdog starts writing over the install whether this process has gone
        // or not, so from that point the exit cannot rest on this form happening to be the one
        // that ends the message loop.
        private volatile bool SelfUpdateExitRequested;

        private readonly IFormProvider FormProvider;
        private readonly IIpAddressProvider IpAddressProvider;
        private readonly ISoftwareUpdateProvider SoftwareUpdateProvider;
        private readonly IAppUpdateService AppUpdateService;
        private readonly IExceptionHandler ExceptionHandler;
        private readonly IUserPreferencesProvider UserPrefsProvider;
        private readonly IServerPreferencesProvider ServerPrefsProvider;
        private readonly IPlayerDataRepository PlayerDataRepository;
        private readonly IStartupArgsProvider StartupArgs;
        private readonly ILanguagePackService LanguagePacks;
        private readonly IApplicationLogger Logger;

        public SplashForm(
            IFormProvider formProvider,
            IIpAddressProvider ipAddressProvider,
            ISoftwareUpdateProvider softwareUpdateProvider,
            IAppUpdateService appUpdateService,
            IExceptionHandler exceptionHandler,
            IUserPreferencesProvider userPrefsProvider,
            IServerPreferencesProvider serverPrefsProvider,
            IPlayerDataRepository playerDataRepository,
            IStartupArgsProvider startupArgs,
            ILanguagePackService languagePacks,
            IApplicationLogger logger)
        {
            FormProvider = formProvider;
            IpAddressProvider = ipAddressProvider;
            SoftwareUpdateProvider = softwareUpdateProvider;
            AppUpdateService = appUpdateService;
            ExceptionHandler = exceptionHandler;
            UserPrefsProvider = userPrefsProvider;
            ServerPrefsProvider = serverPrefsProvider;
            PlayerDataRepository = playerDataRepository;
            StartupArgs = startupArgs;
            LanguagePacks = languagePacks;
            Logger = logger;

            // Catch everything from here on; a crash before the main window exists
            // must still surface a crash report instead of dying silently.
            Application.ThreadException += OnUiThreadException;
            AppDomain.CurrentDomain.UnhandledException += OnUnhandledDomainException;

            try
            {
                InitializeComponent();

                // Load the persisted dark-mode preference first so every window created
                // after the splash screen picks up the correct theme.
                ThemeManager.LoadFromPreferences(UserPrefsProvider);
                ThemeManager.Apply(this);
                this.AddApplicationIcon();

                AppNameLabel.Text = $"ValheimBakaLoader v{AssemblyHelper.GetApplicationVersion()}";
                Shown += this.BuildEventHandler(OnSplashShown);
                ExceptionHandler.ExceptionHandled += this.BuildEventHandler(OnExceptionDismissed);
            }
            catch (Exception e)
            {
                ReportException(e, "Startup Init Exception", quitAfter: true);
            }
        }

        #region Window management

        /// <summary>
        /// Creates (but does not show) a main window bound to the given server
        /// profile. The splash form stays alive in the background and shuts the
        /// application down once the last such window closes.
        /// </summary>
        public Form CreateNewMainWindow(string startProfile, bool startServer)
        {
            var window = FormProvider.GetForm<BlendWindow>();
            var contract = (IMainAppWindow)window;

            window.FormClosed += OnMainWindowClosed;
            contract.StartProfile = startProfile;
            contract.StartServerAutomatically = startServer;
            contract.SplashIndex = MainWindows.Count;
            MainWindows.Add(window);

            Logger.Debug("Created {name} [{index}] for profile {name}",
                window.GetType().Name, contract.SplashIndex, startProfile);
            return window;
        }

        /// <summary>
        /// Opens one window per auto-start profile; without any, the most recently
        /// saved profile; without any of those either (first launch), a brand-new
        /// default profile.
        /// </summary>
        private void ChooseWindowsToOpen()
        {
            var profiles = ServerPrefsProvider.LoadPreferences();

            var autoStart = profiles.Where(p => p != null && p.AutoStart).ToList();
            if (autoStart.Count > 0)
            {
                Logger.Information("Loading server profiles with auto-start enabled");
                autoStart.ForEach(p => CreateNewMainWindow(p.ProfileName, true));
                return;
            }

            var newest = profiles.OrderByDescending(p => p.LastSaved).FirstOrDefault();
            if (newest != null)
            {
                Logger.Information("Loading most recently saved profile: {name}", newest.ProfileName);
                CreateNewMainWindow(newest.ProfileName, false);
                return;
            }

            Logger.Information("User preferences not found, creating new file");
            var fresh = new ServerPreferences { ProfileName = Resources.DefaultServerProfileName };
            ServerPrefsProvider.SavePreferences(fresh);
            CreateNewMainWindow(fresh.ProfileName, false);
        }

        /// <summary>
        /// A newer BakaLoader has been staged and every window has been asked to close, so this
        /// process is meant to end. Said here because this form is the one that outlives the
        /// windows: it is the application's main form, it stays alive hidden behind them, and it
        /// is where "the last one has gone" is noticed.
        /// </summary>
        public void RequestExitForSelfUpdate()
        {
            SelfUpdateExitRequested = true;
            Logger.Information("A staged BakaLoader update asked the application to close.");
        }

        private void OnMainWindowClosed(object sender, FormClosedEventArgs e)
        {
            if (InvokeRequired)
            {
                BeginInvoke(() => OnMainWindowClosed(sender, e));
                return;
            }

            if (sender is not Form window || window is not IMainAppWindow contract) return;
            Logger.Debug("Closed {name} [{index}]", window.GetType().Name, contract.SplashIndex);

            // Null the slot rather than removing it, so other windows' indexes hold.
            MainWindows[contract.SplashIndex] = null;

            var remaining = MainWindows.Count(w => w != null);
            if (remaining > 0)
            {
                Logger.Debug("{stillOpen} windows are still open", remaining);
                return;
            }

            Logger.Debug("All windows closed, shutting down application");
            Close();

            // Closing this form ends the message loop, because it is the form the application
            // was run on. A staged update gets the explicit end as well: the watchdog is already
            // counting down to writing over the install, so "the loop probably ends here" is not
            // a good enough answer for the one case where the process staying up corrupts it.
            if (SelfUpdateExitRequested)
            {
                Logger.Information("The staged update closed every window; ending the application.");
                Application.Exit();
            }
        }

        #endregion

        #region Launch sequence

        private void OnSplashShown()
        {
            if (HasShownOnce) return;
            HasShownOnce = true;

            // WinForms hasn't always finished painting by the first Shown event
            // (labels can render as white boxes), so force a repaint.
            Refresh();

            try
            {
                if (!RuntimeVersionIsSupported())
                {
                    Close();
                    return;
                }

                ChooseWindowsToOpen();
                QueueLaunchSteps();
                RunLaunchSteps();
            }
            catch (Exception ex)
            {
                ReportException(ex, "Startup Run Exception", quitAfter: true);
            }
        }

        /// <summary>
        /// Every step carries a clock, because the window only opens once all of them have
        /// finished. Two of these three go out to the internet, and a site that answers its
        /// headers and then stops sending used to hold the splash open for the life of the
        /// process. A step that runs over writes one line and the window opens without it.
        /// </summary>
        private void AddLaunchStep(string name, Func<Task> work, TimeSpan? budget = null)
        {
            LaunchSteps.Add(new LaunchStep(name, async () =>
            {
                Logger.Debug("Starting startup task: {name}", name);
                var timer = Stopwatch.StartNew();

                var finished = await LaunchBudget.WithinAsync(
                    work,
                    budget ?? LaunchBudget.PerStep,
                    line => Logger.Warning("{line}", line),
                    "The startup step \"" + name + "\"");

                Logger.Debug("Finished startup task: {name} ({dur}ms, {state})",
                    name, timer.ElapsedMilliseconds, finished ? "completed" : "given up on");
            }));
        }

        private void QueueLaunchSteps()
        {
            StepCompleted += this.BuildEventHandler<Task>(OnStepCompleted);

            // What the last update attempt left behind, if it left anything. A watchdog that
            // could not write the new files puts the old ones back and writes down why, and
            // this is the launch that reads it.
            ReportLastUpdateAttempt();

            AddLaunchStep("Check for updates", () => SoftwareUpdateProvider.CheckForUpdatesAsync(false));
            AddLaunchStep("Load player data", PlayerDataRepository.LoadAsync);
            // With a clock of its own, because this one goes and fetches four megabytes and the
            // 45 second default is shorter than the download's own deadlines. See
            // SelfUpdateStepBudget.
            AddLaunchStep("Self-update check", CheckForAppSelfUpdateAsync,
                SelfUpdateStepBudget(AppUpdateService.StageBudget));
        }

        /// <summary>
        /// Reads and clears the note the update watchdog leaves when it could not write the
        /// new files. Never throws: a report that cannot be read is not a reason to fail a
        /// launch.
        /// </summary>
        private void ReportLastUpdateAttempt()
        {
            try
            {
                var note = Tools.AppUpdateService.ReadAndClearUpdateReport();
                if (!string.IsNullOrWhiteSpace(note)) Logger.Warning("{note}", note);
            }
            catch (Exception e)
            {
                Logger.Debug("Could not read the last update report: {0}", e.Message);
            }
        }

        private void RunLaunchSteps()
        {
            if (LaunchSteps.Count == 0)
            {
                OpenMainWindows();
                return;
            }

            foreach (var step in LaunchSteps)
            {
                Task.Run(() => step.Run().ContinueWith(t =>
                {
                    StepCompleted?.Invoke(this, t);
                    return Task.CompletedTask;
                }));
            }
        }

        private void OnStepCompleted(Task task)
        {
            if (task is { IsCompletedSuccessfully: false })
            {
                Logger.Warning("Error encountered during startup task");
                ReportException(task.Exception, "Startup Task Exception", quitAfter: true);
                return;
            }

            FinishedSteps++;
            ProgressBar.Value = FinishedSteps * 100 / LaunchSteps.Count;

            if (FinishedSteps >= LaunchSteps.Count)
            {
                OpenMainWindows();
            }
        }

        /// <summary>
        /// How long the self-update step is allowed to hold the splash: never less than the
        /// staging it wraps is allowed to take, and never less than the ordinary per-step clock.
        /// <para>
        /// It used to get the plain 45 seconds while the download inside it was allowed a 30
        /// second header wait and ten minutes of body, so on any link slower than about 90 KB a
        /// second the step was given up on EVERY time: the window opened with nothing staged and
        /// the abandoned work carried on and armed the watchdog behind it, which then waited two
        /// minutes for a PID that was not going anywhere. The update never installed, every
        /// launch fetched the whole zip again, and each one left a stray hidden process behind.
        /// </para>
        /// <para>
        /// So this one number is larger than the 45 seconds the other steps get, and the splash
        /// can be held while a new BakaLoader is genuinely coming down. That is the point: the
        /// alternative is a host on a slow link who never receives an update at all. A site that
        /// STALLS does not reach this ceiling, because the download's own header deadline and
        /// idle watchdog end a read that has stopped sending inside a minute.
        /// </para>
        /// </summary>
        internal static TimeSpan SelfUpdateStepBudget(TimeSpan stageBudget) =>
            stageBudget > LaunchBudget.PerStep ? stageBudget : LaunchBudget.PerStep;

        /// <summary>
        /// Launch-time self-update: when the AutoUpdateBakaLoader pref is on, check GitHub
        /// for a newer release and stage it. Meant to finish during the splash phase, BEFORE any
        /// main window is shown, so that no server has been auto-started or adopted yet and
        /// closing the app to let the watchdog swap files cannot kill a live server via the Job
        /// Object.
        /// <para>
        /// Meant to, and not able to promise it on its own: this step carries a clock like every
        /// other, and a step that is given up on keeps running. So the promise is kept by the
        /// answer handed down to the service instead. It is asked before the download and again
        /// before the watchdog is armed, and it says no the moment the windows are up: staging
        /// then refuses itself and the update installs at the next launch.
        /// </para>
        /// </summary>
        private async Task CheckForAppSelfUpdateAsync()
        {
            if (!UserPrefsProvider.LoadPreferences().AutoUpdateBakaLoader) return;

            if (await AppUpdateService.CheckAndStageUpdateAsync(stillWanted: () => !WindowsOpened))
            {
                AppUpdateStaged = true;
            }
        }

        /// <summary>All launch steps are done: show the profile windows and duck out of sight.</summary>
        private void OpenMainWindows()
        {
            // First line of it, and before the staged flag is read: from here on the self-update
            // step may no longer arm a swap, because the swap works by closing the app and what
            // is about to be shown may hold a live server through the Job Object. A step that
            // ran over is still running while this line executes, so the answer it is given has
            // to change as early as possible.
            WindowsOpened = true;

            if (AppUpdateStaged)
            {
                // A newer BakaLoader was staged during the splash phase; close now (before
                // any main window opens or any server auto-starts) so the headless watchdog
                // can replace the files and relaunch the updated app.
                Logger.Information("Self-update staged at launch; closing to install the new version.");
                Close();
                return;
            }

            // From here the app is open for as long as the host leaves it open, which on a server
            // box is weeks. The launch check alone would mean a release that ships tomorrow is
            // never mentioned, so the quiet re-check starts now and runs on its own throttle.
            SoftwareUpdateProvider.StartPeriodicChecks();

            var startMinimized = UserPrefsProvider.LoadPreferences().StartMinimized;
            foreach (var window in MainWindows)
            {
                window.Show();
                if (startMinimized) window.WindowState = FormWindowState.Minimized;
            }

            // The language folder's housekeeping: the staging folder emptied, the versions
            // nobody is reading dropped, and the faces no remaining pack names taken out of
            // the shared store. It runs AFTER the windows are up and on a worker of its own,
            // never as a launch step: it is deleting folders a host is not waiting for, and a
            // launch step is a thing the window opens behind. The service stands aside on its
            // own if a pack is being fetched, so it cannot sweep a download's working folder.
            _ = Task.Run(() =>
            {
                try
                {
                    var removed = LanguagePacks.PruneOnBoot();
                    if (removed > 0) Logger.Information("Swept {removed} stale language files", removed);
                }
                catch (Exception e)
                {
                    Logger.Warning(e, "The language folder could not be swept");
                }
            });

            // The splash form must stay alive (it is the application main form),
            // so it hides instead of closing.
            Hide();
        }

        private bool RuntimeVersionIsSupported()
        {
            var runtime = AssemblyHelper.GetDotnetRuntimeVersion();
            if (runtime.Major >= 6) return true;

            Logger.Warning("Incompatible .NET version detected: {dotnetVersion}", runtime);

            var nl = Environment.NewLine;
            var choice = MessageBox.Show(
                $"ValheimBakaLoader needs the .NET 6.0 Desktop Runtime or newer.{nl}" +
                $"This machine is running .NET {runtime}.{nl}{nl}" +
                "Open the download page now?",
                ".NET Upgrade Required",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);

            if (choice == DialogResult.Yes)
            {
                OpenHelper.OpenWebAddress(Resources.UrlDotnetDownload);
            }

            return false;
        }

        #endregion

        #region Exception plumbing

        private void ReportException(Exception exception, string contextMessage, bool quitAfter)
        {
            Logger.Error("Encountered exception - {typeName}: {message}",
                exception.GetType().Name, exception.Message);
            QuitWhenExceptionDismissed = quitAfter;
            ExceptionHandler.HandleException(exception, contextMessage);
        }

        // Only quit for exceptions that happen before any main window is visible;
        // once the app is up, a fault shouldn't take down a running server manager.
        private bool NoWindowVisible => !MainWindows.Any(w => w?.Visible == true);

        private void OnUnhandledDomainException(object sender, UnhandledExceptionEventArgs e)
            => ReportException(e.ExceptionObject as Exception, "Unhandled Exception", NoWindowVisible);

        private void OnUiThreadException(object sender, System.Threading.ThreadExceptionEventArgs e)
            => ReportException(e.Exception, "Thread Exception", NoWindowVisible);

        private void OnExceptionDismissed()
        {
            if (QuitWhenExceptionDismissed) Close();
        }

        #endregion
    }
}
