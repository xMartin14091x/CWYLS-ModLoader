using System;
using UnityEngine;
using CwyLs.ModLoader.Core.Input;
using CwyLs.ModLoader.Core.Logging;

namespace CwyLs.ModLoader.Input
{
    /// <summary>
    /// The hidden, scene-surviving object that polls <see cref="Hotkeys"/> once a frame.
    /// </summary>
    /// <remarks>
    /// Same reason as every other host in this framework: the game destroys BepInEx plugin
    /// components about 0.2 s after boot, so an <c>Update</c> on a plugin would stop working
    /// almost immediately. If something destroys this object too,
    /// it rebuilds itself a bounded number of times rather than leaving every key dead.
    /// </remarks>
    internal sealed class HotkeyHost : MonoBehaviour
    {
        /// <summary>Name of the hidden host GameObject, for anyone reading a scene inspector.</summary>
        internal const string HostObjectName = "CWY.HotkeyHost";

        /// <summary>Rebuilds attempted before we stop fighting whatever is destroying the host.</summary>
        private const int MaxRebuilds = 3;

        private static readonly object Gate = new object();

        private static HotkeyHost? _instance;
        private static bool _failed;
        private static bool _quitting;
        private static int _rebuilds;

        /// <summary>
        /// Creates the host once. Called by every <see cref="Hotkeys.Register"/>, so the first
        /// registration arms polling and the rest are free.
        /// </summary>
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
                    _instance = host.AddComponent<HotkeyHost>();
                }
                catch (Exception ex)
                {
                    // Without a host no key works; say so once rather than every registration.
                    _failed = true;
                    CwyLsModLoader.Log.LogError(
                        $"hotkeys are not polled: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        private void Awake() => Application.quitting += MarkQuitting;

        private void Update()
        {
            // First frame: every plugin's Awake has run, so the summary is complete.
            if (Hotkeys.TryMarkAnnounced())
            {
                // The table itself is the primary column: "smoke.run = Ctrl+Shift+S | F9, ...".
                var line = Hotkeys.StartupLine();
                ConsoleEvents.Emit(
                    "keys",
                    "table",
                    ConsoleTags.Keys,
                    line.StartsWith(HotkeyInfo.LinePrefix, StringComparison.Ordinal)
                        ? line.Substring(HotkeyInfo.LinePrefix.Length)
                        : line,
                    "count",
                    ("count", Hotkeys.List().Count));
            }

            Hotkeys.Poll(Time.frameCount);
        }

        private static void MarkQuitting() => _quitting = true;

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
                    CwyLsModLoader.Log.LogWarning(
                        $"the hotkey host could not be kept alive after {MaxRebuilds} rebuilds; keys are now dead");
                }
            }

            if (rebuild)
            {
                CwyLsModLoader.Log.LogWarning("the hotkey host was destroyed (reason unknown); rebuilding");
                Ensure();
            }
        }
    }
}
