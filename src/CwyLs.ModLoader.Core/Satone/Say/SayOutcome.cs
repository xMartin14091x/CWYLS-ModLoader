namespace CwyLs.ModLoader.Core.Satone.Say
{
    /// <summary>How a <c>Satone.Say</c> line ended.</summary>
    public enum SayOutcome
    {
        /// <summary>Still queued or still being spoken.</summary>
        Pending = 0,

        /// <summary>
        /// The game's scenario reader played the line and its <c>EndStory</c> was observed: voice,
        /// mouth, motion and timing were all the game's own.
        /// </summary>
        Spoken = 1,

        /// <summary>
        /// The scenario path was not available in time, so the line was spoken voice only: the clip
        /// was played through <c>HeroineService.PlayVoice</c> and her mouth moves, but the scenario
        /// motion and the game's text box do not happen, and nothing else draws the text (it is
        /// only in the console's <c>SAY</c> line). A line without a voice is only in the log.
        /// </summary>
        FellBack = 2,

        /// <summary>The caller cancelled it before it started.</summary>
        Cancelled = 3,

        /// <summary>It was never accepted: bad request, feature off, or the queue was full.</summary>
        Rejected = 4,

        /// <summary>
        /// It started but the game never reported the scenario ending, so the queue gave up on it
        /// rather than blocking every later line.
        /// </summary>
        TimedOut = 5
    }

    /// <summary>What <see cref="SayQueue.Next"/> wants the caller to do this frame.</summary>
    public enum SayStepKind
    {
        /// <summary>Nothing to do.</summary>
        Idle = 0,

        /// <summary>Start this line through the game's scenario reader.</summary>
        Start = 1,

        /// <summary>
        /// Speak this line the simple way: it waited longer than the queue's patience while the
        /// game was busy.
        /// </summary>
        Fallback = 2,

        /// <summary>
        /// Give up on the active line: the game never reported its scenario ending.
        /// </summary>
        Abandon = 3
    }

    /// <summary>One instruction from <see cref="SayQueue.Next"/>.</summary>
    public readonly struct SayStep
    {
        /// <summary>The "nothing to do" instruction.</summary>
        public static readonly SayStep Idle = new SayStep(SayStepKind.Idle, null);

        /// <summary>Creates a step.</summary>
        /// <param name="kind">What to do.</param>
        /// <param name="line">The line it applies to; null only for <see cref="SayStepKind.Idle"/>.</param>
        public SayStep(SayStepKind kind, SayLine? line)
        {
            Kind = kind;
            Line = line;
        }

        /// <summary>What to do.</summary>
        public SayStepKind Kind { get; }

        /// <summary>The line to do it to, or null when there is nothing to do.</summary>
        public SayLine? Line { get; }

        /// <summary>True when there is work in this step.</summary>
        public bool HasWork => Kind != SayStepKind.Idle && Line != null;

        /// <summary>A one-line description for a log.</summary>
        public override string ToString() => Line is null ? Kind.ToString() : Kind + " " + Line;
    }
}
