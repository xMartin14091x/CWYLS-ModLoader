using System.Globalization;

namespace CwyLs.ModLoader.Core.Satone
{
    /// <summary>
    /// What one <c>Satone.RestoreDefaults</c> call did, per channel. The single
    /// <c>SATONE restore face=… motion=… gaze=…</c> console line is <see cref="Describe"/>.
    /// </summary>
    /// <remarks>
    /// "Scheduled" is the honest word for the expression: the game's own reset is
    /// <c>InitHeroineFacialAfterDelay(0.77f)</c> (<c>HeroineService</c> -&gt;
    /// <c>HeroineFacialController</c>), which waits before it writes <c>Facial = 0</c>. The gaze
    /// is the same shape - <c>LookInitSlowly()</c> eases out over 1.5 s after a half-second delay
    /// (<c>HeroineService</c> -&gt; <c>HeroineLookDirectionService</c>). Only the motion
    /// re-dispatch is immediate.
    /// </remarks>
    public sealed class RestoreResult
    {
        /// <summary>Creates a result. Prefer the factories for the common shapes.</summary>
        /// <param name="requested">The scope the caller asked for.</param>
        /// <param name="faceScheduled">True when the expression reset was handed to the game.</param>
        /// <param name="faceDelaySeconds">The delay that reset will wait, in seconds.</param>
        /// <param name="motionRedispatched">True when the AI state was re-dispatched.</param>
        /// <param name="motionState">The state that was (or would have been) re-dispatched.</param>
        /// <param name="motionSkipReason">Why the motion was left alone; empty when it was not.</param>
        /// <param name="gazeReset">True when the gaze ease-out was started.</param>
        /// <param name="problem">Why nothing happened at all; empty when something did.</param>
        /// <param name="queued">True when the call was queued for the next frame from another thread.</param>
        public RestoreResult(
            RestoreScope requested,
            bool faceScheduled,
            float faceDelaySeconds,
            bool motionRedispatched,
            string? motionState,
            string? motionSkipReason,
            bool gazeReset,
            string? problem,
            bool queued)
        {
            Requested = requested;
            FaceScheduled = faceScheduled;
            FaceDelaySeconds = faceDelaySeconds;
            MotionRedispatched = motionRedispatched;
            MotionState = motionState ?? string.Empty;
            MotionSkipReason = motionSkipReason ?? string.Empty;
            GazeReset = gazeReset;
            Problem = problem ?? string.Empty;
            Queued = queued;
        }

        /// <summary>The scope the caller asked for.</summary>
        public RestoreScope Requested { get; }

        /// <summary>True when the game's delayed expression reset was started.</summary>
        public bool FaceScheduled { get; }

        /// <summary>Seconds the expression reset waits; 0 when none was scheduled.</summary>
        public float FaceDelaySeconds { get; }

        /// <summary>True when the current AI state was re-dispatched to restore the base loop.</summary>
        public bool MotionRedispatched { get; }

        /// <summary>
        /// The AI state involved: the one re-dispatched, or the one that was refused. Empty when
        /// the motion channel was not requested.
        /// </summary>
        public string MotionState { get; }

        /// <summary>Why the motion was not restored. Empty when it was, or was not requested.</summary>
        public string MotionSkipReason { get; }

        /// <summary>True when the gaze ease-out was started.</summary>
        public bool GazeReset { get; }

        /// <summary>Why nothing happened at all. Empty on a normal call.</summary>
        public string Problem { get; }

        /// <summary>
        /// True when the call came from another thread and was queued for the next frame, so the
        /// per-channel flags are not filled in yet.
        /// </summary>
        public bool Queued { get; }

        /// <summary>The channels that actually did something.</summary>
        public RestoreScope Applied
        {
            get
            {
                var scope = RestoreScope.None;
                if (FaceScheduled)
                {
                    scope |= RestoreScope.Face;
                }

                if (MotionRedispatched)
                {
                    scope |= RestoreScope.Motion;
                }

                if (GazeReset)
                {
                    scope |= RestoreScope.Gaze;
                }

                return scope;
            }
        }

        /// <summary>True when every requested channel did something.</summary>
        public bool Complete => !Queued && Problem.Length == 0 && Applied == Requested;

        /// <summary>Nothing could be done, and why.</summary>
        /// <param name="requested">The scope the caller asked for.</param>
        /// <param name="problem">Why. Shown in the console line.</param>
        public static RestoreResult Refused(RestoreScope requested, string problem) =>
            new RestoreResult(requested, false, 0f, false, null, null, false, problem, queued: false);

        /// <summary>The call was queued for the main thread and its verdict is not known yet.</summary>
        /// <param name="requested">The scope the caller asked for.</param>
        public static RestoreResult QueuedFor(RestoreScope requested) =>
            new RestoreResult(requested, false, 0f, false, null, null, false, string.Empty, queued: true);

        /// <summary>e.g. <c>face=in 0.77s motion=work_pc gaze=easing</c>.</summary>
        public string Describe()
        {
            if (Queued)
            {
                return "queued for the next frame (scope " + Requested.Describe() + ")";
            }

            if (Problem.Length > 0)
            {
                return "nothing: " + Problem + " (scope " + Requested.Describe() + ")";
            }

            return "face=" + Face() + " motion=" + Motion() + " gaze=" + Gaze();
        }

        /// <summary>Same as <see cref="Describe"/>.</summary>
        public override string ToString() => Describe();

        private string Face()
        {
            if (!Requested.Has(RestoreScope.Face))
            {
                return "-";
            }

            return FaceScheduled
                ? "in " + FaceDelaySeconds.ToString("0.##", CultureInfo.InvariantCulture) + "s"
                : "skipped";
        }

        private string Motion()
        {
            if (!Requested.Has(RestoreScope.Motion))
            {
                return "-";
            }

            if (MotionRedispatched)
            {
                return MotionState.Length == 0 ? "redispatched" : MotionState;
            }

            var state = MotionState.Length == 0 ? "?" : MotionState;
            return "skipped(" + state + ": "
                   + (MotionSkipReason.Length == 0 ? "no reason given" : MotionSkipReason) + ")";
        }

        private string Gaze()
        {
            if (!Requested.Has(RestoreScope.Gaze))
            {
                return "-";
            }

            return GazeReset ? "easing" : "skipped";
        }
    }
}
