namespace CwyLs.ModLoader.Core.Trace
{
    /// <summary>How much of the game the trace watches.</summary>
    public enum TraceLevel
    {
        /// <summary>Nothing is patched and nothing is written.</summary>
        Off = 0,

        /// <summary>Only the curated events in <see cref="CuratedEvents"/> are patched.</summary>
        Events = 1,

        /// <summary>Every method the filter accepts is patched.</summary>
        Trace = 2,
    }
}
