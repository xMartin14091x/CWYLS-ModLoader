using System;
using System.Globalization;
using CwyLs.ModLoader.Core.Logging;
using CwyLs.ModLoader.Threading;
using UnityEngine;

namespace CwyLs.ModLoader.Bulbul.Satone.Motion
{
    /// <summary>
    /// The Satone motion API's main-thread side: it marshals every game call onto Unity's main
    /// thread, schedules delayed calls, watches requests land and keeps the cached state that
    /// off-thread readers see.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Marshalling.</b> Every game call goes through <see cref="Post"/>. On the main thread it
    /// runs immediately, which keeps <c>Satone.Motion(...)</c> synchronous for the common case;
    /// from any other thread - an LLM callback, a TTS completion, a file watcher - it is queued
    /// and runs on the next frame. Unity's API is main-thread-only and throws otherwise, so this
    /// is not optional.
    /// </para>
    /// <para>
    /// The queue, the delayed list and the hidden runner object this class
    /// used to own are now the framework's shared dispatcher (<see cref="MainThread"/>), under the
    /// owner name <see cref="Owner"/>; the per-frame work is a ticker on it. What stays here is
    /// what is Satone's own: the observation of the newest request and the state cache.
    /// </para>
    /// </remarks>
    public static class SatoneMotionHost
    {
        /// <summary>Owner name of this feature's work on the shared dispatcher.</summary>
        public const string Owner = "satone.motion";

        /// <summary>Frames between cached-state refreshes, once an off-thread reader has asked once.</summary>
        public const int StatePollFrames = 15;

        private static readonly object Gate = new object();

        private static IDisposable? _ticker;
        private static SatoneObservation? _observation;
        private static SatoneStateCache _cache = SatoneStateCache.Unknown;
        private static bool _cacheWanted;
        private static int _nextPollFrame;

        /// <summary>True once the per-frame ticker is registered and the shared host is alive.</summary>
        public static bool HasRunner => _ticker != null && MainThread.HasHost;

        /// <summary>Seconds on the shared dispatcher's monotonic clock; safe to read from any thread.</summary>
        public static double Now => MainThread.Now;

        /// <summary>How many delayed calls are waiting for their moment.</summary>
        public static int DelayedCount => MainThread.DelayedOf(Owner);

        /// <summary>Calls dropped because the queue was full; should always be 0.</summary>
        public static long Dropped => MainThread.DroppedOf(Owner);

        /// <summary>The request currently being watched, or the last one watched. Null before the first.</summary>
        public static SatoneObservation? LastObservation
        {
            get { lock (Gate) { return _observation; } }
        }

        /// <summary>True when the caller is on Unity's main thread, so a game call may run now.</summary>
        public static bool OnMainThread => MainThread.IsMainThread;

        /// <summary>
        /// Makes sure the shared dispatcher knows the main thread and registers this feature's
        /// per-frame tick. Call from the plugin's <c>Awake</c>, which is the main thread by
        /// definition. Idempotent.
        /// </summary>
        public static void Arm()
        {
            MainThread.Arm();
            lock (Gate)
            {
                if (_ticker != null)
                {
                    return;
                }

                _ticker = MainThread.AddTicker("motion tick", Tick, Owner);
            }

            CwyLsModLoader.Log.LogDebug("SATONE motion ticks on the shared main-thread host (owner " + Owner + ")");
        }

        /// <summary>
        /// Runs <paramref name="work"/> on the main thread: now when already there, otherwise on
        /// the next frame.
        /// </summary>
        /// <param name="label">What the call is, for the log line when it fails or is dropped.</param>
        /// <param name="work">The work. Exceptions are caught and logged, never rethrown at the caller.</param>
        /// <returns>True when it ran or was queued; false when the queue was full.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="work"/> is null.</exception>
        public static bool Post(string label, Action work)
        {
            if (work is null)
            {
                throw new ArgumentNullException(nameof(work));
            }

            return MainThread.RunOrPost(label, work, Owner);
        }

        /// <summary>
        /// Runs <paramref name="work"/> on the main thread after <paramref name="seconds"/> have
        /// passed. This is how a synchronous caller - a smoke check's <c>finally</c>, a Say line
        /// that has just started - asks for something to happen <em>later</em> without blocking a
        /// frame.
        /// </summary>
        /// <param name="seconds">
        /// How long to wait. Zero or less is the same as <see cref="Post"/>. Measured on the shared
        /// dispatcher's Stopwatch clock, so it is unaffected by <c>Time.timeScale</c> or a paused game.
        /// </param>
        /// <param name="label">What the call is, for the log line when it fails or is dropped.</param>
        /// <param name="work">The work. Exceptions are caught and logged, never rethrown.</param>
        /// <returns>True when it ran or was scheduled; false when the delayed queue was full.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="work"/> is null.</exception>
        public static bool PostAfter(float seconds, string label, Action work)
        {
            if (work is null)
            {
                throw new ArgumentNullException(nameof(work));
            }

            return seconds <= 0f ? Post(label, work) : MainThread.PostAfterFor(Owner, seconds, label, work).IsAccepted;
        }

        /// <summary>
        /// Drops every delayed call that has not run yet - not yet due, or due and still waiting -
        /// for example when the room scene has gone and a restore would only find a dead
        /// <c>HeroineService</c>. The shared dispatcher runs framework owners such as
        /// this one in full every frame, so a mod backlog can no longer hold a restore back.
        /// </summary>
        /// <param name="reason">Why, for the log line. Nothing is logged when nothing was waiting.</param>
        /// <returns>How many calls were dropped.</returns>
        public static int CancelDelayed(string reason)
        {
            var count = MainThread.CancelDelayed(Owner);
            if (count > 0)
            {
                CwyLsModLoader.Log.LogDebug(
                    "SATONE dropped " + count.ToString(CultureInfo.InvariantCulture)
                    + " delayed call(s): " + reason);
            }

            return count;
        }

        /// <summary>
        /// Starts watching a request. Replaces any previous observation - only the newest matters.
        /// </summary>
        /// <param name="observation">The observation, already stamped with its start time.</param>
        public static void Watch(SatoneObservation observation)
        {
            lock (Gate)
            {
                _observation = observation;
            }
        }

        /// <summary>Last known Satone state; refreshed on the main thread.</summary>
        public static SatoneStateCache Cache
        {
            get { lock (Gate) { return _cache; } }
        }

        /// <summary>
        /// Notes that something read state from another thread, so the host starts refreshing the
        /// cache every <see cref="StatePollFrames"/> frames. Until then nothing is polled at all.
        /// </summary>
        public static void WantCache()
        {
            lock (Gate)
            {
                _cacheWanted = true;
            }
        }

        /// <summary>Publishes a fresh cache snapshot. Main thread only.</summary>
        /// <param name="cache">The snapshot.</param>
        public static void PublishCache(SatoneStateCache cache)
        {
            lock (Gate)
            {
                _cache = cache;
            }
        }

        /// <summary>
        /// One frame of Satone's own work, after the frame's queued calls: advance the pending
        /// observation and refresh the state cache. A ticker on the shared dispatcher.
        /// </summary>
        private static void Tick()
        {
            SatoneMotionFeature.Tick();

            bool wanted;
            lock (Gate)
            {
                wanted = _cacheWanted;
            }

            if (wanted && Time.frameCount >= _nextPollFrame)
            {
                _nextPollFrame = Time.frameCount + StatePollFrames;
                SatoneMotionFeature.RefreshCache();
            }
        }

        /// <summary>
        /// Settles the pending observation if it is done, and reports it. Main thread only.
        /// </summary>
        /// <param name="confirmed">Whether the expected effect is visible now.</param>
        /// <param name="detail">
        /// Evidence for the verdict, given the observation and whether it was confirmed.
        /// </param>
        internal static void Advance(
            Func<SatoneObservation, bool> confirmed,
            Func<SatoneObservation, bool, string> detail)
        {
            SatoneObservation? pending;
            lock (Gate)
            {
                pending = _observation;
            }

            if (pending is null || pending.Outcome != SatoneObservationOutcome.Pending)
            {
                return;
            }

            var elapsed = (int)((Time.realtimeSinceStartup - pending.StartedRealtime) * 1000f);
            if (confirmed(pending))
            {
                pending.Settle(SatoneObservationOutcome.Confirmed, elapsed, detail(pending, true));
                Report(pending);
                return;
            }

            if (Time.realtimeSinceStartup - pending.StartedRealtime >= pending.BudgetSeconds)
            {
                pending.Settle(SatoneObservationOutcome.TimedOut, elapsed, detail(pending, false));
                Report(pending);
            }
        }

        /// <summary>
        /// What actually happened after a request. A confirmation is trace-file business - the
        /// console already showed the request - so only a timeout still interrupts, as a warning.
        /// </summary>
        /// <param name="observation">The finished observation.</param>
        private static void Report(SatoneObservation observation)
        {
            if (observation.Outcome == SatoneObservationOutcome.TimedOut && observation.WarnOnTimeout)
            {
                CwyLsModLoader.Log.LogWarning("SATONE " + observation);
            }

            ConsoleEvents.File(
                SatoneMotionFeature.TraceSystem,
                observation.Kind,
                ("phase", "observed"),
                ("what", observation.What),
                ("expected", observation.Expected),
                ("outcome", observation.Outcome.ToString()),
                ("ms", observation.ElapsedMs),
                ("detail", observation.Detail));
        }
    }
}
