using System;
using System.Runtime.CompilerServices;
using UnityEngine;
using UnityEngine.UI;

namespace CwyLs.ModLoader.Bulbul.UI
{
    /// <summary>
    /// What a game-style window reads from the game's own UI: whether the player has hidden it,
    /// and how the game scales its canvas. The only place in the toolkit that names a game type.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both come from <c>Bulbul.UICanvasProvider</c>, the component on the game's <c>Canvas/UI</c>
    /// object. The PC Hide UI button fades <c>UIParentCanvasGroup</c> to 0 and deactivates
    /// <c>UIParent</c> (<c>UIShowManagerForPC.DeactivateUIParent</c>); the canvas above it carries
    /// the game's CanvasScaler (ScaleWithScreenSize, 1920x1080) whose match value
    /// <c>Games.UI.CanvasScalerAdjustor</c> moves with the aspect ratio every frame.
    /// </para>
    /// <para>
    /// Lookups are throttled to once a second while nothing is found (the provider exists only in
    /// <c>RoomScene</c>), and every game-type access sits in a non-inlined method, so a game update
    /// that removes the type costs a logged warning and "UI shown, default scaling", never an
    /// exception at the caller.
    /// </para>
    /// </remarks>
    public static class GameUiBridge
    {
        private const float ProbeIntervalSeconds = 1f;

        private static Component? _provider;
        private static CanvasScaler? _scaler;
        private static float _nextProbe;
        private static bool _broken;

        /// <summary>
        /// How visible the game's UI is, 0 (hidden) to 1 (shown). 1 when the game's UI cannot be
        /// found (no room loaded, or the type is gone), so an unknown state never hides anything.
        /// </summary>
        public static float GameUiAlpha()
        {
            if (_broken)
            {
                return 1f;
            }

            try
            {
                return ReadAlpha();
            }
            catch (Exception ex)
            {
                _broken = true;
                GameStyleUi.WarnOnce(
                    "could not read the game's UI state, so game-style windows will not follow Hide UI: "
                    + ex.GetType().Name + ": " + ex.Message);
                return 1f;
            }
        }

        /// <summary>
        /// Copies the game canvas's scaling onto <paramref name="target"/>, or sets the game's
        /// defaults when the game canvas is not found. Cheap enough for every frame.
        /// </summary>
        /// <param name="target">Our canvas's scaler.</param>
        public static void FollowGameScaling(CanvasScaler target)
        {
            var source = _broken ? null : SafeScaler();

            target.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            if (source == null)
            {
                target.referenceResolution = GameStyleUi.ReferenceResolution;
                target.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;

                // What CanvasScalerAdjustor settles on for any aspect wider than 3:2.
                target.matchWidthOrHeight = 1f;
                return;
            }

            target.referenceResolution = source.referenceResolution;
            target.screenMatchMode = source.screenMatchMode;
            target.matchWidthOrHeight = source.matchWidthOrHeight;
            target.referencePixelsPerUnit = source.referencePixelsPerUnit;
        }

        private static CanvasScaler? SafeScaler()
        {
            try
            {
                if (_scaler != null)
                {
                    return _scaler;
                }

                var provider = Provider();
                if (provider == null)
                {
                    return null;
                }

                _scaler = provider.GetComponentInParent<CanvasScaler>(true);
                return _scaler;
            }
            catch (Exception ex)
            {
                _broken = true;
                GameStyleUi.WarnOnce(
                    "could not read the game's canvas scaling, so game-style windows use 1920x1080: "
                    + ex.GetType().Name + ": " + ex.Message);
                return null;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static float ReadAlpha()
        {
            var provider = Provider() as global::Bulbul.UICanvasProvider;
            if (provider == null)
            {
                return 1f;
            }

            var parent = provider.UIParent;
            if (parent == null)
            {
                return 1f;
            }

            if (!parent.activeInHierarchy)
            {
                return 0f;
            }

            var group = provider.UIParentCanvasGroup;
            return group == null ? 1f : Mathf.Clamp01(group.alpha);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Component? Provider()
        {
            // Unity's == reports a destroyed component (a reloaded room) as null.
            if (_provider != null)
            {
                return _provider;
            }

            var now = Time.unscaledTime;
            if (now < _nextProbe)
            {
                return null;
            }

            _nextProbe = now + ProbeIntervalSeconds;
            _scaler = null;

            // Include inactive objects: the provider's own object is what Hide UI deactivates.
            var found = UnityEngine.Object.FindObjectsOfType<global::Bulbul.UICanvasProvider>(true);
            _provider = found.Length > 0 ? found[0] : null;
            return _provider;
        }
    }
}
