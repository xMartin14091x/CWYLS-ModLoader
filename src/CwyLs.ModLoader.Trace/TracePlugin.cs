using System;
using System.Globalization;
using BepInEx;
using BepInEx.Configuration;
using CwyLs.ModLoader.Core.Bus;
using CwyLs.ModLoader.Core.Logging;
using CwyLs.ModLoader.Core.Trace;
using CwyLs.ModLoader.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CwyLs.ModLoader.Trace
{
    /// <summary>
    /// The game trace log. Reflectively prefixes the game's system methods and writes a
    /// JSONL stream of what the game is doing, plus a periodic state snapshot, to
    /// <c>BepInEx/trace/trace-&lt;session&gt;.jsonl</c>.
    /// </summary>
    /// <remarks>
    /// This component only configures, patches and hands off. Everything with a lifetime lives in
    /// <see cref="TraceHost"/> on its own DontDestroyOnLoad object, because this game destroys the
    /// BepInEx plugin component about 0.2 s after boot; when that happened the flush loop and the
    /// snapshot timer went with it and the trace stopped after three lines.
    /// </remarks>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    [BepInDependency(CwyLsModLoader.PluginGuid)]
    public class TracePlugin : BaseUnityPlugin
    {
        /// <summary>BepInEx plugin id.</summary>
        public const string PluginGuid = "cwyls.modloader.trace";

        /// <summary>BepInEx plugin name.</summary>
        public const string PluginName = "CWYLS Trace";

        /// <summary>BepInEx plugin version.</summary>
        public const string PluginVersion = "0.1.0";

        private bool _started;

        /// <summary>The level this session started tracing at; Off when it did not start.</summary>
        private static TraceLevel _startedLevel = TraceLevel.Off;

        /// <summary>The bound <c>[Trace] Level</c> entry, for <see cref="TraceSwitch"/>.</summary>
        private ConfigEntry<TraceLevel>? _levelEntry;

        private static TraceLevel RunningLevel() =>
            TraceRuntime.IsEnabled ? _startedLevel : TraceLevel.Off;

        /// <summary>
        /// 'trace off' while recording: the same clean end as quitting (footer, file closed, the shutdown
        /// line names the reason). The hooks stay patched until the game quits, but each returns at once.
        /// </summary>
        private static void StopNow() => TraceRuntime.Shutdown("console: trace off");

        private void Awake()
        {
            // Demo lock: full game only (ModLoaderPlugin has already logged why).
            if (DemoLock.IsLocked)
            {
                enabled = false;
                return;
            }

            TraceDiagnostics.UseSink((message, error) =>
                CwyLsModLoader.Log.LogWarning(error is null ? message : message + ": " + TraceFailureText.Chain(error)));

            var config = ReadConfig();

            // RP-1: the console's 'trace on|off|full|status' reads and changes the trace through this.
            TraceSwitch.Register(_levelEntry!, RunningLevel, StopNow);

            if (config.Level == TraceLevel.Off)
            {
                ConsoleMirror.Print(TraceConsoleLines.Status(
                    TraceRuntime.Now, "off; nothing patched, nothing written (console: trace on)"));
                return;
            }

            try
            {
                StartTracing(config);
                _started = true;
                _startedLevel = config.Level;
            }
            catch (Exception ex)
            {
                // A trace that cannot start must never stop the game or the rest of the framework.
                CwyLsModLoader.Log.LogError($"TRACE: failed to start and is now inert: {ex}");
                TraceRuntime.Disable();
            }
        }

        /// <summary>
        /// Builds the writer, patches the game and arms the host.
        /// </summary>
        /// <remarks>
        /// NOT called <c>Start</c>: this component is a MonoBehaviour, and Unity rejects a
        /// <c>Start</c> that takes a parameter - "Script error (TracePlugin): Start() can not take
        /// parameters." was printed twice on every launch.
        /// </remarks>
        /// <param name="config">The bound trace configuration.</param>
        private void StartTracing(TraceConfig config)
        {
            ModLoaderPaths.EnsureCreated();

            // The stamp names this session's files; retention never touches that session.
            var stamp = TraceFileName.StampFor(DateTime.Now);
            var path = System.IO.Path.Combine(ModLoaderPaths.TraceRoot, TraceFileName.SessionFileName(stamp));

            var writer = new BufferedFileTraceWriter(
                path,
                BufferedFileTraceWriter.DefaultFlushThresholdBytes,
                config.MaxFileMB > 0 ? config.MaxFileMB * 1024L * 1024L : 0L);

            var formatter = new ArgFormatter(config.MaxStringLength, config.ArgDepth)
            {
                ObjectHook = UnityArgHook.Format,
            };

            TraceRuntime.Start(config, writer, formatter);
            TraceRuntime.Frame = Time.frameCount;
            TraceRuntime.Realtime = Time.realtimeSinceStartup;

            var harmony = new Harmony(PluginGuid);
            TraceHost.UseHarmony(harmony, config);
            var platform = TraceExclusions.FromUnityPlatformName(Application.platform.ToString());
            var report = new TracePatcher(harmony, new TraceFilter(config), platform)
                .PatchAssemblies(TraceCandidates.TracedAssemblies);
            report.Patched += UiTracer.Install(harmony, config);

            // The header is the self-check and must be the first line in the file, so it is written
            // before the hooks are armed.
            TraceRuntime.WriteRaw(TraceRuntime.Builder.Header(
                TraceRuntime.Now, config.Level, report.Patched, report.Failed, report.Classes, report.Milliseconds));
            TraceRuntime.Flush();

            TracePatcher.LogReport(report, config.Level);
            ConsoleMirror.Print(TraceConsoleLines.Status(
                TraceRuntime.Now, "writing " + path + " (console: trace off stops it)"));

            TraceRuntime.Enable();

            // Attached after Enable and after the header, so the replay of everything other plugins
            // published while we were still patching lands in the file in order, not before it.
            TraceRuntime.AttachBus();

            UnityLogCapture.Install(config);

            // Static handlers and a host of our own: nothing below depends on this component living.
            SceneManager.sceneLoaded += TraceRuntime.OnSceneLoaded;
            SceneManager.sceneUnloaded += TraceRuntime.OnSceneUnloaded;
            Application.quitting += TraceRuntime.OnApplicationQuitting;
            Application.quitting += TraceRetention.Stop;
            Application.focusChanged += OnFocusChanged;
            Application.lowMemory += OnLowMemory;

            TraceHost.Install(config, new GameStateProbe());

            // Last: a background thread that waits 30 s, then archives past sessions and applies the cap.
            TraceRetention.Start(config, ModLoaderPaths.TraceRoot, stamp);
        }

        private static void OnFocusChanged(bool focused)
        {
            // Coming back is when a sleep or a clock change shows: fix the clock before anything
            // else is dated. The writer thread would catch it within 50 ms anyway.
            if (focused)
            {
                TraceRuntime.CheckClock("focus");
            }

            // Recorded first, so a spike in the same frame is already attributable.
            TraceHost.NoteFocus(focused);
            TraceBus.EmitFields("health", "focus", ("focused", focused));
        }

        private static void OnLowMemory() => TraceBus.Emit("health", "lowMemory");

        /// <summary>
        /// Records that the manager component died, and keeps tracing. This is NOT a shutdown: the
        /// file is closed only on <c>Application.quitting</c>.
        /// </summary>
        private void OnDestroy()
        {
            if (!_started || TraceHost.IsQuitting)
            {
                return;
            }

            var realtime = TraceRuntime.Realtime.ToString("0.###", CultureInfo.InvariantCulture);
            CwyLsModLoader.Log.LogWarning(
                $"TRACE: the plugin component was destroyed at rt={realtime}; tracing continues on {TraceHost.HostObjectName}");
            TraceRuntime.WriteSelfEvent(
                "Trace", "pluginComponentDestroyed", new[] { "rt=" + realtime });
        }

        /// <summary>Binds the BepInEx config entries and turns them into the Core config object.</summary>
        private TraceConfig ReadConfig()
        {
            // RP-1: off by default. Players turn it on for a bug report (console 'trace on'), developers
            // use Trace ('trace full'). It patches at startup, so a change applies at the next launch.
            var level = Config.Bind(
                "Trace", "Level", TraceLevel.Off,
                "Diagnostic trace for bug reports. Off (default) = nothing patched, nothing written. " +
                "Events = key events only, small files (console: trace on). " +
                "Trace = every game method, large files, slower game; for developers (console: trace full). " +
                "Takes effect at the next launch; console 'trace off' also stops a running trace at once.");
            _levelEntry = level;

            var includeRegex = Config.Bind(
                "Trace", "IncludeRegex", string.Empty,
                "When set, only Class.Method names matching this regex are traced. Applied last.");

            var excludeRegex = Config.Bind(
                "Trace", "ExcludeRegex", string.Empty,
                "Class.Method names matching this regex are never traced. Applied last.");

            var rateLimit = Config.Bind(
                "Trace", "RateLimitPerMethodPerSecond", TraceConfig.DefaultRateLimitPerMethodPerSecond,
                "Lines written per method per second before the rest are counted as suppressed. 0 = no limit.");

            var argDepth = Config.Bind(
                "Trace", "ArgDepth", 1,
                "How far to describe an argument. 1 = type plus its ID/Name/Title members.");

            var maxStringLength = Config.Bind(
                "Trace", "MaxStringLength", TraceConfig.DefaultMaxStringLength,
                "Longest string written for one argument before it is cut.");

            // The Mirror* keys are gone. There is one console sink now
            // (CwyLs.ModLoader.Logging.ConsoleMirror), it decides per event through
            // Core.Logging.ConsoleEventMap, and a producer that wants an event kept out of the
            // console publishes it with ("mirror", false). An old .cfg keeps the orphan keys until
            // BepInEx rewrites it; they are read by nothing.

            var snapshotInterval = Config.Bind(
                "Trace", "SnapshotIntervalSeconds", (float)TraceConfig.DefaultSnapshotIntervalSeconds,
                "Seconds between full state snapshots. 0 = off.");

            var pollingCap = Config.Bind(
                "Trace", "PollingCapPerSecond", TraceConfig.DefaultPollingCapPerSecond,
                "A method that overruns RateLimitPerMethodPerSecond in 3 windows in a row is treated as polling " +
                "and written as this many aggregate lines per second, each carrying the exact call count and the " +
                "last arguments seen. Five quiet windows put it back to normal.");

            var unpatchHot = Config.Bind(
                "Trace", "UnpatchHotMethods", true,
                "Remove the trace prefix from a method that stays extremely hot. Aggregation caps what is " +
                "written; only unpatching removes what the prefix itself costs. Curated events are never unpatched.");

            var unpatchAbove = Config.Bind(
                "Trace", "UnpatchAbovePerSecond", TraceConfig.DefaultUnpatchAbovePerSecond,
                "Calls per second above which a polling method becomes an unpatch candidate.");

            var unpatchAfter = Config.Bind(
                "Trace", "UnpatchAfterSeconds", (float)TraceConfig.DefaultUnpatchAfterSeconds,
                "Seconds a method must stay that hot before it is unpatched.");

            // RP-1: false by default (privacy); a shared trace must not carry the player's notes.
            var logInputText = Config.Bind(
                "Trace", "LogInputText", false,
                "Write what the user types into text fields. False logs only the length: notes and " +
                "todos go through these fields, so turn it off before sharing a trace.");

            var maxFileMB = Config.Bind(
                "Trace", "MaxFileMB", TraceConfig.DefaultMaxFileMB,
                "Megabytes per trace file before it rolls over to trace-<stamp>-partN.jsonl. 0 = never rotate. " +
                "trace/latest.txt always names the part being written.");

            var retainMaxTotalMB = Config.Bind(
                "Trace", "RetainMaxTotalMB", TraceConfig.DefaultRetainMaxTotalMB,
                "Retention, 30 s after launch on a background thread: once past sessions are compressed, delete " +
                "whole past sessions, oldest first, until the trace folder is under this many megabytes. " +
                "0 = no cap (nothing is ever deleted). The running session is never touched.");

            var retainMinSessions = Config.Bind(
                "Trace", "RetainMinSessions", TraceConfig.DefaultRetainMinSessions,
                "The newest past sessions the cap never deletes, even when the folder stays over it.");

            var compressClosedParts = Config.Bind(
                "Trace", "CompressClosedParts", true,
                "Gzip every part of every past session to .jsonl.gz (read with: gzip -dc file | less). The original " +
                "is deleted only after the archive decompresses to the same length and CRC-32.");

            return new TraceConfig
            {
                Level = level.Value,
                IncludeRegex = includeRegex.Value,
                ExcludeRegex = excludeRegex.Value,
                RateLimitPerMethodPerSecond = rateLimit.Value,
                ArgDepth = argDepth.Value,
                MaxStringLength = maxStringLength.Value,
                SnapshotIntervalSeconds = snapshotInterval.Value,
                PollingCapPerSecond = pollingCap.Value,
                MaxFileMB = maxFileMB.Value,
                LogInputText = logInputText.Value,
                UnpatchHotMethods = unpatchHot.Value,
                UnpatchAbovePerSecond = unpatchAbove.Value,
                UnpatchAfterSeconds = unpatchAfter.Value,
                RetainMaxTotalMB = retainMaxTotalMB.Value,
                RetainMinSessions = retainMinSessions.Value,
                CompressClosedParts = compressClosedParts.Value,
            };
        }
    }
}
