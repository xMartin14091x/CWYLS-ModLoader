using System;
using UnityEngine;

namespace CwyLs.ModLoader.Console
{
    /// <summary>
    /// The hidden, scene-surviving object the console overlay is drawn from.
    /// </summary>
    /// <remarks>
    /// Same reason as every other host in this framework: the game destroys BepInEx plugin
    /// components about 0.2 s after boot, so an
    /// <c>OnGUI</c> on the plugin would stop drawing almost immediately. The component is
    /// disabled unless the IMGUI surface is the one on screen, so a closed console - or one drawn
    /// by the game-style surface - costs the game no <c>OnGUI</c> call at all.
    /// </remarks>
    internal sealed class ConsoleHost : MonoBehaviour
    {
        /// <summary>Name of the hidden host GameObject, for anyone reading a scene inspector.</summary>
        internal const string HostObjectName = "CWY.ConsoleHost";

        /// <summary>Rebuilds attempted before we stop fighting whatever is destroying the host.</summary>
        private const int MaxRebuilds = 3;

        private static readonly object Gate = new object();

        private static ConsoleHost? _instance;
        private static bool _failed;
        private static bool _quitting;
        private static int _rebuilds;

        /// <summary>
        /// Creates the host once and returns it; later calls return the same instance. Returns
        /// null only when the GameObject could not be created, which is logged - the console then
        /// has no overlay and says so.
        /// </summary>
        internal static ConsoleHost? Ensure()
        {
            lock (Gate)
            {
                // Unity's == also reports a destroyed component as null, so a lost host is rebuilt.
                if (_instance != null || _failed || _quitting)
                {
                    return _instance;
                }

                try
                {
                    var host = new GameObject(HostObjectName);
                    DontDestroyOnLoad(host);
                    host.hideFlags = HideFlags.HideAndDontSave;

                    var component = host.AddComponent<ConsoleHost>();
                    component.enabled = ConsoleFeature.IsImguiShowing;
                    _instance = component;
                    return _instance;
                }
                catch (Exception ex)
                {
                    _failed = true;
                    CwyLsModLoader.Log.LogError(
                        $"{ConsoleFeature.LogPrefix}: no overlay, the host could not be created: "
                        + $"{ex.GetType().Name}: {ex.Message}");
                    return null;
                }
            }
        }

        /// <summary>
        /// Turns drawing on or off. A disabled component gets no <c>OnGUI</c> at all, which is
        /// what makes a closed console free.
        /// </summary>
        /// <param name="drawing">True while the overlay is open.</param>
        internal static void SetDrawing(bool drawing)
        {
            var host = Ensure();
            if (host == null)
            {
                return;
            }

            try
            {
                host.enabled = drawing;
            }
            catch (Exception ex)
            {
                // A destroyed component throws on the property; the next Ensure rebuilds it.
                CwyLsModLoader.Log.LogWarning(
                    $"{ConsoleFeature.LogPrefix}: could not {(drawing ? "show" : "hide")} the overlay: "
                    + $"{ex.GetType().Name}: {ex.Message}");
            }
        }

        private void Awake() => Application.quitting += MarkQuitting;

        private void OnGUI()
        {
            // Open is not enough: when the game-style surface is on screen, IMGUI must not draw
            // a second console on top of it.
            if (!ConsoleFeature.IsImguiShowing)
            {
                return;
            }

            try
            {
                ConsoleWindow.Draw();
            }
            catch (Exception ex)
            {
                // IMGUI calls this several times a frame; a throwing overlay would bury the log
                // and could stop the game drawing, so it closes itself and says why, once.
                ConsoleFeature.Close();
                CwyLsModLoader.Log.LogError(
                    $"{ConsoleFeature.LogPrefix}: the overlay threw and was closed: "
                    + $"{ex.GetType().Name}: {ex.Message}");
            }
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
                        $"{ConsoleFeature.LogPrefix}: the host could not be kept alive after "
                        + $"{MaxRebuilds} rebuilds; the overlay is gone for this session");
                }
            }

            if (rebuild)
            {
                CwyLsModLoader.Log.LogWarning(
                    $"{ConsoleFeature.LogPrefix}: the host was destroyed (reason unknown); rebuilding");
                Ensure();
                SetDrawing(ConsoleFeature.IsImguiShowing);
            }
        }
    }
}
