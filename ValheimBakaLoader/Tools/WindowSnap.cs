using System.Drawing;

namespace ValheimBakaLoader.Tools
{
    /// <summary>
    /// The answers that let Windows snap the borderless window the way it snaps any other.
    /// <para>
    /// Snapping is Windows' own business and it only does it for a window that says it can
    /// be sized and maximized: a thick frame, a caption, the two boxes. The window keeps
    /// all of those as styles and then claims its whole rectangle as client area, so none
    /// of the frame is drawn and the page still paints every pixel. The title bar drag
    /// already runs through Windows' own move loop (win.dragStart), and with the styles in
    /// place that loop snaps to an edge, a side or a corner by itself.
    /// </para>
    /// <para>
    /// The snap layouts flyout is the one part the move loop cannot give: Windows 11 shows
    /// it when the pointer rests on the window's maximize button, and it asks the window
    /// where that is with WM_NCHITTEST. The page draws the button, but the WebView is a
    /// child window that would take every hit test, so the host cuts the button's square
    /// out of the WebView and answers HTMAXBUTTON there. Everything here is pure so the
    /// mapping is a table in the test suite rather than something only a mouse can check.
    /// </para>
    /// </summary>
    public static class WindowSnap
    {
        public const int HTCLIENT = 1;
        public const int HTMAXBUTTON = 9;

        public const int WS_MAXIMIZEBOX = 0x00010000;
        public const int WS_MINIMIZEBOX = 0x00020000;
        public const int WS_THICKFRAME = 0x00040000;
        public const int WS_SYSMENU = 0x00080000;
        public const int WS_CAPTION = 0x00C00000;

        /// <summary>The styles Windows reads before it will snap, size by Win+arrow or
        /// offer snap layouts.</summary>
        public const int SnapStyles = WS_CAPTION | WS_THICKFRAME | WS_SYSMENU | WS_MAXIMIZEBOX | WS_MINIMIZEBOX;

        /// <summary>A style with the snap styles added. WinForms rewrites the style from
        /// its own CreateParams on some property changes, so this is applied again every
        /// time the style changes rather than once.</summary>
        public static int WithSnapStyles(int style) => style | SnapStyles;

        /// <summary>
        /// What a point in the window answers. Only the maximize button's square is ever
        /// the host's: the drag area is the page's and goes through win.dragStart, the
        /// resize edges through win.resizeStart, and everything else is client.
        /// </summary>
        public static int HitTest(Point client, Rectangle maxButton) =>
            !maxButton.IsEmpty && maxButton.Contains(client) ? HTMAXBUTTON : HTCLIENT;

        /// <summary>
        /// Where the maximize button is now, from where the page last said it was. The
        /// button sits at the right of the bar, so it keeps its distance from the right
        /// edge: a window being sized wider moves it before the page has said so again.
        /// Anything that cannot be a button on this window answers empty, and an empty
        /// button means no cut and no hit test.
        /// </summary>
        public static Rectangle MaxButton(Rectangle reported, int reportedWidth, Size client)
        {
            if (reported.Width <= 0 || reported.Height <= 0 || reportedWidth <= 0) return Rectangle.Empty;
            if (reported.X < 0 || reported.Y < 0 || reported.Right > reportedWidth) return Rectangle.Empty;

            var fromRight = reportedWidth - reported.X;
            var moved = new Rectangle(client.Width - fromRight, reported.Y, reported.Width, reported.Height);
            if (moved.X < 0 || moved.Bottom > client.Height) return Rectangle.Empty;
            return moved;
        }

        /// <summary>
        /// The client rectangle the window claims. Normal or snapped, the whole window: no
        /// frame is drawn. Maximized, the work area of its display, because a maximized
        /// window with a thick frame hangs a frame's width past every edge of the screen and
        /// the title bar would start under the top of it. A work area that is not where the
        /// window is (another display's, which Windows answers with while a maximized window
        /// is being restored from the taskbar, issue 26) does not win, or the client would be
        /// placed outside the window and the page with it.
        /// </summary>
        public static Rectangle ClientRect(Rectangle proposed, bool maximized, Rectangle workArea) =>
            maximized && OnThisDisplay(proposed, workArea) ? workArea : proposed;

        /// <summary>
        /// True when most of the work area lies inside the proposed rectangle. A maximized
        /// window hangs a frame past every edge of its display, so its own work area is inside
        /// it almost whole, while a neighbouring display's overlaps it by that frame at most.
        /// </summary>
        private static bool OnThisDisplay(Rectangle proposed, Rectangle workArea)
        {
            if (workArea.IsEmpty) return false;
            var shared = Rectangle.Intersect(proposed, workArea);
            return (long)shared.Width * shared.Height * 2 >= (long)workArea.Width * workArea.Height;
        }

        /// <summary>
        /// The size to remember. Windows keeps the size a window had before it was snapped
        /// as its normal placement, and a snapped half of the screen is not a size the host
        /// chose, so the placement wins whenever Windows has one.
        /// </summary>
        public static Size SizeToRemember(Size current, Size normalPlacement) =>
            normalPlacement.Width > 0 && normalPlacement.Height > 0 ? normalPlacement : current;
    }
}
