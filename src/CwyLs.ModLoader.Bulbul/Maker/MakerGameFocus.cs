using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using BepInEx.Configuration;
using CwyLs.ModLoader.Core.Maker;
using UnityEngine;

namespace CwyLs.ModLoader.Bulbul.Maker
{
    /// <summary>
    /// Brings the game to the front when Pack Maker plays a line in it (Martin, 2026-11-07): the creator clicks
    /// "Play … in game" in the browser and then watches the game, which runs at its full frame rate only while focused.
    /// A web page cannot focus another app, so the game asks for itself. macOS: <c>open -a</c> on the game's own app
    /// bundle (activates the running copy). Windows: find and raise the game's window, best effort.
    /// Switch: <c>[Maker] FocusGameOnPlay</c>.
    /// </summary>
    internal static class MakerGameFocus
    {
        internal const string ConfigKey = "FocusGameOnPlay";

        private static ConfigEntry<bool>? _enabled;
        private static string? _macBundle;
        private static bool _windows;
        private static readonly object WindowsFocusLock = new object();

        /// <summary>The cached configuration value shown by Studio status. Reading it does not touch Unity.</summary>
        internal static bool IsEnabled => _enabled?.Value ?? true;

        /// <summary>Main thread (reads Unity's paths once).</summary>
        internal static void Initialize(ConfigFile? config)
        {
            _enabled = config?.Bind(
                MakerDubHold.ConfigSection,
                ConfigKey,
                true,
                "Bring the game to the front when Pack Maker plays a line in it.");
            try
            {
                _windows = Application.platform == RuntimePlatform.WindowsPlayer;
                if (Application.platform == RuntimePlatform.OSXPlayer)
                {
                    // dataPath is ".../Chill With You.app/Contents" in a macOS player.
                    var contents = Application.dataPath;
                    var bundle = Directory.GetParent(contents)?.FullName;
                    _macBundle = bundle != null && bundle.EndsWith(".app", StringComparison.OrdinalIgnoreCase) ? bundle : null;
                }
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogWarning(MakerEditor.LogPrefix + " focus on play: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>Asks the OS to bring the game forward. Any thread; never throws; never blocks the caller.</summary>
        internal static void Bring()
        {
            if (_enabled != null && !_enabled.Value)
            {
                return;
            }

            Task.Run(() =>
            {
                try
                {
                    if (_macBundle != null)
                    {
                        using var open = Process.Start(new ProcessStartInfo("/usr/bin/open", "-a \"" + _macBundle + "\"")
                        {
                            UseShellExecute = false,
                            CreateNoWindow = true,
                        });
                    }
                    else if (_windows)
                    {
                        lock (WindowsFocusLock) BringWindows();
                    }
                }
                catch (Exception ex)
                {
                    CwyLsModLoader.Log.LogWarning(MakerEditor.LogPrefix + " focus on play: " + ex.GetType().Name + ": " + ex.Message);
                }
            });
        }

        private static void BringWindows()
        {
            var handle = IntPtr.Zero;
            var step = "find";
            var foreground = false;
            try
            {
                using var process = Process.GetCurrentProcess();
                var candidates = new List<WindowCandidate>();
                EnumWindows((window, _) =>
                {
                    GetWindowThreadProcessId(window, out var owner);
                    if (owner == (uint)process.Id && IsWindowVisible(window))
                    {
                        var name = new StringBuilder(256);
                        GetClassName(window, name, name.Capacity);
                        candidates.Add(new WindowCandidate(window, name.ToString() == "UnityWndClass"));
                    }
                    return true;
                }, IntPtr.Zero);
                handle = GameWindowChoice.Choose(candidates, IntPtr.Zero);
                if (handle == IntPtr.Zero) handle = process.MainWindowHandle;
                if (handle == IntPtr.Zero) return;

                step = "restore";
                if (IsIconic(handle)) ShowWindow(handle, 9); // SW_RESTORE
                step = "raise";
                // Always undo TOPMOST, including when a native call fails or throws.
                try { SetWindowPos(handle, new IntPtr(-1), 0, 0, 0, 0, 0x0003); }
                finally { SetWindowPos(handle, new IntPtr(-2), 0, 0, 0, 0, 0x0003); }

                step = "foreground";
                var foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
                var currentThread = GetCurrentThreadId();
                var attached = false;
                try
                {
                    if (foregroundThread != 0 && foregroundThread != currentThread)
                        attached = AttachThreadInput(currentThread, foregroundThread, true);
                    BringWindowToTop(handle);
                    SetForegroundWindow(handle);
                    foreground = GetForegroundWindow() == handle;
                    step = attached ? "foreground-attached" : "foreground-unattached";
                }
                finally
                {
                    if (attached) AttachThreadInput(currentThread, foregroundThread, false);
                }
            }
            catch (Exception)
            {
                step += "-error";
            }
            finally
            {
                CwyLsModLoader.Log.LogInfo(MakerEditor.LogPrefix + " focus on play: found=" +
                    (handle != IntPtr.Zero) + " foreground=" + foreground + " step=" + step);
            }
        }

        private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);
        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr window, StringBuilder className, int maxCount);
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")]
        private static extern bool AttachThreadInput(uint from, uint to, bool attach);
        [DllImport("user32.dll")]
        private static extern bool BringWindowToTop(IntPtr window);
        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    }
}
