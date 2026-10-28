using System.Threading;

namespace CwyLs.ModLoader.Core.Trace
{
    /// <summary>
    /// Everything one patched method needs, resolved once at patch time so the Harmony prefix does
    /// no string building, no hashing by name and no locking.
    /// </summary>
    /// <remarks>
    /// The mutable fields are public because <see cref="Interlocked"/> and <see cref="Volatile"/>
    /// need a <c>ref</c> to a field and cannot take a property. Nothing outside
    /// <see cref="TraceAdmission"/> may write them; read them through the properties below.
    /// </remarks>
    public sealed class TraceEntry
    {
        /// <summary>Creates an entry for one method.</summary>
        public TraceEntry(string? namespaceName, string className, string methodName, bool curated)
        {
            NamespaceName = namespaceName;
            ClassName = className;
            MethodName = methodName;
            Curated = curated;
            Key = className + "." + methodName;
        }

        /// <summary>Declaring namespace, or null/empty for the root namespace.</summary>
        public string? NamespaceName { get; }

        /// <summary>Simple class name.</summary>
        public string ClassName { get; }

        /// <summary>Method name.</summary>
        public string MethodName { get; }

        /// <summary><c>Class.Method</c>, precomputed: the hot path must never concatenate.</summary>
        public string Key { get; }

        /// <summary>Curated methods are never unpatched and may be mirrored to the console; they aggregate like any poller when hot.</summary>
        public bool Curated { get; }

        // ----- mutable state, written only by TraceAdmission, only with Interlocked/Volatile -----

        /// <summary>Stopwatch timestamp at which the current window opened.</summary>
        public long WindowStartTicks;

        /// <summary>Calls observed in the current window.</summary>
        public int Used;

        /// <summary>Calls dropped in the current window (normal mode only).</summary>
        public int Suppressed;

        /// <summary>1 while the method is being aggregated.</summary>
        public int PollingFlag;

        /// <summary>Consecutive windows over the cap.</summary>
        public int HotWindows;

        /// <summary>Consecutive windows under the cap while polling.</summary>
        public int QuietWindows;

        /// <summary>The raw argument array of the most recent call, for the next aggregate line.</summary>
        public object? LastArgs;

        /// <summary>Calls counted in the window that closed most recently: the observed rate.</summary>
        public int LastObserved;

        /// <summary>Stopwatch timestamp at which this method entered polling mode, or 0.</summary>
        public long PollingSinceTicks;

        /// <summary>1 once the method has been unpatched for being too hot.</summary>
        public int UnpatchedFlag;

        /// <summary>Whether the method is currently being aggregated.</summary>
        public bool IsPolling => Volatile.Read(ref PollingFlag) != 0;

        /// <summary>Whether the method has been unpatched and is no longer being counted.</summary>
        public bool IsUnpatched => Volatile.Read(ref UnpatchedFlag) != 0;

        /// <summary>Calls seen in the last window that closed.</summary>
        public int ObservedPerSecond => Volatile.Read(ref LastObserved);

        /// <summary>Marks the method as unpatched. Returns false if it already was.</summary>
        public bool MarkUnpatched() => Interlocked.Exchange(ref UnpatchedFlag, 1) == 0;
    }
}
