using System;
using System.Text.RegularExpressions;
using ValheimBakaLoader.Tests.Tools;
using Xunit;

namespace ValheimBakaLoader.Tests.Forms
{
    /// <summary>
    /// The title bar's two halves, pinned where they are written.
    /// <para>
    /// Neither end of this can be reached by instantiating anything: the host half lives in
    /// a Form that needs a real WebView2 and an HWND, and the page half is a listener on a
    /// document. Both are read as source instead, which is the convention this suite already
    /// uses for the title bar (see
    /// <see cref="WebUiLanguageGroundworkTests.The_titlebar_drag_handler_leaves_interactive_elements_alone"/>).
    /// What a source gate can hold is the ORDER and the GUARDS, and those are exactly the two
    /// things that would quietly rot: a double click that stopped being counted before the
    /// window was handed to the native move loop is a double click that never happens, and a
    /// restore posted from a window that is not maximized moves the window for no reason.
    /// </para>
    /// <para>
    /// The live click test is deferred: a second BakaLoader may not be launched while the
    /// server is running, so double clicking both directions, dragging a maximized window on
    /// an ultra wide layout and watching the glyph swap all belong to the stopped-window
    /// checklist.
    /// </para>
    /// </summary>
    public class TitlebarWindowStateTests
    {
        private static string AppJs() => AppSourceTree.Web("app.js");

        private static string BlendWindow() => AppSourceTree.Files()["BlendWindow.cs"];

        /// <summary>The text between two markers, so a gate can ask about order inside one case.</summary>
        private static string Between(string text, string from, string to)
        {
            var start = text.IndexOf(from, StringComparison.Ordinal);
            Assert.True(start >= 0, "the source no longer has: " + from);
            var end = text.IndexOf(to, start + from.Length, StringComparison.Ordinal);
            Assert.True(end > start, "the source no longer has: " + to);
            return text.Substring(start, end - start);
        }

        // ------------------------------------------------------------------ the page

        /// <summary>
        /// The press handler is the same drag handler it always was, with the maximized case
        /// added under it. Anything interactive in the bar is still excluded first: a control
        /// added there must not move the window, and it must not arm the restore either.
        /// </summary>
        [Fact]
        public void The_titlebar_press_still_excludes_interactive_elements_before_anything_else()
        {
            var handler = Between(AppJs(), "$(\"#titlebar\").addEventListener(\"mousedown\"", "\n});");

            Assert.Contains("const TB_NO_DRAG=\".cmdchip,.winbtns,.winbtn,.tb-interactive\";", AppJs());
            Assert.Contains("if(e.target.closest(TB_NO_DRAG)) return;", handler);

            // Before the message, and before anything the press arms.
            var excluded = handler.IndexOf("closest(TB_NO_DRAG)", StringComparison.Ordinal);
            var posted = handler.IndexOf("Native.post(\"win.dragStart\")", StringComparison.Ordinal);
            var armed = handler.IndexOf("TB_DRAG={", StringComparison.Ordinal);
            Assert.True(excluded < posted, "the exclusion no longer runs before the drag message");
            Assert.True(excluded < armed, "the exclusion no longer runs before the restore is armed");
        }

        /// <summary>
        /// The restore is only ever posted from a maximized window. The press arms it behind
        /// the state check and nothing else arms it, so a normal window cannot reach the
        /// message however far the pointer travels, and a plain click cannot either: the
        /// pointer has to pass the slop first.
        /// </summary>
        [Fact]
        public void The_restore_is_only_posted_from_a_maximized_window()
        {
            var js = AppJs();
            var handler = Between(js, "$(\"#titlebar\").addEventListener(\"mousedown\"", "\n});");

            Assert.Contains("if(document.body.dataset.win!==\"max\") return;", handler);
            var gated = handler.IndexOf("dataset.win!==\"max\"", StringComparison.Ordinal);
            var armed = handler.IndexOf("TB_DRAG={", StringComparison.Ordinal);
            Assert.True(gated < armed, "the press arms the restore before it checks the state");

            // One place arms it, one place sends it, and the send is behind both the arm and
            // the distance. Two arms would be two ways to reach the message, and only one of
            // them would be the one this gate is reading.
            Assert.Single(Regex.Matches(js, Regex.Escape("TB_DRAG={")));
            Assert.Single(Regex.Matches(js, Regex.Escape("Native.post(\"win.dragRestore\"")));

            var move = Between(js, "window.addEventListener(\"mousemove\"", "\n});");
            Assert.Contains("if(!TB_DRAG) return;", move);
            Assert.Contains("TB_DRAG_SLOP", move);
            Assert.Contains("Native.post(\"win.dragRestore\",{xRatio:held.xRatio});", move);
            Assert.True(
                move.IndexOf("if(!TB_DRAG) return;", StringComparison.Ordinal)
                    < move.IndexOf("Native.post(\"win.dragRestore\"", StringComparison.Ordinal),
                "the move handler posts the restore before it checks that a press armed it");
        }

        /// <summary>
        /// The state the host pushes is the one thing everything else reads, and the button's
        /// tooltip is a catalog id in both states rather than a sentence written here.
        /// </summary>
        [Fact]
        public void The_page_takes_its_window_state_from_the_host_and_swaps_the_glyph_with_it()
        {
            var js = AppJs();

            Assert.Contains("Native.on(\"win.state\",d=>{", js);
            Assert.Contains("document.body.dataset.win=d&&d.maximized?\"max\":\"normal\";", js);

            var render = Between(js, "function renderWinState(){", "\n}");
            Assert.Contains("T(\"titlebar.win.restore\")", render);
            Assert.Contains("T(\"titlebar.win.maximize\")", render);
            Assert.Contains("TB_GLYPH_RESTORE", render);
            Assert.Contains("TB_GLYPH_MAXIMIZE", render);

            // The maximize glyph is the markup's own, so the two cannot drift apart.
            Assert.Contains("const TB_GLYPH_MAXIMIZE=TB_MAXBTN?TB_MAXBTN.innerHTML:\"\";", js);
            // And the restore glyph is the two overlapping squares.
            Assert.Equal(2, Regex.Matches(
                Between(js, "const TB_GLYPH_RESTORE=", "</svg>'"), "<rect ").Count);

            // Repainted with the rest of the copy when the catalog lands, so a tooltip asked
            // for before the words arrived is not left reading its own id.
            Assert.Contains("try{renderWinState();}catch(_){}", js);

            // And a walk can drive it, because nothing in a browser pushes win.state.
            Assert.Contains("winState:maximized=>{", js);
        }

        // ------------------------------------------------------------------ the host

        /// <summary>
        /// The double click is counted BEFORE the window is handed to Windows' own caption
        /// move loop. Once ReleaseCapture and WM_NCLBUTTONDOWN have gone out, the loop owns
        /// the mouse and there is no second click left to count, so the order here is the
        /// whole feature rather than a matter of taste.
        /// </summary>
        [Fact]
        public void The_drag_message_counts_the_double_click_before_it_enters_the_move_loop()
        {
            var press = Between(BlendWindow(), "case \"win.dragStart\":", "case \"win.dragRestore\":");

            Assert.Contains("TitlebarClicks.IsDoubleClick(", press);
            Assert.Contains("SystemInformation.DoubleClickTime", press);
            Assert.Contains("SystemInformation.DoubleClickSize", press);

            var counted = press.IndexOf("TitlebarClicks.IsDoubleClick(", StringComparison.Ordinal);
            var loop = press.IndexOf("WM_NCLBUTTONDOWN", StringComparison.Ordinal);
            Assert.True(loop > 0, "the drag no longer enters the native move loop at all");
            Assert.True(counted < loop, "the move loop is entered before the double click is counted");

            // A pair toggles and does NOT also drag, and the record is cleared so a third
            // press opens a new sequence rather than toggling the window straight back.
            var pair = Between(press, "if (second)", "LastTitlebarPressTicks = pressedAt;");
            Assert.Contains("LastTitlebarPressTicks = 0;", pair);
            Assert.Contains("FormWindowState.Maximized", pair);
            Assert.Contains("break;", pair);
        }

        /// <summary>
        /// The restore only answers a maximized window, reads the grab offset while the window
        /// is still maximized, and hands the move straight back to Windows.
        /// </summary>
        [Fact]
        public void The_restore_message_puts_the_window_under_the_pointer_and_keeps_the_move_going()
        {
            var restore = Between(BlendWindow(), "case \"win.dragRestore\":", "case \"win.resizeStart\":");

            Assert.Contains("if (WindowState != FormWindowState.Maximized) break;", restore);
            Assert.Contains("TitlebarClicks.RestoredLocation(", restore);
            Assert.Contains("Screen.FromPoint(cursor).WorkingArea", restore);
            Assert.Contains("WM_NCLBUTTONDOWN", restore);

            // The offset down the title bar is read BEFORE the window comes back to size,
            // because after that the window has already moved out from under the pointer.
            var measured = restore.IndexOf("var grabOffsetY = cursor.Y - Top;", StringComparison.Ordinal);
            var restored = restore.IndexOf("WindowState = FormWindowState.Normal;", StringComparison.Ordinal);
            Assert.True(measured > 0, "the grab offset is no longer measured");
            Assert.True(measured < restored, "the grab offset is measured after the window changed size");
        }

        /// <summary>
        /// The page is told the state from the resize handler, which is the one place every
        /// maximize and every restore passes through, and once more when a page finishes
        /// loading and has therefore heard nothing at all.
        /// </summary>
        [Fact]
        public void The_window_state_is_posted_from_the_resize_handler_and_once_on_load()
        {
            var source = BlendWindow();

            var resize = Between(source, "protected override void OnResize(EventArgs e)", "\n        }");
            Assert.Contains("base.OnResize(e);", resize);
            Assert.Contains("PostWindowState();", resize);

            Assert.Contains("PostEvent(\"win.state\", new { maximized });", source);
            Assert.Contains("core.NavigationCompleted += (s, e) => PostWindowState(force: true);", source);

            // Registered before the page is asked to load, or the load it is waiting for has
            // already happened by the time anything is listening for it.
            Assert.True(
                source.IndexOf("core.NavigationCompleted +=", StringComparison.Ordinal)
                    < source.IndexOf("core.Navigate(", StringComparison.Ordinal),
                "the state push is wired after Navigate, so the first load can slip past it");
        }
    }
}
