using System;
using System.Linq;
using CwyLs.ModLoader.Core.Language;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// B-LANG-FIX: with pack languages added, the game's language pulldown keeps its own open height
    /// (the settings page clips anything taller) and the list scrolls inside it. Geometry is the
    /// game's (RoomScene.unity, game 1.17.3): open height 338.4, list top 46.6 below the pulldown's
    /// top, seven rows of 39.8 with 0.5 spacing and no vertical padding. A Thai language pack adds one
    /// row; a Vietnamese pack a second.
    /// </summary>
    public class LanguageSelectorListLayoutTests
    {
        private const float OpenHeight = 338.4f;
        private const float ListTop = 46.6f;
        private const float Row = 39.8f;
        private const float Spacing = 0.5f;
        private const int GameRows = 7;
        private const float Precision = 0.001f;

        private static float[] Rows(int packRows) => Enumerable.Repeat(Row, GameRows + packRows).ToArray();

        private static SelectorListPlan Plan(int packRows) =>
            SelectorListLayout.Plan(OpenHeight, ListTop, Rows(packRows), GameRows, Spacing, 0f, 0f);

        private static void Near(float expected, float actual) => Assert.InRange(actual, expected - Precision, expected + Precision);

        [Fact]
        public void The_games_seven_rows_fit_and_nothing_scrolls()
        {
            var plan = Plan(0);

            Assert.True(plan.IsUsable);
            Assert.False(plan.Scrolls);
            Near(281.6f, plan.ContentHeight);
            Near(281.6f, plan.ViewportHeight);
            Assert.Equal(0f, plan.MaxOffset);
            Assert.Equal(7, plan.VisibleRows);
        }

        [Fact]
        public void Thai_scrolls_inside_the_games_open_height_showing_the_games_seven_rows()
        {
            var plan = Plan(1);

            Assert.True(plan.Scrolls);
            Assert.Equal(OpenHeight, plan.OpenHeight);
            Near(281.6f, plan.ViewportHeight);
            Near(321.9f, plan.ContentHeight);
            Near(40.3f, plan.MaxOffset);
            Assert.Equal(7, plan.VisibleRows);
            Assert.Equal(8, plan.TotalRows);
            Assert.Equal(GameRows, plan.GameRows);
        }

        [Fact]
        public void The_visible_list_ends_where_the_games_list_ends_inside_the_open_pulldown()
        {
            var plan = Plan(1);

            // Vanilla: the list ends 10.2 above the open pulldown's bottom edge. It still does.
            Near(OpenHeight - 10.2f, plan.ListTop + plan.ViewportHeight);
            Assert.True(plan.ListTop + plan.ViewportHeight <= plan.OpenHeight);
        }

        [Fact]
        public void The_pre_fix_growth_would_have_pushed_the_pulldown_one_row_lower()
        {
            // The boot log's "pulldown=378.7" (2026-09-29 09:50): the fallback formula, unchanged.
            Near(378.7f, SelectorListLayout.GrownOpenHeight(OpenHeight, 1, Row, Spacing));
            Near(419.0f, SelectorListLayout.GrownOpenHeight(OpenHeight, 2, Row, Spacing));
            Assert.Equal(OpenHeight, SelectorListLayout.GrownOpenHeight(OpenHeight, 0, Row, Spacing));
            Assert.Equal(OpenHeight, SelectorListLayout.GrownOpenHeight(OpenHeight, -3, Row, Spacing));
        }

        [Fact]
        public void Rows_sit_where_the_vertical_layout_group_puts_them()
        {
            var plan = Plan(2);

            Assert.Equal(0f, plan.RowTop(0));
            Near(39.8f, plan.RowBottom(0));
            Near(40.3f, plan.RowTop(1)); // the scene's LanguageENSelectButton sits at y -40.3
            Near(282.1f, plan.RowTop(7));
            Near(321.9f, plan.RowBottom(7));
            Near(322.4f, plan.RowTop(8));
            Near(362.2f, plan.RowBottom(8));
            Assert.Throws<ArgumentOutOfRangeException>(() => plan.RowTop(9));
            Assert.Throws<ArgumentOutOfRangeException>(() => plan.RowBottom(-1));
        }

        [Fact]
        public void Opening_with_a_game_language_selected_shows_the_list_as_the_game_does()
        {
            var plan = Plan(1);

            for (var row = 0; row < GameRows; row++)
            {
                Assert.Equal(0f, plan.Reveal(row, 0f));
            }
        }

        [Fact]
        public void Opening_with_Thai_selected_scrolls_one_row_so_Thai_is_whole()
        {
            var plan = Plan(1);

            var offset = plan.Reveal(7, 0f);

            Near(40.3f, offset);
            Assert.True(plan.IsWhollyVisible(7, offset));
            Assert.False(plan.IsWhollyVisible(0, offset));
            Assert.True(plan.IsWhollyVisible(1, offset));
        }

        [Fact]
        public void A_second_pack_language_scrolls_two_rows_and_Thai_stays_reachable()
        {
            var plan = Plan(2);

            Near(80.6f, plan.MaxOffset);
            Near(80.6f, plan.Reveal(8, 0f));
            Near(40.3f, plan.Reveal(7, 0f));

            // Already whole at the bottom: no movement.
            Near(80.6f, plan.Reveal(7, 80.6f));

            // Scrolled to the bottom, the first row comes back to the top edge.
            Assert.Equal(0f, plan.Reveal(0, 80.6f));
            Near(40.3f, plan.Reveal(1, 80.6f));
        }

        [Fact]
        public void Every_row_can_be_brought_whole_into_view_for_one_to_twenty_pack_languages()
        {
            for (var packRows = 1; packRows <= 20; packRows++)
            {
                var plan = Plan(packRows);
                Assert.True(plan.Scrolls);
                Near(281.6f, plan.ViewportHeight);
                Assert.Equal(7, plan.VisibleRows);

                foreach (var start in new[] { 0f, plan.MaxOffset / 2f, plan.MaxOffset })
                {
                    for (var row = 0; row < plan.TotalRows; row++)
                    {
                        var offset = plan.Reveal(row, start);
                        Assert.InRange(offset, 0f, plan.MaxOffset);
                        Assert.True(plan.IsWhollyVisible(row, offset), "pack rows " + packRows + ", row " + row + ", from " + start + " -> " + offset);
                    }
                }
            }
        }

        [Fact]
        public void Offsets_are_clamped_to_the_list()
        {
            var plan = Plan(1);

            Assert.Equal(0f, plan.ClampOffset(-5f));
            Assert.Equal(0f, plan.ClampOffset(float.NaN));
            Near(40.3f, plan.ClampOffset(1000f));
            Near(12f, plan.ClampOffset(12f));
            Near(40.3f, plan.Reveal(7, float.PositiveInfinity));
        }

        [Fact]
        public void An_unknown_row_keeps_the_offset()
        {
            var plan = Plan(1);

            Assert.Equal(0f, plan.Reveal(-1, 0f));
            Near(20f, plan.Reveal(-1, 20f));
            Near(20f, plan.Reveal(99, 20f));
        }

        [Fact]
        public void A_larger_open_height_fits_every_row_and_nothing_scrolls()
        {
            // A game update that gave the pulldown room for eight rows: leave it alone.
            var plan = SelectorListLayout.Plan(OpenHeight + 50f, ListTop, Rows(1), GameRows, Spacing, 0f, 0f);

            Assert.False(plan.Scrolls);
            Near(321.9f, plan.ViewportHeight);
            Assert.Equal(8, plan.VisibleRows);
        }

        [Fact]
        public void An_open_height_shorter_than_the_games_rows_caps_the_viewport_and_still_reaches_every_row()
        {
            var plan = SelectorListLayout.Plan(200f, ListTop, Rows(1), GameRows, Spacing, 0f, 0f);

            Near(153.4f, plan.ViewportHeight);
            Assert.Equal(3, plan.VisibleRows);
            Assert.True(plan.Scrolls);
            for (var row = 0; row < plan.TotalRows; row++)
            {
                Assert.True(plan.IsWhollyVisible(row, plan.Reveal(row, 0f)));
            }
        }

        [Fact]
        public void Padding_and_uneven_rows_follow_the_layout_rules()
        {
            var plan = SelectorListLayout.Plan(300f, 10f, new[] { 40f, 60f, 40f, 50f }, 2, 2f, 5f, 3f);

            Assert.Equal(5f, plan.RowTop(0));
            Assert.Equal(47f, plan.RowTop(1));
            Assert.Equal(109f, plan.RowTop(2));
            Assert.Equal(151f, plan.RowTop(3));
            Assert.Equal(5f + 40f + 60f + 40f + 50f + (3 * 2f) + 3f, plan.ContentHeight);

            // Fits in the 290 of room below the list top.
            Assert.False(plan.Scrolls);
            Assert.Equal(plan.ContentHeight, plan.ViewportHeight);

            var tight = SelectorListLayout.Plan(150f, 10f, new[] { 40f, 60f, 40f, 50f }, 2, 2f, 5f, 3f);
            Assert.Equal(5f + 40f + 2f + 60f + 3f, tight.ViewportHeight);
            Assert.True(tight.Scrolls);
            Assert.Equal(151f + 50f - tight.ViewportHeight, tight.Reveal(3, 0f));
        }

        [Fact]
        public void A_row_taller_than_the_viewport_shows_its_top()
        {
            var plan = SelectorListLayout.Plan(100f, 0f, new[] { 40f, 40f, 150f }, 2, 0f, 0f, 0f);

            Assert.Equal(80f, plan.ViewportHeight);
            Assert.Equal(80f, plan.Reveal(2, 0f));
            Assert.Equal(80f, plan.Reveal(2, 150f));
        }

        [Fact]
        public void Stack_height_counts_spacing_between_rows_only()
        {
            var heights = new[] { 10f, 20f, 30f };

            Assert.Equal(1f + 2f, SelectorListLayout.StackHeight(heights, 0, 5f, 1f, 2f));
            Assert.Equal(1f + 10f + 2f, SelectorListLayout.StackHeight(heights, 1, 5f, 1f, 2f));
            Assert.Equal(1f + 10f + 5f + 20f + 5f + 30f + 2f, SelectorListLayout.StackHeight(heights, 3, 5f, 1f, 2f));
            Assert.Equal(SelectorListLayout.StackHeight(heights, 3, 5f, 1f, 2f), SelectorListLayout.StackHeight(heights, 99, 5f, 1f, 2f));
            Assert.Throws<ArgumentNullException>(() => SelectorListLayout.StackHeight(null!, 1, 0f, 0f, 0f));
        }

        [Theory]
        [InlineData(float.NaN, ListTop, GameRows, "open height")]
        [InlineData(0f, ListTop, GameRows, "open height")]
        [InlineData(OpenHeight, float.NaN, GameRows, "below the pulldown's top")]
        [InlineData(OpenHeight, -1f, GameRows, "below the pulldown's top")]
        [InlineData(OpenHeight, ListTop, 0, "are the game's")]
        [InlineData(OpenHeight, ListTop, 9, "are the game's")]
        [InlineData(OpenHeight, 320f, GameRows, "less than one row")]
        public void Unusable_measurements_are_named_and_never_scroll(float openHeight, float listTop, int gameRows, string expected)
        {
            var plan = SelectorListLayout.Plan(openHeight, listTop, Rows(1), gameRows, Spacing, 0f, 0f);

            Assert.False(plan.IsUsable);
            Assert.Contains(expected, plan.Problem);
            Assert.False(plan.Scrolls);
            Assert.Equal(0, plan.VisibleRows);
            Assert.Equal(0f, plan.Reveal(7, 0f));
            Assert.False(plan.IsWhollyVisible(0, 0f));
            Assert.StartsWith("unusable: ", plan.ToString());
        }

        [Theory]
        [InlineData(0f)]
        [InlineData(-4f)]
        [InlineData(float.NaN)]
        [InlineData(float.PositiveInfinity)]
        public void A_row_without_a_real_height_is_unusable(float height)
        {
            var heights = Rows(1);
            heights[7] = height;

            var plan = SelectorListLayout.Plan(OpenHeight, ListTop, heights, GameRows, Spacing, 0f, 0f);

            Assert.False(plan.IsUsable);
            Assert.Contains("row 7", plan.Problem);
        }

        [Fact]
        public void Bad_spacing_or_padding_is_unusable()
        {
            Assert.False(SelectorListLayout.Plan(OpenHeight, ListTop, Rows(1), GameRows, float.NaN, 0f, 0f).IsUsable);
            Assert.False(SelectorListLayout.Plan(OpenHeight, ListTop, Rows(1), GameRows, Spacing, -1f, 0f).IsUsable);
            Assert.False(SelectorListLayout.Plan(OpenHeight, ListTop, Rows(1), GameRows, Spacing, 0f, float.PositiveInfinity).IsUsable);
            Assert.Throws<ArgumentNullException>(() => SelectorListLayout.Plan(OpenHeight, ListTop, null!, GameRows, Spacing, 0f, 0f));
        }

        [Fact]
        public void The_summary_reads_rows_viewport_and_offset()
        {
            Assert.Equal("7/8 rows, viewport 281.6 of 321.9, max offset 40.3", Plan(1).ToString());
        }

        [Fact]
        public void The_scrollbar_is_the_games_pulldown_scrollbar_and_fits_beside_the_rows()
        {
            // The pulldown frame is 406.98 wide; the list spans 31.49..364 and its rows 57.74..345.74
            // (VerticalLayoutGroup padding-left 8, MiddleCenter, rows 288 wide).
            const float frameWidth = 406.98f;
            const float rowsRight = 345.74f;

            Assert.Equal(14.5f, SelectorListLayout.ScrollbarWidth);
            var scrollbarLeft = frameWidth - SelectorListLayout.ScrollbarRightInset - SelectorListLayout.ScrollbarWidth;
            Assert.True(scrollbarLeft > rowsRight, "the scrollbar would cover the rows: " + scrollbarLeft);
        }
    }
}
