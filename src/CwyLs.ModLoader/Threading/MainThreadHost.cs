using System;
using UnityEngine;

namespace CwyLs.ModLoader.Threading
{
    /// <summary>
    /// The hidden, scene-surviving object that pumps <see cref="MainThread"/> once a frame.
    /// </summary>
    /// <remarks>
    /// Same reason as every host in this framework: the game destroys BepInEx plugin components
    /// about 0.2 s after boot, so an <c>Update</c> on a plugin stops
    /// almost at once. If something destroys this object, it is rebuilt a bounded number of times,
    /// like <c>HotkeyHost</c>, rather than leaving every queued call and every mod tick dead.
    /// </remarks>
    internal sealed class MainThreadHost : MonoBehaviour
    {
        /// <summary>Name of the hidden host GameObject, for anyone reading a scene inspector.</summary>
        internal const string HostObjectName = "CWY.MainThread";

        /// <summary>Rebuilds attempted before we stop fighting whatever is destroying the host.</summary>
        private const int MaxRebuilds = 3;

        private static readonly object Gate = new object();

        private static MainThreadHost? _instance;
        private static bool _failed;
        private static bool _quitting;
        private static bool _warnedNotPumping;
        private static int _rebuilds;

        /// <summary>True while the host exists and pumps.</summary>
        internal static bool IsAlive
        {
            get
            {
                lock (Gate)
                {
                    // Reference check only: Unity's overloaded == must not run off the main thread.
                    return !ReferenceEquals(_instance, null);
                }
            }
        }

        /// <summary>Creates the host once. Main thread only.</summary>
        internal static void Ensure()
        {
            lock (Gate)
            {
                // Unity's == also reports a destroyed component as null, so a lost host is rebuilt.
                if (_instance != null || _failed || _quitting)
                {
                    return;
                }

                try
                {
                    var host = new GameObject(HostObjectName);
                    DontDestroyOnLoad(host);
                    host.hideFlags = HideFlags.HideAndDontSave;
                    _instance = host.AddComponent<MainThreadHost>();
                    CwyLsModLoader.Log.LogDebug(
                        "MAINTHREAD host armed (" + HostObjectName + ", budget " + MainThread.FrameBudgetMs + " ms/frame)");
                }
                catch (Exception ex)
                {
                    _failed = true;
                    CwyLsModLoader.Log.LogError(
                        "MAINTHREAD host could not be created, so nothing posted to the main thread will run " +
                        "and no mod will load: " + ex.GetType().Name + ": " + ex.Message);
                }
            }
        }

        /// <summary>Says once that work is waiting for a host that does not exist.</summary>
        /// <param name="label">The work that just arrived.</param>
        internal static void WarnNotPumping(string label)
        {
            lock (Gate)
            {
                if (_warnedNotPumping || _quitting)
                {
                    return;
                }

                _warnedNotPumping = true;
            }

            CwyLsModLoader.Log.LogWarning(
                "MAINTHREAD '" + label + "' was queued but the dispatcher host is not running; it runs once the host " +
                "exists (MainThread.Arm is called from ModLoaderPlugin.Awake)");
        }

        private void Awake() => Application.quitting += MarkQuitting;

        private void Update() => MainThread.PumpFrame();

        private static void MarkQuitting()
        {
            lock (Gate)
            {
                _quitting = true;
            }
        }

        private void OnDestroy()
        {
            Application.quitting -= MarkQuitting;

            bool rebuild;
            lock (Gate)
            {
                if (ReferenceEquals(_instance, this))
                {
                    _instance = null;
                }

                rebuild = !_quitting && !_failed && ++_rebuilds <= MaxRebuilds;
                if (!rebuild && !_quitting && !_failed)
                {
                    _failed = true;
                    CwyLsModLoader.Log.LogError(
                        "MAINTHREAD host could not be kept alive after " + MaxRebuilds +
                        " rebuilds; queued work and mod ticks have stopped");
                }
            }

            if (rebuild)
            {
                CwyLsModLoader.Log.LogWarning("MAINTHREAD host was destroyed (reason unknown); rebuilding");
                Ensure();
            }
        }
    }
}
