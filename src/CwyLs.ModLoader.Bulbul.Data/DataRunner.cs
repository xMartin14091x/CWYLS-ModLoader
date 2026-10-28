using System;
using CwyLs.ModLoader;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CwyLs.ModLoader.Bulbul.Data
{
    /// <summary>
    /// The bus's one frame-driven component. Lives on the hidden host GameObject created by
    /// <see cref="DataHost.EnsureRunner"/>; nothing else should add it.
    /// </summary>
    /// <remarks>
    /// Three jobs, all of which need a frame and none of which may live on the BepInEx plugin
    /// component (it is destroyed seconds into startup in this game): take the debounced snapshot,
    /// emit log lines produced on the writer thread, and attach to the game's own observables once
    /// the room's DI scope exists.
    /// </remarks>
    public sealed class DataRunner : MonoBehaviour
    {
        /// <summary>Seconds between attempts to attach to the game's observables.</summary>
        private const float SubscribeRetrySeconds = 2f;

        private float _nextSubscribeAttempt;

        private void OnEnable() => SceneManager.sceneLoaded += OnSceneLoaded;

        private void OnDisable() => SceneManager.sceneLoaded -= OnSceneLoaded;

        private void Start()
        {
            // A runner created after the room is already up would otherwise wait for a sceneLoaded
            // that never comes again.
            if (SceneManager.GetActiveScene().name == DataHost.SceneName)
            {
                DataHost.MarkSceneLoaded();
            }
        }

        private void Update()
        {
            // Readiness is a computed state, not an event, so the first frame it holds is the
            // moment to announce it - and by then the resolution path is known.
            if (GameData.IsReady)
            {
                DataHost.EmitReadyOnce();
            }

            var log = DataHost.TakePendingLog();
            if (log != null)
            {
                // The writer runs on a thread pool thread; logging is deferred to here so the log
                // line is emitted from the main thread like every other line the bus writes.
                // The successful snapshot is a DATA record published by the writer itself,
                // so what arrives here is the failure path; anything else is trace detail.
                if (log.StartsWith("!", StringComparison.Ordinal))
                {
                    CwyLsModLoader.Log.LogError(log.Substring(1));
                }
                else
                {
                    CwyLsModLoader.Log.LogDebug(log);
                }
            }

            if (Time.unscaledTime >= _nextSubscribeAttempt && !GameEventBridge.IsComplete)
            {
                _nextSubscribeAttempt = Time.unscaledTime + SubscribeRetrySeconds;
                GameEventBridge.TryAttach();
            }

            if (!DataHost.TryConsumeDue())
            {
                return;
            }

            try
            {
                // Capture on the main thread (it reads live game objects), serialise and write on
                // a worker so a large notebook cannot show up as a frame hitch.
                DataHost.WriteAsync(GameData.Snapshot());
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogError(
                    $"DATA snapshot capture failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (scene.name != DataHost.SceneName)
            {
                return;
            }

            DataHost.MarkSceneLoaded();

            // The room's DI scope only exists from here, so this is the first moment the game's
            // services can be resolved.
            _nextSubscribeAttempt = 0f;
        }
    }
}
