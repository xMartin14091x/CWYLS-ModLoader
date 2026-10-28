using System;
using System.Collections.Generic;
using CwyLs.ModLoader.Core.Console;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace CwyLs.ModLoader.Bulbul.UI
{
    /// <summary>
    /// A row of tabs in the style of the game's settings window: dimmed tab plates with muted
    /// labels, the outlined hover plate under the pointer, and the bright plate with a white
    /// label for the selected tab.
    /// </summary>
    public sealed class GameStyleTabStrip
    {
        /// <summary>Widest a tab gets; narrower when many tabs share the row.</summary>
        public const float MaxTabWidth = 168f;

        /// <summary>Gap between tabs.</summary>
        public const float Spacing = 6f;

        /// <summary>Opacity of a resting tab plate (the settings tabs' 0.66).</summary>
        private const float RestingAlpha = 0.6588235f;

        private readonly GameStyleAssets _assets;
        private readonly List<Tab> _tabs = new List<Tab>();
        private string[] _labels = Array.Empty<string>();
        private int _selected = -1;
        private float _laidOutWidth = -1f;

        private GameStyleTabStrip(RectTransform rect, GameStyleAssets assets)
        {
            Rect = rect;
            _assets = assets;
        }

        /// <summary>Raised with the index of a clicked tab.</summary>
        public event Action<int>? TabClicked;

        /// <summary>The strip's rect; the caller places and sizes it.</summary>
        public RectTransform Rect { get; }

        /// <summary>The selected tab, or -1.</summary>
        public int Selected => _selected;

        /// <summary>
        /// Shows these tabs with this one selected. Rebuilds only when the labels changed, so it is
        /// cheap to call every frame.
        /// </summary>
        /// <param name="labels">Tab labels, left to right.</param>
        /// <param name="selected">Index of the selected tab.</param>
        public void SetTabs(IReadOnlyList<string> labels, int selected)
        {
            if (!SameLabels(labels))
            {
                Rebuild(labels);
                _selected = -1;
            }

            if (selected != _selected)
            {
                _selected = selected;
                for (var i = 0; i < _tabs.Count; i++)
                {
                    _tabs[i].SetSelected(i == selected);
                }
            }

            Layout();
        }

        /// <summary>Re-places the tabs when the strip's width changed. Cheap when it did not.</summary>
        public void Layout()
        {
            var width = Rect.rect.width;
            if (Mathf.Approximately(width, _laidOutWidth) || _tabs.Count == 0)
            {
                return;
            }

            _laidOutWidth = width;
            var tabWidth = Mathf.Min(MaxTabWidth, (width - (Spacing * (_tabs.Count - 1))) / _tabs.Count);
            tabWidth = Mathf.Max(24f, tabWidth);

            for (var i = 0; i < _tabs.Count; i++)
            {
                var rect = _tabs[i].Rect;
                rect.anchorMin = new Vector2(0f, 0f);
                rect.anchorMax = new Vector2(0f, 1f);
                rect.pivot = new Vector2(0f, 0.5f);
                rect.anchoredPosition = new Vector2(i * (tabWidth + Spacing), 0f);
                rect.sizeDelta = new Vector2(tabWidth, 0f);
            }
        }

        /// <summary>Builds an empty strip under <paramref name="parent"/>. The caller sets its anchors and size.</summary>
        /// <param name="parent">Where the strip goes.</param>
        /// <param name="assets">The game's UI assets.</param>
        internal static GameStyleTabStrip Create(RectTransform parent, GameStyleAssets assets)
        {
            var rect = GameStyleUi.Child(parent, "Tabs");
            return new GameStyleTabStrip(rect, assets);
        }

        private bool SameLabels(IReadOnlyList<string> labels)
        {
            if (labels.Count != _labels.Length)
            {
                return false;
            }

            for (var i = 0; i < labels.Count; i++)
            {
                if (!string.Equals(labels[i], _labels[i], StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        private void Rebuild(IReadOnlyList<string> labels)
        {
            foreach (var tab in _tabs)
            {
                UnityEngine.Object.Destroy(tab.Rect.gameObject);
            }

            _tabs.Clear();
            _labels = new string[labels.Count];

            for (var i = 0; i < labels.Count; i++)
            {
                _labels[i] = labels[i];
                var index = i;
                var tab = Tab.Create(Rect, _assets, labels[i]);
                tab.Clicked += () => TabClicked?.Invoke(index);
                _tabs.Add(tab);
            }

            _laidOutWidth = -1f;
        }

        private sealed class Tab
        {
            private readonly Image _active;
            private readonly TextMeshProUGUI _label;

            private Tab(RectTransform rect, Image active, TextMeshProUGUI label, GameStyleClickable clickable)
            {
                Rect = rect;
                _active = active;
                _label = label;
                clickable.Clicked += () => Clicked?.Invoke();
            }

            public event Action? Clicked;

            public RectTransform Rect { get; }

            public static Tab Create(RectTransform parent, GameStyleAssets assets, string label)
            {
                var rect = GameStyleUi.Child(parent, "Tab " + label);
                rect.gameObject.SetActive(false);

                var plate = GameStyleUi.AddImage(rect, assets.SpriteNamed(GameStyleAssetSet.Tab), GameStyleUi.NavyFill, raycast: true);
                var plateColor = plate.color;
                plateColor.a *= RestingAlpha;
                plate.color = plateColor;

                var hover = GameStyleUi.Child(rect, "Hover");
                GameStyleUi.Stretch(hover);
                var hoverImage = GameStyleUi.AddImage(
                    hover, assets.SpriteNamed(GameStyleAssetSet.TabHover), new Color(1f, 1f, 1f, 0.1f), raycast: false);

                var active = GameStyleUi.Child(rect, "Active");
                GameStyleUi.Stretch(active);
                var activeImage = GameStyleUi.AddImage(
                    active, assets.SpriteNamed(GameStyleAssetSet.TabActive), GameStyleUi.NavyFill, raycast: false);
                activeImage.enabled = false;

                var text = GameStyleUi.Child(rect, "Label");
                GameStyleUi.Stretch(text, 8f, 0f, 8f, 0f);
                var labelText = GameStyleUi.AddLabel(text, assets.Font, GameStyleUi.LabelSize, GameStyleUi.TabLabel, TextAlignmentOptions.Center);
                labelText.text = label;

                var clickable = rect.gameObject.AddComponent<GameStyleClickable>();
                clickable.Setup(hoverImage, null);

                rect.gameObject.SetActive(true);
                return new Tab(rect, activeImage, labelText, clickable);
            }

            public void SetSelected(bool selected)
            {
                _active.enabled = selected;
                _label.color = selected ? GameStyleUi.Text : GameStyleUi.TabLabel;
            }
        }
    }
}
