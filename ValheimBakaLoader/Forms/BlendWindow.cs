using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Serilog;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using ValheimBakaLoader.Game;
using ValheimBakaLoader.Tools;
using ValheimBakaLoader.Tools.Logging;

namespace ValheimBakaLoader.Forms
{
    /// <summary>
    /// The "Blend" UI shell - a borderless window hosting the HTML/CSS/JS interface
    /// (WebUI/ folder, visual spec: design-mockups/blend.html) inside WebView2.
    ///
    /// Message protocol (JSON over postMessage):
    ///   JS -> C# fire-and-forget:  { method: "win.minimize" }            (no id)
    ///   JS -> C# RPC:              { id: 1, method: "...", params: {} }
    ///   C# -> JS RPC reply:        { id: 1, ok: true, result: ... } | { id, ok: false, error }
    ///   C# -> JS event push:       { event: "...", data: ... }
    /// </summary>
    public partial class BlendWindow : Form, IMainAppWindow
    {
        private readonly ILogger Logger;

        private WebView2 WebView;
        private readonly Dictionary<string, Func<JObject, Task<object>>> RpcHandlers = new();
        private bool IsFirstShown = true;

        #region IMainAppWindow (SplashForm startup contract)

        public int SplashIndex { get; set; }

        /// <summary>The server profile to load when the window is first shown.</summary>
        public string StartProfile { get; set; }

        /// <summary>If true, start the server from StartProfile as soon as the window is shown.</summary>
        public bool StartServerAutomatically { get; set; }

        #endregion

        private const string VirtualHost = "app.baka";
        private const string AtlasVirtualHost = "atlas.baka";

        #region Window sizing (DPI aware)

        // The Blend layout is designed in CSS pixels, and WebView2 measures its viewport
        // in CSS pixels. A WinForms client area is physical pixels, so on a display at
        // 150% scaling a 1408x800 client area only hands the page 939x534 CSS px - below
        // the size the halls are laid out for. Every number below is therefore a
        // device-independent (96 dpi) design size that gets multiplied by the display's
        // scale before it reaches ClientSize/MinimumSize.
        private const int DesignWidth = 1408;
        private const int DesignHeight = 800;
        private const int DesignMinWidth = 1024;
        private const int DesignMinHeight = 680;

        // Breathing room so a scaled window never sits flush against the work area edges.
        private const int WorkAreaMargin = 24;

        /// <summary>Display scale for this window: 1.0 at 96 dpi, 1.5 at 150%.</summary>
        private float DpiScale => DeviceDpi <= 0 ? 1f : DeviceDpi / 96f;

        private static int ScaleUnits(int designPx, float scale) => Math.Max(1, (int)Math.Round(designPx * scale));

        private static int ClampInt(int value, int min, int max) => value < min ? min : (value > max ? max : value);

        #endregion

        /// <summary>Writable cache folder for rendered Atlas map PNGs, served via the atlas.baka virtual host.</summary>
        internal static string GetAtlasCacheDir()
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ValheimBakaLoader", "AtlasCache");
            Directory.CreateDirectory(dir);
            return dir;
        }

        #region Native interop (drag / resize for the borderless window)

        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        private const int WM_NCLBUTTONDOWN = 0x00A1;
        private const int HTCAPTION = 2;

        private static readonly Dictionary<string, int> ResizeHitTests = new()
        {
            ["left"] = 10,        // HTLEFT
            ["right"] = 11,       // HTRIGHT
            ["top"] = 12,         // HTTOP
            ["topleft"] = 13,     // HTTOPLEFT
            ["topright"] = 14,    // HTTOPRIGHT
            ["bottom"] = 15,      // HTBOTTOM
            ["bottomleft"] = 16,  // HTBOTTOMLEFT
            ["bottomright"] = 17, // HTBOTTOMRIGHT
        };

        #endregion

        public BlendWindow(
            ILogger logger,
            IServiceProvider serviceProvider,
            IUserPreferencesProvider userPrefsProvider,
            IServerPreferencesProvider serverPrefsProvider,
            IWorldPreferencesProvider worldPrefsProvider,
            IPlayerDataRepository playerDataProvider,
            IIpAddressProvider ipAddressProvider,
            IModScanner modScanner,
            IThunderstoreClient thunderstoreClient,
            IModUpdateService modUpdateService,
            IModRemovalService modRemovalService,
            IRequiredModChecker requiredModChecker,
            Game.ItemCatalog itemCatalog,
            PlayerListService playerListService,
            IAppUpdateService appUpdateService,
            IHeartbeatService heartbeatService,
            IApplicationLogger appLogger)
        {
            Logger = logger;

            InitializeShell();
            InitializeBridge(
                serviceProvider,
                userPrefsProvider,
                serverPrefsProvider,
                worldPrefsProvider,
                playerDataProvider,
                ipAddressProvider,
                modScanner,
                thunderstoreClient,
                modUpdateService,
                modRemovalService,
                requiredModChecker,
                itemCatalog,
                playerListService,
                appUpdateService,
                heartbeatService,
                appLogger);
        }

        private void InitializeShell()
        {
            SuspendLayout();

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            // Wide enough that the Hearth status row (incl. the stop box) fits without
            // clipping at the default size. These are the 96 dpi values; ApplyDpiSizing
            // multiplies them by the display scale once the handle exists (a form has no
            // DeviceDpi to read yet at this point).
            ClientSize = new Size(DesignWidth, DesignHeight);
            MinimumSize = new Size(DesignMinWidth, DesignMinHeight);
            BackColor = Color.FromArgb(0x0A, 0x0B, 0x0D); // matches the Blend backdrop while loading
            Text = Properties.Resources.ApplicationTitle;
            this.AddApplicationIcon();

            WebView = new WebView2
            {
                Dock = DockStyle.Fill,
                DefaultBackgroundColor = Color.FromArgb(0x0A, 0x0B, 0x0D),
            };
            Controls.Add(WebView);

            ResumeLayout(false);

            Load += OnFormLoad;
            Shown += OnFormShown;
            // Save-the-world-first close guard (implemented in BlendWindow.Bridge.cs).
            FormClosing += OnCloseRequested;
            // Remember how the host left the window, so the next launch opens the same way.
            ResizeEnd += (s, e) => SaveWindowBounds();
            FormClosing += (s, e) => SaveWindowBounds();
        }

        /// <summary>
        /// Sizes the window for the display it opens on: the designed 1408x800 (or the
        /// host's remembered size) multiplied by the display scale, clamped to the work
        /// area so a large scale factor can never push the window off screen.
        /// </summary>
        private void ApplyDpiSizing()
        {
            var scale = DpiScale;
            var work = Screen.FromControl(this).WorkingArea;
            var maxWidth = Math.Max(320, work.Width - WorkAreaMargin);
            var maxHeight = Math.Max(240, work.Height - WorkAreaMargin);

            // The minimum is clamped too: a minimum bigger than the screen would leave the
            // window unable to fit anywhere.
            MinimumSize = new Size(
                Math.Min(ScaleUnits(DesignMinWidth, scale), maxWidth),
                Math.Min(ScaleUnits(DesignMinHeight, scale), maxHeight));

            var (savedWidth, savedHeight) = ReadSavedWindowSize();
            var wantWidth = ScaleUnits(savedWidth > 0 ? savedWidth : DesignWidth, scale);
            var wantHeight = ScaleUnits(savedHeight > 0 ? savedHeight : DesignHeight, scale);

            ClientSize = new Size(
                ClampInt(wantWidth, MinimumSize.Width, maxWidth),
                ClampInt(wantHeight, MinimumSize.Height, maxHeight));

            // StartPosition centred the window at its pre-scale size, so re-centre it now.
            Location = new Point(
                work.X + Math.Max(0, (work.Width - Width) / 2),
                work.Y + Math.Max(0, (work.Height - Height) / 2));

            if (ReadSavedMaximized()) WindowState = FormWindowState.Maximized;

            Logger.Debug(
                "Window sizing: scale {scale}, dpi {dpi}, work {work}, min {min}, saved {saved}x{savedH}, want {w}x{h}, client {client}, size {size}, state {state}",
                scale, DeviceDpi, work, MinimumSize, savedWidth, savedHeight, wantWidth, wantHeight, ClientSize, Size, WindowState);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Logger.Debug("Window shown: client {client}, size {size}, bounds {bounds}, min {min}, state {state}",
                ClientSize, Size, Bounds, MinimumSize, WindowState);
        }

        /// <summary>
        /// The window moved to a display with a different scale. WinForms has already
        /// rescaled the form itself, so this only re-derives the minimum for the new
        /// scale and clamps the current size into the new work area. (Nothing raises this
        /// while the app runs SystemAware; it is here so a later switch to PerMonitorV2
        /// keeps the same guarantees.)
        /// </summary>
        protected override void OnDpiChanged(DpiChangedEventArgs e)
        {
            base.OnDpiChanged(e);
            if (WindowState != FormWindowState.Normal) return;

            var scale = DpiScale;
            var work = Screen.FromControl(this).WorkingArea;
            var maxWidth = Math.Max(320, work.Width - WorkAreaMargin);
            var maxHeight = Math.Max(240, work.Height - WorkAreaMargin);

            MinimumSize = new Size(
                Math.Min(ScaleUnits(DesignMinWidth, scale), maxWidth),
                Math.Min(ScaleUnits(DesignMinHeight, scale), maxHeight));

            ClientSize = new Size(
                ClampInt(ClientSize.Width, MinimumSize.Width, maxWidth),
                ClampInt(ClientSize.Height, MinimumSize.Height, maxHeight));
        }

        /// <summary>The remembered window size in device-independent pixels, or (0, 0).</summary>
        private (int Width, int Height) ReadSavedWindowSize()
        {
            try
            {
                var saved = UserPrefsProvider?.LoadPreferences()?.WindowBounds;
                if (string.IsNullOrWhiteSpace(saved)) return (0, 0);

                var parts = saved.Split('x', 'X');
                if (parts.Length != 2) return (0, 0);
                if (!int.TryParse(parts[0].Trim(), out var width)) return (0, 0);
                if (!int.TryParse(parts[1].Trim(), out var height)) return (0, 0);
                if (width < 320 || height < 240 || width > 20000 || height > 20000) return (0, 0);
                return (width, height);
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "Could not read the saved window size");
                return (0, 0);
            }
        }

        private bool ReadSavedMaximized()
        {
            try { return UserPrefsProvider?.LoadPreferences()?.WindowMaximized == true; }
            catch (Exception ex) { Logger.Warning(ex, "Could not read the saved window state"); return false; }
        }

        /// <summary>Writes the current size back, in device-independent pixels.</summary>
        private void SaveWindowBounds()
        {
            if (IsDisposed || !IsHandleCreated) return;
            // A minimized window carries no size worth remembering, and its state is not
            // the one the host chose to leave the app in.
            if (WindowState == FormWindowState.Minimized) return;
            try
            {
                var prefs = UserPrefsProvider?.LoadPreferences();
                if (prefs == null) return;

                var maximized = WindowState == FormWindowState.Maximized;
                if (WindowState == FormWindowState.Normal)
                {
                    var scale = DpiScale <= 0 ? 1f : DpiScale;
                    var width = (int)Math.Round(ClientSize.Width / scale);
                    var height = (int)Math.Round(ClientSize.Height / scale);
                    if (width < 320 || height < 240) return;

                    var bounds = width + "x" + height;
                    if (prefs.WindowBounds == bounds && prefs.WindowMaximized == false) return;
                    prefs.WindowBounds = bounds;
                }
                else if (prefs.WindowMaximized == maximized)
                {
                    return;
                }

                prefs.WindowMaximized = maximized;
                UserPrefsProvider.SavePreferences(prefs);
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "Could not save the window size");
            }
        }

        private void OnFormShown(object sender, EventArgs e)
        {
            if (!IsFirstShown) return;
            IsFirstShown = false;

            // Mirrors MainWindow.OnShown: orphaned-server adoption first, then the
            // profile's auto-start. Implemented in BlendWindow.Bridge.cs.
            OnBlendStartup();
        }

        private async void OnFormLoad(object sender, EventArgs e)
        {
            // Size the window for this display before the page loads, so the first layout
            // the halls ever measure is already the designed CSS viewport. Never fatal:
            // a window at the unscaled default is still a usable window.
            try { ApplyDpiSizing(); }
            catch (Exception ex) { Logger.Warning(ex, "Could not apply DPI-aware window sizing"); }

            try
            {
                await InitializeWebViewAsync();
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to initialize the Blend UI WebView");
                MessageBox.Show(
                    "The embedded browser (WebView2 runtime) failed to start.\n\n" + ex.Message,
                    "Blend UI failed to load",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                Close();
            }
        }

        private async Task InitializeWebViewAsync()
        {
            // Keep browser profile data out of the install folder (which may be read-only).
            var userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ValheimBakaLoader", "WebView2");

            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: userDataFolder);
            await WebView.EnsureCoreWebView2Async(environment);

            var core = WebView.CoreWebView2;

            var webUiPath = Path.Combine(AppContext.BaseDirectory, "WebUI");
            core.SetVirtualHostNameToFolderMapping(
                VirtualHost, webUiPath, CoreWebView2HostResourceAccessKind.Allow);

            // Rendered Atlas map PNGs live in a writable LocalAppData cache
            // (the install folder backing app.baka may be read-only).
            core.SetVirtualHostNameToFolderMapping(
                AtlasVirtualHost, GetAtlasCacheDir(), CoreWebView2HostResourceAccessKind.Allow);

            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
#if !DEBUG
            core.Settings.AreDevToolsEnabled = false;
#endif

            core.WebMessageReceived += OnWebMessageReceived;

            core.Navigate($"https://{VirtualHost}/index.html");
        }

        #region Messaging

        private async void OnWebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            JObject message;
            try
            {
                message = JObject.Parse(e.WebMessageAsJson);
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "Blend UI sent an unparseable message");
                return;
            }

            var method = message.Value<string>("method");
            if (string.IsNullOrEmpty(method)) return;

            var id = message["id"]?.Value<long?>();
            var parameters = message["params"] as JObject ?? new JObject();

            // Window-chrome messages are fire-and-forget and must run on the UI thread.
            if (method.StartsWith("win.", StringComparison.Ordinal))
            {
                HandleWindowMessage(method, parameters);
                return;
            }

            if (id == null)
            {
                Logger.Warning("Blend UI called {method} without an id - ignored", method);
                return;
            }

            if (!RpcHandlers.TryGetValue(method, out var handler))
            {
                PostReply(id.Value, ok: false, error: $"Unknown method: {method}");
                return;
            }

            try
            {
                var result = await handler(parameters);
                PostReply(id.Value, ok: true, result: result);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Blend UI RPC {method} failed", method);
                PostReply(id.Value, ok: false, error: ex.Message);
            }
        }

        private void HandleWindowMessage(string method, JObject parameters)
        {
            switch (method)
            {
                case "win.minimize":
                    WindowState = FormWindowState.Minimized;
                    break;

                case "win.maximize":
                    WindowState = WindowState == FormWindowState.Maximized
                        ? FormWindowState.Normal
                        : FormWindowState.Maximized;
                    break;

                case "win.close":
                    Close();
                    break;

                case "win.dragStart":
                    if (WindowState == FormWindowState.Normal)
                    {
                        ReleaseCapture();
                        SendMessage(Handle, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
                    }
                    break;

                case "win.resizeStart":
                    var edge = parameters.Value<string>("edge");
                    if (WindowState == FormWindowState.Normal
                        && edge != null
                        && ResizeHitTests.TryGetValue(edge, out var hitTest))
                    {
                        ReleaseCapture();
                        SendMessage(Handle, WM_NCLBUTTONDOWN, (IntPtr)hitTest, IntPtr.Zero);
                    }
                    break;

                default:
                    Logger.Warning("Blend UI sent unknown window message {method}", method);
                    break;
            }
        }

        /// <summary>Register an RPC method callable from the Blend UI.</summary>
        protected void RegisterRpc(string method, Func<JObject, Task<object>> handler)
        {
            RpcHandlers[method] = handler;
        }

        private void PostReply(long id, bool ok, object result = null, string error = null)
        {
            PostJson(new { id, ok, result, error });
        }

        /// <summary>Push an event to the Blend UI (safe to call from any thread).</summary>
        protected void PostEvent(string eventName, object data)
        {
            PostJson(new { @event = eventName, data });
        }

        private void PostJson(object payload)
        {
            // CRITICAL: never touch WebView/CoreWebView2 here. This method is called
            // from background threads (server stdout, timers, RCON). The CoreWebView2
            // getter is UI-thread-only; on some machines even a null-check of it from
            // another thread throws E_NOINTERFACE and kills the process (GitHub crash
            // report: InvalidOperationException "CoreWebView2 can only be accessed from
            // the UI thread" via PipelineLogger.Write). Marshal FIRST, dereference later.
            if (IsDisposed || !IsHandleCreated) return;

            var json = JsonConvert.SerializeObject(payload);
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action(() => PostJsonOnUiThread(json))); }
                catch (ObjectDisposedException) { /* window closing */ }
                catch (InvalidOperationException) { /* handle destroyed mid-call */ }
            }
            else
            {
                PostJsonOnUiThread(json);
            }
        }

        private void PostJsonOnUiThread(string json)
        {
            // A UI push must never take the app down, and must never log (logging
            // routes back through PostEvent -> PostJson -> here: infinite recursion).
            try
            {
                WebView?.CoreWebView2?.PostWebMessageAsJson(json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"PostJson failed: {ex.Message}");
            }
        }

        #endregion
    }
}
