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
    public partial class BlendWindow : Form, IMainAppWindow, ISelfUpdateClosable
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

        /// <summary>
        /// Where an installed language pack is read from, on the page's own origin.
        /// <para>
        /// A pack's fonts have to have a URL, because <c>@font-face src</c> cannot read a
        /// local file, and every font fetch is a CORS request. Serving the pack from this
        /// origin rather than a host of its own means there is no CORS question to get
        /// wrong, and it lets this side decide the content type and refuse any path that
        /// resolves outside the languages folder.
        /// </para>
        /// </summary>
        internal const string LanguagePathPrefix = "/lang/";

        /// <summary>The address shape a pack file is asked for by: https://app.baka/lang/...</summary>
        internal const string LanguageUrlPrefix = "https://" + VirtualHost + LanguagePathPrefix;

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

        /// <summary>
        /// Where installed language packs live: beside userprefs, not in the install folder,
        /// which may be read-only and which a manual re-extract does not carry anything across.
        /// </summary>
        internal static string GetLanguagesDir()
        {
            var dir = Environment.ExpandEnvironmentVariables(Properties.Resources.LanguagesFolderPath);
            Directory.CreateDirectory(dir);
            return dir;
        }

        /// <summary>
        /// Turns a <c>/lang/...</c> request path into the file it names under the languages
        /// folder, or null when it names anything else.
        /// <para>
        /// This is the whole security boundary of serving a pack, so it is a plain function
        /// with no I/O in it and the tests drive it directly. The order matters: decode
        /// first, canonicalise second, and only then ask whether the answer is still inside
        /// the folder. Asking any earlier is how <c>%2e%2e</c> gets through.
        /// </para>
        /// </summary>
        /// <param name="languagesRoot">The languages folder every answer must stay under.</param>
        /// <param name="requestPath">The URL path, leading slash and all.</param>
        /// <returns>A full path under <paramref name="languagesRoot"/>, or null.</returns>
        internal static string MapLanguageResourcePath(string languagesRoot, string requestPath)
        {
            if (string.IsNullOrWhiteSpace(languagesRoot) || string.IsNullOrWhiteSpace(requestPath))
                return null;

            var path = requestPath.Replace('\\', '/');

            // A query or a fragment is not part of the file name.
            var cut = path.IndexOfAny(new[] { '?', '#' });
            if (cut >= 0) path = path.Substring(0, cut);

            if (!path.StartsWith(LanguagePathPrefix, StringComparison.OrdinalIgnoreCase)) return null;

            var relative = path.Substring(LanguagePathPrefix.Length);
            if (relative.Length == 0) return null;

            // One decode. A double-encoded run decodes to a literal name rather than to a
            // traversal, which is exactly what should happen: it names no file and 404s.
            try { relative = Uri.UnescapeDataString(relative); }
            catch (Exception) { return null; }

            relative = relative.Replace('\\', '/').TrimStart('/');
            if (relative.Length == 0) return null;

            // A drive letter, an alternate data stream, a NUL, or anything Windows refuses
            // in a path at all. None of these can name a file inside the folder.
            if (relative.IndexOf(':') >= 0) return null;
            if (relative.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return null;
            if (relative.IndexOf('\0') >= 0) return null;

            try
            {
                var root = Path.GetFullPath(languagesRoot)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    + Path.DirectorySeparatorChar;

                var candidate = relative.Replace('/', Path.DirectorySeparatorChar);
                if (Path.IsPathRooted(candidate)) return null;

                var full = Path.GetFullPath(Path.Combine(root, candidate));

                // The answer has to be a file INSIDE the folder. Equal to the folder itself
                // is a directory, and one character past it is a sibling folder whose name
                // merely starts the same way.
                if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return null;
                if (full.Length == root.Length) return null;

                return full;
            }
            catch (Exception)
            {
                // Too long, badly formed, or a path the platform will not canonicalise.
                return null;
            }
        }

        /// <summary>What a pack file is served as. Anything unrecognised is bytes.</summary>
        internal static string LanguageContentType(string path)
        {
            var extension = Path.GetExtension(path ?? string.Empty).ToLowerInvariant();
            return extension switch
            {
                ".woff2" => "font/woff2",
                ".woff" => "font/woff",
                ".ttf" => "font/ttf",
                ".otf" => "font/otf",
                ".json" => "application/json; charset=utf-8",
                ".css" => "text/css; charset=utf-8",
                ".txt" => "text/plain; charset=utf-8",
                _ => "application/octet-stream",
            };
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
                if (UserPrefsProvider == null) return;

                var maximized = WindowState == FormWindowState.Maximized;
                string bounds = null;
                if (WindowState == FormWindowState.Normal)
                {
                    var scale = DpiScale <= 0 ? 1f : DpiScale;
                    var width = (int)Math.Round(ClientSize.Width / scale);
                    var height = (int)Math.Round(ClientSize.Height / scale);
                    if (width < 320 || height < 240) return;

                    bounds = width + "x" + height;
                }

                // Saving the window size writes the WHOLE of userprefs.json, servers and worlds
                // included, so it goes through the one gate rather than loading a document here and
                // writing it back over whatever landed in between. Returning false writes nothing,
                // which keeps a resize that changed no value as free as it was before.
                UserPrefsProvider.Mutate(prefs =>
                {
                    if (bounds != null)
                    {
                        if (prefs.WindowBounds == bounds && prefs.WindowMaximized == false) return false;
                        prefs.WindowBounds = bounds;
                    }
                    else if (prefs.WindowMaximized == maximized)
                    {
                        return false;
                    }

                    prefs.WindowMaximized = maximized;
                    return true;
                });
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

            // An installed language pack is served from this origin, from the languages
            // folder beside userprefs. Registered BEFORE Navigate, because a filter added
            // afterwards only applies to requests made after it.
            WebEnvironment = environment;
            LanguagesDir = GetLanguagesDir();
            core.AddWebResourceRequestedFilter(
                LanguageUrlPrefix + "*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += OnLanguageResourceRequested;

            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
#if !DEBUG
            core.Settings.AreDevToolsEnabled = false;
#endif

            core.WebMessageReceived += OnWebMessageReceived;

            // Both halves of "the interface you see is the interface you installed". The
            // version reaches the page before any script of its own runs, so index.html can
            // stamp app.css and app.js with it and a new release becomes a new address. And
            // the first launch of a build that has never run here throws away what the
            // browser kept, which covers whatever a stamp cannot reach. Neither is allowed
            // to stop the window coming up.
            await AnnounceVersionToPageAsync(core);
            await ClearCacheOnceForThisVersionAsync(core);

            core.Navigate($"https://{VirtualHost}/index.html");
        }

        /// <summary>The WebView2 environment, kept so a pack response can be built from it.</summary>
        private CoreWebView2Environment WebEnvironment;

        /// <summary>The languages folder, resolved once at startup rather than per request.</summary>
        private string LanguagesDir;

        /// <summary>
        /// Answers a https://app.baka/lang/... request out of the languages folder.
        /// Anything that does not resolve to a file inside that folder is a 404, and the
        /// path question itself lives in <see cref="MapLanguageResourcePath"/> where the
        /// tests can reach it.
        /// </summary>
        private void OnLanguageResourceRequested(object sender, CoreWebView2WebResourceRequestedEventArgs e)
        {
            var environment = WebEnvironment;
            if (environment == null || e?.Request == null) return;

            try
            {
                // Still escaped on purpose. MapLanguageResourcePath does the ONE decode
                // this path is allowed, and decoding here as well would hand it a name
                // that has already been through Unescape once: %252e%252e would arrive
                // as %2e%2e and decode to .. inside the very function whose job is to
                // stop that. One decode, in the place the tests can drive.
                var path = new Uri(e.Request.Uri).GetComponents(UriComponents.Path, UriFormat.UriEscaped);
                var file = MapLanguageResourcePath(LanguagesDir, "/" + path);

                if (file != null && File.Exists(file))
                {
                    var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
                    e.Response = environment.CreateWebResourceResponse(
                        stream, 200, "OK",
                        "Content-Type: " + LanguageContentType(file) + "\r\nCache-Control: no-cache");
                    return;
                }
            }
            catch (Exception ex)
            {
                // A pack that cannot be read is a missing font, never a broken window.
                Logger.Debug(ex, "A language pack file could not be served: {uri}", e.Request.Uri);
            }

            try { e.Response = environment.CreateWebResourceResponse(null, 404, "Not Found", string.Empty); }
            catch (Exception) { /* the request went away while we answered it */ }
        }

        /// <summary>
        /// Hands the page the version this build ships, before any of its own script runs.
        /// index.html reads it to stamp its two includes; with no host in front of it (the
        /// mock preview) it falls back to the constant written into the file.
        /// </summary>
        private async Task AnnounceVersionToPageAsync(CoreWebView2 core)
        {
            try
            {
                var version = AssemblyHelper.GetApplicationVersion();
                if (string.IsNullOrWhiteSpace(version)) return;

                await core.AddScriptToExecuteOnDocumentCreatedAsync(
                    "window.BAKA_VERSION=" + JsonConvert.SerializeObject(version) + ";");
            }
            catch (Exception ex)
            {
                // The page falls back to its own constant, which is the version it shipped with.
                Logger.Warning(ex, "Could not hand the interface its version");
            }
        }

        /// <summary>
        /// Throws away the browser's disk cache the first time each version runs, and never
        /// again for that version. See WebUiCacheStamp for the rule and the marker.
        /// </summary>
        private async Task ClearCacheOnceForThisVersionAsync(CoreWebView2 core)
        {
            try
            {
                var current = AssemblyHelper.GetApplicationVersion();
                var last = WebUiCacheStamp.ReadLastVersion();
                if (!WebUiCacheStamp.ShouldClear(last, current)) return;

                // The disk cache and nothing else: cookies, storage and settings belong to the
                // interface's own state, and a version bump is no reason to throw those out.
                await core.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.DiskCache);
                WebUiCacheStamp.RememberVersion(current);

                Logger.Information(
                    "Cleared the interface cache for version {version} (previous marker: {last}).",
                    current, last ?? "none");
            }
            catch (Exception ex)
            {
                // A cache that will not clear is a stale hall at worst, and the stamped includes
                // are the first line of defence anyway. Never worth failing a launch over.
                Logger.Warning(ex, "Could not clear the interface cache for this version");
            }
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

                // The English sentence travels exactly as it always has, and an id travels
                // beside it when the throw wrote one. A page that reads only "error" is
                // unaffected; a page that knows the id can say it in another language.
                PostReply(
                    id.Value,
                    ok: false,
                    error: ex.Message,
                    errorId: HostFacingException.IdOf(ex),
                    errorParams: HostFacingException.ParamsOf(ex));
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

        /// <summary>
        /// One answer to one RPC. <c>error</c> is the English sentence and stays what it has
        /// always been; <c>errorId</c> and <c>errorParams</c> are how that sentence can be
        /// said in another language, and are null for anything that did not name itself.
        /// </summary>
        private void PostReply(
            long id,
            bool ok,
            object result = null,
            string error = null,
            string errorId = null,
            object errorParams = null)
        {
            PostJson(new { id, ok, result, error, errorId, errorParams });
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
