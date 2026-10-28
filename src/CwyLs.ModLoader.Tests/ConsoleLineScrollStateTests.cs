using System;
using CwyLs.ModLoader.Core.Console;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// B-UI-FIX: the line view's scroll position keeps what the reader asked for - follow the
    /// newest line, stay on the lines being read, or stay at the top - through new lines, window
    /// resizes and tab switches.
    /// </summary>
    public class ConsoleLineScrollStateTests
    {
        private const float Row = 20f;
        private const float Spacing = 4f;
        private const float Box = 230f;

        private static readonly Func<int, float> Fixed = _ => Row;

        [Fact]
        public void A_new_view_follows_the_newest_line()
        {
            var state = new LineScrollState();
            var plan = state.Layout(100, Box, Spacing, Fixed);

            Assert.Equal(LineAnchor.Bottom, plan.Anchor);
            Assert.Equal(100, plan.End);
            Assert.False(state.PinnedToTop);
        }

        [Fact]
        public void Following_keeps_following_when_lines_arrive()
        {
            var state = new LineScrollState();
            state.Layout(100, Box, Spacing, Fixed);

            state.Appended(5);
            var plan = state.Layout(105, Box, Spacing, Fixed);

            Assert.Equal(0, plan.Offset);
            Assert.Equal(105, plan.End);
        }

        [Fact]
        public void A_scrolled_up_view_stays_on_its_lines_when_lines_arrive()
        {
            var state = new LineScrollState();
            state.Layout(100, Box, Spacing, Fixed);
            state.Scroll(10);
            var before = state.Layout(100, Box, Spacing, Fixed);

            state.Appended(5);
            var after = state.Layout(105, Box, Spacing, Fixed);

            Assert.Equal(90, before.End);
            Assert.Equal(90, after.End);
            Assert.Equal(15, after.Offset);
        }

        [Fact]
        public void ToStart_puts_line_zero_at_the_top_and_keeps_it_there_through_a_resize()
        {
            var state = new LineScrollState();
            state.ToStart();

            var tall = state.Layout(100, 500f, Spacing, Fixed);
            var small = state.Layout(100, 120f, Spacing, Fixed);
            var grown = state.Layout(140, 120f, Spacing, Fixed);

            foreach (var plan in new[] { tall, small, grown })
            {
                Assert.Equal(LineAnchor.Top, plan.Anchor);
                Assert.Equal(0, plan.Start);
            }

            Assert.True(small.MaxOffset > tall.MaxOffset);
        }

        [Fact]
        public void A_following_view_keeps_the_newest_line_at_the_bottom_through_a_resize()
        {
            var state = new LineScrollState();
            foreach (var box in new[] { 230f, 90f, 600f, 230f })
            {
                var plan = state.Layout(100, box, Spacing, Fixed);
                Assert.Equal(LineAnchor.Bottom, plan.Anchor);
                Assert.Equal(100, plan.End);
            }
        }

        [Fact]
        public void A_view_scrolled_part_way_keeps_its_bottom_line_through_a_resize()
        {
            var state = new LineScrollState();
            state.Layout(100, Box, Spacing, Fixed);
            state.Scroll(20);

            Assert.Equal(80, state.Layout(100, Box, Spacing, Fixed).End);
            Assert.Equal(80, state.Layout(100, 400f, Spacing, Fixed).End);
            Assert.Equal(80, state.Layout(100, 100f, Spacing, Fixed).End);
        }

        [Fact]
        public void Growing_the_box_until_everything_fits_shows_it_from_the_top()
        {
            var state = new LineScrollState();
            state.Layout(20, Box, Spacing, Fixed);

            var plan = state.Layout(20, 2000f, Spacing, Fixed);

            Assert.True(plan.Fits);
            Assert.Equal(LineAnchor.Top, plan.Anchor);
            Assert.Equal(0, plan.Start);
            Assert.Equal(20, plan.End);
        }

        [Fact]
        public void Scrolling_up_to_the_top_pins_it_and_scrolling_down_unpins_it()
        {
            var state = new LineScrollState();
            state.Layout(100, Box, Spacing, Fixed);

            state.Scroll(1000);
            Assert.True(state.PinnedToTop);
            Assert.Equal(LineAnchor.Top, state.Layout(100, Box, Spacing, Fixed).Anchor);

            state.Scroll(-3);
            Assert.False(state.PinnedToTop);
            var plan = state.Layout(100, Box, Spacing, Fixed);
            Assert.Equal(LineAnchor.Bottom, plan.Anchor);
            Assert.Equal(plan.MaxOffset - 3, plan.Offset);
        }

        [Fact]
        public void A_pinned_view_stays_at_the_top_while_lines_arrive()
        {
            var state = new LineScrollState();
            state.ToStart();
            state.Layout(100, Box, Spacing, Fixed);

            state.Appended(7);
            var plan = state.Layout(107, Box, Spacing, Fixed);

            Assert.Equal(LineAnchor.Top, plan.Anchor);
            Assert.Equal(0, plan.Start);
        }

        [Fact]
        public void ToEnd_follows_the_newest_line_again()
        {
            var state = new LineScrollState();
            state.ToStart();
            state.Layout(100, Box, Spacing, Fixed);

            state.ToEnd();
            var plan = state.Layout(100, Box, Spacing, Fixed);

            Assert.False(state.PinnedToTop);
            Assert.Equal(0, plan.Offset);
            Assert.Equal(LineAnchor.Bottom, plan.Anchor);
        }

        [Fact]
        public void A_page_is_the_wholly_visible_lines_less_one()
        {
            var state = new LineScrollState();
            var plan = state.Layout(100, Box, Spacing, Fixed);

            state.PageUp();
            Assert.Equal(plan.FullyVisible - 1, state.Offset);

            state.PageDown();
            Assert.Equal(0, state.Offset);
        }

        [Fact]
        public void Scrolling_before_the_first_layout_goes_nowhere()
        {
            var state = new LineScrollState();
            state.Scroll(5);

            Assert.Equal(0, state.Offset);
            Assert.False(state.PinnedToTop);
        }

        [Fact]
        public void The_scrollbar_follows_the_plan_and_drives_it()
        {
            var state = new LineScrollState();
            var plan = state.Layout(100, Box, Spacing, Fixed);
            Assert.Equal(0f, state.ScrollbarValue);
            Assert.Equal((float)plan.PageLines / 100, state.HandleSize, 4);

            state.FromScrollbar(1f);
            Assert.True(state.PinnedToTop);
            Assert.Equal(1f, state.ScrollbarValue);
            Assert.Equal(LineAnchor.Top, state.Layout(100, Box, Spacing, Fixed).Anchor);

            state.FromScrollbar(0.5f);
            Assert.False(state.PinnedToTop);
            var middle = state.Layout(100, Box, Spacing, Fixed);
            Assert.Equal(LineAnchor.Bottom, middle.Anchor);
            Assert.InRange(middle.Offset, 44, 47);

            state.FromScrollbar(0f);
            Assert.Equal(0, state.Layout(100, Box, Spacing, Fixed).Offset);
        }

        [Fact]
        public void Content_that_fits_has_a_full_handle_at_rest()
        {
            var state = new LineScrollState();
            state.Layout(5, Box, Spacing, Fixed);

            Assert.Equal(1f, state.HandleSize);
            Assert.Equal(0f, state.ScrollbarValue);
        }

        [Fact]
        public void Every_change_of_position_bumps_the_version_and_a_layout_does_not()
        {
            var state = new LineScrollState();
            var v0 = state.Version;

            state.Layout(100, Box, Spacing, Fixed);
            Assert.Equal(v0, state.Version);

            state.Appended(0);
            Assert.Equal(v0, state.Version);

            state.Appended(2);
            state.Scroll(1);
            state.ToStart();
            state.ToEnd();
            state.FromScrollbar(0.3f);
            Assert.Equal(v0 + 5, state.Version);
        }

        [Fact]
        public void A_huge_scroll_does_not_overflow()
        {
            var state = new LineScrollState();
            state.Layout(100, Box, Spacing, Fixed);
            state.Scroll(10);
            state.Appended(int.MaxValue);
            state.Scroll(int.MaxValue);

            var plan = state.Layout(100, Box, Spacing, Fixed);
            Assert.Equal(plan.MaxOffset, plan.Offset);
        }
    }
}
