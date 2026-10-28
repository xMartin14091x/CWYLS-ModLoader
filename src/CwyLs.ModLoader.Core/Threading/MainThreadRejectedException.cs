using System;

namespace CwyLs.ModLoader.Core.Threading
{
    /// <summary>Why the main-thread dispatcher refused a piece of work.</summary>
    public enum MainThreadRejectReason
    {
        /// <summary>The owner's queue or the whole queue is at its cap (<see cref="MainThreadQueue.DefaultCapacity"/> and friends).</summary>
        QueueFull = 0,

        /// <summary>The owner is not running any more (a mod that failed or shut down).</summary>
        OwnerNotRunning = 1,

        /// <summary>The game is quitting; the dispatcher accepts nothing new.</summary>
        ShuttingDown = 2,
    }

    /// <summary>
    /// The framework refused to run a piece of main-thread work. Thrown by the blocking
    /// <c>Invoke</c> calls and set as the fault of an <c>InvokeAsync</c> task. Derives from
    /// <see cref="InvalidOperationException"/>, which is what these refusals were before, so an
    /// existing <c>catch (InvalidOperationException)</c> still sees them.
    /// </summary>
    public sealed class MainThreadRejectedException : InvalidOperationException
    {
        /// <summary>Creates the exception with the standard wording for <paramref name="reason"/>.</summary>
        /// <param name="reason">Why the work was refused.</param>
        /// <param name="owner">Who posted it: a feature name or <c>mod:&lt;id&gt;</c>.</param>
        /// <param name="label">What the work was.</param>
        public MainThreadRejectedException(MainThreadRejectReason reason, string? owner, string? label)
            : base(Describe(reason, owner ?? string.Empty, label ?? string.Empty))
        {
            Reason = reason;
            Owner = owner ?? string.Empty;
            Label = label ?? string.Empty;
        }

        /// <summary>Why the work was refused.</summary>
        public MainThreadRejectReason Reason { get; }

        /// <summary>Who posted it.</summary>
        public string Owner { get; }

        /// <summary>What the work was.</summary>
        public string Label { get; }

        /// <summary>The message for a refusal, e.g. <c>the main-thread queue of 'mod:x' is full; 'load' was not queued</c>.</summary>
        /// <param name="reason">Why.</param>
        /// <param name="owner">Who.</param>
        /// <param name="label">What.</param>
        public static string Describe(MainThreadRejectReason reason, string owner, string label)
        {
            switch (reason)
            {
                case MainThreadRejectReason.QueueFull:
                    return "the main-thread queue of '" + owner + "' is full; '" + label + "' was not queued";
                case MainThreadRejectReason.OwnerNotRunning:
                    return "'" + owner + "' is not running; '" + label + "' was not run";
                default:
                    return "the game is quitting; '" + label + "' of '" + owner + "' was not queued";
            }
        }
    }
}
