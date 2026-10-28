using System;
using System.Collections.Generic;
using CwyLs.ModLoader.Core.Console;
using CwyLs.ModLoader.Core.Subtitles;
using CwyLs.ModLoader.Core.Ui;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace CwyLs.ModLoader.Bulbul.UI
{
    /// <summary>
    /// Chat bubbles in the game's style: the character's on the left in the game's outlined
    /// dark panel, the player's on the right tinted with the game's cyan, system notes centred in
    /// small grey text. A bubble grows as text streams in, shows animated typing dots, and can carry
    /// small game-style buttons (e.g. "replay"). The view stays on the newest bubble unless the
    /// player scrolled up (<see cref="ScrollState"/>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Text is drawn with the game's <c>Base_JP</c> font, whose fallback table reaches the game's
    /// <c>Base_TH</c>/<c>Dynamic_TH</c> with the pack's mark records (<c>LanguageFonts</c>), so Thai
    /// tone marks stack as in the story box. Thai has no spaces between words: before measuring, the
    /// displayed text goes through <see cref="ThaiText.ForDisplay"/>, the subtitle helper: zero-width
    /// syllable breaks, so a long Thai sentence wraps between syllables instead of mid-cluster, and the
    /// font's own split of a tone mark before ำ. The bubble's own text is never changed.
    /// </para>
    /// <para>
    /// Bubbles are plain text (rich text off), so a reply containing <c>&lt;</c> shows as written.
    /// Only changed bubbles are measured again (one hidden label, as in <see cref="GameStyleTextView"/>);
    /// the typing dots re-measure their bubble only when the dot frame changes. Main thread only.
    /// </para>
    /// </remarks>
    public sealed class GameStyleChatView
    {
        /// <summary>Wheel notch in canvas units.</summary>
        public const float WheelStep = 72f;

        private const float MeasureLimit = 100000f;
        private const float ButtonFontSize = 15f;

        private static readonly Color PlayerTint = new Color(0.62f, 0.93f, 1f, 1f);

        private readonly GameStyleAssets _assets;
        private readonly RectTransform _viewport;
        private readonly RectTransform _content;
        private readonly TextMeshProUGUI _measure;
        private readonly Scrollbar _scrollbar;
        private readonly ScrollState _scroll = new ScrollState(followEnd: true);
        private readonly Dictionary<long, Bubble> _byId = new Dictionary<long, Bubble>();
        private readonly List<Bubble> _order = new List<Bubble>();
        private readonly List<float> _heights = new List<float>();
        private float[] _tops = new float[16];
        private float _width = -1f;
        private float _contentHeight;
        private float _removedAbove;
        private bool _layoutDirty = true;
        private bool _scrollDirty = true;
        private bool _suppressScrollbar;

        private GameStyleChatView(
            RectTransform rect,
            RectTransform viewport,
            RectTransform content,
            TextMeshProUGUI measure,
            Scrollbar scrollbar,
            GameStyleScrollArea wheel,
            GameStyleAssets assets)
        {
            Rect = rect;
            _viewport = viewport;
            _content = content;
            _measure = measure;
            _scrollbar = scrollbar;
            _assets = assets;

            wheel.Scrolled += delta =>
            {
                _scroll.ScrollBy(-delta * WheelStep);
                _scrollDirty = true;
            };
            _scrollbar.onValueChanged.AddListener(OnScrollbar);
        }

        /// <summary>Raised when a bubble button is clicked: the bubble's id and the button's id.</summary>
        public event Action<long, string>? ButtonClicked;

        /// <summary>The view's rect; the caller places and sizes it.</summary>
        public RectTransform Rect { get; }

        /// <summary>True while the view follows the newest bubble.</summary>
        public bool IsFollowing => _scroll.Following;

        /// <summary>Bubbles drawn.</summary>
        public int Count => _order.Count;

        /// <summary>Makes the view show exactly these bubbles, oldest first. Unchanged bubbles cost nothing.</summary>
        /// <param name="states">The bubbles (<see cref="ChatViewModel.Snapshot"/>).</param>
        /// <param name="now">The clock the typing dots run on, in seconds.</param>
        public void Apply(ChatBubbleState[] states, double now)
        {
            if (states is null)
            {
                throw new ArgumentNullException(nameof(states));
            }

            var keep = new HashSet<long>();
            foreach (var state in states)
            {
                keep.Add(state.Id);
            }

            // Bubbles that went away: those before the first survivor were dropped from the top
            // (the cap), and the view must not jump while the player reads further down.
            var aboveFirstSurvivor = true;
            foreach (var bubble in _order)
            {
                if (keep.Contains(bubble.Id))
                {
                    aboveFirstSurvivor = false;
                    continue;
                }

                if (aboveFirstSurvivor)
                {
                    _removedAbove += bubble.Height + ChatLayout.Spacing;
                }

                _byId.Remove(bubble.Id);
                UnityEngine.Object.Destroy(bubble.Rect.gameObject);
                _layoutDirty = true;
            }

            _order.Clear();
            foreach (var state in states)
            {
                if (!_byId.TryGetValue(state.Id, out var bubble))
                {
                    bubble = Bubble.Create(_content, _assets, state.Id, state.Role);
                    bubble.ButtonClicked += (id, button) => ButtonClicked?.Invoke(id, button);
                    _byId[state.Id] = bubble;
                    _layoutDirty = true;
                }

                if (bubble.Version != state.Version || bubble.Version < 0)
                {
                    bubble.Update(state, _assets);
                    Measure(bubble, now);
                    _layoutDirty = true;
                }

                _order.Add(bubble);
            }
        }

        /// <summary>Jumps to the newest bubble and follows again.</summary>
        public void ScrollToEnd()
        {
            _scroll.ToEnd();
            _scrollDirty = true;
        }

        /// <summary>Animates the typing dots, follows a resize, lays out and scrolls. Once a frame while shown.</summary>
        /// <param name="now">The clock the typing dots run on, in seconds.</param>
        public void Tick(double now)
        {
            var box = _viewport.rect;
            if (!Mathf.Approximately(box.width, _width))
            {
                _width = box.width;
                foreach (var bubble in _order)
                {
                    Measure(bubble, now);
                }

                _layoutDirty = true;
            }

            foreach (var bubble in _order)
            {
                if (bubble.Typing && !string.Equals(bubble.DotsFrame, TypingDots.At(now), StringComparison.Ordinal))
                {
                    Measure(bubble, now);
                    _layoutDirty = true;
                }
            }

            if (_layoutDirty)
            {
                _layoutDirty = false;
                Layout();
                _scroll.Resize(_contentHeight, box.height, _removedAbove);
                _removedAbove = 0f;
                _scrollDirty = true;
            }
            else if (!Mathf.Approximately(box.height, _scroll.Viewport))
            {
                _scroll.Resize(_contentHeight, box.height);
                _scrollDirty = true;
            }

            if (!_scrollDirty)
            {
                return;
            }

            _scrollDirty = false;
            _content.anchoredPosition = new Vector2(0f, _scroll.Top);
            SyncScrollbar();
        }

        /// <summary>Builds a view under <paramref name="parent"/>. The caller sets its anchors and size.</summary>
        /// <param name="parent">Where the view goes.</param>
        /// <param name="assets">The game's UI assets.</param>
        internal static GameStyleChatView Create(RectTransform parent, GameStyleAssets assets)
        {
            var rect = GameStyleUi.Child(parent, "ChatView");
            rect.gameObject.SetActive(false);

            GameStyleUi.AddImage(rect, assets.SpriteNamed(GameStyleAssetSet.Panel), GameStyleUi.PanelFill, raycast: true);
            var wheel = rect.gameObject.AddComponent<GameStyleScrollArea>();

            var viewport = GameStyleUi.Child(rect, "Viewport");
            GameStyleUi.Stretch(viewport, 4f, 4f, 26f, 4f);
            viewport.gameObject.AddComponent<RectMask2D>();

            var content = GameStyleUi.Child(viewport, "Content");
            content.anchorMin = new Vector2(0f, 1f);
            content.anchorMax = new Vector2(1f, 1f);
            content.pivot = new Vector2(0.5f, 1f);
            content.sizeDelta = Vector2.zero;
            content.anchoredPosition = Vector2.zero;

            // Measures bubble text with the bubbles' font and wrapping; disabled, so it never draws.
            var measureRect = GameStyleUi.Child(rect, "Measure");
            GameStyleUi.Stretch(measureRect);
            var measure = GameStyleUi.AddLabel(measureRect, assets.Font, GameStyleUi.BodySize, GameStyleUi.Text, TextAlignmentOptions.TopLeft);
            measure.enableWordWrapping = true;
            measure.overflowMode = TextOverflowModes.Overflow;
            measure.enabled = false;

            var scrollbar = GameStyleTextView.BuildScrollbar(rect, assets);

            rect.gameObject.SetActive(true);
            return new GameStyleChatView(rect, viewport, content, measure, scrollbar, wheel, assets);
        }

        /// <summary>The text drawn for a bubble: soft Thai breaks, then the typing dots.</summary>
        private static string Display(Bubble bubble, double now)
        {
            var text = string.IsNullOrEmpty(bubble.Text) ? string.Empty : ThaiText.ForDisplay(bubble.Text) ?? bubble.Text;
            return TypingDots.Display(text, bubble.Typing, now);
        }

        private void Measure(Bubble bubble, double now)
        {
            var display = Display(bubble, now);
            bubble.DotsFrame = bubble.Typing ? TypingDots.At(now) : string.Empty;

            var width = Mathf.Max(1f, _width < 0f ? _viewport.rect.width : _width);
            var max = ChatLayout.TextMaxWidth(width, bubble.Role);
            var size = bubble.Role == CwyChatRole.System ? GameStyleUi.SmallSize : GameStyleUi.BodySize;
            if (!Mathf.Approximately(_measure.fontSize, size))
            {
                _measure.fontSize = size;
            }

            var measured = _measure.GetPreferredValues(display, max, MeasureLimit);
            if (float.IsNaN(measured.y) || measured.y <= 0f)
            {
                // As in GameStyleTextView: the hidden label gave nothing, so measure with the drawn one.
                GameStyleUi.WarnOnce("the chat view's measuring label gave no size; measuring with the bubble's own label");
                measured = bubble.Measure(display, max);
            }

            var textWidth = float.IsNaN(measured.x) || measured.x <= 0f ? max : Mathf.Min(max, measured.x);
            var textHeight = float.IsNaN(measured.y) || measured.y <= 0f ? size * 1.3f : measured.y;

            ChatLayout.BubbleSize(textWidth, textHeight, bubble.ButtonsWidth, bubble.HasButtons, max, out var bubbleWidth, out var bubbleHeight);
            bubble.SetText(display);
            bubble.Width = bubbleWidth;
            bubble.Height = bubbleHeight;
        }

        private void Layout()
        {
            _heights.Clear();
            foreach (var bubble in _order)
            {
                _heights.Add(bubble.Height);
            }

            if (_tops.Length < _heights.Count)
            {
                _tops = new float[Math.Max(_heights.Count, _tops.Length * 2)];
            }

            _contentHeight = ChatLayout.Stack(_heights, _tops);
            var width = Mathf.Max(1f, _width);
            for (var i = 0; i < _order.Count; i++)
            {
                var bubble = _order[i];
                bubble.Place(ChatLayout.Left(bubble.Role, width, bubble.Width), _tops[i]);
            }

            _content.sizeDelta = new Vector2(0f, _contentHeight);
        }

        private void SyncScrollbar()
        {
            _suppressScrollbar = true;
            try
            {
                _scrollbar.size = _scroll.HandleSize;
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
            _scrollDirty = true;
        }

        /// <summary>One bubble's objects and its last-drawn state.</summary>
        private sealed class Bubble
        {
            private readonly RectTransform _labelRect;
            private readonly TextMeshProUGUI _label;
            private readonly List<KeyValuePair<string, GameStyleButton>> _buttons = new List<KeyValuePair<string, GameStyleButton>>();
            private string _buttonsKey = string.Empty;

            private Bubble(long id, CwyChatRole role, RectTransform rect, RectTransform labelRect, TextMeshProUGUI label)
            {
                Id = id;
                Role = role;
                Rect = rect;
                _labelRect = labelRect;
                _label = label;
            }

            public event Action<long, string>? ButtonClicked;

            public long Id { get; }

            public CwyChatRole Role { get; }

            public RectTransform Rect { get; }

            public int Version { get; private set; } = -1;

            public string Text { get; private set; } = string.Empty;

            public bool Typing { get; private set; }

            public string DotsFrame { get; set; } = string.Empty;

            public bool HasButtons => _buttons.Count > 0;

            public float ButtonsWidth { get; private set; }

            public float Width { get; set; }

            public float Height { get; set; }

            public static Bubble Create(RectTransform content, GameStyleAssets assets, long id, CwyChatRole role)
            {
                var rect = GameStyleUi.Child(content, "Bubble " + id.ToString(System.Globalization.CultureInfo.InvariantCulture));
                rect.gameObject.SetActive(false);
                rect.anchorMin = new Vector2(0f, 1f);
                rect.anchorMax = new Vector2(0f, 1f);
                rect.pivot = new Vector2(0f, 1f);

                if (role != CwyChatRole.System)
                {
                    var background = GameStyleUi.AddImage(rect, assets.SpriteNamed(GameStyleAssetSet.Panel), GameStyleUi.PanelFill, raycast: false);
                    if (role == CwyChatRole.Player)
                    {
                        background.color = background.sprite == null ? new Color(0.10f, 0.26f, 0.33f, 0.95f) : PlayerTint;
                    }
                }

                var labelRect = GameStyleUi.Child(rect, "Text");
                var system = role == CwyChatRole.System;
                var label = GameStyleUi.AddLabel(
                    labelRect,
                    assets.Font,
                    system ? GameStyleUi.SmallSize : GameStyleUi.BodySize,
                    system ? GameStyleUi.TabLabel : GameStyleUi.Text,
                    system ? TextAlignmentOptions.Top : TextAlignmentOptions.TopLeft);
                label.enableWordWrapping = true;
                label.overflowMode = TextOverflowModes.Overflow;

                rect.gameObject.SetActive(true);
                return new Bubble(id, role, rect, labelRect, label);
            }

            /// <summary>Takes a new state: text, typing, buttons (rebuilt only when they changed).</summary>
            public void Update(ChatBubbleState state, GameStyleAssets assets)
            {
                Version = state.Version;
                Text = state.Text ?? string.Empty;
                Typing = state.Typing;

                var key = ButtonsKey(state.Buttons);
                if (!string.Equals(key, _buttonsKey, StringComparison.Ordinal))
                {
                    _buttonsKey = key;
                    RebuildButtons(state.Buttons, assets);
                }

                var bottom = ChatLayout.PadY + (HasButtons ? ChatLayout.ButtonGap + ChatLayout.ButtonHeight : 0f);
                GameStyleUi.Stretch(_labelRect, ChatLayout.PadX, ChatLayout.PadY, ChatLayout.PadX, bottom);
            }

            /// <summary>The bubble label's preferred size for a text at a width (the measuring fallback).</summary>
            public Vector2 Measure(string display, float width) => _label.GetPreferredValues(display, width, MeasureLimit);

            public void SetText(string display)
            {
                if (!string.Equals(_label.text, display, StringComparison.Ordinal))
                {
                    _label.text = display;
                }
            }

            public void Place(float left, float top)
            {
                Rect.anchoredPosition = new Vector2(left, -top);
                Rect.sizeDelta = new Vector2(Width, Height);
            }

            private static string ButtonsKey(CwyButtonSpec[] buttons)
            {
                if (buttons == null || buttons.Length == 0)
                {
                    return string.Empty;
                }

                var parts = new string[buttons.Length];
                for (var i = 0; i < buttons.Length; i++)
                {
                    parts[i] = buttons[i].Id + "\u0001" + buttons[i].Label;
                }

                return string.Join("\u0002", parts);
            }

            private void RebuildButtons(CwyButtonSpec[] buttons, GameStyleAssets assets)
            {
                foreach (var pair in _buttons)
                {
                    UnityEngine.Object.Destroy(pair.Value.Rect.gameObject);
                }

                _buttons.Clear();
                ButtonsWidth = 0f;
                if (buttons == null)
                {
                    return;
                }

                var x = 0f;
                foreach (var spec in buttons)
                {
                    var buttonId = spec.Id;
                    var button = GameStyleButton.Create(Rect, assets, spec.Label, ButtonFontSize);
                    var width = button.PreferredWidth;
                    var rect = button.Rect;
                    rect.anchorMin = new Vector2(0f, 0f);
                    rect.anchorMax = new Vector2(0f, 0f);
                    rect.pivot = new Vector2(0f, 0f);
                    rect.anchoredPosition = new Vector2(ChatLayout.PadX + x, ChatLayout.PadY);
                    rect.sizeDelta = new Vector2(width, ChatLayout.ButtonHeight);
                    button.Clicked += () => ButtonClicked?.Invoke(Id, buttonId);
                    _buttons.Add(new KeyValuePair<string, GameStyleButton>(buttonId, button));
                    x += width + ChatLayout.ButtonGap;
                }

                ButtonsWidth = Mathf.Max(0f, x - ChatLayout.ButtonGap);
            }
        }
    }
}
