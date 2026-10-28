using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Threading;
using CwyLs.ModLoader.Core.Bus;
using CwyLs.ModLoader.Core.Logging;
using CwyLs.ModLoader.Core.Trace;
using CwyLs.ModLoader.Logging;
using UnityEngine.SceneManagement;

namespace CwyLs.ModLoader.Trace
{
    /// <summary>
    /// The static state every Harmony prefix reaches, and the writer thread that does the work the
    /// prefix refuses to do.
    /// </summary>
    /// <remarks>
    /// The A/B measurement that forced this shape: at Level=Events (152 methods) the game ran at
    /// 37.3 fps, at Level=Trace (2622 methods) at 24.1 fps — +55 % frame time against a 10 % budget.
    /// The cost was not the patching, it was the per-call work: one process-wide lock and a
    /// string-keyed dictionary lookup, then formatting, JSON and a file write, all on the game
    /// thread, for methods called thousands of times a second.
    /// <para>
    /// So the prefix now does the least it can: a handle lookup, an interlocked admission decision
    /// and, for the roughly one call in a hundred that survives it, one small object onto a queue.
    /// JSON, file writes and console mirroring happen on a dedicated background thread. Arguments
    /// and instances are still rendered on the calling thread, but only for lines that will
    /// actually be written — Unity objects cannot be read from another thread, and deferring the
    /// render would make an argument show a value it never had.
    /// </para>
    /// </remarks>
    internal static class TraceRuntime
    {
        /// <summary>Hook failures logged in full before the rest are only counted.</summary>
        private const int MaxLoggedHookFailures = 10;

        /// <summary>Lines the writer queue holds before it starts dropping.</summary>
        private const int QueueCapacity = 50_000;

        /// <summary>Lines drained in one pass before the writer thread yields.</summary>
        private const int DrainBatch = 4096;

        /// <summary>How long the writer thread sleeps when the queue is empty.</summary>
        private const int IdleSleepMs = 2;

        /// <summary>One line waiting to be written. Allocated only for lines that survive admission.</summary>
        private sealed class Pending
        {
            public TraceEntry? Entry;
            public string? Prebuilt;
            public long Timestamp;
            public int Frame;
            public int ThreadId;
            public IReadOnlyList<string>? Args;
            public string? Instance;
            public string? Return;
            public string? Note;
            public int? Aggregate;
        }

        /// <summary>Method descriptors keyed by runtime method handle. Read-only after patching.</summary>
        private static readonly Dictionary<IntPtr, TraceEntry> Entries = new Dictionary<IntPtr, TraceEntry>();

        /// <summary>The same entries as a list, for the unpatch scan. Read-only after patching.</summary>
        private static readonly List<TraceEntry> EntryList = new List<TraceEntry>();

        /// <summary>The method each entry was patched from, for unpatching. Read-only after patching.</summary>
        private static readonly Dictionary<TraceEntry, MethodBase> Originals = new Dictionary<TraceEntry, MethodBase>();

        /// <summary>Payload member a producer sets when a bad reading is the window's fault, not the game's.</summary>
        internal const string UnfocusedField = "unfocused";

        /// <summary>
        /// Payload member naming what is to blame: <c>focus-change</c> when the stall IS the
        /// window switch, <c>unfocused</c> when the window was in the background, null otherwise.
        /// </summary>
        internal const string CauseField = "cause";

        /// <summary>One click line per frame per position.</summary>
        private static readonly ClickDedupe Clicks = new ClickDedupe();

        private static readonly BoundedTraceQueue<Pending> Queue = new BoundedTraceQueue<Pending>(QueueCapacity);
        private static readonly HookCostMeter Meter = new HookCostMeter();
        private static readonly List<string> UnpatchedMethods = new List<string>();
        private static readonly object UnpatchedGate = new object();

        [ThreadStatic]
        private static bool _inHook;

        private static TraceConfig? _config;
        private static ITraceWriter? _writer;
        private static ArgFormatter? _formatter;
        private static TraceLineBuilder? _builder;
        private static IDisposable? _busSubscription;
        private static Thread? _writerThread;

        private static volatile bool _enabled;
        private static volatile bool _draining;
        private static int _hookFailures;
        private static int _shutdownDone;
        private static int _mainThreadId;
        private static int _permits;
        private static long _windowTicks;
        private static long _pollingWindowTicks;
        private static long _unpatchAfterTicks;
        private static TraceClock? _clock;
        private static int _clockPollFailures;

        /// <summary>Bus event name of the line written when the clock re-anchors.</summary>
        internal const string ClockEvent = "clock";

        /// <summary>Unity frame counter, refreshed once per frame by the host.</summary>
        internal static volatile int Frame;

        /// <summary>Seconds since start-up, refreshed once per frame by the host.</summary>
        internal static volatile float Realtime;

        /// <summary>The game's own clock, refreshed twice a second by the host.</summary>
        internal static volatile string? GameClock;

        /// <summary>Hook invocations that threw and were swallowed.</summary>
        internal static int HookFailures => _hookFailures;

        /// <summary>The line builder, shared with the host for its own header/snapshot lines.</summary>
        internal static TraceLineBuilder Builder => _builder ??= new TraceLineBuilder();

        /// <summary>
        /// Now, on the trace's one clock. Every line the trace plugin writes - file or console -
        /// takes its time from here, so game-method lines and the plugin's own lines agree. Before
        /// <see cref="Start"/> it is the system clock.
        /// </summary>
        internal static DateTimeOffset Now => _clock?.Now ?? DateTimeOffset.Now;

        /// <summary>Installs the pipeline. Hooks stay inert until <see cref="Enable"/> is called.</summary>
        internal static void Start(TraceConfig config, ITraceWriter writer, ArgFormatter formatter)
        {
            _config = config;
            _writer = writer;
            _formatter = formatter;
            _builder = new TraceLineBuilder();

            _permits = config.RateLimitPerMethodPerSecond;
            _windowTicks = Stopwatch.Frequency;
            var pollingCap = config.PollingCapPerSecond < 1 ? 1 : config.PollingCapPerSecond;
            _pollingWindowTicks = Math.Max(1L, Stopwatch.Frequency / pollingCap);
            _unpatchAfterTicks = (long)(config.UnpatchAfterSeconds * Stopwatch.Frequency);

            // Stopwatch ticks are cheap and monotonic; wall-clock time is reconstructed from them on
            // the writer thread, so no call ever pays for DateTime.Now. The Stopwatch stops while a
            // Mac sleeps, so the clock re-anchors to the system clock when they part (PollClock,
            // CheckClock).
            _clock = new TraceClock(Stopwatch.GetTimestamp, Stopwatch.Frequency, () => DateTimeOffset.Now);

            // Awake always runs on Unity's main thread; the hooks use this to decide whether they
            // may touch a Unity API (such as rebuilding the host GameObject).
            _mainThreadId = Thread.CurrentThread.ManagedThreadId;
        }

        /// <summary>Arms the hooks and starts the writer thread once patching has finished.</summary>
        internal static void Enable()
        {
            _enabled = true;
            _draining = true;

            _writerThread = new Thread(DrainLoop)
            {
                Name = "CWY.Trace.Writer",
                IsBackground = true,
                Priority = ThreadPriority.BelowNormal,
            };

            _writerThread.Start();
        }

        /// <summary>Turns the hooks off.</summary>
        internal static void Disable() => _enabled = false;

        /// <summary>True while the hooks are recording (RP-1: the console's trace status).</summary>
        internal static bool IsEnabled => _enabled;

        /// <summary>Pushes buffered lines to disk. Called by the host twice a second.</summary>
        internal static void Flush() => _writer?.Flush();

        /// <summary>Writes an already-built line (the header, a snapshot) straight to the file.</summary>
        internal static void WriteRaw(string line) => _writer?.Write(line);

        /// <summary>
        /// Records what a patched method is. Called only during start-up patching; the tables are
        /// never written to afterwards, which is what makes lock-free reads from the hooks safe.
        /// </summary>
        internal static void Register(MethodBase method, TraceEntry entry)
        {
            Entries[method.MethodHandle.Value] = entry;
            EntryList.Add(entry);
            Originals[entry] = method;
        }

        /// <summary>The descriptors, for the host's unpatch scan. Read-only.</summary>
        internal static IReadOnlyList<TraceEntry> AllEntries => EntryList;

        /// <summary>The method an entry was patched from.</summary>
        internal static MethodBase? OriginalOf(TraceEntry entry) =>
            Originals.TryGetValue(entry, out var method) ? method : null;

        /// <summary>Records that a method has been unpatched, for the footer.</summary>
        internal static void NoteUnpatched(TraceEntry entry)
        {
            lock (UnpatchedGate)
            {
                UnpatchedMethods.Add(entry.Key);
            }
        }

        /// <summary>Methods unpatched for being too hot.</summary>
        internal static IReadOnlyList<string> Unpatched
        {
            get
            {
                lock (UnpatchedGate)
                {
                    return new List<string>(UnpatchedMethods);
                }
            }
        }

        /// <summary>Reads the prefix cost and queue depth for the health snapshot, then resets the interval.</summary>
        internal static void ReadCost(TraceHealth health)
        {
            var samples = Meter.Samples;
            Meter.Read(out var average, out var p95);

            health.HookNs = new HookCostHealth { Average = average, P95 = p95, Samples = samples };
            health.Queue = new QueueHealth { Length = Queue.Count, Dropped = Queue.Dropped };
        }

        // ----- the hot path --------------------------------------------------------------------

        /// <summary>Shared prefix for instance methods.</summary>
        internal static void PrefixInstance(MethodBase __originalMethod, object __instance, object[] __args) =>
            Record(__originalMethod, __instance, __args);

        /// <summary>Shared prefix for static methods, which have no instance to describe.</summary>
        internal static void PrefixStatic(MethodBase __originalMethod, object[] __args) =>
            Record(__originalMethod, null, __args);

        /// <summary>Fallback prefix for an instance method whose arguments Harmony would not capture.</summary>
        internal static void PrefixInstanceNoArgs(MethodBase __originalMethod, object __instance) =>
            Record(__originalMethod, __instance, null);

        /// <summary>Fallback prefix for a static method whose arguments Harmony would not capture.</summary>
        internal static void PrefixStaticNoArgs(MethodBase __originalMethod) =>
            Record(__originalMethod, null, null);

        private static void Record(MethodBase original, object? instance, object?[]? args)
        {
            if (!_enabled || _inHook)
            {
                return;
            }

            var sampled = Meter.ShouldSample();
            var started = sampled ? Stopwatch.GetTimestamp() : 0L;

            _inHook = true;
            try
            {
                if (!Entries.TryGetValue(original.MethodHandle.Value, out var entry))
                {
                    return;
                }

                var now = Stopwatch.GetTimestamp();
                var verdict = TraceAdmission.Admit(entry, now, _windowTicks, _pollingWindowTicks, _permits);

                if (verdict.Action == TraceAdmit.Suppress)
                {
                    // The whole point of the rewrite: for a polling method this is the entire cost.
                    if (verdict.RemembersPayload)
                    {
                        TraceAdmission.RememberArgs(entry, args);
                    }

                    return;
                }

                // Something in this game destroys components at boot; if it took our host with it,
                // the first admitted call on the main thread puts it back.
                if (TraceHost.IsLost && Thread.CurrentThread.ManagedThreadId == _mainThreadId)
                {
                    TraceHost.RestoreIfLost();
                }

                if (verdict.EnteredPolling)
                {
                    // A file-only note. Knowing that MusicService.GetCurrentMusicProgress
                    // is polled is useful when reading the trace and is noise in the console.
                    WriteSelfEvent(
                        "Trace",
                        "polling",
                        new[] { entry.Key, verdict.ObservedPerSecond.ToString(CultureInfo.InvariantCulture) + "/s" });
                }

                var payload = args;
                string? note = null;
                int? aggregate = null;

                if (verdict.Action == TraceAdmit.Aggregate)
                {
                    aggregate = verdict.AggregateCount;
                    payload = verdict.AggregatePayload as object?[] ?? args;

                    if (verdict.RemembersPayload)
                    {
                        TraceAdmission.RememberArgs(entry, args);
                    }

                    if (verdict.LeftPolling)
                    {
                        note = "stopped polling";
                        WriteSelfEvent("Trace", "stoppedPolling", new[] { entry.Key });
                    }
                }
                else if (verdict.Suppressed > 0)
                {
                    note = "suppressed " + verdict.Suppressed.ToString(CultureInfo.InvariantCulture) + " calls";
                }

                Enqueue(new Pending
                {
                    Entry = entry,
                    Timestamp = now,
                    Frame = Frame,
                    ThreadId = Thread.CurrentThread.ManagedThreadId,
                    Args = FormatArgs(payload),
                    Instance = instance is null ? null : FormatSafe(instance),
                    Note = note,
                    Aggregate = aggregate,
                });
            }
            catch (Exception ex)
            {
                NoteHookFailure(original, ex);
            }
            finally
            {
                _inHook = false;
                if (sampled)
                {
                    Meter.Add(Stopwatch.GetTimestamp() - started);
                }
            }
        }

        /// <summary>Return-value postfix for curated methods returning a reference type.</summary>
        internal static void PostfixObject(MethodBase __originalMethod, object __result) =>
            RecordReturn(__originalMethod, __result);

        /// <summary>Return-value postfix for curated methods returning <see cref="bool"/>.</summary>
        internal static void PostfixBool(MethodBase __originalMethod, bool __result) =>
            RecordReturn(__originalMethod, __result);

        /// <summary>Return-value postfix for curated methods returning <see cref="int"/>.</summary>
        internal static void PostfixInt(MethodBase __originalMethod, int __result) =>
            RecordReturn(__originalMethod, __result);

        /// <summary>Return-value postfix for curated methods returning <see cref="float"/>.</summary>
        internal static void PostfixFloat(MethodBase __originalMethod, float __result) =>
            RecordReturn(__originalMethod, __result);

        private static void RecordReturn(MethodBase original, object? result)
        {
            if (!_enabled || _inHook)
            {
                return;
            }

            _inHook = true;
            try
            {
                if (!Entries.TryGetValue(original.MethodHandle.Value, out var entry))
                {
                    return;
                }

                // The prefix already paid the rate limit for this call; the postfix reuses that
                // budget by writing a second, return-only line rather than taking a new permit.
                Enqueue(new Pending
                {
                    Entry = entry,
                    Timestamp = Stopwatch.GetTimestamp(),
                    Frame = Frame,
                    ThreadId = Thread.CurrentThread.ManagedThreadId,
                    Args = Array.Empty<string>(),
                    Return = FormatSafe(result),
                    Note = "return",
                });
            }
            catch (Exception ex)
            {
                NoteHookFailure(original, ex);
            }
            finally
            {
                _inHook = false;
            }
        }

        /// <summary>
        /// Postfix for <c>InputController.GetClickDown</c>. The method is polled every frame, so it
        /// is downsampled like any other poller; a frame where it returns true is a real click and
        /// gets its own line regardless.
        /// </summary>
        internal static void PostfixInputClick(bool __result)
        {
            if (!__result || !_enabled || _inHook)
            {
                return;
            }

            _inHook = true;
            try
            {
                var position = UnityEngine.Input.mousePosition;

                // The method is polled by more than one caller, and every caller that sees
                // it return true lands here, so one physical click used to write two lines.
                if (!Clicks.ShouldWrite(Frame, position.x, position.y))
                {
                    return;
                }

                var x = position.x.ToString("0", CultureInfo.InvariantCulture);
                var y = position.y.ToString("0", CultureInfo.InvariantCulture);

                // A click on the framework's own overlay (the console, a mod window) is ours, not
                // the player's doing something to the game (UiOwnership). One raycast, and only
                // for a click that would otherwise be written.
                if (UiTracer.IsPointerOverOwnUi(position))
                {
                    return;
                }

                WriteSelfEvent("Input", "click", new[] { x, y }, system: "input");
                ConsoleMirror.Print(TraceConsoleLines.Click(Now, x, y));
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogWarning($"TRACE: input click line failed: {ex.Message}");
            }
            finally
            {
                _inHook = false;
            }
        }

        // ----- the writer thread ---------------------------------------------------------------

        private static void Enqueue(Pending pending)
        {
            if (!Queue.TryEnqueue(pending))
            {
                // Dropped rather than blocked: a game thread waiting on a log is worse than a hole
                // in it, and the count reaches the health snapshot and the footer.
                return;
            }
        }

        private static void DrainLoop()
        {
            while (_draining)
            {
                try
                {
                    // At most once per TraceClock.PollInterval of Stopwatch time, so a sleep is
                    // corrected within about 50 ms of waking. Lines captured in between keep the
                    // pre-sleep anchor (see TraceClock); lines captured before the sleep keep theirs
                    // whenever they are written.
                    PollClock();

                    var drained = 0;
                    while (drained < DrainBatch && Queue.TryDequeue(out var pending))
                    {
                        WritePending(pending);
                        drained++;
                    }

                    if (drained == 0)
                    {
                        Thread.Sleep(IdleSleepMs);
                    }
                }
                catch (Exception ex)
                {
                    // Losing the writer thread would silently end the trace, so it swallows
                    // everything and keeps going.
                    CwyLsModLoader.Log.LogWarning($"TRACE: the writer thread hit {ex.GetType().Name}: {ex.Message}");
                    Thread.Sleep(IdleSleepMs);
                }
            }
        }

        private static void WritePending(Pending pending)
        {
            var writer = _writer;
            var builder = _builder;
            if (writer is null || builder is null)
            {
                return;
            }

            if (pending.Prebuilt != null)
            {
                writer.Write(pending.Prebuilt);
            }
            else if (pending.Entry != null)
            {
                writer.Write(builder.Call(
                    WallClock(pending.Timestamp),
                    pending.Frame,
                    Realtime,
                    GameClock,
                    pending.Entry.NamespaceName,
                    pending.Entry.ClassName,
                    pending.Entry.MethodName,
                    pending.Args,
                    pending.ThreadId,
                    pending.Instance,
                    pending.Return,
                    pending.Note,
                    pending.Aggregate));
            }

        }

        /// <summary>Wall-clock time of a stopwatch reading, from the anchor current when it was taken.</summary>
        private static DateTimeOffset WallClock(long timestamp) =>
            _clock?.ToWall(timestamp) ?? DateTimeOffset.Now;

        // ----- the clock -----------------------------------------------------------------------

        /// <summary>
        /// Re-anchors the trace clock now if it has drifted from the system clock. Called on events
        /// after which a jump is likely: the window regaining focus, the application resuming.
        /// </summary>
        /// <param name="reason">What prompted the check, for the <c>TRACE</c> line.</param>
        internal static void CheckClock(string reason)
        {
            var clock = _clock;
            if (clock is null)
            {
                return;
            }

            try
            {
                ReportClockJump(clock.Check(reason));
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogWarning($"TRACE: the clock check ({reason}) failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// The periodic check, on the writer thread: at most one measurement per
        /// <see cref="TraceClock.PollInterval"/>, so a sleep is corrected within that long of waking.
        /// </summary>
        private static void PollClock()
        {
            var clock = _clock;
            if (clock is null || Volatile.Read(ref _clockPollFailures) > 0)
            {
                return;
            }

            try
            {
                ReportClockJump(clock.Poll("poll"));
            }
            catch (Exception ex)
            {
                // Once, then off: this runs every few milliseconds, and a repeating failure would
                // flood the log. Focus and resume still check the clock.
                if (Interlocked.Increment(ref _clockPollFailures) == 1)
                {
                    CwyLsModLoader.Log.LogWarning(
                        $"TRACE: the periodic clock check failed and is now off: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        /// <summary>One <c>TRACE</c> line per re-anchor: console, console log and trace file.</summary>
        private static void ReportClockJump(TraceClockJump? jump)
        {
            if (jump is null)
            {
                return;
            }

            ConsoleEvents.Emit(
                TraceLineBuilder.SelfSystem,
                ClockEvent,
                ConsoleTags.Trace,
                jump.Summary,
                "reason",
                ("reason", jump.Reason),
                ("jump", jump.JumpText),
                ("jumpMs", (long)Math.Round(jump.Jump.TotalMilliseconds)),
                ("traceTime", Core.MiniJson.Iso8601(jump.TraceTime)),
                ("systemTime", Core.MiniJson.Iso8601(jump.SystemTime)),
                ("offsetChanged", jump.OffsetChanged),
                ("reanchors", jump.Count));
        }

        // ----- lines the trace writes itself ----------------------------------------------------

        /// <summary>
        /// Writes a line the trace produced itself (scene loads, clicks, shutdown notes) to the
        /// FILE. There is no <c>mirror</c> flag: a console line is a
        /// <see cref="Core.Logging.ConsoleRecord"/> printed through <see cref="ConsoleMirror"/> by
        /// whoever owns the event, never a second formatting of a file line.
        /// </summary>
        /// <param name="className">Class the self event belongs to, e.g. <c>Scene</c>.</param>
        /// <param name="methodName">Event name, e.g. <c>sceneLoaded</c>.</param>
        /// <param name="args">Rendered arguments.</param>
        /// <param name="system">Trace <c>sys</c> tag; defaults to the trace's own.</param>
        internal static void WriteSelfEvent(
            string className,
            string methodName,
            IReadOnlyList<string> args,
            string system = TraceLineBuilder.SelfSystem)
        {
            var builder = _builder;
            if (_writer is null || builder is null)
            {
                return;
            }

            try
            {
                var line = builder.Call(
                    Now, Frame, Realtime, GameClock, system, className, methodName,
                    args, Thread.CurrentThread.ManagedThreadId, null);

                QueuePrebuilt(line);
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogWarning($"TRACE: writing the {className}.{methodName} line failed: {ex.Message}");
            }
        }

        /// <summary>Writes one Unity user-interface event.</summary>
        /// <param name="source">"user" or "code" for a value change UGUI cannot attribute itself.</param>
        /// <param name="aggregateCount">Set when the line stands for a window of a code-driven control.</param>
        internal static void WriteUi(
            string controlType,
            string eventName,
            string? path,
            string? label,
            string? value,
            string? source,
            int? aggregateCount,
            bool mirror)
        {
            var builder = _builder;
            if (!_enabled || _writer is null || builder is null)
            {
                return;
            }

            try
            {
                // One reading for both copies, so the file and the console show the same time.
                var now = Now;
                var line = builder.Ui(
                    now, Frame, Realtime, GameClock, controlType, eventName, path, label, value,
                    Thread.CurrentThread.ManagedThreadId, source, aggregateCount);

                QueuePrebuilt(line);

                if (mirror)
                {
                    // One sink: the console copy is a record, printed on this thread, in event order.
                    ConsoleMirror.Print(TraceConsoleLines.Ui(
                        now, controlType, eventName, path, label, value));
                }
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogWarning($"TRACE: writing the ui {controlType}.{eventName} line failed: {ex.Message}");
            }
        }

        /// <summary>Attaches the diagnostic bus and replays whatever was published before we were ready.</summary>
        internal static void AttachBus()
        {
            TraceBus.SetErrorSink(ex => CwyLsModLoader.Log.LogWarning($"TRACE: a bus consumer threw: {ex.Message}"));
            _busSubscription = TraceBus.Attach(WriteBus);
            ConsoleMirror.Print(TraceConsoleLines.Status(
                Now,
                $"bus attached ({TraceBus.Emitted} events emitted so far, {TraceBus.Dropped} dropped before attach)"));
        }

        /// <summary>
        /// Writes one event another plugin published on <see cref="TraceBus"/>. The payload becomes
        /// top-level members so the file can be filtered on them directly.
        /// </summary>
        internal static void WriteBus(BusEvent evt)
        {
            var builder = _builder;
            if (_writer is null || builder is null)
            {
                return;
            }

            try
            {
                var line = builder.Bus(
                    evt.Time, Frame, Realtime, GameClock, evt.Sys, evt.Name, evt.Payload,
                    Thread.CurrentThread.ManagedThreadId);

                QueuePrebuilt(line);
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogWarning($"TRACE: writing the bus {evt.Sys}/{evt.Name} line failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Hands an already-built line to the writer thread, so every line in the file comes from
        /// one producer and stays in order.
        /// </summary>
        private static void QueuePrebuilt(string line) =>
            Enqueue(new Pending { Prebuilt = line, Timestamp = Stopwatch.GetTimestamp() });

        // ----- lifecycle -----------------------------------------------------------------------

        /// <summary>Scene-load handler, registered statically so a destroyed component cannot lose it.</summary>
        internal static void OnSceneLoaded(Scene scene, LoadSceneMode mode) =>
            WriteSelfEvent("Scene", "sceneLoaded", new[] { scene.name, mode.ToString() });

        /// <summary>Scene-unload handler, registered statically for the same reason.</summary>
        internal static void OnSceneUnloaded(Scene scene) =>
            WriteSelfEvent("Scene", "sceneUnloaded", new[] { scene.name });

        /// <summary><c>Application.quitting</c> handler. The only path that closes the trace file.</summary>
        internal static void OnApplicationQuitting()
        {
            TraceHost.MarkQuitting();
            Shutdown("Application.quitting");
        }

        /// <summary>
        /// Writes the footer and closes the file. Idempotent, and deliberately the ONLY place the
        /// writer is disposed: a destroyed component or host must never end the session.
        /// </summary>
        internal static void Shutdown(string reason)
        {
            if (Interlocked.Exchange(ref _shutdownDone, 1) != 0)
            {
                return;
            }

            var writer = _writer;
            if (writer is null)
            {
                return;
            }

            try
            {
                // Stop taking new work, then let the writer thread finish what is already queued.
                UnityLogCapture.Uninstall();
                _busSubscription?.Dispose();
                _busSubscription = null;
                _enabled = false;

                DrainRemaining();
                _draining = false;
                _writerThread?.Join(500);

                WriteSelfEventDirect("Trace", "shutdown", new[] { reason });

                var cost = new TraceHealth();
                ReadCost(cost);

                writer.Write(Builder.Footer(
                    Now, Frame, Realtime, writer.LinesWritten,
                    PendingSuppressed(), PollingKeys(), TraceBus.Emitted, TraceBus.Dropped, Unpatched, cost));

                writer.Flush();
                ConsoleMirror.Print(TraceConsoleLines.Status(
                    Now,
                    $"{writer.LinesWritten} lines written, {PollingKeys().Count} aggregated, " +
                    $"{Unpatched.Count} unpatched, {Queue.Dropped} dropped ({_hookFailures} hook failures, reason: {reason})"));
                writer.Dispose();
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogWarning($"TRACE: shutdown ({reason}) did not complete cleanly: {ex.Message}");
            }
        }

        /// <summary>Empties the queue synchronously, bounded so shutdown cannot hang.</summary>
        private static void DrainRemaining()
        {
            var guard = QueueCapacity + DrainBatch;
            while (guard-- > 0 && Queue.TryDequeue(out var pending))
            {
                WritePending(pending);
            }
        }

        /// <summary>Writes straight to the file, bypassing the queue: used once the thread has stopped.</summary>
        private static void WriteSelfEventDirect(string className, string methodName, IReadOnlyList<string> args) =>
            _writer?.Write(Builder.Call(
                Now, Frame, Realtime, GameClock, TraceLineBuilder.SelfSystem,
                className, methodName, args, Thread.CurrentThread.ManagedThreadId, null));

        private static long PendingSuppressed()
        {
            long total = 0;
            foreach (var entry in EntryList)
            {
                total += Volatile.Read(ref entry.Suppressed);
            }

            return total;
        }

        private static IReadOnlyList<string> PollingKeys()
        {
            var keys = new List<string>();
            foreach (var entry in EntryList)
            {
                if (entry.IsPolling)
                {
                    keys.Add(entry.Key);
                }
            }

            keys.Sort(StringComparer.Ordinal);
            return keys;
        }

        // ----- helpers -------------------------------------------------------------------------

        private static IReadOnlyList<string> FormatArgs(object?[]? args)
        {
            var formatter = _formatter;
            return formatter is null ? Array.Empty<string>() : formatter.FormatAll(args);
        }

        private static string FormatSafe(object? value)
        {
            var formatter = _formatter;
            if (formatter is null)
            {
                return value is null ? "null" : value.GetType().Name;
            }

            return formatter.Format(value);
        }

        private static void NoteHookFailure(MethodBase original, Exception ex)
        {
            var count = Interlocked.Increment(ref _hookFailures);
            if (count <= MaxLoggedHookFailures)
            {
                CwyLsModLoader.Log.LogWarning(
                    $"TRACE: hook for {original.DeclaringType?.FullName}.{original.Name} threw and was ignored: {ex.Message}");
            }
            else if (count == MaxLoggedHookFailures + 1)
            {
                CwyLsModLoader.Log.LogWarning("TRACE: further hook failures will only be counted, not logged");
            }
        }

        /// <summary>Stopwatch ticks a method must stay hot before it is unpatched.</summary>
        internal static long UnpatchAfterTicks => _unpatchAfterTicks;
    }
}
