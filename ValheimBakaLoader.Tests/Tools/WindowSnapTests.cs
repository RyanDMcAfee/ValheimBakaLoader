using System.Drawing;
using ValheimBakaLoader.Tools;
using Xunit;

namespace ValheimBakaLoader.Tests.Tools
{
    /// <summary>
    /// The mapping behind native snapping. The real behaviour (an edge, a side, a corner,
    /// Win+arrow, the snap layouts flyout) is the owner's to check by hand; what can be
    /// pinned is what the window answers and where it thinks the button is.
    /// </summary>
    public class WindowSnapTests
    {
        // A 38x30 button at 1.5x, 61 px in from the right of a 2112 px wide page.
        private static readonly Rectangle Button = new(2051 - 57, 7, 57, 45);
        private const int PageWidth = 2112;

        [Theory]
        [InlineData(1994, 7, WindowSnap.HTMAXBUTTON)]   // top left pixel of the button
        [InlineData(2050, 51, WindowSnap.HTMAXBUTTON)]  // bottom right pixel
        [InlineData(2020, 30, WindowSnap.HTMAXBUTTON)]  // the middle
        [InlineData(2051, 30, WindowSnap.HTCLIENT)]     // one past the right edge: the close button's side
        [InlineData(1993, 30, WindowSnap.HTCLIENT)]     // one before the left edge: minimize's side
        [InlineData(2020, 52, WindowSnap.HTCLIENT)]     // just under it
        [InlineData(400, 20, WindowSnap.HTCLIENT)]      // the drag area is the page's, through win.dragStart
        public void Only_the_maximize_button_is_the_hosts(int x, int y, int expected)
        {
            Assert.Equal(expected, WindowSnap.HitTest(new Point(x, y), Button));
        }

        [Fact]
        public void No_reported_button_answers_client_everywhere()
        {
            Assert.Equal(WindowSnap.HTCLIENT, WindowSnap.HitTest(new Point(0, 0), Rectangle.Empty));
            Assert.Equal(WindowSnap.HTCLIENT, WindowSnap.HitTest(new Point(10, 10), Rectangle.Empty));
        }

        [Fact]
        public void The_button_keeps_its_distance_from_the_right_edge()
        {
            // Same size as measured: the button is where the page said.
            Assert.Equal(Button, WindowSnap.MaxButton(Button, PageWidth, new Size(PageWidth, 1200)));

            // Sized 300 px narrower before the page has measured again: it moved with the edge.
            var narrower = WindowSnap.MaxButton(Button, PageWidth, new Size(PageWidth - 300, 1200));
            Assert.Equal(new Rectangle(Button.X - 300, Button.Y, Button.Width, Button.Height), narrower);
        }

        [Theory]
        [InlineData(0, 0, 0, 0, 2112)]         // nothing measured yet
        [InlineData(1994, 7, 57, 45, 0)]       // no page width
        [InlineData(-5, 7, 57, 45, 2112)]      // off the left
        [InlineData(2100, 7, 57, 45, 2112)]    // past the right of the page it was measured on
        public void A_report_that_cannot_be_a_button_is_no_button(int x, int y, int w, int h, int width)
        {
            Assert.Equal(Rectangle.Empty, WindowSnap.MaxButton(new Rectangle(x, y, w, h), width, new Size(2112, 1200)));
        }

        [Fact]
        public void A_window_too_small_for_the_button_has_none()
        {
            Assert.Equal(Rectangle.Empty, WindowSnap.MaxButton(Button, PageWidth, new Size(40, 1200)));
            Assert.Equal(Rectangle.Empty, WindowSnap.MaxButton(Button, PageWidth, new Size(PageWidth, 40)));
        }

        [Fact]
        public void The_styles_Windows_reads_before_it_snaps_are_all_added_and_nothing_is_taken()
        {
            const int popup = unchecked((int)0x80000000);
            const int visible = 0x10000000;
            var style = WindowSnap.WithSnapStyles(popup | visible);

            Assert.Equal(popup | visible, style & (popup | visible));
            Assert.Equal(WindowSnap.WS_THICKFRAME, style & WindowSnap.WS_THICKFRAME);
            Assert.Equal(WindowSnap.WS_CAPTION, style & WindowSnap.WS_CAPTION);
            Assert.Equal(WindowSnap.WS_MAXIMIZEBOX, style & WindowSnap.WS_MAXIMIZEBOX);
            Assert.Equal(WindowSnap.WS_MINIMIZEBOX, style & WindowSnap.WS_MINIMIZEBOX);
            Assert.Equal(WindowSnap.WS_SYSMENU, style & WindowSnap.WS_SYSMENU);
            // Applying it again on every style change is harmless.
            Assert.Equal(style, WindowSnap.WithSnapStyles(style));
        }

        [Fact]
        public void The_whole_window_is_client_and_a_maximized_one_stops_at_the_work_area()
        {
            var proposed = new Rectangle(-8, -8, 1936, 1056);   // maximized with a frame hanging off
            var work = new Rectangle(0, 0, 1920, 1040);          // the display above the taskbar

            Assert.Equal(work, WindowSnap.ClientRect(proposed, true, work));
            var normal = new Rectangle(200, 100, 1408, 800);
            Assert.Equal(normal, WindowSnap.ClientRect(normal, false, work));
            // No work area to read: the proposal stands rather than a zero sized window.
            Assert.Equal(proposed, WindowSnap.ClientRect(proposed, true, Rectangle.Empty));
        }

        /// <summary>Issue 26, the cause: restoring a maximized window from the taskbar on a PC
        /// with two displays, Windows answered the size question with the other display's work
        /// area (the handle's display cannot be read mid-restore), and the client area was
        /// placed there, outside the window: a blank page that took no clicks until a move made
        /// Windows ask again. The work area is read by rectangle now, and one that is not where
        /// the window is does not win.</summary>
        [Fact]
        public void A_work_area_from_another_display_does_not_put_the_client_outside_the_window()
        {
            var proposed = new Rectangle(1912, -8, 1936, 1056);   // maximized on the second display, frame hanging off
            var other = new Rectangle(0, 0, 1920, 1040);          // the first display's work area
            var own = new Rectangle(1920, 0, 1920, 1040);         // the second display's

            Assert.Equal(proposed, WindowSnap.ClientRect(proposed, true, other));
            Assert.Equal(own, WindowSnap.ClientRect(proposed, true, own));
            // A display below, touching by the frame's height, is not it either.
            Assert.Equal(proposed, WindowSnap.ClientRect(proposed, true, new Rectangle(1920, 1048, 1920, 1040)));

            var host = AppSourceTree.Read("ValheimBakaLoader", "Forms", "BlendWindow.cs");
            Assert.Contains("maximized ? WorkAreaOf(proposed) : Rectangle.Empty", host);
            Assert.Contains("MonitorFromRect(ref rect, MONITOR_DEFAULTTONEAREST)", host);
            Assert.DoesNotContain("MonitorFromWindow", host);
        }

        /// <summary>Defined is not wired: the window answers with this mapping, and the page
        /// tells it where the button is every time the bar changes size.</summary>
        [Fact]
        public void The_window_and_the_page_both_use_it()
        {
            var host = AppSourceTree.Read("ValheimBakaLoader", "Forms", "BlendWindow.cs");
            Assert.Contains("WindowSnap.HitTest(PointToClient(screen), MaxButtonBounds()) == WindowSnap.HTMAXBUTTON", host);
            Assert.Contains("WindowSnap.WithSnapStyles(GetWindowStyle(Handle, GWL_STYLE))", host);
            Assert.Contains("Marshal.WriteInt32(m.LParam, 4, WindowSnap.WithSnapStyles(next));", host);
            Assert.Contains("WindowSnap.ClientRect(proposed, maximized,", host);
            Assert.Contains("WindowSnap.SizeToRemember(ClientSize, normal)", host);
            Assert.Contains("case \"win.chrome\":", host);
            Assert.Contains("region.Exclude(hole);", host);
            // The styles stay out of CreateParams, or every ClientSize the host sets would be
            // a frame larger than the window it claims.
            Assert.DoesNotContain("override CreateParams", host);

            var page = AppSourceTree.Web("app.js");
            Assert.Contains("Native.post(\"win.chrome\",", page);
            Assert.Contains("ro.observe($(\"#titlebar\"));", page);
            Assert.Contains("if(TB_MAXBTN) ro.observe(TB_MAXBTN);", page);
        }

        /// <summary>Issue 26: a child WebView hears nothing when its window is minimized or
        /// restored, and the page came back blank on some machines until the window moved.
        /// The host hides it on the way down and shows it on the way up.</summary>
        [Fact]
        public void The_page_goes_to_the_taskbar_with_the_window_and_comes_back_with_it()
        {
            var host = AppSourceTree.Read("ValheimBakaLoader", "Forms", "BlendWindow.cs");
            var resize = host.Substring(host.IndexOf("protected override void OnResize(EventArgs e)"));
            resize = resize.Substring(0, resize.IndexOf("if (minimized) return;"));
            Assert.Contains("KeepWebViewWithWindow(minimized);", resize);
            Assert.Contains("WebView.Visible = !minimized;", host);
            Assert.Contains("if (!minimized) ActiveControl = WebView;", host);
        }

        [Fact]
        public void A_snapped_window_remembers_the_size_it_had_before_the_edge()
        {
            var half = new Size(960, 1040);
            var before = new Size(1408, 800);
            Assert.Equal(before, WindowSnap.SizeToRemember(half, before));
            // Not snapped: Windows' normal placement is the window itself.
            Assert.Equal(before, WindowSnap.SizeToRemember(before, before));
            // No placement to read: the window's own size.
            Assert.Equal(half, WindowSnap.SizeToRemember(half, Size.Empty));
        }
    }
}
