using System;
using System.Collections;
using System.Globalization;
using System.Threading;
using CwyLs.ModLoader.Core.Bus;
using CwyLs.ModLoader.Core.Logging;
using CwyLs.ModLoader.Core.Trace;
using CwyLs.ModLoader.Logging;
using UnityEngine;

namespace CwyLs.ModLoader.Trace
{
    /// <summary>
    /// Owns every piece of the trace that needs a living MonoBehaviour: the once-per-frame
    /// frame/time cache, the flush coroutine and the snapshot timer.
    /// </summary>
    /// <remarks>
    /// This exists because something in this game destroys the BepInEx plugin component ~0.2 s
    /// after boot, which took the flush loop and the snapshot timer down with it. The host is a
    /// hidden, DontDestroyOnLoad GameObject of our own, and if it is ever destroyed too it is
    /// re-created lazily from the next traced call. The writer is never closed except on quit.
    /// </remarks>
    internal sealed class TraceHost : MonoBehaviour
    {
        /// <summary>Name of the hidden host GameObject, for anyone reading a scene inspector.</summary>
        internal const string HostObjectName = "CWY.TraceHost";

        /// <summary>How often buffered lines are pushed to disk, in seconds.</summary>
        private const float FlushIntervalSeconds = 0.5f;

        /// <summary>Average frames per second below which an interval is reported as unhealthy.</summary>
        private const double LowFpsThreshold = 20d;

        /// <summary>
        /// How many times the host is rebuilt before we stop trying. Without a cap, a host that
        /// cannot survive would have every traced call attempting a new GameObject.
        /// </summary>
        private const int MaxRebuilds = 5;

        private static readonly object Gate = new object();

        private static TraceHost? _instance;
        private static TraceConfig? _config;
        private static GameStateProbe? _probe;
        private static HarmonyLib.Harmony? _harmony;
        private static volatile bool _lost;
        private static volatile bool _quitting;
        private static int _rebuilds;

        private static readonly HealthProbe Health = new HealthProbe();

        /// <summary>
        /// Whether the window has focus, and when that last changed. Used to attribute frame-time
        /// spikes: the game lowers its own frame rate in the background, so an alt-tab produces
        /// 250 ms+ frames that are not a stutter.
        /// </summary>
        private static readonly FocusWindow Focus = new FocusWindow();

        /// <summary>
        /// Spike and low-fps events waiting to be attributed. Unity raises focusChanged AFTER the
        /// stall the switch caused, so an event emitted where it is measured is always tagged
        /// "focused" and always reaches the console. Held events are attributed a fraction of a
        /// second later, by which time the callback has arrived.
        /// </summary>
        private static readonly HealthEventHold Held = new HealthEventHold();

        private static int _unpatchedReported;
        private static bool _heldDropWarned;

        private double _sinceSnapshot;
        private bool _lowFpsReported;

        /// <summary>True when the host has been destroyed and not yet rebuilt.</summary>
        internal static bool IsLost => _lost;

        /// <summary>True once the application has begun shutting down.</summary>
        internal static bool IsQuitting => _quitting;

        /// <summary>Records that the process is quitting, so a destroyed host is not rebuilt.</summary>
        internal static void MarkQuitting() => _quitting = true;

        /// <summary>
        /// Records a window focus change, so health readings taken around it can be attributed to
        /// it. Called from <c>Application.focusChanged</c>.
        /// </summary>
        /// <param name="focused">The new focus state.</param>
        internal static void NoteFocus(bool focused) => Focus.Change(focused, TraceRuntime.Realtime);

        /// <summary>
        /// Holds a health event until its attribution is knowable, then emits it. See
        /// <see cref="Held"/> for why a spike cannot be attributed where it is measured.
        /// </summary>
        private static void HoldHealth(string name, params (string Key, object? Value)[] fields)
        {
            Held.Hold(name, TraceRuntime.Realtime, fields);

            if (Held.Dropped > 0 && !_heldDropWarned)
            {
                _heldDropWarned = true;
                CwyLsModLoader.Log.LogWarning(
                    $"TRACE: more than {HealthEventHold.Capacity} health events were stalled at once; " +
                    "the oldest are being dropped");
            }
        }

        /// <summary>
        /// Emits every held event whose wait is over, tagged with what is to blame. Called once a
        /// frame; allocates nothing while nothing is held.
        /// </summary>
        private static void ReleaseHeldHealth(double nowSeconds)
        {
            var due = Held.Release(nowSeconds);
            for (var i = 0; i < due.Count; i++)
            {
                Emit(due[i], nowSeconds);
            }
        }

        private static void Emit(HeldHealthEvent held, double nowSeconds)
        {
            var cause = Focus.CauseAt(held.AtSeconds, nowSeconds);

            // Two extra members on every health event: whether the window is to blame, and which
            // way. TraceRuntime keeps an "unfocused" event out of the console but still files it.
            var fields = new (string Key, object? Value)[held.Fields.Length + 2];
            Array.Copy(held.Fields, fields, held.Fields.Length);
            fields[held.Fields.Length] = (TraceRuntime.UnfocusedField, cause != null);
            fields[held.Fields.Length + 1] = (TraceRuntime.CauseField, cause);

            TraceBus.EmitFields("health", held.Name, fields);
        }

        /// <summary>
        /// Remembers the Harmony instance so the unpatch scan can undo a patch. Called before
        /// patching, because a method can be hot enough to unpatch before the host even exists.
        /// </summary>
        internal static void UseHarmony(HarmonyLib.Harmony harmony, TraceConfig config)
        {
            _harmony = harmony;
            _config = config;
        }

        /// <summary>Creates the host for the first time and remembers what it needs to be rebuilt with.</summary>
        internal static void Install(TraceConfig config, GameStateProbe probe)
        {
            _config = config;
            _probe = probe;
            Create();
        }

        /// <summary>
        /// Rebuilds the host if it was destroyed. Called from the trace hooks, so it must be cheap
        /// in the common case and must only ever run on Unity's main thread.
        /// </summary>
        internal static void RestoreIfLost()
        {
            if (!_lost || _quitting)
            {
                return;
            }

            if (Interlocked.Increment(ref _rebuilds) > MaxRebuilds)
            {
                // Give up quietly rather than fight whatever keeps killing it: the writer is still
                // open, lines still buffer, and the file is still closed properly on quit.
                _lost = false;
                CwyLsModLoader.Log.LogWarning(
                    $"TRACE: the host could not be kept alive after {MaxRebuilds} rebuilds; " +
                    "calls are still recorded, but frame numbers and snapshots stop here");
                return;
            }

            Create();
        }

        private static void Create()
        {
            lock (Gate)
            {
                // Unity's overloaded == also reports a destroyed component as null.
                if (_instance != null)
                {
                    _lost = false;
                    return;
                }

                try
                {
                    var host = new GameObject(HostObjectName);
                    DontDestroyOnLoad(host);
                    host.hideFlags = HideFlags.HideAndDontSave;

                    _instance = host.AddComponent<TraceHost>();
                    _lost = false;
                    ConsoleMirror.Print(TraceConsoleLines.Status(
                        TraceRuntime.Now, $"host '{HostObjectName}' armed (flush {FlushIntervalSeconds:0.#}s)"));
                }
                catch (Exception ex)
                {
                    // Without a host there is no flush loop; lines still buffer and are written on
                    // quit, so this degrades rather than fails.
                    _lost = true;
                    CwyLsModLoader.Log.LogError($"TRACE: could not create the trace host: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        private void Awake() => StartCoroutine(FlushLoop());

        /// <summary>
        /// Caches the frame counter and the clock once per frame. The Harmony hooks can run on any
        /// thread, and a Unity API call off the main thread throws, so they read these instead.
        /// </summary>
        private void Update()
        {
            TraceRuntime.Frame = Time.frameCount;
            TraceRuntime.Realtime = Time.realtimeSinceStartup;

            // unscaledDeltaTime, so a paused or slowed game does not read as a stutter.
            var frameMs = Time.unscaledDeltaTime * 1000f;
            Health.AddFrame(frameMs);

            if (frameMs > HealthStats.BigSpikeMs)
            {
                // Held, not dropped: the spike is real and belongs in the file. It is emitted
                // once we can say whether the user caused it by switching windows.
                HoldHealth(
                    "spike",
                    ("ms", Math.Round(frameMs, 1)),
                    ("frame", TraceRuntime.Frame));
            }

            ReleaseHeldHealth(TraceRuntime.Realtime);
        }

        private IEnumerator FlushLoop()
        {
            while (true)
            {
                yield return new WaitForSecondsRealtime(FlushIntervalSeconds);

                if (_probe != null)
                {
                    TraceRuntime.GameClock = _probe.ReadGameClock();
                }

                TraceRuntime.Flush();
                ScanForHotMethods();

                var interval = _config?.SnapshotIntervalSeconds ?? 0d;
                if (interval <= 0d)
                {
                    continue;
                }

                _sinceSnapshot += FlushIntervalSeconds;
                if (_sinceSnapshot + 1e-6 < interval)
                {
                    continue;
                }

                _sinceSnapshot = 0d;
                WriteSnapshot();
            }
        }

        private void WriteSnapshot()
        {
            var probe = _probe;
            var config = _config;
            if (probe is null || config is null)
            {
                return;
            }

            try
            {
                UnityLogCapture.TakeCounts(out var warnings, out var errors);
                var health = Health.Capture(warnings, errors, probe.ReadSteamInitialized());
                TraceRuntime.ReadCost(health);

                var snapshot = probe.Capture();
                snapshot.Health = health;
                TraceRuntime.WriteRaw(TraceRuntime.Builder.Snapshot(snapshot));

                PublishHealth(health);

                // The snapshot summary is a trace-file line. It repeats what the health,
                // music, scene and window events already said on the console, and the file's
                // snapshot record carries far more than the summary ever did.
                CwyLsModLoader.Log.LogDebug(TraceRuntime.Builder.SnapshotSummary(snapshot));
            }
            catch (Exception ex)
            {
                // Snapshots are best-effort: one bad reading must not end the loop.
                CwyLsModLoader.Log.LogWarning($"TRACE: snapshot failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Removes the prefix from any method that has stayed far too hot for far too long.
        /// Aggregation caps what is written, but the prefix still runs; for a method called
        /// thirteen thousand times a second that is the whole remaining cost. Runs on the main
        /// thread because Harmony rewrites the method body.
        /// </summary>
        private void ScanForHotMethods()
        {
            var config = _config;
            var harmony = _harmony;
            if (harmony is null || config is null || !config.UnpatchHotMethods || _quitting)
            {
                return;
            }

            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            var entries = TraceRuntime.AllEntries;

            for (var i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                if (!TraceAdmission.ShouldUnpatch(entry, now, config.UnpatchAbovePerSecond, TraceRuntime.UnpatchAfterTicks))
                {
                    continue;
                }

                var original = TraceRuntime.OriginalOf(entry);
                if (original is null || !entry.MarkUnpatched())
                {
                    continue;
                }

                var rate = entry.ObservedPerSecond.ToString(CultureInfo.InvariantCulture);

                try
                {
                    harmony.Unpatch(original, HarmonyLib.HarmonyPatchType.All, harmony.Id);
                    TraceRuntime.NoteUnpatched(entry);

                    // The first one explains itself in the console so the behaviour is not a
                    // mystery; the rest are file-only, because a dozen of them is a wall of text.
                    if (Interlocked.Increment(ref _unpatchedReported) == 1)
                    {
                        ConsoleMirror.Print(TraceConsoleLines.Status(
                            TraceRuntime.Now,
                            $"unpatched hot method {entry.Key} (~{rate}/s); counts stop. " +
                            "Further unpatches are written to the trace file only."));
                    }

                    TraceRuntime.WriteSelfEvent(
                        "Trace", "unpatched", new[] { entry.Key, rate + "/s" });
                }
                catch (Exception ex)
                {
                    CwyLsModLoader.Log.LogWarning($"TRACE: could not unpatch {entry.Key}: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Puts the interval reading on the bus so other consumers see it, and raises a low-frame-rate
        /// event once per bad spell rather than on every interval of it.
        /// </summary>
        private void PublishHealth(TraceHealth health)
        {
            TraceBus.EmitFields(
                "health", "interval",
                ("fpsAvg", Math.Round(health.Fps?.Average ?? 0d, 1)),
                ("fpsMin", Math.Round(health.Fps?.Min ?? 0d, 1)),
                ("frameMsP95", Math.Round(health.FrameMs?.P95 ?? 0d, 2)),
                ("spikes", health.Spikes),
                ("monoUsedMB", Math.Round(health.MonoUsedMB ?? 0d, 1)),
                ("rssMB", Math.Round(health.RssMB ?? 0d, 1)),
                ("threads", health.Threads),
                ("playingSources", health.Audio?.PlayingSources),
                ("tweens", health.Tweens),
                ("errors", health.ErrorsInInterval),
                ("warnings", health.WarningsInInterval),
                ("hookNsAvg", Math.Round(health.HookNs?.Average ?? 0d, 0)),
                ("hookNsP95", Math.Round(health.HookNs?.P95 ?? 0d, 0)),
                ("queueLen", health.Queue?.Length),
                ("queueDropped", health.Queue?.Dropped));

            var average = health.Fps?.Average ?? 0d;
            var measured = health.FrameMs?.Average > 0d;

            if (measured && average < LowFpsThreshold)
            {
                if (!_lowFpsReported)
                {
                    _lowFpsReported = true;
                    HoldHealth(
                        "lowFps",
                        ("fpsAvg", Math.Round(average, 1)),
                        ("spikes", health.Spikes));
                }
            }
            else
            {
                _lowFpsReported = false;
            }
        }

        private void OnApplicationPause(bool paused)
        {
            if (!paused)
            {
                // Resuming is when a sleep shows; see TraceRuntime.CheckClock.
                TraceRuntime.CheckClock("resume");
            }

            TraceBus.EmitFields("health", "pause", ("paused", paused));
        }

        private void OnApplicationQuit()
        {
            _quitting = true;

            // Whatever is still waiting was measured; it is filed with the attribution we have.
            var now = TraceRuntime.Realtime;
            foreach (var held in Held.ReleaseAll())
            {
                Emit(held, now);
            }

            TraceRuntime.Shutdown("TraceHost.OnApplicationQuit");
        }

        private void OnDestroy()
        {
            lock (Gate)
            {
                if (ReferenceEquals(_instance, this))
                {
                    _instance = null;
                }
            }

            if (_quitting)
            {
                return;
            }

            // Whatever killed the plugin component can kill this too. Say so, keep the writer open
            // and let the next traced call rebuild the host.
            _lost = true;
            CwyLsModLoader.Log.LogWarning("TRACE: host destroyed (reason unknown)");
            TraceRuntime.WriteSelfEvent(
                "Trace", "hostDestroyed", new[] { "rt=" + TraceRuntime.Realtime.ToString("0.###", CultureInfo.InvariantCulture) });
        }
    }
}
