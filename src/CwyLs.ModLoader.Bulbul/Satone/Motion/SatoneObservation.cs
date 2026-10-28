using System;
using System.Globalization;

namespace CwyLs.ModLoader.Bulbul.Satone.Motion
{
    /// <summary>What became of one request after it left our hands and entered the Animator.</summary>
    public enum SatoneObservationOutcome
    {
        /// <summary>Still inside the observation window.</summary>
        Pending,

        /// <summary>The expected effect was seen.</summary>
        Confirmed,

        /// <summary>The window elapsed without the expected effect.</summary>
        TimedOut,

        /// <summary>Nothing could be observed - no Animator read-back, or no state to compare against.</summary>
        Unobservable
    }

    /// <summary>
    /// A request being watched for its effect: the call itself is synchronous, but the Animator
    /// only moves on later frames, so "did it work" is answered a frame or two afterwards.
    /// </summary>
    /// <remarks>
    /// One observation is live at a time - each new request replaces the previous one, which is
    /// what a human watching the log expects. Times are <c>Time.realtimeSinceStartup</c>, so they
    /// survive a paused or time-scaled game.
    /// </remarks>
    public sealed class SatoneObservation
    {
        /// <summary>Creates a pending observation.</summary>
        /// <param name="kind"><c>motion</c>, <c>face</c> or <c>look</c>.</param>
        /// <param name="what">The name that was requested, for the log line.</param>
        /// <param name="expected">What confirmation looks like, e.g. an Animator state name.</param>
        /// <param name="startedRealtime"><c>Time.realtimeSinceStartup</c> at the request.</param>
        /// <param name="budgetSeconds">How long to wait before calling it a timeout.</param>
        /// <param name="warnOnTimeout">
        /// Whether a timeout deserves a warning. False for the look seam, whose effect is
        /// legitimately invisible to us while the game is playing a click or short-talk scenario
        /// (HeroineLookDirectionService.ChangeLookScaleByManualForStory takes a branch that never
        /// writes the Animator parameter), so a warning there would be noise, not news.
        /// </param>
        public SatoneObservation(
            string kind,
            string what,
            string expected,
            float startedRealtime,
            float budgetSeconds,
            bool warnOnTimeout = true)
        {
            Kind = kind ?? "?";
            What = what ?? "?";
            Expected = expected ?? string.Empty;
            StartedRealtime = startedRealtime;
            BudgetSeconds = budgetSeconds;
            WarnOnTimeout = warnOnTimeout;
            Outcome = SatoneObservationOutcome.Pending;
            Detail = string.Empty;
        }

        /// <summary>Whether a timeout on this observation is logged as a warning.</summary>
        public bool WarnOnTimeout { get; }

        /// <summary><c>motion</c>, <c>face</c> or <c>look</c>.</summary>
        public string Kind { get; }

        /// <summary>The name that was requested.</summary>
        public string What { get; }

        /// <summary>What confirmation looks like - an Animator state name, or a parameter value.</summary>
        public string Expected { get; }

        /// <summary><c>Time.realtimeSinceStartup</c> at the request.</summary>
        public float StartedRealtime { get; }

        /// <summary>Seconds allowed before the observation times out.</summary>
        public float BudgetSeconds { get; }

        /// <summary>Current verdict.</summary>
        public SatoneObservationOutcome Outcome { get; private set; }

        /// <summary>Milliseconds from the request to the verdict; -1 while pending.</summary>
        public int ElapsedMs { get; private set; } = -1;

        /// <summary>Human-readable evidence for the verdict.</summary>
        public string Detail { get; private set; }

        /// <summary>Records a verdict. The first verdict wins; later calls are ignored.</summary>
        /// <param name="outcome">The verdict.</param>
        /// <param name="elapsedMs">Milliseconds since the request.</param>
        /// <param name="detail">Evidence.</param>
        public void Settle(SatoneObservationOutcome outcome, int elapsedMs, string detail)
        {
            if (Outcome != SatoneObservationOutcome.Pending)
            {
                return;
            }

            Outcome = outcome;
            ElapsedMs = elapsedMs;
            Detail = detail ?? string.Empty;
        }

        /// <summary>e.g. <c>motion wild001_stretch_shoulder: Confirmed after 183 ms (state ...)</c>.</summary>
        public override string ToString()
        {
            var timing = ElapsedMs >= 0
                ? " after " + ElapsedMs.ToString(CultureInfo.InvariantCulture) + " ms"
                : string.Empty;
            var detail = string.IsNullOrEmpty(Detail) ? string.Empty : " (" + Detail + ")";
            return Kind + " " + What + ": " + Outcome + timing + detail;
        }
    }
}
