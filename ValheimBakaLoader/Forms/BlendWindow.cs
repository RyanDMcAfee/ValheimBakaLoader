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
        /// The host an installed language pack is read from: a folder mapping of its own over
        /// the languages folder beside userprefs.
        /// <para>
        /// A pack's fonts have to have a URL, because <c>@font-face src</c> cannot read a local
        /// file. The obvious shape is to serve them off the page's own origin and answer the
        /// requests by hand, and that shape cannot work: a host registered with
        /// <c>SetVirtualHostNameToFolderMapping</c> is claimed whole by the folder mapper and
        /// never raises <c>WebResourceRequested</c>, so a filter on a path under
        /// <c>app.baka</c> is attached and never called once. That is what shipped in 1.2.0,
        /// and it is why every language answered "could not be downloaded" while the download
        /// and the install were working perfectly: the read-back was dead, not the pack.
        /// </para>
        /// <para>
        /// So the packs get a host of their own and the mapper serves them. That puts them on a
        /// second origin, which makes every pack fetch a cross-origin one, and a font fetch is
        /// always a CORS request: <see cref="CoreWebView2HostResourceAccessKind.Allow"/> is the
        /// only access kind that answers both the JSON and the fonts, measured in the engine
        /// rather than reasoned about. What the page can read is then the whole languages
        /// folder and nothing else, which is downloaded translation data; userprefs, the logs
        /// and the caches sit BESIDE that folder rather than inside it.
        /// </para>
        /// </summary>
        private const string LangVirtualHost = "lang.baka";

        /// <summary>The address shape a pack file is asked for by: https://lang.baka/...</summary>
        internal const string LanguageUrlPrefix = "https://" + LangVirtualHost + "/";

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
        /// The page has no way to ask Windows how the window is sitting, so the window tells
        /// it. Every size change comes through here, including the one a maximize or a restore
        /// makes, and the state is said again only when it actually changed: a live edge drag
        /// raises this many times a second and none of those are news.
        /// </summary>
        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            // A minimized window is not a state the page can do anything with, and it comes
            // back to whichever of the other two it left, which raises this again.
            if (WindowState == FormWindowState.Minimized) return;
            PostWindowState();
        }

        /// <summary>Whether the page has already been told the window is maximized.</summary>
        private bool? PostedMaximized;

        /// <summary>
        /// Pushes win.state to the page. <paramref name="force"/> is for the one push that has
        /// to happen whether or not anything changed: the page that just finished loading has
        /// never been told anything, and every size change it missed happened before it existed.
        /// </summary>
        private void PostWindowState(bool force = false)
        {
            var maximized = WindowState == FormWindowState.Maximized;
            if (!force && PostedMaximized == maximized) return;
            PostedMaximized = maximized;
            PostEvent("win.state", new { maximized });
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

            // An installed language pack is served from a host of its own, mapped onto the
            // languages folder beside userprefs, BEFORE Navigate so the page can ask for a pack
            // the moment it comes up. Allow rather than Deny or DenyCors: the fonts are
            // cross-origin from the page and a font fetch is always a CORS request, so the
            // other two kinds answer neither the catalog nor the faces.
            //
            // Mapping a folder that is not there THROWS, and a throw here reaches OnLoad, which
            // says the embedded browser failed to start and closes the window. That would be a
            // dead app for every host who has never downloaded a pack, so the folder and the
            // mapping go inside one try: GetLanguagesDir creates it, and if either half still
            // fails the window comes up with no language route and languages simply do not load,
            // which is no worse than the state this replaces.
            try
            {
                LanguagesDir = GetLanguagesDir();
                core.SetVirtualHostNameToFolderMapping(
                    LangVirtualHost, LanguagesDir, CoreWebView2HostResourceAccessKind.Allow);
            }
            catch (Exception ex)
            {
                LanguagesDir = null;
                Logger.Warning(ex,
                    "The languages folder could not be served to the page, so installed language packs will not load");
            }

            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
#if !DEBUG
            core.Settings.AreDevToolsEnabled = false;
#endif

            core.WebMessageReceived += OnWebMessageReceived;

            // The page's copy of the window state, handed over the moment there is a page to
            // hand it to. Registered before Navigate, because the load this is waiting for is
            // the one the line at the bottom of this method starts.
            core.NavigationCompleted += (s, e) => PostWindowState(force: true);

            // Both halves of "the interface you see is the interface you installed". The
            // version reaches the page before any script of its own runs, so index.html can
            // stamp app.css and app.js with it and a new release becomes a new address. And
            // the first launch of a build that has never run here throws away what the
            // browser kept, which covers whatever a stamp cannot reach. Neither is allowed
            // to stop the window coming up.
            await AnnounceVersionToPageAsync(core);

            // And the language, the same way and for the same reason. The page paints as it
            // is evaluated and the catalog is fetched, so a host saved into Russian used to
            // read one English frame before the words arrived. This tells the bootstrap what
            // is coming before the document exists, so it can hold the paint back.
            await AnnounceLanguageToPageAsync(core);

            await ClearCacheOnceForThisVersionAsync(core);

            core.Navigate($"https://{VirtualHost}/index.html");
        }

        /// <summary>
        /// The languages folder, resolved once at startup rather than per request, and null when
        /// it could not be served at all.
        /// </summary>
        private string LanguagesDir;

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
        /// The language the page should come up in, or null when the first frame is English
        /// anyway and there is nothing to say.
        /// <para>
        /// Three things have to hold together for a window to be worth holding back. The
        /// saved preference has to be a language this app knows, so an unreadable spelling
        /// left in userprefs.json cannot decide anything. It has to be a language other than
        /// English, because English is the markup in index.html and is already what the
        /// first frame paints. And a pack for it has to be on disk, because the words come
        /// out of a pack: naming a language with nothing installed would hide the window
        /// and then paint the English it was hiding.
        /// </para>
        /// <para>
        /// The pack is asked about through a delegate rather than read here, which is what
        /// makes this a decision rather than a lookup: the suite drives all four roads
        /// through it without a languages folder, and the short circuit above the call means
        /// an English window never asks the pack service anything at all.
        /// </para>
        /// </summary>
        /// <param name="savedLanguage">The language preference exactly as it was saved.</param>
        /// <param name="hasPack">Answers whether a pack is installed for a language.</param>
        internal static string LanguageToAnnounce(string savedLanguage, Func<string, bool> hasPack)
        {
            var normalized = LanguageCodes.Normalize(savedLanguage);
            if (normalized == null || LanguageCodes.IsEnglish(normalized)) return null;

            return hasPack != null && hasPack(normalized) ? normalized : null;
        }

        /// <summary>
        /// Hands the page the language it is about to be read in, before any of its own
        /// script runs. index.html holds the paint back while this is set, and lets it go
        /// when the first applyLanguage for that language finishes or the safety timeout
        /// fires, whichever comes first.
        /// <para>
        /// Nothing is said for an English window, and nothing is said for a language with no
        /// pack on disk. Setting nothing is not a failure: the page reads an absent
        /// BAKA_LANG as "paint now", which is exactly what it did before this existed.
        /// </para>
        /// </summary>
        private async Task AnnounceLanguageToPageAsync(CoreWebView2 core)
        {
            try
            {
                var code = LanguageToAnnounce(
                    UserPrefsProvider.LoadPreferences()?.Language,
                    language => LanguagePacks?.InstalledAny(language) != null);
                if (code == null) return;

                await core.AddScriptToExecuteOnDocumentCreatedAsync(
                    "window.BAKA_LANG=" + JsonConvert.SerializeObject(code) + ";");
            }
            catch (Exception ex)
            {
                // Say nothing and the page paints straight away, in English, and switches a
                // moment later exactly as it used to. Never worth failing a launch over.
                Logger.Warning(ex, "Could not hand the interface its language");
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

        /// <summary>When the last title bar press arrived, or 0 when a sequence just ended.</summary>
        private long LastTitlebarPressTicks;

        /// <summary>Where on screen that press was, which is the other half of the rule.</summary>
        private Point LastTitlebarPressPosition;

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

                // Every press on the title bar arrives here, which is why the double click is
                // counted here too. The page cannot count it: the line below hands the window to
                // Windows' own caption move loop, which owns the mouse until the button comes
                // back up, and a browser only raises dblclick after two whole down/up/click
                // cycles. Second press of a pair: toggle, and do not enter the loop, so the
                // window is not being dragged and resized at the same moment.
                case "win.dragStart":
                {
                    var pressedAt = Environment.TickCount64;
                    var pressedWhere = Cursor.Position;
                    var second = TitlebarClicks.IsDoubleClick(
                        LastTitlebarPressTicks,
                        pressedAt,
                        LastTitlebarPressPosition,
                        pressedWhere,
                        SystemInformation.DoubleClickTime,
                        SystemInformation.DoubleClickSize);

                    if (second)
                    {
                        // A third press starts a fresh sequence rather than toggling again.
                        LastTitlebarPressTicks = 0;
                        WindowState = WindowState == FormWindowState.Maximized
                            ? FormWindowState.Normal
                            : FormWindowState.Maximized;
                        break;
                    }

                    LastTitlebarPressTicks = pressedAt;
                    LastTitlebarPressPosition = pressedWhere;

                    if (WindowState == FormWindowState.Normal)
                    {
                        ReleaseCapture();
                        SendMessage(Handle, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
                    }
                    break;
                }

                // The page saw the pointer travel far enough across a maximized window's title
                // bar to mean a drag rather than a click. Windows does this for a real caption:
                // the window comes back down to size under the pointer and the move carries on
                // without the button ever being let go. Same here, and the move itself is still
                // Windows' own loop, so snapping to an edge works the way it always has.
                case "win.dragRestore":
                {
                    if (WindowState != FormWindowState.Maximized) break;

                    var cursor = Cursor.Position;
                    // Read while the window is still maximized: how far down the title bar the
                    // host took hold. The bar is the top of the window in either state, so the
                    // same distance keeps the pointer on it.
                    var grabOffsetY = cursor.Y - Top;
                    var xRatio = parameters.Value<double?>("xRatio") ?? 0.5;

                    // WinForms remembers the bounds it left Normal at, so this is the size the
                    // host last chose rather than a size invented here.
                    WindowState = FormWindowState.Normal;

                    Location = TitlebarClicks.RestoredLocation(
                        cursor,
                        grabOffsetY,
                        Size,
                        Screen.FromPoint(cursor).WorkingArea,
                        xRatio);

                    // A restore is not half of a double click.
                    LastTitlebarPressTicks = 0;

                    ReleaseCapture();
                    SendMessage(Handle, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
                    break;
                }

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
