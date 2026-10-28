using CwyLs.ModLoader.Core.Trace;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// P1-9a follow-up, from a recorded session: every ~1.1 s spike was followed within 20 ms by
    /// <c>HEALTH focus focused=false</c>. The stall IS the window switch, but Unity raises the
    /// callback after it, so a spike emitted where it is measured always read as focused and
    /// always reached the console. Health events are held long enough to be told the truth.
    /// </summary>
    public class TraceHealthHoldTests
    {
        private static HealthEventHold Held(params double[] atSeconds)
        {
            var hold = new HealthEventHold();
            foreach (var at in atSeconds)
            {
                hold.Hold("spike", at, ("ms", 260d));
            }

            return hold;
        }

        [Fact]
        public void An_event_is_not_released_before_its_hold_is_over()
        {
            var hold = Held(10d);

            Assert.Equal(1, hold.Count);
            Assert.Empty(hold.Release(10d));
            Assert.Empty(hold.Release(10d + FocusWindow.HoldSeconds - 0.01d));
            Assert.Single(hold.Release(10d + FocusWindow.HoldSeconds));
            Assert.Equal(0, hold.Count);
        }

        [Fact]
        public void The_hold_is_three_quarters_of_a_second()
        {
            Assert.Equal(0.75d, FocusWindow.HoldSeconds);
            Assert.Equal(0.75d, FocusWindow.NearChangeSeconds);
        }

        [Fact]
        public void Events_come_back_in_the_order_they_were_measured()
        {
            var hold = new HealthEventHold();
            hold.Hold("spike", 10d, ("ms", 300d));
            hold.Hold("lowFps", 10.2d, ("fpsAvg", 8d));
            hold.Hold("spike", 11d, ("ms", 400d));

            var due = hold.Release(11d);

            Assert.Equal(2, due.Count);
            Assert.Equal("spike", due[0].Name);
            Assert.Equal(10d, due[0].AtSeconds);
            Assert.Equal("lowFps", due[1].Name);
            Assert.Equal(1, hold.Count);
        }

        [Fact]
        public void The_measured_payload_is_carried_through_untouched()
        {
            var hold = new HealthEventHold();
            hold.Hold("spike", 10d, ("ms", 260d), ("frame", 4242));

            var due = Assert.Single(hold.Release(20d));

            Assert.Equal(new[] { "ms", "frame" }, new[] { due.Fields[0].Key, due.Fields[1].Key });
            Assert.Equal(260d, due.Fields[0].Value);
            Assert.Equal(4242, due.Fields[1].Value);
        }

        [Fact]
        public void Nothing_due_allocates_no_list()
        {
            var hold = new HealthEventHold();

            Assert.Empty(hold.Release(100d));
            Assert.Same(hold.Release(100d), hold.Release(200d));
        }

        [Fact]
        public void A_stall_storm_drops_the_oldest_and_says_how_many()
        {
            var hold = new HealthEventHold();
            for (var i = 0; i < HealthEventHold.Capacity + 5; i++)
            {
                hold.Hold("spike", 1000d + i, ("ms", 300d));
            }

            Assert.Equal(HealthEventHold.Capacity, hold.Count);
            Assert.Equal(5L, hold.Dropped);
        }

        [Fact]
        public void Shutdown_releases_whatever_is_still_waiting()
        {
            var hold = Held(10d, 10.5d);

            Assert.Equal(2, hold.ReleaseAll().Count);
            Assert.Equal(0, hold.Count);
            Assert.Empty(hold.ReleaseAll());
        }
    }
}
