namespace CwyLs.ModLoader.Core.Trace
{
    /// <summary>Frame-rate half of a health reading, over one snapshot interval.</summary>
    public sealed class FpsHealth
    {
        /// <summary>Mean frames per second.</summary>
        public double Average { get; set; }

        /// <summary>Lowest frames per second, i.e. the longest frame.</summary>
        public double Min { get; set; }

        /// <summary>Highest frames per second.</summary>
        public double Max { get; set; }
    }

    /// <summary>Frame-time half of a health reading, in milliseconds.</summary>
    public sealed class FrameMsHealth
    {
        /// <summary>Mean frame time.</summary>
        public double Average { get; set; }

        /// <summary>95th-percentile frame time.</summary>
        public double P95 { get; set; }

        /// <summary>Longest frame.</summary>
        public double Max { get; set; }
    }

    /// <summary>Audio engine configuration and load.</summary>
    public sealed class AudioHealth
    {
        /// <summary>Output sample rate in Hz.</summary>
        public int? SampleRate { get; set; }

        /// <summary>DSP buffer length in samples; the smaller it is the tighter the latency budget.</summary>
        public int? DspBufferSize { get; set; }

        /// <summary>Speaker mode, e.g. Stereo.</summary>
        public string? SpeakerMode { get; set; }

        /// <summary>Global listener volume, 0..1.</summary>
        public double? ListenerVolume { get; set; }

        /// <summary>AudioSources currently playing.</summary>
        public int? PlayingSources { get; set; }
    }

    /// <summary>Window and display state.</summary>
    public sealed class ScreenHealth
    {
        /// <summary>Back-buffer width in pixels.</summary>
        public int? Width { get; set; }

        /// <summary>Back-buffer height in pixels.</summary>
        public int? Height { get; set; }

        /// <summary>Whether the game is full screen.</summary>
        public bool? FullScreen { get; set; }

        /// <summary>Reported dots per inch.</summary>
        public double? Dpi { get; set; }
    }

    /// <summary>What the trace's own Harmony prefix costs, in nanoseconds per call.</summary>
    public sealed class HookCostHealth
    {
        /// <summary>Mean measured prefix duration.</summary>
        public double Average { get; set; }

        /// <summary>95th-percentile measured prefix duration.</summary>
        public double P95 { get; set; }

        /// <summary>Calls timed in the interval (one in every 256).</summary>
        public long Samples { get; set; }
    }

    /// <summary>Depth of the hand-off queue between the game threads and the writer thread.</summary>
    public sealed class QueueHealth
    {
        /// <summary>Lines waiting to be written.</summary>
        public int Length { get; set; }

        /// <summary>Lines dropped because the queue was full, for the whole session.</summary>
        public long Dropped { get; set; }
    }

    /// <summary>
    /// Everything about how the process itself is doing, sampled once per snapshot interval and
    /// also published on the bus so it reaches the console. Every member is nullable: a reading the
    /// platform will not give us is written as null rather than guessed.
    /// </summary>
    public sealed class TraceHealth
    {
        /// <summary>Value of <see cref="FpsCapReason"/> when the window is in the background.</summary>
        public const string UnfocusedCapReason = "unfocused";

        /// <summary>Frames per second over the interval.</summary>
        public FpsHealth? Fps { get; set; }

        /// <summary>Frame times over the interval.</summary>
        public FrameMsHealth? FrameMs { get; set; }

        /// <summary>Frames over <see cref="HealthStats.SpikeMs"/> in the interval.</summary>
        public int Spikes { get; set; }

        /// <summary>Managed heap in use, in megabytes (<c>GC.GetTotalMemory</c>).</summary>
        public double? GcMB { get; set; }

        /// <summary>Mono heap in use, in megabytes.</summary>
        public double? MonoUsedMB { get; set; }

        /// <summary>Mono heap reserved, in megabytes.</summary>
        public double? MonoHeapMB { get; set; }

        /// <summary>Total allocated by Unity, in megabytes.</summary>
        public double? AllocatedMB { get; set; }

        /// <summary>Total reserved by Unity, in megabytes.</summary>
        public double? ReservedMB { get; set; }

        /// <summary>Process resident set size, in megabytes.</summary>
        public double? RssMB { get; set; }

        /// <summary>Operating-system threads in the process.</summary>
        public int? Threads { get; set; }

        /// <summary>Audio configuration and load.</summary>
        public AudioHealth? Audio { get; set; }

        /// <summary>DOTween tweens currently playing, or null when DOTween is not loaded.</summary>
        public int? Tweens { get; set; }

        /// <summary>Configured frame-rate cap (<c>Application.targetFrameRate</c>).</summary>
        public int? TargetFps { get; set; }

        /// <summary>VSync count from QualitySettings.</summary>
        public int? Vsync { get; set; }

        /// <summary>Window and display state.</summary>
        public ScreenHealth? Screen { get; set; }

        /// <summary>Whether the application has focus.</summary>
        public bool? Focused { get; set; }

        /// <summary>
        /// Why the frame rate in this reading is held down by something other than the game's own
        /// work, or null when nothing is. <see cref="UnfocusedCapReason"/> means the window is in
        /// the background: the game lowers its own frame rate there, so a low average and a pile
        /// of spikes are expected and are not a stutter.
        /// </summary>
        public string? FpsCapReason { get; set; }

        /// <summary>Current <c>Time.timeScale</c>.</summary>
        public double? TimeScale { get; set; }

        /// <summary>Seconds since the process started.</summary>
        public double UptimeSec { get; set; }

        /// <summary>Steam initialisation state, or null when the manager is not resolvable.</summary>
        public bool? Steam { get; set; }

        /// <summary>Unity warnings seen since the last reading.</summary>
        public int WarningsInInterval { get; set; }

        /// <summary>Unity errors, exceptions and failed assertions seen since the last reading.</summary>
        public int ErrorsInInterval { get; set; }

        /// <summary>What the trace's own prefix cost over the interval.</summary>
        public HookCostHealth? HookNs { get; set; }

        /// <summary>Depth and losses of the writer queue.</summary>
        public QueueHealth? Queue { get; set; }
    }
}
