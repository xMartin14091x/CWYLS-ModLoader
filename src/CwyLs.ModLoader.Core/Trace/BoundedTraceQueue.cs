using System.Collections.Concurrent;
using System.Threading;

namespace CwyLs.ModLoader.Core.Trace
{
    /// <summary>
    /// A hand-off queue between the game threads and the single writer thread: unbounded in the
    /// BCL, bounded here by an interlocked counter so a burst can never eat memory.
    /// </summary>
    /// <remarks>
    /// Overflow drops the newest item rather than blocking. A game thread waiting on a log is the
    /// one outcome worse than a missing line, and the drop count is reported in the health snapshot
    /// and the footer so a truncated trace is never silent.
    /// </remarks>
    public sealed class BoundedTraceQueue<T>
    {
        private readonly ConcurrentQueue<T> _items = new ConcurrentQueue<T>();
        private readonly int _capacity;

        private int _count;
        private long _dropped;

        /// <summary>Creates a queue.</summary>
        /// <param name="capacity">Maximum items held. Values below 1 are treated as 1.</param>
        public BoundedTraceQueue(int capacity)
        {
            _capacity = capacity < 1 ? 1 : capacity;
        }

        /// <summary>Maximum items this queue will hold.</summary>
        public int Capacity => _capacity;

        /// <summary>Items waiting to be written.</summary>
        public int Count => Volatile.Read(ref _count);

        /// <summary>Items dropped because the queue was full.</summary>
        public long Dropped => Interlocked.Read(ref _dropped);

        /// <summary>Adds an item unless the queue is full. Never blocks.</summary>
        /// <returns>False when the item was dropped.</returns>
        public bool TryEnqueue(T item)
        {
            if (Interlocked.Increment(ref _count) > _capacity)
            {
                Interlocked.Decrement(ref _count);
                Interlocked.Increment(ref _dropped);
                return false;
            }

            _items.Enqueue(item);
            return true;
        }

        /// <summary>Takes the oldest item.</summary>
        public bool TryDequeue(out T item)
        {
            if (!_items.TryDequeue(out item!))
            {
                return false;
            }

            Interlocked.Decrement(ref _count);
            return true;
        }

        /// <summary>Forgets everything queued. Only used when shutting down or by tests.</summary>
        public void Clear()
        {
            while (TryDequeue(out _))
            {
            }
        }
    }
}
