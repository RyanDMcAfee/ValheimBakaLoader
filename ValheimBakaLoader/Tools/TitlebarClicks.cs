using System;
using System.Drawing;

namespace ValheimBakaLoader.Tools
{
    /// <summary>
    /// The two questions the fake title bar has to answer that a real caption answers by
    /// itself: was that second press the second half of a double click, and where does a
    /// maximized window land when the host drags it back down to size.
    /// <para>
    /// The window is borderless (FormBorderStyle.None), so Windows never sends
    /// WM_NCLBUTTONDBLCLK: there is no non client caption for a double click to land on.
    /// The page cannot answer it either. Its mousedown hands the window straight to
    /// Windows' own caption move loop, which owns the mouse until the button comes back
    /// up, and a browser only raises dblclick after two whole down/up/click cycles. So the
    /// host counts the presses itself, off the same two facts Windows would use: how long
    /// ago the last one was and how far away it was.
    /// </para>
    /// <para>
    /// Both are pure, so the rules are a table in the test suite rather than something only
    /// a person with a mouse can check. The live values come from
    /// SystemInformation.DoubleClickTime and SystemInformation.DoubleClickSize, which is
    /// what the host set in Mouse properties, not a number anybody typed here.
    /// </para>
    /// </summary>
    public static class TitlebarClicks
    {
        /// <summary>
        /// True when the press at <paramref name="nowTicks"/> pairs with the one at
        /// <paramref name="previousTicks"/>.
        /// <para>
        /// The rectangle is the one Windows uses: <paramref name="doubleClickSize"/> is its
        /// whole width and height, centred on the first press, so the second may be half of
        /// each away in any direction. A previousTicks of zero or less means there is no
        /// first press to pair with, which is how the caller says "start again" after a
        /// double click has been acted on: the third press of a fast sequence then opens a
        /// fresh one rather than toggling the window a second time.
        /// </para>
        /// </summary>
        /// <param name="previousTicks">Environment.TickCount64 at the previous press, or 0 for none.</param>
        /// <param name="nowTicks">Environment.TickCount64 at this press.</param>
        /// <param name="previousPosition">Screen position of the previous press.</param>
        /// <param name="position">Screen position of this press.</param>
        /// <param name="doubleClickTime">SystemInformation.DoubleClickTime, in milliseconds.</param>
        /// <param name="doubleClickSize">SystemInformation.DoubleClickSize, in pixels.</param>
        public static bool IsDoubleClick(
            long previousTicks,
            long nowTicks,
            Point previousPosition,
            Point position,
            int doubleClickTime,
            Size doubleClickSize)
        {
            if (previousTicks <= 0) return false;

            var elapsed = nowTicks - previousTicks;
            // A clock that went backwards is not a double click either, and neither is a
            // press one millisecond past the host's own setting.
            if (elapsed < 0 || elapsed > Math.Max(0, doubleClickTime)) return false;

            var dx = Math.Abs((long)position.X - previousPosition.X);
            var dy = Math.Abs((long)position.Y - previousPosition.Y);

            // DoubleClickSize is the whole width and height of the rectangle centred on the
            // first press, so a press counts while twice its distance fits inside it. Written
            // with the distance doubled to read like that sentence; it accepts exactly the same
            // presses as comparing against the halved size.
            return dx * 2 <= Math.Max(0, doubleClickSize.Width)
                && dy * 2 <= Math.Max(0, doubleClickSize.Height);
        }

        /// <summary>
        /// Where a window restored mid drag goes, so the pointer keeps its place on the
        /// title bar instead of the window jumping out from under it.
        /// <para>
        /// <paramref name="xRatio"/> is how far along the title bar the host took hold,
        /// measured while the window was still maximized, so the same fraction of the
        /// narrower bar ends up under the pointer. <paramref name="grabOffsetY"/> is how
        /// far below the top of the window the pointer was, which is the same distance in
        /// either state because the title bar is the top of the window in both. The result
        /// is clamped into the working area; a window wider or taller than that area is
        /// pinned to its top left corner rather than pushed off the other side.
        /// </para>
        /// </summary>
        public static Point RestoredLocation(
            Point cursor,
            int grabOffsetY,
            Size restoredSize,
            Rectangle workingArea,
            double xRatio)
        {
            var ratio = double.IsNaN(xRatio) ? 0.5 : xRatio;
            if (ratio < 0) ratio = 0;
            if (ratio > 1) ratio = 1;

            var x = cursor.X - (int)Math.Round(ratio * restoredSize.Width);
            var y = cursor.Y - Math.Max(0, grabOffsetY);

            return new Point(
                Clamp(x, workingArea.Left, workingArea.Right - restoredSize.Width),
                Clamp(y, workingArea.Top, workingArea.Bottom - restoredSize.Height));
        }

        private static int Clamp(int value, int low, int high)
        {
            if (high < low) return low;
            if (value < low) return low;
            return value > high ? high : value;
        }
    }
}
