using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using UnityEngine;

namespace CwyLs.ModLoader.Bulbul.Textures
{
    /// <summary>
    /// Owns the one hidden <c>DontDestroyOnLoad</c> object that gives
    /// <see cref="TextureFeature"/> a frame to work with.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This game destroys the BepInEx plugin component about 0.2 s after startup (observed during
    /// an early trace run), so anything that needs <c>Update</c> or a
    /// coroutine has to live somewhere else. Same pattern as <c>CwyLs.ModLoader.Smoke.SmokeHost</c> and
    /// <c>CwyLs.ModLoader.Bulbul.Data.DataHost</c>.
    /// </para>
    /// <para>
    /// The one frame of delay is not superstition: <c>sceneLoaded</c> fires before the scene's own
    /// <c>Start</c> methods have run, and the game swaps Satone's costume renderers in during that
    /// window. Applying a frame later means we write to the materials the player will actually see.
    /// </para>
    /// </remarks>
    public static class TextureHost
    {
        /// <summary>Name of the hidden host GameObject, for anyone reading a scene inspector.</summary>
        public const string RunnerObjectName = "CWY.TextureRunner";

        /// <summary>How many frames after a trigger the apply pass runs.</summary>
        public const int DeferFrames = 1;

        /// <summary>
        /// Real-time delays, in seconds after a scene trigger, at which the apply is repeated.
        /// </summary>
        /// <remarks>
        /// One frame is not enough on its own. <c>Bulbul.CostumeChangeService.TryChangeModel</c>
        /// runs <c>InstantiateAsync</c> on a costume prefab and re-parents its renderers well after
        /// <c>sceneLoaded</c>, and the game also ships MagicLightmapSwitcher, which re-assigns
        /// materials. Each re-pass audits what the previous one wrote before overwriting it, so the
        /// log says whether something reverted the slot in between.
        /// </remarks>
        public static readonly float[] RepassSeconds = { 2f, 10f };

        private static readonly object Gate = new object();
        private static readonly List<ScheduledPass> Scheduled = new List<ScheduledPass>();

        private static TextureRunner? _runner;
        private static string? _requested;
        private static string? _armed;
        private static int _dueFrame;

        private sealed class ScheduledPass
        {
            internal ScheduledPass(float dueRealtime, string trigger)
            {
                DueRealtime = dueRealtime;
                Trigger = trigger;
            }

            internal float DueRealtime { get; }

            internal string Trigger { get; }
        }

        /// <summary>True once the hidden runner exists; false means triggers apply immediately instead.</summary>
        public static bool HasRunner => _runner != null;

        /// <summary>
        /// Creates the hidden, scene-surviving runner once and returns it; later calls return the
        /// same instance. Returns null only when the GameObject could not be created, which is
        /// logged - <see cref="TextureFeature"/> then applies without the one-frame delay.
        /// </summary>
        public static TextureRunner? EnsureRunner()
        {
            lock (Gate)
            {
                // Unity's == also catches a runner whose GameObject was destroyed, so a lost runner
                // is rebuilt rather than handed back dead.
                if (_runner != null)
                {
                    return _runner;
                }

                try
                {
                    var host = new GameObject(RunnerObjectName);
                    UnityEngine.Object.DontDestroyOnLoad(host);
                    host.hideFlags = HideFlags.HideAndDontSave;

                    _runner = host.AddComponent<TextureRunner>();
                    CwyLsModLoader.Log.LogDebug($"TEXTURES runner armed ({RunnerObjectName}, +{DeferFrames} frame)");
                    return _runner;
                }
                catch (Exception ex)
                {
                    CwyLsModLoader.Log.LogError(
                        "TEXTURES runner could not be created; textures will be applied without the " +
                        $"one-frame delay: {ex.GetType().Name}: {ex.Message}");
                    return null;
                }
            }
        }

        /// <summary>
        /// Asks for an apply pass <see cref="DeferFrames"/> frames from now.
        /// </summary>
        /// <param name="trigger">
        /// What asked for it - <c>sceneLoaded</c>, <c>packsResolved</c> - for the log line.
        /// </param>
        /// <remarks>
        /// Safe to call from any thread: the request is only a string handed over with
        /// <see cref="Interlocked"/>, and everything that touches Unity happens in
        /// <see cref="Pump"/> on the main thread. <c>PackManager.PacksResolved</c> is raised on the
        /// thread that scanned, which is the main thread today and need not stay that way.
        /// A second request before the first runs simply re-arms the timer.
        /// </remarks>
        public static void Request(string trigger)
        {
            var name = string.IsNullOrEmpty(trigger) ? "unknown" : trigger;

            if (_runner == null)
            {
                // No runner means nothing will ever pump the request, so run it on the caller's
                // thread instead - both triggers are raised on the main thread - and say in the
                // log that the delay was skipped.
                TextureFeature.ApplyNow(name + " (immediate, no runner)");
                return;
            }

            Interlocked.Exchange(ref _requested, name);
        }

        /// <summary>
        /// Advances the deferred apply by one frame. Called from <see cref="TextureRunner"/>'s
        /// <c>Update</c>, on the main thread, and nowhere else.
        /// </summary>
        internal static void Pump()
        {
            var requested = Interlocked.Exchange(ref _requested, null);
            if (requested != null)
            {
                _armed = requested;
                _dueFrame = Time.frameCount + DeferFrames;
                ScheduleRepasses(requested);
                return;
            }

            if (_armed != null && Time.frameCount >= _dueFrame)
            {
                var trigger = _armed;
                _armed = null;
                TextureFeature.ApplyNow(trigger);
                return;
            }

            PumpScheduled();
        }

        /// <summary>
        /// Queues the delayed re-passes for a trigger. Only scene triggers get them: a manual F9 or
        /// a pack rescan is the operator asking for one pass, not for three.
        /// </summary>
        /// <param name="trigger">The trigger that was just armed.</param>
        private static void ScheduleRepasses(string trigger)
        {
            if (trigger.IndexOf("scene", StringComparison.OrdinalIgnoreCase) < 0)
            {
                return;
            }

            lock (Gate)
            {
                // A second scene load replaces the queue rather than stacking onto it; the game
                // loads a Transition scene around RoomScene and can fire the trigger twice.
                Scheduled.Clear();

                foreach (var seconds in RepassSeconds)
                {
                    Scheduled.Add(new ScheduledPass(
                        Time.realtimeSinceStartup + seconds,
                        trigger + "+" + seconds.ToString("0.#", CultureInfo.InvariantCulture) + "s"));
                }
            }
        }

        /// <summary>Runs any re-pass whose time has come. Real time, so a paused game still gets it.</summary>
        private static void PumpScheduled()
        {
            ScheduledPass? due = null;

            lock (Gate)
            {
                for (var i = 0; i < Scheduled.Count; i++)
                {
                    if (Time.realtimeSinceStartup >= Scheduled[i].DueRealtime)
                    {
                        due = Scheduled[i];
                        Scheduled.RemoveAt(i);
                        break;
                    }
                }
            }

            // Outside the lock: the pass walks the scene and must not hold it.
            if (due != null)
            {
                TextureFeature.ApplyNow(due!.Trigger);
            }
        }
    }
}
