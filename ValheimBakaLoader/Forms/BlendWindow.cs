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

        /// <summary>
        /// The page zoom the host's Text size preference asks for, or 1.0 until the preferences
        /// have been read.
        /// <para>
        /// It is held here as well as on the control because the window's own MINIMUM is
        /// computed from it: a zoom of 1.45 over a 1024 pixel window hands the page 706 CSS
        /// pixels, which is narrower than any layout in it, so the minimum grows with the zoom
        /// and the work-area clamp below is what keeps that possible on a small display.
        /// </para>
        /// </summary>
        private double TextZoom = 1.0;

        /// <summary>Display scale for this window: 1.0 at 96 dpi, 1.5 at 150%.</summary>
        private float DpiScale => DeviceDpi <= 0 ? 1f : DeviceDpi / 96f;

        private static int ScaleUnits(int designPx, float scale) => Math.Max(1, (int)Math.Round(designPx * scale));

        /// <summary>
        /// The window minimum for one display scale and one text zoom, clamped so a minimum can
        /// never be bigger than the screen it has to fit on.
        /// <para>
        /// Written once and used by all three places that set MinimumSize, because a minimum
        /// that follows the zoom in two of them and not the third is a window a host can drag
        /// down to a size the halls do not lay out in.
        /// </para>
        /// <para>
        /// WHY THE ZOOM MULTIPLIES IT, said plainly, because this is the whole of the premise and
        /// it is a one-line piece of arithmetic: a WebView2 ZoomFactor is a Chromium PAGE zoom,
        /// which divides the CSS viewport by the factor. 1024 device pixels at 1.45 is 706 CSS
        /// pixels, and nothing in these halls lays out at 706. So the minimum in DEVICE pixels
        /// has to grow by the same factor for the 1024x680 design minimum to still be what the
        /// stylesheet gets. The canvas has nothing to do with it: the Atlas sizes its backing
        /// store from its wrap's own CSS size times devicePixelRatio and draws in CSS pixels, so
        /// it follows the zoom exactly as an 11px rule does and needs no multiplier of its own.
        /// </para>
        /// </summary>
        private static Size MinimumFor(float scale, double zoom, int maxWidth, int maxHeight)
        {
            var wide = ScaleUnits((int)Math.Round(DesignMinWidth * zoom), scale);
            var tall = ScaleUnits((int)Math.Round(DesignMinHeight * zoom), scale);
            return new Size(Math.Min(wide, maxWidth), Math.Min(tall, maxHeight));
        }

        /// <summary>
        /// Puts the host's Text size on the page and moves the window minimum with it. Called
        /// once the WebView is ready and again on every save that carried the preference, so the
        /// window follows the setting rather than the next launch.
        /// </summary>
        internal void ApplyTextSize(string size)
        {
            TextZoom = TextSizes.Factor(size);

            try
            {
                if (WebView != null) WebView.ZoomFactor = TextZoom;
            }
            catch (Exception e)
            {
                // A zoom that would not take is not worth a window.
                Logger.Debug("Could not apply the text size {size}: {message}", size, e.Message);
            }

            if (!IsHandleCreated || WindowState != FormWindowState.Normal) return;

            var work = Screen.FromControl(this).WorkingArea;
            var maxWidth = Math.Max(320, work.Width - WorkAreaMargin);
            var maxHeight = Math.Max(240, work.Height - WorkAreaMargin);

            MinimumSize = MinimumFor(DpiScale, TextZoom, maxWidth, maxHeight);
            ClientSize = new Size(
                ClampInt(ClientSize.Width, MinimumSize.Width, maxWidth),
                ClampInt(ClientSize.Height, MinimumSize.Height, maxHeight));
        }

        private static int ClampInt(int value, int min, int max) => value < min ? min : (value > max ? max : value);

        #endregion

        /// <summary>
        /// The WebView2 profile folder. Kept out of the install folder (which may be read-only)
        /// and under <see cref="AppDataRoot.LocalRoot"/>, so an isolated run has its own.
        /// </summary>
        internal static string WebViewUserDataFolder() => Path.Combine(
            AppDataRoot.LocalRoot, "ValheimBakaLoader", "WebView2");

        /// <summary>Writable cache folder for rendered Atlas map PNGs, served via the atlas.baka virtual host.</summary>
        internal static string GetAtlasCacheDir()
        {
            var dir = Path.Combine(
                AppDataRoot.LocalRoot,
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
            var dir = PathCheck.Resolve(Properties.Resources.LanguagesFolderPath);
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

        // Snapping (Tools/WindowSnap.cs says why each of these is here).
        [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
        private static extern int GetWindowStyle(IntPtr hWnd, int index);

        [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
        private static extern int SetWindowStyle(IntPtr hWnd, int index, int value);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

        [DllImport("user32.dll")]
        private static extern bool IsZoomed(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

        [DllImport("user32.dll")]
        private static extern bool GetWindowPlacement(IntPtr hWnd, ref WindowPlacement placement);

        [DllImport("user32.dll")]
        private static extern bool TrackMouseEvent(ref TrackMouse track);

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect
        {
            public int Left, Top, Right, Bottom;
            public Rectangle ToRectangle() => Rectangle.FromLTRB(Left, Top, Right, Bottom);
            public static NativeRect From(Rectangle r) => new() { Left = r.Left, Top = r.Top, Right = r.Right, Bottom = r.Bottom };
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct MonitorInfo
        {
            public int Size;
            public NativeRect Monitor;
            public NativeRect Work;
            public uint Flags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WindowPlacement
        {
            public int Length;
            public int Flags;
            public int ShowCmd;
            public Point MinPosition;
            public Point MaxPosition;
            public NativeRect NormalPosition;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct TrackMouse
        {
            public int Size;
            public uint Flags;
            public IntPtr Window;
            public uint HoverTime;
        }

        private const int GWL_STYLE = -16;
        private const int WM_STYLECHANGING = 0x007C;
        private const int WM_NCCALCSIZE = 0x0083;
        private const int WM_NCHITTEST = 0x0084;
        private const int WM_NCMOUSEMOVE = 0x00A0;
        private const int WM_NCLBUTTONUP = 0x00A2;
        private const int WM_NCLBUTTONDBLCLK = 0x00A3;
        private const int WM_NCMOUSELEAVE = 0x02A2;
        private const uint SWP_FRAMECHANGED = 0x0020, SWP_NOMOVE = 0x0002, SWP_NOSIZE = 0x0001,
            SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;
        private const uint MONITOR_DEFAULTTONEAREST = 2;
        private const uint TME_LEAVE = 0x0002, TME_NONCLIENT = 0x0010;

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
            // The host's Text size, read before the first sizing rather than after it. The zoom
            // itself goes on the WebView once there is one, further down the launch, but the
            // window's own minimum depends on it: reading it there and not here would open the
            // window at the Normal minimum and then grow it, which a host at Extra large would
            // see as a jump on every launch.
            TextZoom = TextSizes.Factor(ReadSavedTextSize());

            var scale = DpiScale;
            var work = Screen.FromControl(this).WorkingArea;
            var maxWidth = Math.Max(320, work.Width - WorkAreaMargin);
            var maxHeight = Math.Max(240, work.Height - WorkAreaMargin);

            // The minimum is clamped too: a minimum bigger than the screen would leave the
            // window unable to fit anywhere. And it carries the text zoom, because zooming the
            // page is the same thing as making the window smaller from the layout's side.
            MinimumSize = MinimumFor(scale, TextZoom, maxWidth, maxHeight);

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
            // The button keeps its distance from the right edge, so the cut follows the size
            // at once rather than waiting for the page to measure it again.
            ApplyMaxButtonCut();
        }

        /// <summary>
        /// The page cannot tell a window that sits behind the game from one the host is looking
        /// at: document.hidden only turns true when the window is minimized, so the hearth fire
        /// and the smouldering runes went on drawing at full rate behind a game in fullscreen,
        /// and the renderer and gpu processes held about a tenth of a core each doing it. The
        /// window knows, so it says so, and the page parks the fire and pauses the animations
        /// until it is in front again.
        /// </summary>
        protected override void OnActivated(EventArgs e)
        {
            base.OnActivated(e);
            PostWindowActive(true);
        }

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            PostWindowActive(false);
        }

        /// <summary>
        /// Pushes window.active to the page. A push before the page exists is dropped inside
        /// PostEvent, which is why NavigationCompleted says it again with the real state.
        /// </summary>
        private void PostWindowActive(bool active)
        {
            PostEvent("window.active", new { active });
        }

        // ---------------------------------------------------------------- snapping
        // The maximize button as the page last reported it, in device pixels, with the page
        // width it was measured against; WindowSnap.MaxButton keeps it on the right edge.
        private Rectangle MaxButtonReported;
        private int MaxButtonPageWidth;
        private int TitlebarTop, TitlebarHeight;
        private bool MaxButtonHover, MaxButtonPressed;

        // The page's own colours for the bar and the button (app.css .titlebar and .winbtn),
        // so the square the host paints is the square the page would have painted.
        private static readonly Color TitlebarTopColor = Color.FromArgb(0x1E, 0x21, 0x26);
        private static readonly Color TitlebarBottomColor = Color.FromArgb(0x18, 0x1B, 0x1F);
        private static readonly Color WinButtonHoverBack = Color.FromArgb(0x26, 0x2A, 0x30);
        private static readonly Color WinButtonGlyph = Color.FromArgb(0xAB, 0xA3, 0x91);
        private static readonly Color WinButtonGlyphHover = Color.FromArgb(0xFF, 0xB3, 0x5C);

        private Rectangle MaxButtonBounds() =>
            WindowSnap.MaxButton(MaxButtonReported, MaxButtonPageWidth, ClientSize);

        /// <summary>
        /// The styles go on once the handle exists rather than through CreateParams. WinForms
        /// sizes a window from its CreateParams, so a caption and a thick frame there would
        /// make every ClientSize the host sets (the DPI sizing, the remembered size) a frame
        /// larger than the window it then claims as client. Kept out of CreateParams, the
        /// arithmetic stays borderless and the window is still one Windows will snap.
        /// </summary>
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            SetWindowStyle(Handle, GWL_STYLE, WindowSnap.WithSnapStyles(GetWindowStyle(Handle, GWL_STYLE)));
            SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0,
                SWP_FRAMECHANGED | SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        }

        protected override void WndProc(ref Message m)
        {
            switch (m.Msg)
            {
                // WinForms writes the style back from its CreateParams now and then; the
                // snap styles go back on every time it does.
                case WM_STYLECHANGING when m.WParam.ToInt64() == GWL_STYLE:
                {
                    // STYLESTRUCT is { old, new }: the new style is the second int.
                    var next = Marshal.ReadInt32(m.LParam, 4);
                    Marshal.WriteInt32(m.LParam, 4, WindowSnap.WithSnapStyles(next));
                    break;
                }

                // No frame is drawn: the whole window is client, and a maximized one is held
                // to its display's work area.
                case WM_NCCALCSIZE when m.WParam != IntPtr.Zero:
                {
                    var proposed = Marshal.PtrToStructure<NativeRect>(m.LParam).ToRectangle();
                    var maximized = IsZoomed(Handle);
                    var client = WindowSnap.ClientRect(proposed, maximized, maximized ? WorkAreaOf(Handle) : Rectangle.Empty);
                    Marshal.StructureToPtr(NativeRect.From(client), m.LParam, false);
                    m.Result = IntPtr.Zero;
                    return;
                }

                case WM_NCCALCSIZE:
                    m.Result = IntPtr.Zero;
                    return;

                case WM_NCHITTEST:
                {
                    var packed = m.LParam.ToInt64();
                    var screen = new Point((short)(packed & 0xFFFF), (short)((packed >> 16) & 0xFFFF));
                    if (WindowSnap.HitTest(PointToClient(screen), MaxButtonBounds()) == WindowSnap.HTMAXBUTTON)
                    {
                        m.Result = (IntPtr)WindowSnap.HTMAXBUTTON;
                        return;
                    }
                    break;
                }

                case WM_NCMOUSEMOVE:
                {
                    var over = m.WParam.ToInt64() == WindowSnap.HTMAXBUTTON;
                    if (over && !MaxButtonHover)
                    {
                        var track = new TrackMouse
                        {
                            Size = Marshal.SizeOf<TrackMouse>(), Flags = TME_LEAVE | TME_NONCLIENT, Window = Handle,
                        };
                        TrackMouseEvent(ref track);
                    }
                    SetMaxButtonHover(over);
                    break;
                }

                case WM_NCMOUSELEAVE:
                    MaxButtonPressed = false;
                    SetMaxButtonHover(false);
                    break;

                // The button is clicked here rather than by DefWindowProc, which would track it
                // as a classic caption button and paint one over the page's look.
                case WM_NCLBUTTONDOWN when m.WParam.ToInt64() == WindowSnap.HTMAXBUTTON:
                case WM_NCLBUTTONDBLCLK when m.WParam.ToInt64() == WindowSnap.HTMAXBUTTON:
                    MaxButtonPressed = true;
                    m.Result = IntPtr.Zero;
                    return;

                case WM_NCLBUTTONUP when m.WParam.ToInt64() == WindowSnap.HTMAXBUTTON:
                    if (MaxButtonPressed)
                    {
                        MaxButtonPressed = false;
                        LastTitlebarPressTicks = 0;
                        WindowState = WindowState == FormWindowState.Maximized
                            ? FormWindowState.Normal
                            : FormWindowState.Maximized;
                    }
                    m.Result = IntPtr.Zero;
                    return;
            }

            base.WndProc(ref m);
        }

        private static Rectangle WorkAreaOf(IntPtr window)
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            var monitor = MonitorFromWindow(window, MONITOR_DEFAULTTONEAREST);
            return monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info) ? info.Work.ToRectangle() : Rectangle.Empty;
        }

        private void SetMaxButtonHover(bool hover)
        {
            if (MaxButtonHover == hover) return;
            MaxButtonHover = hover;
            var hole = MaxButtonBounds();
            if (!hole.IsEmpty) Invalidate(hole);
        }

        /// <summary>
        /// Cuts the maximize button's square out of the WebView, so the hit test there reaches
        /// the window and the button there is the one Windows knows about. Run when the page
        /// says where the button is and whenever the window changes size.
        /// </summary>
        private void ApplyMaxButtonCut()
        {
            if (WebView == null || WebView.IsDisposed) return;
            var hole = MaxButtonBounds();
            // The setter keeps the region it is given and lets go of the old one without
            // freeing it, and this runs many times a second during an edge drag.
            var old = WebView.Region;
            if (hole.IsEmpty)
            {
                if (old == null) return;
                WebView.Region = null;
                old.Dispose();
                return;
            }
            var region = new Region(new Rectangle(Point.Empty, WebView.Size));
            region.Exclude(hole);
            WebView.Region = region;
            old?.Dispose();
            Invalidate(hole);
        }

        /// <summary>
        /// Paints the button in the square the WebView gave up: the bar's gradient, the hover
        /// plate and the glyph, in the page's colours and at the page's size.
        /// </summary>
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var hole = MaxButtonBounds();
            if (hole.IsEmpty) return;

            var g = e.Graphics;
            var band = new Rectangle(hole.X, TitlebarTop, hole.Width, Math.Max(1, TitlebarHeight));
            using (var bar = new System.Drawing.Drawing2D.LinearGradientBrush(
                band, TitlebarTopColor, TitlebarBottomColor, System.Drawing.Drawing2D.LinearGradientMode.Vertical))
            {
                g.FillRectangle(bar, hole);
            }

            // The page's button is 30 px tall, so its height is the scale the page drew at.
            var k = hole.Height / 30f;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            if (MaxButtonHover)
            {
                using var plate = new System.Drawing.Drawing2D.GraphicsPath();
                var d = 12f * k;
                plate.AddArc(hole.X, hole.Y, d, d, 180, 90);
                plate.AddArc(hole.Right - d, hole.Y, d, d, 270, 90);
                plate.AddArc(hole.Right - d, hole.Bottom - d, d, d, 0, 90);
                plate.AddArc(hole.X, hole.Bottom - d, d, d, 90, 90);
                plate.CloseFigure();
                using var fill = new SolidBrush(WinButtonHoverBack);
                g.FillPath(fill, plate);
            }

            var unit = 11f * k;
            var x = hole.X + (hole.Width - unit) / 2f;
            var y = hole.Y + (hole.Height - unit) / 2f;
            using var pen = new Pen(MaxButtonHover ? WinButtonGlyphHover : WinButtonGlyph, 1.3f * k);
            if (WindowState == FormWindowState.Maximized)
            {
                g.DrawRectangle(pen, x + 1.2f * k, y + 3.2f * k, 6.6f * k, 6.6f * k);
                g.DrawRectangle(pen, x + 3.4f * k, y + 1.2f * k, 6.6f * k, 6.6f * k);
            }
            else
            {
                g.DrawRectangle(pen, x + 1.7f * k, y + 1.7f * k, 7.6f * k, 7.6f * k);
            }
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

            MinimumSize = MinimumFor(scale, TextZoom, maxWidth, maxHeight);

            ClientSize = new Size(
                ClampInt(ClientSize.Width, MinimumSize.Width, maxWidth),
                ClampInt(ClientSize.Height, MinimumSize.Height, maxHeight));
        }

        /// <summary>
        /// The stored Text size, read straight off the preferences. It is its own little reader
        /// because the window applies the zoom before the bridge has been asked anything, the
        /// same way the saved window size and the saved language are read here.
        /// </summary>
        private string ReadSavedTextSize()
        {
            try { return TextSizes.Normalize(UserPrefsProvider?.LoadPreferences()?.TextSize); }
            catch { return TextSizes.Normal; }
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
                    // A window snapped to half the screen is Normal too; the size to keep is
                    // the one Windows will give back when it comes off the edge.
                    var placement = new WindowPlacement { Length = Marshal.SizeOf<WindowPlacement>() };
                    var normal = GetWindowPlacement(Handle, ref placement) ? placement.NormalPosition.ToRectangle().Size : Size.Empty;
                    var size = WindowSnap.SizeToRemember(ClientSize, normal);
                    var width = (int)Math.Round(size.Width / scale);
                    var height = (int)Math.Round(size.Height / scale);
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
            var userDataFolder = WebViewUserDataFolder();

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

            // The page starts out believing it is in front. A window that finished loading
            // behind the game (started with Windows, say) has to be told otherwise, or the
            // fire runs until the first time the host clicks it and away again.
            core.NavigationCompleted += (s, e) => PostWindowActive(ActiveForm == this);

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

            // And the size it is read at, before the first frame rather than after it. Setting
            // the zoom once the page is up would show a host who chose Extra large one frame at
            // the size they did not choose, every launch.
            ApplyTextSize(ReadSavedTextSize());

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

            // Counted here and nowhere else: one place, before the handler runs, so a command
            // that threw is still a command a host issued. Nothing but the method name, the
            // profile it was about, and for a console line its verb, ever reaches the tally,
            // and the tally itself ignores every method that is not one of the commands.
            try { CountCommand(method, parameters); }
            catch { /* a count is never worth a command */ }

            // A command for one realm writes its application lines as that realm's, so with two
            // realms running the Saga of the other one does not read them. The scope follows the
            // handler through its awaits and into the work it starts.
            using var realmScope = AppLog.ForRealm(RealmOfCall(method, parameters));

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

                // Where the page drew the maximize button, in device pixels, so the host can
                // answer for it (see WindowSnap). Said again whenever the bar changes size.
                case "win.chrome":
                {
                    int Read(string key) => parameters.Value<int?>(key) ?? 0;
                    MaxButtonReported = new Rectangle(Read("x"), Read("y"), Read("w"), Read("h"));
                    MaxButtonPageWidth = Read("width");
                    TitlebarTop = Read("barTop");
                    TitlebarHeight = Read("barHeight");
                    ApplyMaxButtonCut();
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
        /// <summary>
        /// Adds one host command to the anonymous tally, when the opt-out allows it and the
        /// method is one of the commands. The bridge partial owns the tally and the profile;
        /// this is the seam so the dispatcher above does not have to know about either.
        /// </summary>
        partial void CountCommand(string method, JObject parameters);

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
