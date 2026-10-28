using System;

namespace CwyLs.ModLoader.Core.Trace
{
    /// <summary>Sink for finished JSONL lines. One line in, one line out; no formatting left to do.</summary>
    public interface ITraceWriter : IDisposable
    {
        /// <summary>Queues one line. Implementations must be safe to call from any thread.</summary>
        void Write(string line);

        /// <summary>Pushes everything queued to the underlying store.</summary>
        void Flush();

        /// <summary>Lines accepted so far.</summary>
        long LinesWritten { get; }
    }
}
