using CwyLs.ModLoader.Core.Ui;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// S-UI: the chat view follows the newest bubble unless the player scrolled up; the list view
    /// starts at the top.
    /// </summary>
    public class UiScrollStateTests
    {
        [Fact]
        public void A_following_view_stays_at_the_bottom_while_a_reply_streams()
        {
            var scroll = new ScrollState(followEnd: true);
            scroll.Resize(300f, 400f);
            Assert.Equal(0f, scroll.Top);

            scroll.Resize(500f, 400f);
            Assert.Equal(100f, scroll.Top);
            scroll.Resize(560f, 400f);
            Assert.Equal(160f, scroll.Top);
            Assert.True(scroll.Following);
        }

        [Fact]
        public void Scrolling_up_stops_following_and_the_view_stays_put_as_content_grows()
        {
            var scroll = new ScrollState(followEnd: true);
            scroll.Resize(1000f, 400f);
            scroll.ScrollBy(-200f);
            Assert.False(scroll.Following);
            Assert.Equal(400f, scroll.Top);

            scroll.Resize(1300f, 400f);
            Assert.Equal(400f, scroll.Top);
            Assert.False(scroll.Following);
        }

        [Fact]
        public void Scrolling_back_to_the_bottom_follows_again()
        {
            var scroll = new ScrollState(followEnd: true);
            scroll.Resize(1000f, 400f);
            scroll.ScrollBy(-50f);
            Assert.False(scroll.Following);
            scroll.ScrollBy(49f);
            Assert.True(scroll.Following);
            scroll.Resize(1100f, 400f);
            Assert.Equal(700f, scroll.Top);
        }

        [Fact]
        public void Dropped_bubbles_above_do_not_move_what_the_player_reads()
        {
            var scroll = new ScrollState(followEnd: true);
            scroll.Resize(1000f, 400f);
            scroll.ScrollBy(-300f);
            Assert.Equal(300f, scroll.Top);

            // The oldest bubble (80 high + 10 spacing) dropped past the cap, a new one (90) added below.
            scroll.Resize(1000f, 400f, removedAbove: 90f);
            Assert.Equal(210f, scroll.Top);
        }

        [Fact]
        public void ToEnd_and_ToStart()
        {
            var scroll = new ScrollState(followEnd: false);
            scroll.Resize(1000f, 400f);
            Assert.Equal(0f, scroll.Top);
            scroll.ToEnd();
            Assert.True(scroll.Following);
            Assert.Equal(600f, scroll.Top);
            scroll.ToStart();
            Assert.False(scroll.Following);
            Assert.Equal(0f, scroll.Top);
        }

        [Fact]
        public void Scrollbar_runs_bottom_to_top()
        {
            var scroll = new ScrollState(followEnd: true);
            scroll.Resize(1000f, 400f);
            Assert.Equal(0f, scroll.ScrollbarValue);
            Assert.Equal(0.4f, scroll.HandleSize, 3);

            scroll.FromScrollbar(1f);
            Assert.Equal(0f, scroll.Top);
            Assert.False(scroll.Following);
            Assert.Equal(1f, scroll.ScrollbarValue);

            scroll.FromScrollbar(0f);
            Assert.True(scroll.Following);
            scroll.FromScrollbar(float.NaN);
            Assert.Equal(600f, scroll.Top);
        }

        [Fact]
        public void Everything_fits_means_a_full_handle_and_no_scrolling()
        {
            var scroll = new ScrollState(followEnd: true);
            scroll.Resize(100f, 400f);
            Assert.Equal(1f, scroll.HandleSize);
            scroll.ScrollBy(-50f);
            Assert.Equal(0f, scroll.Top);
            Assert.Equal(0f, scroll.ScrollbarValue);
        }

        [Fact]
        public void Reveal_scrolls_just_enough()
        {
            var scroll = new ScrollState(followEnd: false);
            scroll.Resize(36f * 100f, 360f);
            scroll.Reveal(36f * 50f, 36f);
            Assert.Equal((36f * 51f) - 360f, scroll.Top);
            scroll.Reveal(36f * 2f, 36f);
            Assert.Equal(72f, scroll.Top);
            scroll.Reveal(36f * 3f, 36f);
            Assert.Equal(72f, scroll.Top);
        }

        [Fact]
        public void Non_finite_input_is_ignored()
        {
            var scroll = new ScrollState(followEnd: true);
            scroll.Resize(float.NaN, float.PositiveInfinity);
            Assert.Equal(0f, scroll.Content);
            Assert.Equal(0f, scroll.Viewport);
            scroll.ScrollBy(float.NaN);
            Assert.Equal(0f, scroll.Top);
        }
    }
}
