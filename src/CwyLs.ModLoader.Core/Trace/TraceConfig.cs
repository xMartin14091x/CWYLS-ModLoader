namespace CwyLs.ModLoader.Core.Trace
{
    /// <summary>
    /// Everything the trace reads from configuration. The plugin fills this from the BepInEx
    /// <c>ConfigFile</c>; Core never reads a file itself so the whole pipeline stays unit-testable.
    /// </summary>
    public sealed class TraceConfig
    {
        /// <summary>Default per-method budget, in calls per second.</summary>
        public const int DefaultRateLimitPerMethodPerSecond = 20;

        /// <summary>Default longest string written for one argument.</summary>
        public const int DefaultMaxStringLength = 80;

        /// <summary>Default seconds between state snapshots.</summary>
        public const double DefaultSnapshotIntervalSeconds = 5d;

        /// <summary>Default aggregate lines per second for a method classified as polling.</summary>
        public const int DefaultPollingCapPerSecond = RateLimiter.DefaultPollingCapPerSecond;

        /// <summary>Default size at which the trace file rolls over to a new part, in megabytes.</summary>
        public const int DefaultMaxFileMB = 200;

        /// <summary>Default rate above which a method is a candidate for being unpatched.</summary>
        public const int DefaultUnpatchAbovePerSecond = 2000;

        /// <summary>Default time a method must stay that hot before it is unpatched.</summary>
        public const double DefaultUnpatchAfterSeconds = 20d;

        /// <summary>Default size cap of the trace folder, in megabytes (A2 retention).</summary>
        public const int DefaultRetainMaxTotalMB = 1024;

        /// <summary>Default number of newest past sessions retention never deletes.</summary>
        public const int DefaultRetainMinSessions = 2;

        /// <summary>Off, curated events only, or everything. Defaults to <see cref="TraceLevel.Trace"/>.</summary>
        public TraceLevel Level { get; set; } = TraceLevel.Trace;

        /// <summary>When set, only <c>Class.Method</c> names matching this regex are traced.</summary>
        public string? IncludeRegex { get; set; }

        /// <summary>When set, <c>Class.Method</c> names matching this regex are never traced.</summary>
        public string? ExcludeRegex { get; set; }

        /// <summary>Calls per second written per method before suppression kicks in. 0 or less means no limit.</summary>
        public int RateLimitPerMethodPerSecond { get; set; } = DefaultRateLimitPerMethodPerSecond;

        /// <summary>How far <see cref="ArgFormatter"/> descends into an object. 1 means "type plus its ID/Name/Title".</summary>
        public int ArgDepth { get; set; } = 1;

        /// <summary>Longest string written for one argument before it is cut with an ellipsis.</summary>
        public int MaxStringLength { get; set; } = DefaultMaxStringLength;

        // MirrorEventsToConsole, MirrorCuratedMethods, MirrorSnapshots, MirrorBusEvents and
        // UnmirroredBusEvents were removed. There is one console sink, it decides per event through
        // Core.Logging.ConsoleEventMap, and a producer keeps an event out of the console by
        // publishing it with ("mirror", false) rather than by an allow-list in a config file that
        // could silently hide a whole subsystem.

        /// <summary>Seconds between periodic state snapshots. 0 or less turns snapshots off.</summary>
        public double SnapshotIntervalSeconds { get; set; } = DefaultSnapshotIntervalSeconds;

        /// <summary>
        /// Aggregate lines per second once a method is classified as polling. 1 means one line a
        /// second carrying the call count and the last arguments seen.
        /// </summary>
        public int PollingCapPerSecond { get; set; } = DefaultPollingCapPerSecond;

        /// <summary>Megabytes per trace file before it rolls over to <c>-partN</c>. 0 or less never rotates.</summary>
        public int MaxFileMB { get; set; } = DefaultMaxFileMB;

        /// <summary>
        /// Whether a method that stays extremely hot is unpatched outright. Aggregation caps what
        /// is <em>written</em>; only unpatching removes what the prefix itself costs.
        /// </summary>
        public bool UnpatchHotMethods { get; set; } = true;

        /// <summary>Calls per second above which a polling method becomes an unpatch candidate.</summary>
        public int UnpatchAbovePerSecond { get; set; } = DefaultUnpatchAbovePerSecond;

        /// <summary>Seconds a method must stay that hot before it is unpatched.</summary>
        public double UnpatchAfterSeconds { get; set; } = DefaultUnpatchAfterSeconds;

        /// <summary>
        /// Whether what the user types is written to the trace. When false only the length is,
        /// which is what you want if the log might be shared: text fields hold notes and todos.
        /// </summary>
        public bool LogInputText { get; set; } = true;

        /// <summary>
        /// Retention: megabytes the trace folder may hold before whole past sessions are deleted,
        /// oldest first, after compression. 0 or less disables the cap (nothing is ever deleted).
        /// </summary>
        public int RetainMaxTotalMB { get; set; } = DefaultRetainMaxTotalMB;

        /// <summary>Retention: the newest past sessions that are never deleted, whatever the cap says.</summary>
        public int RetainMinSessions { get; set; } = DefaultRetainMinSessions;

        /// <summary>Retention: gzip every part of every past session (verified before the original is removed).</summary>
        public bool CompressClosedParts { get; set; } = true;
    }
}
