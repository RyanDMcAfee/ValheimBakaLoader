using System.Drawing;
using ValheimBakaLoader.Tools;
using Xunit;

namespace ValheimBakaLoader.Tests.Tools
{
    /// <summary>
    /// The double click the window has to count for itself, as a table.
    /// <para>
    /// The window is borderless, so Windows never sends WM_NCLBUTTONDBLCLK and the page
    /// cannot see the second click either: the first press hands the window to the native
    /// caption move loop, which owns the mouse until the button comes back up. The rule
    /// lives in the host instead, and because it is pure it can be proved here rather than
    /// only by a person with a mouse, which matters while the live server keeps a second
    /// instance of the app from being launched.
    /// </para>
    /// </summary>
    public class TitlebarClicksTests
    {
        private const int Time = 500;   // the Windows default
        private const int Size = 4;     // and the default rectangle, 4 by 4

        /// <summary>
        /// Inside the host's own double click time AND inside the rectangle centred on the
        /// first press. One millisecond late or one pixel wide and it is two clicks, which
        /// is exactly the difference between moving the window and maximizing it.
        /// </summary>
        [Theory]
        // previous, now, prevX, prevY, x, y, expected
        [InlineData(1000, 1400, 400, 12, 400, 12, true)]    // same spot, well inside the time
        [InlineData(1000, 1000, 400, 12, 400, 12, true)]    // as fast as a clock can report
        [InlineData(1000, 1500, 400, 12, 400, 12, true)]    // the last millisecond that counts
        [InlineData(1000, 1501, 400, 12, 400, 12, false)]   // one millisecond late
        [InlineData(1000, 3000, 400, 12, 400, 12, false)]   // a second press much later
        [InlineData(1000, 1400, 400, 12, 402, 12, true)]    // half the rectangle, to the right
        [InlineData(1000, 1400, 400, 12, 398, 14, true)]    // and half of it the other way
        [InlineData(1000, 1400, 400, 12, 403, 12, false)]   // one pixel past the rectangle
        [InlineData(1000, 1400, 400, 12, 400, 15, false)]   // past it downwards
        [InlineData(1000, 1400, 400, 12, 403, 15, false)]   // past it in both
        [InlineData(0, 100, 400, 12, 400, 12, false)]       // no first press to pair with, even a fast one in the same spot
        [InlineData(1000, 900, 400, 12, 400, 12, false)]    // a clock that went backwards
        public void A_second_press_counts_only_inside_the_time_and_the_rectangle(
            long previousTicks, long nowTicks, int previousX, int previousY, int x, int y, bool expected)
        {
            var answer = TitlebarClicks.IsDoubleClick(
                previousTicks,
                nowTicks,
                new Point(previousX, previousY),
                new Point(x, y),
                Time,
                new Size(Size, Size));

            Assert.Equal(expected, answer);
        }

        /// <summary>
        /// The rectangle is the host's, not this file's. A machine set to a wider one accepts
        /// a wider miss; the size is the whole width centred on the first press, so half of
        /// it either way is in and one pixel past that is out.
        /// </summary>
        [Theory]
        [InlineData(20, 2, true)]
        [InlineData(20, 10, true)]
        [InlineData(20, 11, false)]
        [InlineData(5, 2, true)]     // 2 away inside a 5 wide rectangle
        [InlineData(5, 3, false)]
        [InlineData(0, 0, true)]     // a rectangle of nothing still accepts the same pixel
        [InlineData(0, 1, false)]
        public void The_rectangle_is_whatever_the_host_set_it_to(int width, int away, bool expected)
        {
            var answer = TitlebarClicks.IsDoubleClick(
                1000, 1100, new Point(400, 12), new Point(400 + away, 12), Time, new Size(width, width));

            Assert.Equal(expected, answer);
        }

        /// <summary>
        /// Three fast presses are one double click and then the start of something new, not
        /// two double clicks. The host acts on the pair and clears what it was holding, and a
        /// cleared record is what the first argument of 0 means here, so the third press opens
        /// a fresh sequence instead of toggling the window straight back.
        /// </summary>
        [Fact]
        public void A_third_press_starts_a_new_sequence_rather_than_toggling_again()
        {
            var where = new Point(400, 12);

            // First press: nothing to pair with, so the host records it and drags as before.
            Assert.False(TitlebarClicks.IsDoubleClick(0, 1000, where, where, Time, new Size(Size, Size)));

            // Second: inside both, so the window toggles and the host clears the record.
            Assert.True(TitlebarClicks.IsDoubleClick(1000, 1100, where, where, Time, new Size(Size, Size)));

            // Third, just as fast: the record is gone, so this is a first press again.
            Assert.False(TitlebarClicks.IsDoubleClick(0, 1200, where, where, Time, new Size(Size, Size)));
        }

        // ------------------------------------------------------------- where a restore lands

        /// <summary>
        /// Dragging a maximized window brings it back to size under the pointer. The pointer
        /// keeps its fraction of the way along the title bar, so taking hold near the right
        /// hand end of a 3440 wide bar does not fling the window off to the left.
        /// </summary>
        [Theory]
        [InlineData(0.0, 1700)]      // held at the far left: the window starts under the pointer
        [InlineData(0.5, 996)]       // held in the middle: half the restored width to the left
        [InlineData(1.0, 292)]       // held at the far right: the whole width to the left
        public void A_restored_window_keeps_the_pointer_at_the_same_fraction_of_its_title_bar(
            double xRatio, int expectedX)
        {
            var at = TitlebarClicks.RestoredLocation(
                cursor: new Point(1700, 18),
                grabOffsetY: 18,
                restoredSize: new Size(1408, 800),
                workingArea: new Rectangle(0, 0, 3440, 1400),
                xRatio: xRatio);

            Assert.Equal(expectedX, at.X);
            Assert.Equal(0, at.Y);        // the pointer stays 18px down, where it took hold
        }

        /// <summary>
        /// And it lands on screen. A grab at either end of an ultra wide bar would otherwise
        /// put half the window past the edge of the desktop, and a window larger than the
        /// working area is pinned to its corner rather than pushed off the far side.
        /// </summary>
        [Theory]
        // cursor x, ratio, window width, working area, expected x
        [InlineData(60, 1.0, 1408, 0, 3440, 0)]          // would sit at -1348
        [InlineData(3400, 0.0, 1408, 0, 3440, 2032)]     // would sit at 3400, past the right edge
        [InlineData(1700, 0.5, 4000, 0, 3440, 0)]        // wider than the desktop: pinned left
        [InlineData(1700, 0.5, 1408, 1920, 1920, 1920)]  // a second monitor starts at 1920
        public void A_restored_window_is_clamped_into_the_working_area(
            int cursorX, double xRatio, int width, int workLeft, int workWidth, int expectedX)
        {
            var at = TitlebarClicks.RestoredLocation(
                cursor: new Point(cursorX, 18),
                grabOffsetY: 18,
                restoredSize: new Size(width, 800),
                workingArea: new Rectangle(workLeft, 0, workWidth, 1400),
                xRatio: xRatio);

            Assert.Equal(expectedX, at.X);
        }

        /// <summary>A ratio from a page that measured a bar of zero width cannot throw.</summary>
        [Theory]
        [InlineData(double.NaN, 996)]
        [InlineData(-2.0, 1700)]
        [InlineData(7.0, 292)]
        public void A_ratio_that_makes_no_sense_is_brought_back_into_range(double xRatio, int expectedX)
        {
            var at = TitlebarClicks.RestoredLocation(
                cursor: new Point(1700, 18),
                grabOffsetY: 18,
                restoredSize: new Size(1408, 800),
                workingArea: new Rectangle(0, 0, 3440, 1400),
                xRatio: xRatio);

            Assert.Equal(expectedX, at.X);
        }
    }
}
