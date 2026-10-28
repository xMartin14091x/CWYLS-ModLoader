using System;
using CwyLs.ModLoader;
using CwyLs.ModLoader.Core.Events;
using CwyLs.ModLoader.Core.Logging;
using UnityEngine;

namespace CwyLs.ModLoader.Bulbul.Events
{
    /// <summary>
    /// Owns the one frame-driven component the bus needs, on a hidden
    /// <c>DontDestroyOnLoad</c> GameObject.
    /// </summary>
    /// <remarks>
    /// Not on the BepInEx plugin component: in this game that component receives
    /// <c>OnDestroy</c> about 0.2 s after startup, before the boot scene has finished loading
    /// (observed in an early trace run). Static Harmony patches survive that;
    /// <c>Update</c> and coroutines attached to the plugin do not. Same pattern as
    /// <c>CwyLs.ModLoader.Smoke.SmokeHost</c>. The bus is wound down only by
    /// <see cref="Application.quitting"/>.
    /// </remarks>
    public static class EventHost
    {
        /// <summary>Name of the hidden host GameObject, for anyone reading a scene inspector.</summary>
        public const string RunnerObjectName = "CWY.EventRunner";

        /// <summary>The scene whose load means the room's services exist.</summary>
        public const string SceneName = GameScopes.RoomSceneName;

        private static readonly object Gate = new object();

        private static EventRunner? _runner;

        /// <summary>True once <see cref="SceneName"/> has been seen loading this session.</summary>
        public static bool RoomSceneSeen { get; internal set; }

        /// <summary>True once <see cref="RoomReady"/> has been published for the current room.</summary>
        public static bool RoomReadyRaised { get; internal set; }

        /// <summary>The host component, or null if it could not be created.</summary>
        public static EventRunner? Runner => _runner;

        /// <summary>
        /// Creates the hidden runner once and returns it; later calls return the same instance.
        /// Returns null only if the GameObject could not be created, which is logged. In that case
        /// the patch-driven events still fire - only the clock tick, the room-ready signal, the
        /// observable bridge and the cross-thread queue stop.
        /// </summary>
        public static EventRunner? EnsureRunner()
        {
            lock (Gate)
            {
                // Unity's == also catches a runner whose GameObject was destroyed, so a lost
                // runner is rebuilt rather than handed back dead.
                if (_runner != null)
                {
                    return _runner;
                }

                try
                {
                    var host = new GameObject(RunnerObjectName);
                    UnityEngine.Object.DontDestroyOnLoad(host);
                    host.hideFlags = HideFlags.HideAndDontSave;

                    _runner = host.AddComponent<EventRunner>();
                    Application.quitting += OnQuitting;
                    return _runner;
                }
                catch (Exception ex)
                {
                    CwyLsModLoader.Log.LogError(
                        $"EVENT runner could not be created: {ex.GetType().Name}: {ex.Message}. "
                        + "Patch-driven events still work; the clock tick and the observable bridge do not.");
                    return null;
                }
            }
        }

        private static void OnQuitting()
        {
            CWYEvents.IsReady = false;
            SatoneSpeech.Forget();
            ConsoleEvents.Setup(
                "events",
                "bus closed",
                ("published", CWYEvents.TotalPublished),
                ("spoken", CWYEvents.CountOf<LineSpoken>()),
                ("dropped", CWYEvents.DroppedFromQueue));
        }
    }
}
