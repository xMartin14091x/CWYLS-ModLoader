using System;
using CwyLs.ModLoader.Core.Console;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// B-UI-FIX: the game-style console's line view pins line 0 to the top of the text box when it
    /// is scrolled to the top (or everything fits) and the newest line to the bottom when it
    /// follows, whatever the line heights - wrapped lines and fallback-font rows included. The old
    /// view drew a fixed number of logical lines bottom-aligned, which hid line 0 under the top
    /// edge when lines wrapped and left an empty band above short content.
    /// </summary>
    public class ConsoleLineLayoutTests
    {
        private const float Row = 20f;
        private const float Spacing = 4f;

        private static Func<int, float> Fixed(float height) => _ => height;

        private static Func<int, float> Heights(params float[] heights) => i => heights[i];

        [Fact]
        public void An_empty_list_draws_nothing()
        {
            var plan = LineLayout.Plan(0, 0, 300f, Spacing, Fixed(Row));

            Assert.Equal(0, plan.Count);
            Assert.True(plan.Fits);
            Assert.Equal(LineAnchor.Top, plan.Anchor);
        }

        [Fact]
        public void Content_that_fits_starts_at_the_top_with_no_band_above_it()
        {
            // The State tab: nine short lines in a tall box. The old view drew them bottom-aligned.
            var plan = LineLayout.Plan(9, 0, 500f, Spacing, Fixed(Row));

            Assert.Equal(LineAnchor.Top, plan.Anchor);
            Assert.Equal(0, plan.Start);
            Assert.Equal(9, plan.End);
            Assert.True(plan.Fits);
            Assert.Equal(0, plan.Offset);
            Assert.Equal(9, plan.FullyVisible);
        }

        [Fact]
        public void A_run_is_its_heights_plus_one_spacing_between_each_pair()
        {
            Assert.Equal(10 * Row + (9 * Spacing), LineLayout.RunHeight(0, 10, Spacing, Fixed(Row)), 3);
            Assert.Equal(0f, LineLayout.RunHeight(3, 3, Spacing, Fixed(Row)));
            Assert.Equal(20f + 4f + 60f, LineLayout.RunHeight(0, 2, Spacing, Heights(20f, 60f)), 3);
        }

        [Fact]
        public void Lines_fit_exactly_when_their_run_equals_the_box()
        {
            var box = LineLayout.RunHeight(0, 10, Spacing, Fixed(Row));

            Assert.True(LineLayout.Plan(10, 0, box, Spacing, Fixed(Row)).Fits);
            Assert.False(LineLayout.Plan(11, 0, box, Spacing, Fixed(Row)).Fits);
        }

        [Fact]
        public void Following_the_newest_line_pins_it_to_the_bottom_and_fills_the_box()
        {
            var plan = LineLayout.Plan(100, 0, 230f, Spacing, Fixed(Row));

            Assert.Equal(LineAnchor.Bottom, plan.Anchor);
            Assert.Equal(100, plan.End);
            Assert.Equal(0, plan.Offset);

            // Line 99-k ends 24k above the bottom: lines 90..99 reach into the box, 91..99 wholly.
            Assert.Equal(90, plan.Start);
            Assert.Equal(9, plan.FullyVisible);
            Assert.True(LineLayout.RunHeight(plan.Start, plan.End, Spacing, Fixed(Row)) >= 230f);
        }

        [Fact]
        public void Scrolled_to_the_top_line_zero_starts_at_the_top_edge()
        {
            var plan = LineLayout.Plan(100, int.MaxValue, 230f, Spacing, Fixed(Row));

            // Line k ends at 24k + 20: lines 0..8 are whole (line 8 ends at 212), line 9 starts
            // at 216 and is cut by the bottom edge, line 10 would start below it.
            Assert.Equal(LineAnchor.Top, plan.Anchor);
            Assert.Equal(0, plan.Start);
            Assert.Equal(10, plan.End);
            Assert.Equal(9, plan.PageLines);
            Assert.Equal(9, plan.FullyVisible);
            Assert.Equal(91, plan.MaxOffset);
            Assert.Equal(91, plan.Offset);
        }

        [Fact]
        public void Wrapped_lines_at_the_top_keep_line_zero_visible()
        {
            // Targets tab: a header, then long lines that wrap to two or three rows. The old view
            // drew floor(260 / 24) = 10 logical lines bottom-aligned - 24 rows of text in an
            // 11-row box - so the header and the first lines sat above the mask for good.
            var heights = new float[40];
            for (var i = 0; i < heights.Length; i++)
            {
                heights[i] = i == 0 ? Row : (i % 2 == 0 ? 2 * Row + Spacing : 3 * Row + (2 * Spacing));
            }

            var box = (11 * Row) + (10 * Spacing);
            var plan = LineLayout.Plan(heights.Length, int.MaxValue, box, Spacing, Heights(heights));

            Assert.Equal(LineAnchor.Top, plan.Anchor);
            Assert.Equal(0, plan.Start);
            Assert.True(plan.FullyVisible >= 1);
            Assert.True(LineLayout.RunHeight(0, plan.PageLines, Spacing, Heights(heights)) <= box + LineLayout.FitTolerance);
            Assert.True(LineLayout.RunHeight(0, plan.End, Spacing, Heights(heights)) >= box);
        }

        [Fact]
        public void Wrapped_lines_when_following_keep_the_newest_line_whole_at_the_bottom()
        {
            var heights = new float[] { 20f, 44f, 20f, 68f, 20f, 44f, 68f };
            var plan = LineLayout.Plan(heights.Length, 0, 120f, Spacing, Heights(heights));

            Assert.Equal(LineAnchor.Bottom, plan.Anchor);
            Assert.Equal(7, plan.End);

            // From the bottom: 68 (whole), then 44 starts 72 up and ends at 116 (whole), then 20
            // starts 120 up: exactly at the edge, so nothing of it shows and it is not drawn.
            Assert.Equal(5, plan.Start);
            Assert.Equal(2, plan.FullyVisible);
        }

        [Fact]
        public void Taller_rows_from_a_fallback_font_change_what_fits()
        {
            // Rows with Thai or Japanese from a fallback font can stand taller than the base row.
            var plain = LineLayout.Plan(12, int.MaxValue, 280f, Spacing, Fixed(Row));
            var mixed = LineLayout.Plan(12, int.MaxValue, 280f, Spacing, i => i % 3 == 0 ? 27f : Row);

            Assert.Equal(11, plain.PageLines);
            Assert.True(mixed.PageLines < plain.PageLines);
            Assert.True(LineLayout.RunHeight(0, mixed.PageLines, Spacing, i => i % 3 == 0 ? 27f : Row) <= 280f + LineLayout.FitTolerance);
        }

        [Fact]
        public void Scrolled_part_way_the_chosen_line_is_pinned_to_the_bottom()
        {
            var plan = LineLayout.Plan(100, 30, 230f, Spacing, Fixed(Row));

            Assert.Equal(LineAnchor.Bottom, plan.Anchor);
            Assert.Equal(70, plan.End);
            Assert.Equal(30, plan.Offset);
        }

        [Theory]
        [InlineData(-5, 0)]
        [InlineData(0, 0)]
        [InlineData(90, 90)]
        [InlineData(91, 91)]
        [InlineData(5000, 91)]
        public void The_offset_is_clamped_to_the_list(int wanted, int used)
        {
            Assert.Equal(used, LineLayout.Plan(100, wanted, 230f, Spacing, Fixed(Row)).Offset);
        }

        [Fact]
        public void A_line_taller_than_the_box_shows_its_start_at_the_top_and_its_end_when_following()
        {
            var heights = new float[] { 400f, Row, Row };

            var top = LineLayout.Plan(3, int.MaxValue, 200f, Spacing, Heights(heights));
            Assert.Equal(LineAnchor.Top, top.Anchor);
            Assert.Equal(0, top.Start);
            Assert.Equal(1, top.End);
            Assert.Equal(1, top.PageLines);
            Assert.Equal(0, top.FullyVisible);

            var tall = LineLayout.Plan(1, 0, 200f, Spacing, Fixed(400f));
            Assert.Equal(1, tall.Count);
            Assert.Equal(0, tall.FullyVisible);
        }

        [Theory]
        [InlineData(0f)]
        [InlineData(-10f)]
        [InlineData(float.NaN)]
        public void A_box_not_laid_out_yet_still_draws_the_pinned_line(float box)
        {
            var following = LineLayout.Plan(50, 0, box, Spacing, Fixed(Row));
            Assert.Equal(49, following.Start);
            Assert.Equal(50, following.End);

            var top = LineLayout.Plan(50, int.MaxValue, box, Spacing, Fixed(Row));
            Assert.Equal(0, top.Start);
            Assert.Equal(1, top.End);
        }

        [Fact]
        public void Unmeasurable_heights_count_as_zero()
        {
            var plan = LineLayout.Plan(3, 0, 100f, Spacing, Heights(float.NaN, -5f, Row));

            Assert.True(plan.Fits);
            Assert.Equal(3, plan.Count);
        }

        [Fact]
        public void A_missing_height_function_is_refused()
        {
            Assert.Throws<ArgumentNullException>(() => LineLayout.Plan(3, 0, 100f, Spacing, null!));
        }

        [Fact]
        public void The_padding_is_the_note_windows()
        {
            Assert.Equal(8f, LineLayout.Padding);
        }

        /// <summary>
        /// The rules on a spread of generated lists: the pinned line is never cut (unless it alone
        /// is taller than the box), the other edge is always reached unless everything fits, and
        /// the scrollbar maths agree with the plan's range.
        /// </summary>
        [Fact]
        public void Every_plan_pins_one_edge_and_covers_the_other()
        {
            var random = new Random(20260929);
            for (var round = 0; round < 400; round++)
            {
                var total = random.Next(1, 120);
                var heights = new float[total];
                for (var i = 0; i < total; i++)
                {
                    var rows = random.Next(0, 10) < 7 ? 1 : random.Next(2, 5);
                    var row = random.Next(0, 10) < 8 ? Row : 26f;
                    heights[i] = (rows * row) + ((rows - 1) * Spacing);
                }

                var box = random.Next(40, 700);
                var offset = random.Next(0, 4) == 0 ? int.MaxValue : random.Next(0, total + 5);
                var plan = LineLayout.Plan(total, offset, box, Spacing, Heights(heights));
                var context = $"round {round}: {plan} total {total} box {box}";

                Assert.True(plan.Count >= 1, context);
                Assert.Equal(plan.MaxOffset, LineScroll.MaxOffset(total, plan.PageLines));

                if (plan.Anchor == LineAnchor.Top)
                {
                    Assert.Equal(0, plan.Start);
                    Assert.Equal(plan.MaxOffset, plan.Offset);
                    if (heights[0] <= box)
                    {
                        Assert.True(plan.FullyVisible >= 1, context);
                    }
                }
                else
                {
                    Assert.Equal(total - plan.Offset, plan.End);
                    Assert.True(plan.Offset < plan.MaxOffset, context);
                    if (heights[plan.End - 1] <= box)
                    {
                        Assert.True(plan.FullyVisible >= 1, context);
                    }
                }

                var drawn = LineLayout.RunHeight(plan.Start, plan.End, Spacing, Heights(heights));
                if (plan.Fits)
                {
                    Assert.Equal(total, plan.Count);
                    Assert.True(drawn <= box + LineLayout.FitTolerance || (total == 1 && heights[0] > box), context);
                }
                else
                {
                    // Reaches the far edge; at most the spacing between two lines is left, which
                    // is empty between any two rows anyway.
                    Assert.True(drawn + Spacing >= box, context);

                    // Nothing drawn that the box cannot show: without its far line the run is short.
                    var trimmed = plan.Anchor == LineAnchor.Top
                        ? LineLayout.RunHeight(plan.Start, plan.End - 1, Spacing, Heights(heights))
                        : LineLayout.RunHeight(plan.Start + 1, plan.End, Spacing, Heights(heights));
                    Assert.True(plan.Count == 1 || trimmed + Spacing < box, context);
                }
            }
        }
    }
}
