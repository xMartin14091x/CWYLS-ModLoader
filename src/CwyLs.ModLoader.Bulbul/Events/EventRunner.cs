using System;
using CwyLs.ModLoader;
using CwyLs.ModLoader.Core.Events;
using UnityEngine;
using UnityEngine.SceneManagement;
using Game = global::Bulbul;

namespace CwyLs.ModLoader.Bulbul.Events
{
    /// <summary>
    /// The bus's one frame-driven component. Lives on the hidden host GameObject created by
    /// <see cref="EventHost.EnsureRunner"/>; nothing else should add it.
    /// </summary>
    /// <remarks>
    /// Five jobs, none of which can live on the BepInEx plugin component (it is destroyed seconds
    /// into startup in this game): drain the cross-thread publish queue, close a spoken line when
    /// Satone stops, raise the once-a-second clock tick, keep the voice index and observable bridge
    /// trying until they succeed, and turn scene loads into events.
    /// </remarks>
    public sealed class EventRunner : MonoBehaviour
    {
        /// <summary>Seconds between clock ticks, index refreshes and attach attempts.</summary>
        private const float SlowTickSeconds = 1f;

        private Game.HeroineService? _heroine;
        private float _nextSlowTick;

        private void OnEnable()
        {
            SceneManager.sceneLoaded += OnSceneLoaded;
            SceneManager.sceneUnloaded += OnSceneUnloaded;
        }

        private void OnDisable()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneUnloaded -= OnSceneUnloaded;
        }

        private void Start()
        {
            // A runner created after the room is already up would otherwise wait for a sceneLoaded
            // that never comes again.
            if (SceneManager.GetActiveScene().name == EventHost.SceneName)
            {
                EventHost.RoomSceneSeen = true;
                CWYEvents.Publish(new SceneChanged(EventHost.SceneName, true));
            }
        }

        private void Update()
        {
            // First, so an event produced off the main thread is delivered before anything this
            // frame reads a counter that it would have changed.
            CWYEvents.PumpMainThread();

            var now = Time.unscaledTime;

            try
            {
                SatoneSpeech.PollLineEnded(_heroine != null ? _heroine.IsFinishedVoice : (bool?)null, now);
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogError(
                    $"EVENT could not poll the voice state: {ex.GetType().Name}: {ex.Message}");
                _heroine = null;
            }

            if (now < _nextSlowTick)
            {
                return;
            }

            _nextSlowTick = now + SlowTickSeconds;
            SlowTick();
        }

        private void SlowTick()
        {
            // The game's clock is DateTime.Now - DateService does nothing else -
            // so this is the same value every clock in the room shows.
            CWYEvents.Publish(new GameClockTick(DateTime.Now));

            try
            {
                SatoneSpeech.RefreshIndex();
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogError(
                    $"EVENT voice index refresh failed: {ex.GetType().Name}: {ex.Message}");
            }

            if (!EventBridge.IsComplete)
            {
                EventBridge.TryAttach();
            }

            if (EventHost.RoomSceneSeen && !EventHost.RoomReadyRaised)
            {
                TryRoomReady();
            }
        }

        private void TryRoomReady()
        {
            // "Ready" means the room's services can actually be resolved, not merely that the
            // scene name appeared: a mod that touches Satone the instant the scene loads would
            // otherwise find a half-built DI scope.
            if (_heroine == null && GameScopes.TryRoom<Game.HeroineService>(out var heroine))
            {
                _heroine = heroine;
            }

            if (_heroine == null)
            {
                return;
            }

            EventHost.RoomReadyRaised = true;
            CWYEvents.Publish(new RoomReady());
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (scene.name == EventHost.SceneName)
            {
                EventHost.RoomSceneSeen = true;
                EventHost.RoomReadyRaised = false;
                _heroine = null;

                // The room's DI scope only exists from here, so this is the first moment the
                // game's services can be resolved.
                _nextSlowTick = 0f;
            }

            CWYEvents.Publish(new SceneChanged(scene.name, true));
        }

        private void OnSceneUnloaded(Scene scene)
        {
            if (scene.name == EventHost.SceneName)
            {
                EventHost.RoomReadyRaised = false;
                _heroine = null;
                SatoneSpeech.Forget();
                EventPatches.ForgetScenarios();
            }

            CWYEvents.Publish(new SceneChanged(scene.name, false));
        }
    }
}
