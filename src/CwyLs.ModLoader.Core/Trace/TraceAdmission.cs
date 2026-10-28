using System.Threading;

namespace CwyLs.ModLoader.Core.Trace
{
    /// <summary>What the admission decided about one call.</summary>
    public enum TraceAdmit
    {
        /// <summary>Write the call as its own line.</summary>
        Write,

        /// <summary>Write one aggregate line standing for the window that just closed.</summary>
        Aggregate,

        /// <summary>Write nothing; the call has been counted.</summary>
        Suppress,
    }

    /// <summary>The admission's answer. A struct with no allocation: this is the hot path.</summary>
    public struct AdmitResult
    {
        /// <summary>What to do with this call.</summary>
        public TraceAdmit Action;

        /// <summary>On the first permit of a new window, how many calls the previous one dropped.</summary>
        public int Suppressed;

        /// <summary>For <see cref="TraceAdmit.Aggregate"/>: calls observed in the window just closed.</summary>
        public int AggregateCount;

        /// <summary>For <see cref="TraceAdmit.Aggregate"/>: the raw arguments last seen in that window.</summary>
        public object? AggregatePayload;

        /// <summary>True while the entry is aggregating, so the caller should store this call's arguments.</summary>
        public bool RemembersPayload;

        /// <summary>True exactly once, on the call that puts the method into polling mode.</summary>
        public bool EnteredPolling;

        /// <summary>True exactly once, on the call that takes it back out.</summary>
        public bool LeftPolling;

        /// <summary>Calls seen in the window that just closed, for the transition log line.</summary>
        public int ObservedPerSecond;
    }

    /// <summary>
    /// The per-method rate decision, made entirely with interlocked operations on the method's own
    /// <see cref="TraceEntry"/>.
    /// </summary>
    /// <remarks>
    /// The previous version took one process-wide lock and did a string-keyed dictionary lookup per
    /// call. With <c>AudioPlayer.Stop</c> alone at roughly 13,000 calls a second and eighty more
    /// pollers behind it, that lock <em>was</em> the 55 % frame-time regression. Here the common
    /// case — a call inside an open window on a method already classified as polling — is one
    /// volatile read, one compare, one interlocked increment and one volatile write.
    /// <para>
    /// Window rollover is elected by a compare-and-swap on <see cref="TraceEntry.WindowStartTicks"/>:
    /// exactly one thread wins and does the bookkeeping, every other thread takes the ordinary path.
    /// </para>
    /// </remarks>
    public static class TraceAdmission
    {
        /// <summary>Consecutive over-cap windows before a method is treated as polling.</summary>
        public const int WindowsToEnterPolling = 3;

        /// <summary>Consecutive under-cap windows before a polling method goes back to normal.</summary>
        public const int WindowsToLeavePolling = 5;

        /// <summary>
        /// Takes one permit and says what to do with the call.
        /// </summary>
        /// <param name="entry">The method's descriptor; its state is updated in place.</param>
        /// <param name="nowTicks">A <c>Stopwatch.GetTimestamp()</c> reading.</param>
        /// <param name="windowTicks">Length of a normal window, in stopwatch ticks.</param>
        /// <param name="pollingWindowTicks">Length of an aggregate window, in stopwatch ticks.</param>
        /// <param name="permitsPerSecond">Lines per window before suppression. 0 or less means no limit.</param>
        public static AdmitResult Admit(
            TraceEntry entry,
            long nowTicks,
            long windowTicks,
            long pollingWindowTicks,
            int permitsPerSecond)
        {
            var result = default(AdmitResult);

            if (entry is null)
            {
                result.Action = TraceAdmit.Suppress;
                return result;
            }

            if (permitsPerSecond <= 0)
            {
                result.Action = TraceAdmit.Write;
                return result;
            }

            var polling = Volatile.Read(ref entry.PollingFlag) != 0;
            var window = polling ? pollingWindowTicks : windowTicks;
            var start = Volatile.Read(ref entry.WindowStartTicks);

            if (nowTicks - start >= window
                && Interlocked.CompareExchange(ref entry.WindowStartTicks, nowTicks, start) == start)
            {
                return RollWindow(entry, nowTicks, permitsPerSecond, polling, ref result);
            }

            var used = Interlocked.Increment(ref entry.Used);

            if (polling)
            {
                result.Action = TraceAdmit.Suppress;
                result.RemembersPayload = true;
                return result;
            }

            if (used <= permitsPerSecond)
            {
                result.Action = TraceAdmit.Write;
                return result;
            }

            Interlocked.Increment(ref entry.Suppressed);
            result.Action = TraceAdmit.Suppress;
            return result;
        }

        /// <summary>The one thread that won the compare-and-swap closes the window and opens the next.</summary>
        private static AdmitResult RollWindow(
            TraceEntry entry,
            long nowTicks,
            int permitsPerSecond,
            bool polling,
            ref AdmitResult result)
        {
            // This call is the first of the new window.
            var observed = Interlocked.Exchange(ref entry.Used, 1);
            var suppressed = Interlocked.Exchange(ref entry.Suppressed, 0);

            Volatile.Write(ref entry.LastObserved, observed);
            result.ObservedPerSecond = observed;

            if (polling)
            {
                result.Action = TraceAdmit.Aggregate;
                result.AggregateCount = observed;
                result.AggregatePayload = Interlocked.Exchange(ref entry.LastArgs, null);
                result.RemembersPayload = true;

                if (observed <= permitsPerSecond)
                {
                    if (Interlocked.Increment(ref entry.QuietWindows) >= WindowsToLeavePolling)
                    {
                        Volatile.Write(ref entry.PollingFlag, 0);
                        Volatile.Write(ref entry.QuietWindows, 0);
                        Volatile.Write(ref entry.HotWindows, 0);
                        Volatile.Write(ref entry.PollingSinceTicks, 0L);
                        result.RemembersPayload = false;
                        result.LeftPolling = true;
                    }
                }
                else
                {
                    Volatile.Write(ref entry.QuietWindows, 0);
                }

                return result;
            }

            result.Action = TraceAdmit.Write;
            result.Suppressed = suppressed;

            if (observed > permitsPerSecond)
            {
                if (Interlocked.Increment(ref entry.HotWindows) >= WindowsToEnterPolling)
                {
                    Volatile.Write(ref entry.PollingFlag, 1);
                    Volatile.Write(ref entry.HotWindows, 0);
                    Volatile.Write(ref entry.QuietWindows, 0);
                    Volatile.Write(ref entry.PollingSinceTicks, nowTicks);
                    result.EnteredPolling = true;
                }
            }
            else
            {
                Volatile.Write(ref entry.HotWindows, 0);
            }

            return result;
        }

        /// <summary>Stores the raw arguments of the call just seen, for the next aggregate line.</summary>
        public static void RememberArgs(TraceEntry entry, object? payload)
        {
            if (entry != null)
            {
                Volatile.Write(ref entry.LastArgs, payload);
            }
        }

        /// <summary>
        /// Whether this method has been hot enough for long enough to be worth unpatching.
        /// </summary>
        /// <param name="entry">The method's descriptor.</param>
        /// <param name="nowTicks">A <c>Stopwatch.GetTimestamp()</c> reading.</param>
        /// <param name="abovePerSecond">Rate above which a method counts as too hot.</param>
        /// <param name="afterTicks">How long it must stay that hot, in stopwatch ticks.</param>
        public static bool ShouldUnpatch(TraceEntry entry, long nowTicks, int abovePerSecond, long afterTicks)
        {
            if (entry is null || entry.Curated || entry.IsUnpatched || !entry.IsPolling)
            {
                return false;
            }

            var since = Volatile.Read(ref entry.PollingSinceTicks);
            return since > 0
                && nowTicks - since >= afterTicks
                && Volatile.Read(ref entry.LastObserved) >= abovePerSecond;
        }
    }
}
