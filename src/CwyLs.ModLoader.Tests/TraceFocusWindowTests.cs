using CwyLs.ModLoader.Core.Trace;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// P1-9a. Alt-tabbing produced a stream of health/spike and health/lowFps lines in the
    /// console. The game lowers its own frame rate in the background, so those readings describe
    /// the window, not the mod, and they are tagged rather than reported.
    /// </summary>
    public class TraceFocusWindowTests
    {
        [Fact]
        public void A_focused_window_running_normally_attributes_nothing()
        {
            var window = new FocusWindow();

            Assert.True(window.Focused);
            Assert.False(window.Attributes(nowSeconds: 120d));
        }

        [Fact]
        public void Everything_while_the_window_is_in_the_background_is_attributed()
        {
            var window = new FocusWindow();
            window.Change(focused: false, atSeconds: 10d);

            Assert.False(window.Focused);
            Assert.True(window.Attributes(10d));
            Assert.True(window.Attributes(60d));
            Assert.True(window.Attributes(6000d));
        }

        /// <summary>Coming back costs a frame too, so the window covers both edges.</summary>
        [Fact]
        public void The_first_seconds_after_coming_back_are_still_attributed()
        {
            var window = new FocusWindow();
            window.Change(focused: false, atSeconds: 10d);
            window.Change(focused: true, atSeconds: 30d);

            Assert.True(window.Focused);
            Assert.True(window.Attributes(30d));
            Assert.True(window.Attributes(30d + FocusWindow.AttributionSeconds));
            Assert.False(window.Attributes(30d + FocusWindow.AttributionSeconds + 0.001d));
        }

        [Fact]
        public void A_repeated_state_does_not_restart_the_window()
        {
            var window = new FocusWindow();
            window.Change(focused: false, atSeconds: 10d);
            window.Change(focused: true, atSeconds: 30d);

            // Unity can raise focusChanged(true) again without anything having happened.
            window.Change(focused: true, atSeconds: 40d);

            Assert.False(window.Attributes(33d));
        }

        [Fact]
        public void The_window_is_two_seconds()
        {
            Assert.Equal(2d, FocusWindow.AttributionSeconds);
            Assert.Equal("unfocused", TraceHealth.UnfocusedCapReason);
        }

        /// <summary>
        /// A recorded session: the spike at t is the switch, and focusChanged(false) arrives ~20 ms
        /// later. Attributed at t+0.75 (the hold), the cause has to be the change, not "running
        /// badly" - and the event never reaches the console.
        /// </summary>
        [Fact]
        public void A_stall_that_is_the_switch_itself_is_named_as_such()
        {
            var window = new FocusWindow();
            window.Change(focused: false, atSeconds: 10.02d);

            Assert.Equal(
                FocusWindow.FocusChangeCause,
                window.CauseAt(eventAtSeconds: 10d, nowSeconds: 10.75d));
        }

        /// <summary>
        /// The callback can also arrive just BEFORE the stall is measured, so the window is
        /// symmetric: a change within 0.75 s either side owns the event.
        /// </summary>
        [Fact]
        public void The_near_window_is_symmetric()
        {
            var window = new FocusWindow();
            window.Change(focused: false, atSeconds: 10d);

            // Focus was lost at t=10: a stall measured three quarters of a second either side of
            // it is that switch, whether the callback beat the measurement or followed it.
            Assert.Equal(FocusWindow.FocusChangeCause, window.CauseAt(10d - 0.75d, 10.5d));
            Assert.Equal(FocusWindow.FocusChangeCause, window.CauseAt(10d + 0.75d, 11.5d));
            Assert.True(window.ChangedNear(10.5d, FocusWindow.NearChangeSeconds));
            Assert.False(window.ChangedNear(11d, FocusWindow.NearChangeSeconds));
        }

        [Fact]
        public void A_background_window_owns_everything_measured_in_it()
        {
            var window = new FocusWindow();
            window.Change(focused: false, atSeconds: 10d);

            Assert.Equal(FocusWindow.UnfocusedCause, window.CauseAt(60d, 60.75d));
        }

        /// <summary>The first two seconds back are the game catching up, not a stutter.</summary>
        [Fact]
        public void The_seconds_after_a_regain_are_still_the_windows_fault()
        {
            var window = new FocusWindow();
            window.Change(focused: false, atSeconds: 10d);
            window.Change(focused: true, atSeconds: 30d);

            Assert.Equal(FocusWindow.UnfocusedCause, window.CauseAt(31d, 31.75d));
            Assert.Equal(FocusWindow.UnfocusedCause, window.CauseAt(31.2d, 32d));
            Assert.Null(window.CauseAt(35d, 35.75d));
        }

        [Fact]
        public void A_real_stutter_is_blamed_on_nobody()
        {
            var window = new FocusWindow();

            Assert.Null(window.CauseAt(120d, 120.75d));
        }
    }
}
