using System;
using System.Collections.Generic;
using CwyLs.ModLoader.Core.Console;
using CwyLs.ModLoader.Core.Ui;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace CwyLs.ModLoader.Bulbul.UI
{
    /// <summary>
    /// A scrolling list of one-line items in the game's style: the outlined dark panel, rows
    /// that light up under the pointer like the game's list entries, the selected row in the game's
    /// cyan, the game's scrollbar and wheel scrolling. Only the rows in view exist (a small pool), so
    /// a long list costs nothing extra.
    /// </summary>
    /// <remarks>Main thread only. Call <see cref="Refresh"/> once a frame while shown.</remarks>
    public sealed class GameStyleListView
    {
        /// <summary>Row height in canvas units.</summary>
        public const float RowHeight = 36f;

        /// <summary>Wheel notch in rows.</summary>
        public const float RowsPerNotch = 3f;

        private static readonly Color SelectedFill = new Color(GameStyleUi.Cyan.r, GameStyleUi.Cyan.g, GameStyleUi.Cyan.b, 0.22f);

        private readonly GameStyleAssets _assets;
        private readonly RectTransform _viewport;
        private readonly Scrollbar _scrollbar;
        private readonly List<Row> _rows = new List<Row>();
        private readonly ScrollState _scroll = new ScrollState(followEnd: false);
        private string[] _items = Array.Empty<string>();
        private int _selected = -1;
        private bool _dirty = true;
        private bool _suppressScrollbar;
        private float _viewportHeight = -1f;

        private GameStyleListView(RectTransform rect, RectTransform viewport, Scrollbar scrollbar, GameStyleScrollArea wheel, GameStyleAssets assets)
        {
            Rect = rect;
            _viewport = viewport;
            _scrollbar = scrollbar;
            _assets = assets;

            wheel.Scrolled += delta =>
            {
                _scroll.ScrollBy(-delta * RowHeight * RowsPerNotch);
                _dirty = true;
            };
            _scrollbar.onValueChanged.AddListener(OnScrollbar);
        }

        /// <summary>Raised with the index of a clicked item.</summary>
        public event Action<int>? ItemClicked;

        /// <summary>The view's rect; the caller places and sizes it.</summary>
        public RectTransform Rect { get; }

        /// <summary>Shows these items with this one highlighted.</summary>
        /// <param name="items">The items (not copied; do not change the array afterwards).</param>
        /// <param name="selected">The highlighted item, or -1.</param>
        public void SetItems(string[] items, int selected)
        {
            _items = items ?? Array.Empty<string>();
            _selected = selected;
            _dirty = true;
        }

        /// <summary>Scrolls just enough to show an item.</summary>
        /// <param name="index">The item.</param>
        public void Reveal(int index)
        {
            if (index < 0 || index >= _items.Length)
            {
                return;
            }

            Resize();
            _scroll.Reveal(index * RowHeight, RowHeight);
            _dirty = true;
        }

        /// <summary>Brings the rows up to date. Cheap when nothing moved.</summary>
        public void Refresh()
        {
            var height = _viewport.rect.height;
            if (!Mathf.Approximately(height, _viewportHeight))
            {
                _viewportHeight = height;
                EnsurePool(height);
                _dirty = true;
            }

            if (!_dirty)
            {
                return;
            }

            _dirty = false;
            Resize();

            var first = Mathf.Max(0, Mathf.FloorToInt(_scroll.Top / RowHeight));
            for (var k = 0; k < _rows.Count; k++)
            {
                var index = first + k;
                var row = _rows[k];
                if (index >= _items.Length)
                {
                    row.Hide();
                    continue;
                }

                row.Bind(index, _items[index], index == _selected, (index * RowHeight) - _scroll.Top);
            }

            SyncScrollbar();
        }

        /// <summary>Builds a view under <paramref name="parent"/>. The caller sets its anchors and size.</summary>
        /// <param name="parent">Where the view goes.</param>
        /// <param name="assets">The game's UI assets.</param>
        internal static GameStyleListView Create(RectTransform parent, GameStyleAssets assets)
        {
            var rect = GameStyleUi.Child(parent, "ListView");
            rect.gameObject.SetActive(false);

            GameStyleUi.AddImage(rect, assets.SpriteNamed(GameStyleAssetSet.Panel), GameStyleUi.PanelFill, raycast: true);
            var wheel = rect.gameObject.AddComponent<GameStyleScrollArea>();

            var viewport = GameStyleUi.Child(rect, "Viewport");
            GameStyleUi.Stretch(viewport, 8f, LineLayout.Padding, 30f, LineLayout.Padding);
            viewport.gameObject.AddComponent<RectMask2D>();

            var scrollbar = GameStyleTextView.BuildScrollbar(rect, assets);

            rect.gameObject.SetActive(true);
            return new GameStyleListView(rect, viewport, scrollbar, wheel, assets);
        }

        private void Resize() => _scroll.Resize(_items.Length * RowHeight, Mathf.Max(0f, _viewport.rect.height));

        private void EnsurePool(float height)
        {
            var wanted = Mathf.Max(1, Mathf.CeilToInt(height / RowHeight) + 1);
            while (_rows.Count < wanted)
            {
                var row = Row.Create(_viewport, _assets);
                row.Clicked += index => ItemClicked?.Invoke(index);
                _rows.Add(row);
            }

            for (var i = wanted; i < _rows.Count; i++)
            {
                _rows[i].Hide();
            }
        }

        private void SyncScrollbar()
        {
            _suppressScrollbar = true;
            try
            {
                _scrollbar.size = _scroll.HandleSize;

                // The scrollbar runs bottom-to-top like the text view's; ScrollState reports that way.
                _scrollbar.SetValueWithoutNotify(_scroll.ScrollbarValue);
            }
            finally
            {
                _suppressScrollbar = false;
            }
        }

        private void OnScrollbar(float value)
        {
            if (_suppressScrollbar)
            {
                return;
            }

            _scroll.FromScrollbar(value);
            _dirty = true;
        }

        /// <summary>One pooled row: highlight, hover overlay, label.</summary>
        private sealed class Row
        {
            private readonly RectTransform _rect;
            private readonly Image _fill;
            private readonly TextMeshProUGUI _label;
            private int _index = -1;

            private Row(RectTransform rect, Image fill, TextMeshProUGUI label, GameStyleClickable clickable)
            {
                _rect = rect;
                _fill = fill;
                _label = label;
                clickable.Clicked += () =>
                {
                    if (_index >= 0)
                    {
                        Clicked?.Invoke(_index);
                    }
                };
            }

            public event Action<int>? Clicked;

            public static Row Create(RectTransform parent, GameStyleAssets assets)
            {
                var rect = GameStyleUi.Child(parent, "Row");
                rect.gameObject.SetActive(false);
                rect.anchorMin = new Vector2(0f, 1f);
                rect.anchorMax = new Vector2(1f, 1f);
                rect.pivot = new Vector2(0.5f, 1f);
                rect.sizeDelta = new Vector2(0f, RowHeight);

                var fill = GameStyleUi.AddImage(rect, null, Color.clear, raycast: true);

                var hover = GameStyleUi.Child(rect, "Hover");
                GameStyleUi.Stretch(hover);
                var hoverImage = GameStyleUi.AddImage(hover, null, new Color(1f, 1f, 1f, 0.08f), raycast: false);

                var text = GameStyleUi.Child(rect, "Label");
                GameStyleUi.Stretch(text, 10f, 0f, 10f, 0f);
                var label = GameStyleUi.AddLabel(text, assets.Font, GameStyleUi.BodySize, GameStyleUi.Text, TextAlignmentOptions.MidlineLeft);

                var clickable = rect.gameObject.AddComponent<GameStyleClickable>();
                clickable.Setup(hoverImage, null);

                return new Row(rect, fill, label, clickable);
            }

            public void Bind(int index, string text, bool selected, float top)
            {
                _index = index;
                // Display-only Thai rewrite (tone mark before ำ), as in the chat view; the item keeps its text.
                var display = global::CwyLs.ModLoader.Core.Subtitles.ThaiText.ShapeForDisplay(text) ?? string.Empty;
                if (!string.Equals(_label.text, display, StringComparison.Ordinal))
                {
                    _label.text = display;
                }

                var fill = selected ? SelectedFill : Color.clear;
                if (_fill.color != fill)
                {
                    _fill.color = fill;
                }

                _rect.anchoredPosition = new Vector2(0f, -top);
                if (!_rect.gameObject.activeSelf)
                {
                    _rect.gameObject.SetActive(true);
                }
            }

            public void Hide()
            {
                _index = -1;
                if (_rect.gameObject.activeSelf)
                {
                    _rect.gameObject.SetActive(false);
                }
            }
        }
    }
}
