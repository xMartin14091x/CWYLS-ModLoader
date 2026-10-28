using System;

namespace CwyLs.ModLoader.Core.Trace
{
    /// <summary>
    /// Remembers whether the game window has focus and when that last changed, so a frame-time
    /// spike can be attributed to the user alt-tabbing instead of reported as a stutter.
    /// <para>
    /// The game lowers its own frame rate while it is in the background (verified by the user), and
    /// macOS stalls the process across a space switch. Both produce 250 ms+ frames and a low
    /// average fps that have nothing to do with the mod. A reading this type attributes is still
    /// written to the trace file, tagged <c>unfocused</c>, but never mirrored to the console.
    /// </para>
    /// Pure and clock-agnostic: the caller passes <c>Time.realtimeSinceStartup</c>.
    /// </summary>
    public sealed class FocusWindow
    {
        /// <summary>
        /// Seconds after a focus change during which a reading is still blamed on it. Covers the
        /// frame focus was lost on and the reload cost on the way back in.
        /// </summary>
        public const double AttributionSeconds = 2d;

        /// <summary>
        /// How far either side of a focus change a stall is taken to BE that change.
        /// <para>
        /// Measured, not guessed: in the 17:41 session every ~1.1 s spike was followed within
        /// 20 ms by <c>focusChanged(false)</c>. Unity raises the callback after the stall it
        /// caused, so a spike emitted the instant it is measured always reads as focused. Health
        /// events are therefore held for <see cref="HoldSeconds"/> and attributed once the
        /// callback has had its chance to arrive.
        /// </para>
        /// </summary>
        public const double NearChangeSeconds = 0.75d;

        /// <summary>How long a health event waits before it is attributed and emitted.</summary>
        public const double HoldSeconds = NearChangeSeconds;

        /// <summary><see cref="CauseAt"/> when the stall is the focus change itself.</summary>
        public const string FocusChangeCause = "focus-change";

        /// <summary><see cref="CauseAt"/> when the window is in the background, or just came back.</summary>
        public const string UnfocusedCause = "unfocused";

        private readonly object _gate = new object();

        private bool _focused = true;
        private double _changedAt = double.NegativeInfinity;

        /// <summary>Whether the window currently has focus, as last reported.</summary>
        public bool Focused
        {
            get
            {
                lock (_gate)
                {
                    return _focused;
                }
            }
        }

        /// <summary>
        /// Records a focus change. Called from <c>Application.focusChanged</c>; a repeat of the
        /// state we already hold does not restart the window.
        /// </summary>
        /// <param name="focused">The new focus state.</param>
        /// <param name="atSeconds">When it changed, in seconds since start-up.</param>
        public void Change(bool focused, double atSeconds)
        {
            lock (_gate)
            {
                if (_focused == focused)
                {
                    return;
                }

                _focused = focused;
                _changedAt = atSeconds;
            }
        }

        /// <summary>
        /// True when a bad reading taken at <paramref name="nowSeconds"/> should be blamed on the
        /// window: either it has no focus right now, or focus changed within
        /// <see cref="AttributionSeconds"/> - which covers the seconds after a regain, where the
        /// game is still catching up.
        /// </summary>
        /// <param name="nowSeconds">Now, in seconds since start-up.</param>
        public bool Attributes(double nowSeconds) => CauseAt(nowSeconds, nowSeconds) != null;

        /// <summary>
        /// True when a focus change happened within <paramref name="windowSeconds"/> either side
        /// of <paramref name="atSeconds"/>.
        /// </summary>
        /// <param name="atSeconds">When the reading was taken, in seconds since start-up.</param>
        /// <param name="windowSeconds">Half-width of the window; negative is treated as zero.</param>
        public bool ChangedNear(double atSeconds, double windowSeconds)
        {
            var half = windowSeconds > 0d ? windowSeconds : 0d;

            lock (_gate)
            {
                return Math.Abs(atSeconds - _changedAt) <= half;
            }
        }

        /// <summary>
        /// Why a reading taken at <paramref name="eventAtSeconds"/> and attributed at
        /// <paramref name="nowSeconds"/> is the window's fault, or null when it is not.
        /// </summary>
        /// <param name="eventAtSeconds">When the reading was taken, in seconds since start-up.</param>
        /// <param name="nowSeconds">
        /// When it is being attributed - normally <see cref="HoldSeconds"/> later, by which time
        /// Unity's focus callback has arrived.
        /// </param>
        /// <returns>
        /// <see cref="FocusChangeCause"/> when the stall is the switch itself,
        /// <see cref="UnfocusedCause"/> when the window is in the background or just came back,
        /// null when the game is simply running badly and someone should look at it.
        /// </returns>
        public string? CauseAt(double eventAtSeconds, double nowSeconds)
        {
            lock (_gate)
            {
                if (Math.Abs(eventAtSeconds - _changedAt) <= NearChangeSeconds)
                {
                    return FocusChangeCause;
                }

                // Background, or the first seconds back: the game throttles itself in both.
                return !_focused || nowSeconds - _changedAt <= AttributionSeconds
                    ? UnfocusedCause
                    : null;
            }
        }
    }
}
