using CwyLs.ModLoader.Core.Smoke;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    public class SmokePassScheduleTests
    {
        /// <summary>
        /// P1-9a flipped this default. The second pass used to be the thing that made a hands-off
        /// launch end green; it was also a second unannounced run that played the test clip and
        /// repainted the character a minute into every session.
        /// </summary>
        [Fact]
        public void The_second_pass_is_off_by_default()
        {
            Assert.Equal(0f, SmokePassSchedule.DefaultSecondPassSeconds);
            Assert.False(SmokePassSchedule.IsEnabled(SmokePassSchedule.DefaultSecondPassSeconds));
            Assert.False(SmokePassSchedule.TryPlanSecondPass(
                settleSeconds: 3f, SmokePassSchedule.DefaultSecondPassSeconds, out _));
        }

        [Fact]
        public void The_suggested_second_pass_is_a_minute_after_the_scene_loads()
        {
            Assert.Equal(60f, SmokePassSchedule.SuggestedSecondPassSeconds);

            Assert.True(SmokePassSchedule.TryPlanSecondPass(
                settleSeconds: 3f, SmokePassSchedule.SuggestedSecondPassSeconds, out var delay));

            // The first pass already consumed the settle delay, so the runner waits the remainder.
            Assert.Equal(57f, delay);
        }

        [Theory]
        [InlineData(0f)]
        [InlineData(-1f)]
        [InlineData(float.NaN)]
        [InlineData(float.PositiveInfinity)]
        public void Zero_and_nonsense_turn_the_second_pass_off(float seconds)
        {
            Assert.False(SmokePassSchedule.IsEnabled(seconds));
            Assert.False(SmokePassSchedule.TryPlanSecondPass(3f, seconds, out var delay));
            Assert.Equal(0f, delay);
        }

        [Fact]
        public void A_positive_delay_enables_the_pass()
        {
            Assert.True(SmokePassSchedule.IsEnabled(0.001f));
            Assert.True(SmokePassSchedule.IsEnabled(60f));
        }

        [Theory]
        [InlineData(1f)]
        [InlineData(3f)]
        [InlineData(3.4f)]
        public void A_pass_asked_for_at_or_before_the_first_one_is_pushed_past_it(float seconds)
        {
            Assert.True(SmokePassSchedule.TryPlanSecondPass(settleSeconds: 3f, seconds, out var delay));
            Assert.Equal(SmokePassSchedule.MinimumGapSeconds, delay);
        }

        [Fact]
        public void The_gap_is_measured_from_the_scene_load_not_from_the_first_pass()
        {
            Assert.True(SmokePassSchedule.TryPlanSecondPass(settleSeconds: 3f, 90f, out var fromThree));
            Assert.True(SmokePassSchedule.TryPlanSecondPass(settleSeconds: 10f, 90f, out var fromTen));

            Assert.Equal(87f, fromThree);
            Assert.Equal(80f, fromTen);
        }

        [Theory]
        [InlineData(0f)]
        [InlineData(-5f)]
        [InlineData(float.NaN)]
        public void A_nonsense_settle_delay_is_treated_as_no_delay(float settle)
        {
            Assert.True(SmokePassSchedule.TryPlanSecondPass(settle, 60f, out var delay));
            Assert.Equal(60f, delay);
        }
    }
}
