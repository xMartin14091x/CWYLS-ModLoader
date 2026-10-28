using CwyLs.ModLoader.Core.Console;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// B-UI: the scroll position counts from the newest line; paging and scrollbar maths. The range,
    /// follow and capacity helpers went with the pre-B-UI-FIX text view (C5): <c>LineLayout</c> and
    /// <c>LineScrollState</c> cover them now, with their own tests.
    /// </summary>
    public class ConsoleLineScrollTests
    {
        [Fact]
        public void Fewer_lines_than_fit_leave_nothing_to_scroll()
        {
            Assert.Equal(0, LineScroll.MaxOffset(7, 25));
            Assert.Equal(275, LineScroll.MaxOffset(300, 25));
        }

        [Theory]
        [InlineData(-3, 0)]
        [InlineData(10, 10)]
        [InlineData(1000, 275)]
        public void The_offset_stays_between_the_newest_and_the_oldest_line(int offset, int expected)
        {
            Assert.Equal(expected, LineScroll.Clamp(offset, 300, 25));
        }

        [Fact]
        public void A_page_is_a_screen_less_one_line_of_context()
        {
            Assert.Equal(24, LineScroll.Page(25));
            Assert.Equal(1, LineScroll.Page(1));
            Assert.Equal(1, LineScroll.Page(0));
        }

        [Fact]
        public void The_scrollbar_handle_shows_the_visible_share_but_stays_grabbable()
        {
            Assert.Equal(25f / 300f, LineScroll.HandleSize(300, 25), 4);
            Assert.Equal(LineScroll.MinHandleSize, LineScroll.HandleSize(100000, 25), 4);
            Assert.Equal(1f, LineScroll.HandleSize(10, 25), 4);
            Assert.Equal(1f, LineScroll.HandleSize(0, 25), 4);
        }

        [Theory]
        [InlineData(0, 0f)]
        [InlineData(275, 1f)]
        public void The_scrollbar_is_at_the_bottom_for_the_newest_and_the_top_for_the_oldest(int offset, float value)
        {
            Assert.Equal(value, LineScroll.ScrollbarValue(offset, 300, 25), 4);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(137)]
        [InlineData(274)]
        [InlineData(275)]
        public void Scrollbar_value_and_offset_round_trip(int offset)
        {
            var value = LineScroll.ScrollbarValue(offset, 300, 25);
            Assert.Equal(offset, LineScroll.OffsetFromScrollbar(value, 300, 25));
        }

        [Theory]
        [InlineData(-1f, 0)]
        [InlineData(2f, 275)]
        [InlineData(float.NaN, 0)]
        public void A_scrollbar_value_out_of_range_is_clamped(float value, int expected)
        {
            Assert.Equal(expected, LineScroll.OffsetFromScrollbar(value, 300, 25));
        }
    }
}
