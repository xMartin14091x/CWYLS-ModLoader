using System;
using UnityEngine;
using UnityEngine.UI;

namespace CwyLs.ModLoader.Bulbul.UI
{
    /// <summary>
    /// The one overlay canvas every game-style window lives on: above all of the game's UI,
    /// scaled exactly like the game's own canvas, and kept across scene loads.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Sorting: the game's highest canvas in <c>RoomScene</c> sorts at 999 and its dialogs at
    /// 100-120. Windows here sort at <see cref="SortingOrder"/>,
    /// so a mod window is never covered and its clicks are never taken by the game's UI below it
    /// (the EventSystem ranks raycast hits by canvas sorting order).
    /// </para>
    /// <para>
    /// Created on first use rather than at boot, under <c>DontDestroyOnLoad</c> like every other
    /// framework host (the game destroys plugin components about 0.2 s after boot). A canvas that something destroyed is rebuilt on the next request.
    /// </para>
    /// </remarks>
    public static class GameStyleCanvas
    {
        /// <summary>Name of the canvas object, for a scene inspector.</summary>
        public const string ObjectName = "CWY.GameStyleCanvas";

        /// <summary>
        /// Sorting order: well above the game's UI (max 999). The value dates from the retired
        /// subtitle overlay (32000, since removed); it is kept so nothing else moves.
        /// </summary>
        public const int SortingOrder = 32600;

        private static GameObject? _root;
        private static Canvas? _canvas;
        private static CanvasScaler? _scaler;

        /// <summary>
        /// Raised once a frame in <c>LateUpdate</c> - after every <c>Update</c>, so after the game and the
        /// console have read the frame's keys - while the canvas exists (used by the mod-window manager).
        /// A handler that throws is logged once per message and the others still run.
        /// </summary>
        public static event Action? LateTick;

        /// <summary>The canvas's RectTransform, or null when it has not been built.</summary>
        public static RectTransform? Root => _root == null ? null : _root.transform as RectTransform;

        /// <summary>The canvas's current scale factor (screen pixels per canvas unit); 1 before it exists.</summary>
        public static float ScaleFactor => _canvas == null ? 1f : Mathf.Max(0.0001f, _canvas.scaleFactor);

        /// <summary>The canvas's size in canvas units; the reference resolution before it exists.</summary>
        public static Vector2 Size
        {
            get
            {
                var root = Root;
                return root == null ? GameStyleUi.ReferenceResolution : root.rect.size;
            }
        }

        /// <summary>Builds the canvas if needed and returns its RectTransform.</summary>
        /// <param name="problem">Empty on success; why it could not be built otherwise.</param>
        /// <returns>The canvas root, or null when it could not be built.</returns>
        public static RectTransform? Ensure(out string problem)
        {
            problem = string.Empty;
            if (_root != null)
            {
                return Root;
            }

            try
            {
                var go = new GameObject(ObjectName);
                go.layer = 5;   // Unity's built-in "UI" layer, which the game's canvases use too.
                UnityEngine.Object.DontDestroyOnLoad(go);

                // DontSave rather than HideAndDontSave, so it can be found in a scene inspector
                // and is still never written into a scene.
                go.hideFlags = HideFlags.DontSave;
                go.AddComponent<RectTransform>();

                var canvas = go.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = SortingOrder;

                // TextMeshPro's UI shaders read these vertex channels; the game's canvas sets the
                // same three (m_AdditionalShaderChannelsFlag 25).
                canvas.additionalShaderChannels = AdditionalCanvasShaderChannels.TexCoord1
                    | AdditionalCanvasShaderChannels.Normal
                    | AdditionalCanvasShaderChannels.Tangent;

                var scaler = go.AddComponent<CanvasScaler>();
                GameUiBridge.FollowGameScaling(scaler);

                go.AddComponent<GraphicRaycaster>();
                go.AddComponent<CanvasDriver>();

                _root = go;
                _canvas = canvas;
                _scaler = scaler;
                return Root;
            }
            catch (Exception ex)
            {
                problem = "the overlay canvas could not be built: " + ex.GetType().Name + ": " + ex.Message;
                CwyLsModLoader.Log.LogError(GameStyleUi.LogPrefix + " " + problem);
                if (_root != null)
                {
                    UnityEngine.Object.Destroy(_root);
                }

                _root = null;
                _canvas = null;
                _scaler = null;
                return null;
            }
        }

        private static void RaiseLateTick()
        {
            var handlers = LateTick;
            if (handlers == null)
            {
                return;
            }

            foreach (var handler in handlers.GetInvocationList())
            {
                try
                {
                    ((Action)handler)();
                }
                catch (Exception ex)
                {
                    GameStyleUi.WarnOnce("a canvas frame handler threw: " + ex.GetType().Name + ": " + ex.Message);
                }
            }
        }

        /// <summary>Re-reads the game's canvas scaling. Called once a frame by every visible window.</summary>
        public static void FollowGameScaling()
        {
            if (_scaler != null)
            {
                GameUiBridge.FollowGameScaling(_scaler);
            }
        }

        /// <summary>Forwards Unity's <c>LateUpdate</c> to <see cref="LateTick"/>.</summary>
        private sealed class CanvasDriver : MonoBehaviour
        {
            private void LateUpdate() => RaiseLateTick();
        }
    }
}
