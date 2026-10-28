using System;
using System.Collections.Generic;
using System.Text;
using CwyLs.ModLoader.Core.Console;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace CwyLs.ModLoader.Bulbul.UI
{
    /// <summary>
    /// A scrolling view over a list of rich-text lines: the game's outlined dark panel, the game's
    /// white scrollbar handle, mouse-wheel scrolling, and only the visible lines ever rendered.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The caller owns the lines and hands the view a function that returns any one of them as
    /// rich text (<see cref="Render"/>). The view measures the lines it needs at its current width
    /// and font size (a wrapped line is several rows tall) and lets <see cref="LineLayout"/> decide
    /// which to draw and which edge to pin them to: scrolled to the top, or with every line
    /// fitting, line 0 starts exactly at the top of the text box; following the newest line, or
    /// scrolled part-way, the chosen bottom line ends exactly at the bottom. The other edge may
    /// show part of a line, cut by the mask at the text box edge. One TextMeshPro mesh holds about
    /// 16,000 characters, so drawing only the visible range is what makes a 300-line scrollback
    /// safe.
    /// </para>
    /// <para>
    /// The text box is inset <see cref="LineLayout.Padding"/> from the panel's top and bottom
    /// edges (the game's note window) and the mask sits on the text box edge, so nothing is ever
    /// drawn in the padding or outside the panel. A resize or a font size change is picked up on
    /// the next <see cref="Render"/>.
    /// </para>
    /// </remarks>
    public sealed class GameStyleTextView
    {
        /// <summary>Wheel notch in lines.</summary>
        public const int LinesPerNotch = 3;

        /// <summary>Measured line heights kept before the cache starts again (one entry per distinct line).</summary>
        private const int MaxCachedHeights = 2048;

        /// <summary>Height limit handed to the measurement: large enough never to cut a line.</summary>
        private const float MeasureLimit = 100000f;

        /// <summary>What an empty line is drawn as: one space, so it still makes a row of the base font.</summary>
        private const string EmptyLine = " ";

        private readonly RectTransform _viewport;
        private readonly TextMeshProUGUI _text;
        private readonly TextMeshProUGUI _measure;
        private readonly Scrollbar _scrollbar;
        private readonly LineScrollState _state = new LineScrollState();
        private readonly Dictionary<string, float> _heights = new Dictionary<string, float>(StringComparer.Ordinal);
        private readonly Dictionary<int, string> _linesThisRender = new Dictionary<int, string>();
        private readonly Func<int, float> _heightOf;

        private Func<int, string>? _lineAt;
        private TextMeshProUGUI _measurer;
        private float _rowHeight;
        private float _spacing;
        private float _metricsFontSize = -1f;
        private float _metricsWidth = -1f;
        private float _laidOutHeight = -1f;
        private long _laidOutState = -1;
        private int _laidOutTotal = -1;
        private long _laidOutVersion = -1;
        private LineWindow _rendered;
        private bool _hasRendered;
        private bool _suppressScrollbar;
        private bool _dirty = true;

        private GameStyleTextView(
            RectTransform rect,
            RectTransform viewport,
            TextMeshProUGUI text,
            TextMeshProUGUI measure,
            Scrollbar scrollbar,
            GameStyleScrollArea wheel)
        {
            Rect = rect;
            _viewport = viewport;
            _text = text;
            _measure = measure;
            _measurer = measure;
            _scrollbar = scrollbar;
            _heightOf = HeightOf;

            wheel.Scrolled += delta => Scroll(Mathf.RoundToInt(delta * LinesPerNotch));
            _scrollbar.onValueChanged.AddListener(OnScrollbar);
        }

        /// <summary>The view's rect; the caller places and sizes it.</summary>
        public RectTransform Rect { get; }

        /// <summary>Lines scrolled up from the newest; 0 means following the newest line.</summary>
        public int Offset => _state.Offset;

        /// <summary>Text size in points.</summary>
        public float FontSize
        {
            get => _text.fontSize;
            set
            {
                if (!Mathf.Approximately(_text.fontSize, value))
                {
                    _text.fontSize = value;
                    _measure.fontSize = value;
                    _dirty = true;
                }
            }
        }

        /// <summary>How many lines were wholly visible at the last <see cref="Render"/>.</summary>
        public int Capacity => _state.Last.FullyVisible;

        /// <summary>
        /// Brings the view up to date. Cheap when nothing changed: lines are only measured and the
        /// text only rebuilt when the list, the content version, the scroll position, the size or
        /// the font size moved.
        /// </summary>
        /// <param name="total">Lines in the list.</param>
        /// <param name="appended">Lines added since the last call (keeps a scrolled-up view in place).</param>
        /// <param name="version">Changes whenever existing lines changed (a tab switch, a refresh).</param>
        /// <param name="lineAt">Line i as rich text, without a line break.</param>
        public void Render(int total, int appended, long version, Func<int, string> lineAt)
        {
            if (lineAt is null)
            {
                throw new ArgumentNullException(nameof(lineAt));
            }

            var box = _viewport.rect;
            SyncMetrics(box.width);
            _state.Appended(appended);

            total = Math.Max(0, total);
            if (!_dirty && appended <= 0 && total == _laidOutTotal && version == _laidOutVersion
                && _state.Version == _laidOutState && Mathf.Approximately(box.height, _laidOutHeight))
            {
                return;
            }

            _lineAt = lineAt;
            _linesThisRender.Clear();
            try
            {
                var plan = _state.Layout(total, box.height, _spacing, _heightOf);

                // A new line at an index already drawn (the scrollback is full and dropped its
                // oldest line) changes the text even when the planned range stays the same.
                if (_dirty || !_hasRendered || appended > 0 || version != _laidOutVersion
                    || total != _laidOutTotal || !plan.Equals(_rendered))
                {
                    Draw(plan);
                }

                _laidOutTotal = total;
                _laidOutVersion = version;
                _laidOutState = _state.Version;
                _laidOutHeight = box.height;
                _dirty = false;
                SyncScrollbar();
            }
            finally
            {
                _lineAt = null;
                _linesThisRender.Clear();
            }
        }

        /// <summary>Scrolls by whole lines: positive towards older lines.</summary>
        /// <param name="lines">How far.</param>
        public void Scroll(int lines) => _state.Scroll(lines);

        /// <summary>Scrolls up a page.</summary>
        public void PageUp() => _state.PageUp();

        /// <summary>Scrolls down a page.</summary>
        public void PageDown() => _state.PageDown();

        /// <summary>Jumps to the newest line and follows it again.</summary>
        public void ScrollToEnd() => _state.ToEnd();

        /// <summary>Jumps to the oldest line and stays there: how a freshly opened document reads.</summary>
        public void ScrollToStart() => _state.ToStart();

        /// <summary>Builds a view under <paramref name="parent"/>. The caller sets its anchors and size.</summary>
        /// <param name="parent">Where the view goes.</param>
        /// <param name="assets">The game's UI assets.</param>
        internal static GameStyleTextView Create(RectTransform parent, GameStyleAssets assets)
        {
            var rect = GameStyleUi.Child(parent, "TextView");
            rect.gameObject.SetActive(false);

            GameStyleUi.AddImage(rect, assets.SpriteNamed(GameStyleAssetSet.Panel), GameStyleUi.PanelFill, raycast: true);
            var wheel = rect.gameObject.AddComponent<GameStyleScrollArea>();

            // The text box: inset from the panel like the note window's body text, masked on its
            // own edge so a cut line never reaches the padding, the outline or anything outside.
            var viewport = GameStyleUi.Child(rect, "Viewport");
            GameStyleUi.Stretch(viewport, 16f, LineLayout.Padding, 30f, LineLayout.Padding);
            viewport.gameObject.AddComponent<RectMask2D>();

            var body = GameStyleUi.Child(viewport, "Text");
            GameStyleUi.Stretch(body);
            var text = AddBodyText(body, assets);

            // Measures lines with the same font, size and wrapping; disabled, so it never draws
            // and never joins a canvas rebuild (TextMeshPro still wakes it with its object).
            var measureRect = GameStyleUi.Child(rect, "Measure");
            GameStyleUi.Stretch(measureRect);
            var measure = AddBodyText(measureRect, assets);
            measure.enabled = false;

            var scrollbar = BuildScrollbar(rect, assets);

            rect.gameObject.SetActive(true);
            return new GameStyleTextView(rect, viewport, text, measure, scrollbar, wheel);
        }

        private static TextMeshProUGUI AddBodyText(RectTransform rect, GameStyleAssets assets)
        {
            var text = GameStyleUi.AddLabel(rect, assets.Font, GameStyleUi.BodySize, GameStyleUi.Text, TextAlignmentOptions.TopLeft);
            text.richText = true;
            text.enableWordWrapping = true;
            text.overflowMode = TextOverflowModes.Overflow;
            return text;
        }

        /// <summary>The game-style vertical scrollbar on the right edge of a panel (bottom-to-top; also the mod-window list and chat views).</summary>
        /// <param name="parent">The panel.</param>
        /// <param name="assets">The game's UI assets.</param>
        internal static Scrollbar BuildScrollbar(RectTransform parent, GameStyleAssets assets)
        {
            var track = GameStyleUi.Child(parent, "Scrollbar");
            track.anchorMin = new Vector2(1f, 0f);
            track.anchorMax = new Vector2(1f, 1f);
            track.pivot = new Vector2(1f, 0.5f);
            track.anchoredPosition = new Vector2(-10f, 0f);
            track.sizeDelta = new Vector2(12f, -20f);
            GameStyleUi.AddImage(track, null, new Color(GameStyleUi.Lavender.r, GameStyleUi.Lavender.g, GameStyleUi.Lavender.b, 0.18f), raycast: true);

            var area = GameStyleUi.Child(track, "Sliding Area");
            GameStyleUi.Stretch(area);

            var handle = GameStyleUi.Child(area, "Handle");
            GameStyleUi.Stretch(handle);
            var handleImage = GameStyleUi.AddImage(
                handle, assets.SpriteNamed(GameStyleAssetSet.ScrollHandle), new Color(0.97f, 0.97f, 1f, 0.9f), raycast: true);

            var scrollbar = track.gameObject.AddComponent<Scrollbar>();
            scrollbar.handleRect = handle;
            scrollbar.targetGraphic = handleImage;
            scrollbar.direction = Scrollbar.Direction.BottomToTop;
            scrollbar.transition = Selectable.Transition.None;
            scrollbar.navigation = new Navigation { mode = Navigation.Mode.None };
            scrollbar.SetValueWithoutNotify(0f);
            scrollbar.size = 1f;
            return scrollbar;
        }

        private void Draw(LineWindow plan)
        {
            var alignment = plan.Anchor == LineAnchor.Top ? TextAlignmentOptions.TopLeft : TextAlignmentOptions.BottomLeft;
            if (_text.alignment != alignment)
            {
                _text.alignment = alignment;
            }

            var sb = new StringBuilder(plan.Count * 96);
            for (var i = plan.Start; i < plan.End; i++)
            {
                if (i > plan.Start)
                {
                    sb.Append('\n');
                }

                sb.Append(LineText(i));
            }

            // Display-only Thai rewrite (tone mark before ำ); the lines themselves are untouched.
            _text.text = global::CwyLs.ModLoader.Core.Subtitles.ThaiText.ShapeForDisplay(sb.ToString()) ?? string.Empty;
            _rendered = plan;
            _hasRendered = true;
        }

        /// <summary>
        /// Font-size and width changes: every cached height is stale, and the row metrics are
        /// measured again (one row of the base font, and the gap TextMeshPro puts between rows).
        /// </summary>
        private void SyncMetrics(float width)
        {
            var size = _text.fontSize;
            if (Mathf.Approximately(size, _metricsFontSize) && Mathf.Approximately(width, _metricsWidth) && _rowHeight > 0f)
            {
                return;
            }

            _metricsFontSize = size;
            _metricsWidth = width;
            _heights.Clear();
            _dirty = true;

            if (!MeasureRows(_measurer, out var one, out var two) && _measurer != _text)
            {
                // The hidden label gave nothing: measure with the drawn label instead, as the first
                // version of this view did. Harmless - TextMeshPro re-reads its own text before it
                // next draws.
                GameStyleUi.WarnOnce("the line view's measuring label gave no size; measuring with the drawn label");
                _measurer = _text;
                MeasureRows(_measurer, out one, out two);
            }

            if (float.IsNaN(one) || one <= 0f || float.IsNaN(two) || two <= one)
            {
                // Lines are still pinned to the right edge; only how many are drawn is estimated.
                GameStyleUi.WarnOnce(
                    $"the line view could not measure its font (one row {one}, two rows {two}); line heights are estimated");
            }

            _rowHeight = one > 0f ? one : size * 1.3f;
            _spacing = two > one ? two - (2f * _rowHeight) : 0f;
        }

        /// <summary>One row and two rows of the base font; false when either came back empty.</summary>
        private static bool MeasureRows(TextMeshProUGUI label, out float one, out float two)
        {
            one = label.GetPreferredValues("Ag", MeasureLimit, MeasureLimit).y;
            two = label.GetPreferredValues("Ag\nAg", MeasureLimit, MeasureLimit).y;
            return !float.IsNaN(one) && one > 0f && !float.IsNaN(two) && two > one;
        }

        /// <summary>Line i's height on its own at the text box's width, measured once per distinct line.</summary>
        private float HeightOf(int index)
        {
            var line = LineText(index);
            if (_heights.TryGetValue(line, out var height))
            {
                return height;
            }

            height = _measurer.GetPreferredValues(line, Mathf.Max(1f, _metricsWidth), MeasureLimit).y;

            // A line with no characters (only markup) still takes a row once it sits between two
            // line breaks.
            if (float.IsNaN(height) || height <= 0f)
            {
                height = _rowHeight;
            }

            if (_heights.Count >= MaxCachedHeights)
            {
                _heights.Clear();
            }

            _heights[line] = height;
            return height;
        }

        /// <summary>Line i from the caller, asked once per render; an empty line becomes one space.</summary>
        private string LineText(int index)
        {
            if (_linesThisRender.TryGetValue(index, out var cached))
            {
                return cached;
            }

            var line = _lineAt?.Invoke(index);
            if (string.IsNullOrEmpty(line))
            {
                line = EmptyLine;
            }

            _linesThisRender[index] = line!;
            return line!;
        }

        private void SyncScrollbar()
        {
            _suppressScrollbar = true;
            try
            {
                _scrollbar.size = _state.HandleSize;
                _scrollbar.SetValueWithoutNotify(_state.ScrollbarValue);
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

            _state.FromScrollbar(value);
        }
    }
}
