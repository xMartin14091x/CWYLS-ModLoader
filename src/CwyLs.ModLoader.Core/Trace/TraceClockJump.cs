using System;
using System.Globalization;

namespace CwyLs.ModLoader.Core.Trace
{
    /// <summary>
    /// One correction made by <see cref="TraceClock.Check"/>: what the trace clock said, what the
    /// system clock said, and the wording of the one <c>TRACE</c> line that reports it.
    /// </summary>
    public sealed class TraceClockJump
    {
        /// <summary>Creates a record of one re-anchor.</summary>
        /// <param name="reason">What prompted the measurement (e.g. <c>poll</c>, <c>focus</c>).</param>
        /// <param name="traceTime">What the trace clock said at the moment of measurement.</param>
        /// <param name="systemTime">What the system clock said; the new anchor.</param>
        /// <param name="offsetChanged">True when the system clock's UTC offset differs from the old anchor's.</param>
        /// <param name="count">How many re-anchors the clock has made, this one included.</param>
        public TraceClockJump(
            string? reason,
            DateTimeOffset traceTime,
            DateTimeOffset systemTime,
            bool offsetChanged,
            int count)
        {
            Reason = reason ?? string.Empty;
            TraceTime = traceTime;
            SystemTime = systemTime;
            OffsetChanged = offsetChanged;
            Count = count;
        }

        /// <summary>What prompted the measurement.</summary>
        public string Reason { get; }

        /// <summary>What the trace clock said at the moment of measurement.</summary>
        public DateTimeOffset TraceTime { get; }

        /// <summary>What the system clock said. Every reading from now on is dated from this.</summary>
        public DateTimeOffset SystemTime { get; }

        /// <summary>
        /// How far the trace clock moved: positive when it had fallen behind (the machine slept, or
        /// the system clock was set forward), negative when the system clock was set back.
        /// </summary>
        public TimeSpan Jump => SystemTime - TraceTime;

        /// <summary>True when the UTC offset changed (time zone or daylight saving).</summary>
        public bool OffsetChanged { get; }

        /// <summary>How many re-anchors the clock has made, this one included.</summary>
        public int Count { get; }

        /// <summary>The jump as signed text, e.g. <c>+23m59.9s</c>, <c>-1h00m00.0s</c>, <c>+0.4s</c>.</summary>
        public string JumpText => FormatJump(Jump);

        /// <summary>The primary column of the <c>TRACE</c> line.</summary>
        public string Summary
        {
            get
            {
                var why = Jump > TimeSpan.Zero
                    ? "the trace clock had fallen behind the system clock (the machine slept, or the clock was set forward)"
                    : "the system clock was set back";

                if (!OffsetChanged)
                {
                    return "clock re-anchored " + JumpText + ": " + why;
                }

                var text = "clock re-anchored: UTC offset " + Offset(TraceTime.Offset) + " -> "
                    + Offset(SystemTime.Offset) + " (time zone or daylight saving)";

                // A pure offset change moves no instant; only mention a jump that shows at 0.1 s.
                return Jump.Duration() < HalfTenth ? text : text + "; " + JumpText + ": " + why;
            }
        }

        /// <summary>The smallest jump that does not print as <c>0.0s</c>.</summary>
        private static readonly TimeSpan HalfTenth = TimeSpan.FromMilliseconds(50);

        /// <summary>
        /// Signed, to a tenth of a second: <c>+0.4s</c> under a minute, <c>+23m59.9s</c> under an
        /// hour, <c>-1h00m00.0s</c> above.
        /// </summary>
        /// <param name="value">The span to format.</param>
        /// <returns>The text. Zero is <c>+0.0s</c>.</returns>
        public static string FormatJump(TimeSpan value)
        {
            var sign = value < TimeSpan.Zero ? "-" : "+";

            // Rounded once, to whole tenths, so 59.96 s becomes 1m00.0s rather than 60.0s.
            var tenths = (long)Math.Round(
                Math.Abs((double)value.Ticks) / (TimeSpan.TicksPerSecond / 10d),
                MidpointRounding.AwayFromZero);
            var hours = tenths / 36000L;
            var minutes = tenths / 600L % 60L;
            var secondTenths = tenths % 600L;
            var seconds = (secondTenths / 10L).ToString(CultureInfo.InvariantCulture)
                + "." + (secondTenths % 10L).ToString(CultureInfo.InvariantCulture);

            if (hours > 0)
            {
                return sign + hours.ToString(CultureInfo.InvariantCulture) + "h"
                    + minutes.ToString("00", CultureInfo.InvariantCulture) + "m"
                    + (secondTenths < 100L ? "0" : string.Empty) + seconds + "s";
            }

            if (minutes > 0)
            {
                return sign + minutes.ToString(CultureInfo.InvariantCulture) + "m"
                    + (secondTenths < 100L ? "0" : string.Empty) + seconds + "s";
            }

            return sign + seconds + "s";
        }

        private static string Offset(TimeSpan offset) =>
            (offset < TimeSpan.Zero ? "-" : "+") + offset.Duration().ToString(@"hh\:mm", CultureInfo.InvariantCulture);
    }
}
