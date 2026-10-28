using CwyLs.ModLoader.Core.Console;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// B-UI: a dragged or resized game-style window stays entirely on the canvas, as the game's own
    /// windows do (DraggableUI.AdjustPos).
    /// </summary>
    public class ConsoleWindowRectMathTests
    {
        private const float W = 1920f;
        private const float H = 1080f;
        private const float MinW = 640f;
        private const float MinH = 360f;

        [Fact]
        public void The_default_window_takes_the_consoles_share_centred_near_the_top()
        {
            var rect = WindowRectMath.Default(W, H, MinW, MinH);

            Assert.Equal(W * WindowRectMath.DefaultWidthFraction, rect.Width, 3);
            Assert.Equal(H * WindowRectMath.DefaultHeightFraction, rect.Height, 3);
            Assert.Equal((W - rect.Width) / 2f, rect.Left, 3);
            Assert.Equal((H - rect.Height) * 0.25f, rect.Top, 3);
        }

        [Fact]
        public void The_default_window_never_goes_below_its_minimum_or_off_a_small_canvas()
        {
            var rect = WindowRectMath.Default(800f, 450f, MinW, MinH);

            Assert.True(rect.Width >= MinW);
            Assert.True(rect.Height >= MinH);
            Assert.True(rect.Left >= 0f && rect.Right <= 800f);
            Assert.True(rect.Top >= 0f && rect.Bottom <= 450f);
        }

        [Fact]
        public void Dragging_moves_by_the_pointer_delta()
        {
            var start = new WindowRect(100f, 100f, 800f, 500f);
            var moved = WindowRectMath.Move(start, 40f, 25f, W, H, MinW, MinH);

            Assert.Equal(new WindowRect(140f, 125f, 800f, 500f), moved);
        }

        [Theory]
        [InlineData(-5000f, 0f, 0f, 100f)]
        [InlineData(5000f, 0f, 1120f, 100f)]
        [InlineData(0f, -5000f, 100f, 0f)]
        [InlineData(0f, 5000f, 100f, 580f)]
        public void Dragging_past_an_edge_stops_at_the_edge(float dx, float dy, float left, float top)
        {
            var start = new WindowRect(100f, 100f, 800f, 500f);
            var moved = WindowRectMath.Move(start, dx, dy, W, H, MinW, MinH);

            Assert.Equal(left, moved.Left, 3);
            Assert.Equal(top, moved.Top, 3);
            Assert.Equal(800f, moved.Width);
            Assert.Equal(500f, moved.Height);
        }

        [Fact]
        public void Resizing_grows_from_the_bottom_right_and_stops_at_the_canvas_edge()
        {
            var start = new WindowRect(1000f, 600f, 700f, 400f);
            var grown = WindowRectMath.Resize(start, 5000f, 5000f, W, H, MinW, MinH);

            Assert.Equal(1000f, grown.Left);
            Assert.Equal(600f, grown.Top);
            Assert.Equal(W - 1000f, grown.Width, 3);
            Assert.Equal(H - 600f, grown.Height, 3);
        }

        [Fact]
        public void Resizing_never_goes_below_the_minimum()
        {
            var start = new WindowRect(100f, 100f, 700f, 400f);
            var shrunk = WindowRectMath.Resize(start, -5000f, -5000f, W, H, MinW, MinH);

            Assert.Equal(MinW, shrunk.Width);
            Assert.Equal(MinH, shrunk.Height);
        }

        [Fact]
        public void A_window_larger_than_a_shrunken_canvas_is_shrunk_then_kept_on_it()
        {
            var big = new WindowRect(1200f, 700f, 1500f, 900f);
            var clamped = WindowRectMath.Clamp(big, 1280f, 720f, MinW, MinH);

            Assert.Equal(1280f, clamped.Width);
            Assert.Equal(720f, clamped.Height);
            Assert.Equal(0f, clamped.Left);
            Assert.Equal(0f, clamped.Top);
        }

        [Fact]
        public void A_canvas_smaller_than_the_minimum_pins_the_window_top_left_so_close_stays_reachable()
        {
            var clamped = WindowRectMath.Clamp(new WindowRect(300f, 300f, 700f, 400f), 500f, 300f, MinW, MinH);

            Assert.Equal(0f, clamped.Left);
            Assert.Equal(0f, clamped.Top);
            Assert.Equal(MinW, clamped.Width);
            Assert.Equal(MinH, clamped.Height);
        }

        [Theory]
        [InlineData(10f, 20f, 2f, 5f, -10f)]
        [InlineData(-8f, -8f, 0.5f, -16f, 16f)]
        [InlineData(10f, 10f, 0f, 10f, -10f)]
        public void Screen_pixels_become_canvas_units_with_y_down(float sx, float sy, float scale, float dx, float dy)
        {
            WindowRectMath.ScreenToCanvasDelta(sx, sy, scale, out var x, out var y);

            Assert.Equal(dx, x, 3);
            Assert.Equal(dy, y, 3);
        }
    }
}
