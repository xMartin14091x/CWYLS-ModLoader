using System;
using System.Collections;
using UnityEngine;

namespace CwyLs.ModLoader.Loaders
{
    /// <summary>
    /// Owns the one hidden, scene-surviving <see cref="LoaderRunner"/> the async loaders start
    /// their coroutines on. Same shape as <c>SmokeHost</c>, and deliberately a second object rather
    /// than a borrowed one: a loader that outlives a scene change must not be cancelled because the
    /// smoke runner was torn down, and vice versa.
    /// </summary>
    public static class LoaderHost
    {
        /// <summary>Name of the hidden host GameObject, for anyone reading a scene inspector.</summary>
        public const string RunnerObjectName = "CWY.LoaderHost";

        private static readonly object Gate = new object();

        private static LoaderRunner? _runner;

        /// <summary>
        /// Creates the runner once and returns it; later calls return the same instance. Returns
        /// null only when the GameObject could not be created, which is logged - a caller may treat
        /// null as "no coroutine can run here".
        /// </summary>
        public static LoaderRunner? EnsureRunner()
        {
            lock (Gate)
            {
                // Unity's == also catches a runner whose GameObject was destroyed, so a lost runner
                // is rebuilt instead of handed back dead.
                if (_runner != null)
                {
                    return _runner;
                }

                try
                {
                    var host = new GameObject(RunnerObjectName);
                    UnityEngine.Object.DontDestroyOnLoad(host);
                    host.hideFlags = HideFlags.HideAndDontSave;

                    _runner = host.AddComponent<LoaderRunner>();
                    CwyLsModLoader.Log.LogDebug($"loader host ready: {RunnerObjectName}");
                    return _runner;
                }
                catch (Exception ex)
                {
                    CwyLsModLoader.Log.LogError(
                        $"loader host could not be created: {ex.GetType().Name}: {ex.Message}");
                    return null;
                }
            }
        }

        /// <summary>
        /// Starts a coroutine on the shared runner.
        /// </summary>
        /// <param name="routine">The coroutine body.</param>
        /// <returns>The coroutine, or null when no host could be created (already logged).</returns>
        /// <exception cref="ArgumentNullException"><paramref name="routine"/> is null.</exception>
        public static Coroutine? Run(IEnumerator routine)
        {
            if (routine is null)
            {
                throw new ArgumentNullException(nameof(routine));
            }

            var runner = EnsureRunner();
            if (runner == null)
            {
                return null;
            }

            try
            {
                return runner.StartCoroutine(routine);
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogError($"loader coroutine could not start: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }
    }
}
