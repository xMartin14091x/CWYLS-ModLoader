using System;
using System.Collections.Generic;
using CwyLs.ModLoader.Core.Console;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace CwyLs.ModLoader.Bulbul.UI
{
    /// <summary>
    /// The game's UI measurements and colours, and the small helpers every game-style part is
    /// built with. Sizes are in canvas units at the game's 1920x1080 reference resolution.
    /// </summary>
    /// <remarks>
    /// Measured from the game's own windows in its RoomScene (v1.17.2): the note window
    /// <c>UI_FacilityNote</c> (title 28 pt at (97, -47), separator 4 px at y -65, close button
    /// 28x28 at (-24, -24), drag handle 24x16 top centre, 18 pt body text, placeholder at 61 %
    /// opacity) and the settings window's tabs (label 24 pt, <c>#6E6E93</c> unselected, white
    /// selected). Hover and press follow <c>InteractableUI</c> (overlay fades in over 0.15 s and
    /// out over 0.2 s) and <c>HoldButtonAnimation</c> (scale 1.05 on hover, 0.95 when pressed,
    /// over 0.1 s).
    /// </remarks>
    public static class GameStyleUi
    {
        /// <summary>Prefix of every log line the game-style parts write.</summary>
        public const string LogPrefix = "GAMEUI";

        /// <summary>The game's canvas reference resolution.</summary>
        public static readonly Vector2 ReferenceResolution = new Vector2(1920f, 1080f);

        /// <summary>Window title size (the note window's 28 pt).</summary>
        public const float TitleSize = 28f;

        /// <summary>Body text size (the note window's 18 pt).</summary>
        public const float BodySize = 18f;

        /// <summary>Tab and button label size.</summary>
        public const float LabelSize = 18f;

        /// <summary>Small print, e.g. a title-bar status.</summary>
        public const float SmallSize = 15f;

        /// <summary>Height of the title bar; the separator sits at its bottom (the note window's y -65).</summary>
        public const float HeaderHeight = 66f;

        /// <summary>Close button size.</summary>
        public const float CloseSize = 28f;

        /// <summary>Close button centre, from the top-right corner.</summary>
        public const float CloseInset = 24f;

        /// <summary>Separator bar thickness.</summary>
        public const float SeparatorThickness = 4f;

        /// <summary>Hover overlay fade-in time (InteractableUI.MouseOverActivateTime).</summary>
        public const float HoverInSeconds = 0.15f;

        /// <summary>Hover overlay fade-out time (InteractableUI.MouseOverDeactivateTime).</summary>
        public const float HoverOutSeconds = 0.2f;

        /// <summary>Scale animation time (HoldButtonAnimation).</summary>
        public const float ScaleSeconds = 0.1f;

        /// <summary>Scale under the pointer.</summary>
        public const float HoverScale = 1.05f;

        /// <summary>Scale while pressed.</summary>
        public const float PressScale = 0.95f;

        /// <summary>Window show/hide time (NoteUI.Activate / CommonWindowShowHideAnimation).</summary>
        public const float ShowSeconds = 0.2f;

        /// <summary>How far a window rises while it fades in (NoteUI: from y -70 to -62).</summary>
        public const float ShowRise = 8f;

        /// <summary>White text.</summary>
        public static readonly Color Text = Color.white;

        /// <summary>Placeholder text: white at the game's 61 %.</summary>
        public static readonly Color Placeholder = new Color(1f, 1f, 1f, 0.6117647f);

        /// <summary>Unselected tab label (settings tabs' <c>_manualDefaultColor</c>).</summary>
        public static readonly Color TabLabel = new Color(0.43137255f, 0.43137255f, 0.5764706f, 1f);

        /// <summary>The game's separator lavender.</summary>
        public static readonly Color Lavender = FromRgb(ConsoleLineColors.GameLavender, 1f);

        /// <summary>The game's hover cyan.</summary>
        public static readonly Color Cyan = FromRgb(ConsoleLineColors.GameCyan, 1f);

        /// <summary>Stand-in for the window background when its sprite is missing: the sprite's own 90 % black.</summary>
        public static readonly Color WindowFill = new Color(0f, 0f, 0f, 0.9f);

        /// <summary>Stand-in for the navy of buttons and tabs.</summary>
        public static readonly Color NavyFill = new Color(44f / 255f, 41f / 255f, 70f / 255f, 1f);

        /// <summary>Stand-in for the outlined text panel.</summary>
        public static readonly Color PanelFill = new Color(20f / 255f, 23f / 255f, 39f / 255f, 0.95f);

        /// <summary>A Unity colour from a palette colour.</summary>
        /// <param name="rgb">The colour.</param>
        /// <param name="alpha">Opacity, 0 to 1.</param>
        public static Color FromRgb(ConsoleRgb rgb, float alpha) =>
            new Color(rgb.R / 255f, rgb.G / 255f, rgb.B / 255f, alpha);

        /// <summary>
        /// Creates a child object with a RectTransform. Anchors and offsets are left for the
        /// caller; everything is created under an inactive root, so nothing wakes up half-built.
        /// </summary>
        /// <param name="parent">The parent.</param>
        /// <param name="name">Object name, for a scene inspector.</param>
        public static RectTransform Child(Transform parent, string name)
        {
            var go = new GameObject(name);
            go.layer = parent.gameObject.layer;
            var rect = go.AddComponent<RectTransform>();
            rect.SetParent(parent, false);
            return rect;
        }

        /// <summary>Stretches a rect over its parent, inset by the given margins.</summary>
        /// <param name="rect">The rect.</param>
        /// <param name="left">Left inset.</param>
        /// <param name="top">Top inset.</param>
        /// <param name="right">Right inset.</param>
        /// <param name="bottom">Bottom inset.</param>
        public static void Stretch(RectTransform rect, float left = 0f, float top = 0f, float right = 0f, float bottom = 0f)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.offsetMin = new Vector2(left, bottom);
            rect.offsetMax = new Vector2(-right, -top);
        }

        /// <summary>Places a rect of a fixed size against one anchor point of its parent.</summary>
        /// <param name="rect">The rect.</param>
        /// <param name="anchor">Anchor and pivot, e.g. (1, 1) for the top-right corner.</param>
        /// <param name="position">Offset from the anchor.</param>
        /// <param name="size">Size.</param>
        public static void Pin(RectTransform rect, Vector2 anchor, Vector2 position, Vector2 size)
        {
            rect.anchorMin = anchor;
            rect.anchorMax = anchor;
            rect.pivot = anchor;
            rect.anchoredPosition = position;
            rect.sizeDelta = size;
        }

        /// <summary>
        /// Adds an Image drawing a game sprite, or a plain tinted rectangle when the sprite is
        /// missing (an Image without a sprite draws its colour).
        /// </summary>
        /// <param name="rect">Where.</param>
        /// <param name="sprite">The game sprite, or null.</param>
        /// <param name="fallback">Colour to draw when the sprite is missing.</param>
        /// <param name="raycast">True when the image should catch the pointer.</param>
        public static Image AddImage(RectTransform rect, Sprite? sprite, Color fallback, bool raycast)
        {
            var image = rect.gameObject.AddComponent<Image>();
            image.raycastTarget = raycast;

            if (sprite == null)
            {
                image.sprite = null;
                image.color = fallback;
                return image;
            }

            image.sprite = sprite;
            image.color = Color.white;

            // A sprite with a 9-slice border is drawn sliced, as the game does; one without is
            // stretched (for the uniform window background that is what the game's tiling shows).
            image.type = sprite.border.sqrMagnitude > 0f ? Image.Type.Sliced : Image.Type.Simple;
            return image;
        }

        /// <summary>
        /// Adds a TextMeshPro label in the game's font. Called on an inactive hierarchy: a
        /// TextMeshProUGUI that wakes up without a font goes looking for TMP's default font asset,
        /// which this game does not ship (its <c>TMP Settings</c> asset sets no default font asset,
        /// game 1.17.2).
        /// </summary>
        /// <param name="rect">Where.</param>
        /// <param name="font">The game's font.</param>
        /// <param name="size">Point size.</param>
        /// <param name="color">Colour.</param>
        /// <param name="alignment">Alignment.</param>
        public static TextMeshProUGUI AddLabel(RectTransform rect, TMP_FontAsset font, float size, Color color, TextAlignmentOptions alignment)
        {
            var text = rect.gameObject.AddComponent<TextMeshProUGUI>();

            // Pair adjustments - and with them the Thai tone-mark records LanguageFonts installs - only
            // apply while kerning is on (TextMeshProUGUI). A component added to an inactive
            // object wakes up after fontSize is set below, so LoadDefaultSettings skips its defaults
            // (TMP_Text.LoadDefaultSettings) and kerning stays false: ที่ drew its tone mark
            // inside the vowel in every mod window (seen in testing; smoke s.ui.thai overlap 7).
            text.enableKerning = true;
            text.font = font;
            text.fontSize = size;
            text.color = color;
            text.alignment = alignment;
            text.raycastTarget = false;
            text.richText = false;
            text.enableWordWrapping = false;
            text.overflowMode = TextOverflowModes.Ellipsis;
            text.text = string.Empty;
            return text;
        }

        /// <summary>
        /// Moves <paramref name="current"/> towards <paramref name="target"/> at a rate that covers
        /// the whole 0-1 range in <paramref name="seconds"/>.
        /// </summary>
        /// <param name="current">Current value.</param>
        /// <param name="target">Target value.</param>
        /// <param name="seconds">Time for a full 0-1 change.</param>
        /// <param name="deltaTime">Frame time.</param>
        public static float Approach(float current, float target, float seconds, float deltaTime) =>
            seconds <= 0f ? target : Mathf.MoveTowards(current, target, deltaTime / seconds);

        /// <summary>Warns once per distinct message for the session.</summary>
        /// <param name="message">The message, without the prefix.</param>
        public static void WarnOnce(string message)
        {
            lock (Warned)
            {
                if (!Warned.Add(message))
                {
                    return;
                }
            }

            CwyLsModLoader.Log.LogWarning(LogPrefix + " " + message);
        }

        private static readonly HashSet<string> Warned = new HashSet<string>(StringComparer.Ordinal);
    }
}
