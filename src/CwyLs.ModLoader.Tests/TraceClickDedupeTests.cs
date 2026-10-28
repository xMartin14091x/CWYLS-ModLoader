using CwyLs.ModLoader.Core.Trace;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// P1-9a. <c>InputController.GetClickDown</c> is polled by two callers per frame and both
    /// reached the postfix, so a single click wrote two <c>Input.click</c> lines.
    /// </summary>
    public class TraceClickDedupeTests
    {
        [Fact]
        public void One_click_writes_one_line()
        {
            var dedupe = new ClickDedupe();

            Assert.True(dedupe.ShouldWrite(frame: 42, 640f, 360f));
            Assert.False(dedupe.ShouldWrite(frame: 42, 640f, 360f));
            Assert.False(dedupe.ShouldWrite(frame: 42, 640f, 360f));
        }

        [Fact]
        public void The_next_frame_is_a_new_click()
        {
            var dedupe = new ClickDedupe();

            Assert.True(dedupe.ShouldWrite(42, 640f, 360f));
            Assert.True(dedupe.ShouldWrite(43, 640f, 360f));
        }

        [Fact]
        public void A_different_position_in_the_same_frame_is_a_different_event()
        {
            var dedupe = new ClickDedupe();

            Assert.True(dedupe.ShouldWrite(42, 640f, 360f));
            Assert.True(dedupe.ShouldWrite(42, 900f, 360f));
        }

        /// <summary>
        /// The line prints whole pixels, so sub-pixel jitter between the two callers of the same
        /// frame must not slip a duplicate past the check.
        /// </summary>
        [Fact]
        public void Sub_pixel_jitter_is_still_the_same_click()
        {
            var dedupe = new ClickDedupe();

            Assert.True(dedupe.ShouldWrite(42, 640.2f, 360.4f));
            Assert.False(dedupe.ShouldWrite(42, 639.8f, 359.6f));
        }

        [Fact]
        public void A_reset_forgets_the_last_click()
        {
            var dedupe = new ClickDedupe();

            Assert.True(dedupe.ShouldWrite(42, 640f, 360f));
            dedupe.Reset();
            Assert.True(dedupe.ShouldWrite(42, 640f, 360f));
        }
    }
}
