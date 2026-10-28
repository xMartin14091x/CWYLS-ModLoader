using System;
using TMPro;
using UnityEngine;
using CwyLs.ModLoader.Core.Console;

namespace CwyLs.ModLoader.Bulbul.UI
{
    /// <summary>
    /// A pill button in the game's style: the navy <c>t_ui_listchange</c> pill with a white label,
    /// the cyan/magenta outline fading in under the pointer, and the game's grow-on-hover,
    /// shrink-on-press scale.
    /// </summary>
    public sealed class GameStyleButton
    {
        /// <summary>Narrowest button: the game's resting pill (<c>t_ui_listchange</c>, 76x40).</summary>
        public const float MinWidth = 76f;

        /// <summary>Label inset left and right.</summary>
        private const float LabelPadding = 16f;

        private readonly TextMeshProUGUI _label;

        private GameStyleButton(RectTransform rect, TextMeshProUGUI label, GameStyleClickable clickable)
        {
            Rect = rect;
            _label = label;
            clickable.Clicked += () => Clicked?.Invoke();
        }

        /// <summary>Raised on a left click.</summary>
        public event Action? Clicked;

        /// <summary>The button's rect; the caller places and sizes it.</summary>
        public RectTransform Rect { get; }

        /// <summary>The label.</summary>
        public string Label
        {
            get => _label.text;
            set
            {
                var text = value ?? string.Empty;
                if (!string.Equals(_label.text, text, StringComparison.Ordinal))
                {
                    _label.text = text;
                }
            }
        }

        /// <summary>The width the label needs plus the pill's padding, at least the game's 76-unit pill (mod-window rows).</summary>
        public float PreferredWidth
        {
            get
            {
                var label = _label.GetPreferredValues(_label.text, 100000f, 100000f).x;
                if (float.IsNaN(label) || label < 0f)
                {
                    label = 0f;
                }

                return Mathf.Max(MinWidth, label + (2f * LabelPadding) + 4f);
            }
        }

        /// <summary>Shows or hides the button.</summary>
        public bool Visible
        {
            get => Rect.gameObject.activeSelf;
            set
            {
                if (Rect.gameObject.activeSelf != value)
                {
                    Rect.gameObject.SetActive(value);
                }
            }
        }

        /// <summary>Builds a button under <paramref name="parent"/>. The caller sets its anchors and size.</summary>
        /// <param name="parent">Where the button goes.</param>
        /// <param name="assets">The game's UI assets.</param>
        /// <param name="label">Button text.</param>
        internal static GameStyleButton Create(RectTransform parent, GameStyleAssets assets, string label) =>
            Create(parent, assets, label, GameStyleUi.LabelSize);

        /// <summary>Builds a button with a smaller or larger label (bubble buttons use 15 pt).</summary>
        /// <param name="parent">Where the button goes.</param>
        /// <param name="assets">The game's UI assets.</param>
        /// <param name="label">Button text.</param>
        /// <param name="fontSize">Label size in points.</param>
        internal static GameStyleButton Create(RectTransform parent, GameStyleAssets assets, string label, float fontSize)
        {
            var rect = GameStyleUi.Child(parent, "Button " + label);
            rect.gameObject.SetActive(false);

            GameStyleUi.AddImage(rect, assets.SpriteNamed(GameStyleAssetSet.Button), GameStyleUi.NavyFill, raycast: true);

            // The hover sprite is 4 px larger than the resting one (80x44 over 76x40).
            var hover = GameStyleUi.Child(rect, "Hover");
            GameStyleUi.Stretch(hover, -2f, -2f, -2f, -2f);
            var hoverImage = GameStyleUi.AddImage(
                hover, assets.SpriteNamed(GameStyleAssetSet.ButtonHover), new Color(1f, 1f, 1f, 0.12f), raycast: false);

            var text = GameStyleUi.Child(rect, "Label");
            GameStyleUi.Stretch(text, LabelPadding, 0f, LabelPadding, 0f);
            var labelText = GameStyleUi.AddLabel(text, assets.Font, fontSize, GameStyleUi.Text, TextAlignmentOptions.Center);
            labelText.text = label;

            var clickable = rect.gameObject.AddComponent<GameStyleClickable>();
            clickable.Setup(hoverImage, rect);

            rect.gameObject.SetActive(true);
            return new GameStyleButton(rect, labelText, clickable);
        }
    }
}
