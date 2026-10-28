using System;
using System.Threading.Tasks;

namespace CwyLs.ModLoader.Core.Satone.Say
{
    /// <summary>
    /// What <c>Satone.Say</c> hands back: the line's id, whether it has finished, how it finished,
    /// and a way to take it out of the queue again.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>Satone.Say</c> returns as soon as the line is queued - the speaking itself takes seconds
    /// and belongs to the game's own scenario reader. Wait for it with <see cref="Completion"/>, or
    /// poll <see cref="IsCompleted"/> from a frame loop.
    /// </para>
    /// <para>
    /// <see cref="Completion"/> is completed on Unity's main thread, so a synchronous continuation
    /// runs there; an <c>await</c> without a synchronisation context resumes on a thread-pool
    /// thread, and anything it does with the engine has to marshal back itself.
    /// </para>
    /// </remarks>
    public sealed class SayHandle
    {
        private readonly TaskCompletionSource<SayOutcome> _completion =
            new TaskCompletionSource<SayOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly Func<SayLine, bool>? _cancel;
        private readonly object _gate = new object();

        /// <summary>Creates a handle for a line.</summary>
        /// <param name="line">The line. Required.</param>
        /// <param name="cancel">
        /// What to call when the caller cancels: it should take the line out of the queue and
        /// return true when it did. Null makes <see cref="Cancel"/> always answer false.
        /// </param>
        /// <exception cref="ArgumentNullException"><paramref name="line"/> is null.</exception>
        public SayHandle(SayLine line, Func<SayLine, bool>? cancel = null)
        {
            Line = line ?? throw new ArgumentNullException(nameof(line));
            _cancel = cancel;
        }

        /// <summary>The line's session-unique number.</summary>
        public int Id => Line.Id;

        /// <summary>The resolved line, with its ids, voice name and motion indices.</summary>
        public SayLine Line { get; }

        /// <summary>True once the line has finished, one way or another.</summary>
        public bool IsCompleted { get; private set; }

        /// <summary>How it finished; <see cref="SayOutcome.Pending"/> until it has.</summary>
        public SayOutcome Outcome { get; private set; } = SayOutcome.Pending;

        /// <summary>Evidence for <see cref="Outcome"/>, for a log line. Never null.</summary>
        public string Detail { get; private set; } = string.Empty;

        /// <summary>Completes when the line does, with its outcome. Never faults.</summary>
        public Task<SayOutcome> Completion => _completion.Task;

        /// <summary>
        /// Takes the line out of the queue if it has not started yet.
        /// </summary>
        /// <returns>
        /// True when it was still waiting and is now cancelled. False when it has already started,
        /// already finished, or was never queued - the game's scenario reader owns a line it has
        /// begun, and stopping that mid-sentence is not this API's job.
        /// </returns>
        public bool Cancel()
        {
            if (IsCompleted || _cancel is null)
            {
                return false;
            }

            if (!_cancel(Line))
            {
                return false;
            }

            Complete(SayOutcome.Cancelled, "cancelled before it started");
            return true;
        }

        /// <summary>
        /// Records the outcome and releases anything waiting on <see cref="Completion"/>. Called
        /// by the feature that owns the queue; the second call is ignored, so a line that both
        /// timed out and then ended is reported once.
        /// </summary>
        /// <param name="outcome">How it finished.</param>
        /// <param name="detail">Evidence, for the log. Null becomes empty.</param>
        /// <returns>True when this call was the one that completed the handle.</returns>
        public bool Complete(SayOutcome outcome, string? detail = null)
        {
            lock (_gate)
            {
                if (IsCompleted)
                {
                    return false;
                }

                IsCompleted = true;
                Outcome = outcome;
                Detail = detail ?? string.Empty;
            }

            _completion.TrySetResult(outcome);
            return true;
        }

        /// <summary>A handle that was never queued, already completed as <see cref="SayOutcome.Rejected"/>.</summary>
        /// <param name="line">The line that was refused.</param>
        /// <param name="reason">Why, for the log.</param>
        public static SayHandle Rejected(SayLine line, string reason)
        {
            var handle = new SayHandle(line);
            handle.Complete(SayOutcome.Rejected, reason);
            return handle;
        }

        /// <summary>A one-line description for a log.</summary>
        public override string ToString() =>
            Line + " -> " + Outcome + (Detail.Length == 0 ? string.Empty : " (" + Detail + ")");
    }
}
