using System;
using CwyLs.ModLoader.Threading;

namespace CwyLs.ModLoader.Bulbul.Satone.Motion
{
    /// <summary>
    /// The main-thread side of <c>Satone.Say</c>: it marshals the feature's game calls onto Unity's
    /// main thread and gives the line queue its frame tick.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This used to be a copy of <c>SatoneMotionHost</c>'s queue with its own
    /// hidden runner object. Both now use the framework's shared dispatcher
    /// (<see cref="MainThread"/>): calls are queued under the owner name <see cref="Owner"/> and
    /// <c>SatoneSayFeature.Tick</c> is a ticker on it. It stays a separate owner from the motion
    /// feature, so <c>Satone.Say</c> keeps working when <c>Satone.Enabled=false</c> switches the
    /// motion feature off.
    /// </para>
    /// <para>
    /// <b>The clock.</b> <see cref="Now"/> is the dispatcher's Stopwatch, not
    /// <c>Time.unscaledTime</c>: a line may be queued from a TTS callback on a worker thread, and
    /// Unity's time API throws off the main thread.
    /// </para>
    /// </remarks>
    public static class SatoneSayHost
    {
        /// <summary>Owner name of this feature's work on the shared dispatcher.</summary>
        public const string Owner = "satone.say";

        private static readonly object Gate = new object();

        private static IDisposable? _ticker;

        /// <summary>Monotonic seconds on the shared dispatcher's clock. Safe on any thread.</summary>
        public static double Now => MainThread.Now;

        /// <summary>True once the queue tick is registered and the shared host is alive.</summary>
        public static bool HasRunner => _ticker != null && MainThread.HasHost;

        /// <summary>Calls dropped because the queue was full; should always be 0.</summary>
        public static long Dropped => MainThread.DroppedOf(Owner);

        /// <summary>True when the caller is on Unity's main thread, so a game call may run now.</summary>
        public static bool OnMainThread => MainThread.IsMainThread;

        /// <summary>
        /// Makes sure the shared dispatcher knows the main thread and registers the line queue's
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

                _ticker = MainThread.AddTicker("say queue", () => SatoneSayFeature.Tick(Now), Owner);
            }

            CwyLsModLoader.Log.LogDebug("SATONE say queue ticks on the shared main-thread host (owner " + Owner + ")");
        }

        /// <summary>
        /// Runs <paramref name="work"/> on the main thread: now when already there, otherwise at
        /// the next frame.
        /// </summary>
        /// <param name="label">What the call is, for the log line when it fails or is dropped.</param>
        /// <param name="work">The work. Exceptions are caught and logged, never rethrown.</param>
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
    }
}
