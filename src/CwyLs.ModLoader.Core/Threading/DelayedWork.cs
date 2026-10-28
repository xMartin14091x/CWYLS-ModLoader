using System;

namespace CwyLs.ModLoader.Core.Threading
{
    /// <summary>
    /// A handle to one piece of main-thread work queued with a delay (<c>PostAfter</c>
    /// returns this instead of a bool). Dispose it, or call <see cref="Cancel"/>, to drop the work
    /// if it has not started yet; once it runs, or after a cancel, disposing is harmless.
    /// </summary>
    public sealed class DelayedWork : IDisposable
    {
        private readonly MainThreadQueue? _queue;
        private readonly MainThreadQueue.WorkItem? _item;

        internal DelayedWork(MainThreadQueue queue, MainThreadQueue.WorkItem item)
        {
            _queue = queue;
            _item = item;
        }

        internal DelayedWork(MainThreadRejectReason reason)
        {
            RejectReason = reason;
        }

        /// <summary>True when the dispatcher took the work; false when it refused it (see <see cref="RejectReason"/>).</summary>
        public bool IsAccepted => _item != null;

        /// <summary>Why the work was refused, or null when it was accepted.</summary>
        public MainThreadRejectReason? RejectReason { get; }

        /// <summary>True while the work is accepted and has neither started nor been cancelled.</summary>
        public bool IsPending => _item != null && _item.IsWaiting;

        /// <summary>Drops the work if it has not started yet.</summary>
        /// <returns>True when this call dropped it; false when it already ran, started, was cancelled or was never accepted.</returns>
        public bool Cancel() => _queue != null && _item != null && _queue.CancelItem(_item);

        /// <summary>Same as <see cref="Cancel"/>; disposing twice is harmless.</summary>
        public void Dispose() => Cancel();
    }
}
