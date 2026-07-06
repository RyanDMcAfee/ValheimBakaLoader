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
            IUserPreferencesProvider userPrefsProvider,
            IServerPreferencesProvider serverPrefsProvider,
            IWorldPreferencesProvider worldPrefsProvider,
            IPlayerDataRepository playerDataProvider,
            Game.ValheimServer server,
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
                userPrefsProvider,
                serverPrefsProvider,
                worldPrefsProvider,
                playerDataProvider,
                server,
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
            // Wide enough that the Hearth status row (incl. the Douse box) fits without
            // clipping at the default size.
            ClientSize = new Size(1408, 800);
            MinimumSize = new Size(1024, 680);
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
            if (WebView?.CoreWebView2 == null) return;

            var json = JsonConvert.SerializeObject(payload);
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action(() => WebView.CoreWebView2.PostWebMessageAsJson(json))); }
                catch (ObjectDisposedException) { /* window closing */ }
            }
            else
            {
                WebView.CoreWebView2.PostWebMessageAsJson(json);
            }
        }

        #endregion
    }
}
