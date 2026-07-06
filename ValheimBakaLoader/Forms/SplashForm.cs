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

        private readonly IFormProvider FormProvider;
        private readonly IIpAddressProvider IpAddressProvider;
        private readonly ISoftwareUpdateProvider SoftwareUpdateProvider;
        private readonly IAppUpdateService AppUpdateService;
        private readonly IExceptionHandler ExceptionHandler;
        private readonly IUserPreferencesProvider UserPrefsProvider;
        private readonly IServerPreferencesProvider ServerPrefsProvider;
        private readonly IPlayerDataRepository PlayerDataRepository;
        private readonly IStartupArgsProvider StartupArgs;
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

        private void AddLaunchStep(string name, Func<Task> work)
        {
            LaunchSteps.Add(new LaunchStep(name, async () =>
            {
                Logger.Debug("Starting startup task: {name}", name);
                var timer = Stopwatch.StartNew();
                await work();
                Logger.Debug("Finished startup task: {name} ({dur}ms)", name, timer.ElapsedMilliseconds);
            }));
        }

        private void QueueLaunchSteps()
        {
            StepCompleted += this.BuildEventHandler<Task>(OnStepCompleted);

            AddLaunchStep("Check for updates", () => SoftwareUpdateProvider.CheckForUpdatesAsync(false));
            AddLaunchStep("Load player data", PlayerDataRepository.LoadAsync);
            AddLaunchStep("Self-update check", CheckForAppSelfUpdateAsync);
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
        /// Launch-time self-update: when the AutoUpdateBakaLoader pref is on, check GitHub
        /// for a newer release and stage it. Runs during the splash phase, BEFORE any main
        /// window is shown - so no server has been auto-started or adopted yet, and closing
        /// the app to let the watchdog swap files cannot kill a live server via the Job Object.
        /// </summary>
        private async Task CheckForAppSelfUpdateAsync()
        {
            if (!UserPrefsProvider.LoadPreferences().AutoUpdateBakaLoader) return;

            if (await AppUpdateService.CheckAndStageUpdateAsync())
            {
                AppUpdateStaged = true;
            }
        }

        /// <summary>All launch steps are done: show the profile windows and duck out of sight.</summary>
        private void OpenMainWindows()
        {
            if (AppUpdateStaged)
            {
                // A newer BakaLoader was staged during the splash phase; close now (before
                // any main window opens or any server auto-starts) so the headless watchdog
                // can replace the files and relaunch the updated app.
                Logger.Information("Self-update staged at launch; closing to install the new version.");
                Close();
                return;
            }

            var startMinimized = UserPrefsProvider.LoadPreferences().StartMinimized;
            foreach (var window in MainWindows)
            {
                window.Show();
                if (startMinimized) window.WindowState = FormWindowState.Minimized;
            }

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
