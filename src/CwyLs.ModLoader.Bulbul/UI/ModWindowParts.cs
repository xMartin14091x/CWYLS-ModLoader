using System;
using System.Collections.Generic;
using CwyLs.ModLoader.Core.Ui;
using UnityEngine;

namespace CwyLs.ModLoader.Bulbul.UI
{
    /// <summary>
    /// The view of one mod-window part: built once from its Core model, placed by the window's
    /// stack layout, and brought up to date once a frame while shown - redrawn only when the model's
    /// version moved. User input goes back to the model, which raises the mod's events.
    /// </summary>
    internal abstract class PartView
    {
        private int _seenVersion = -1;

        protected PartView(PartModel model, RectTransform rect)
        {
            Model = model;
            Rect = rect;
        }

        /// <summary>The part's model.</summary>
        public PartModel Model { get; }

        /// <summary>The part's root rect.</summary>
        public RectTransform Rect { get; }

        /// <summary>True when the last layout showed the part.</summary>
        public bool IsPlacedVisible { get; private set; } = true;

        /// <summary>Builds the view of a part in a window's body.</summary>
        /// <param name="model">The part's model.</param>
        /// <param name="frame">The window.</param>
        public static PartView Create(PartModel model, GameStyleWindow frame)
        {
            switch (model)
            {
                case TextViewModel text:
                    return new TextPartView(text, frame.AddTextView(frame.Body));
                case ListViewModel list:
                    return new ListPartView(list, frame.AddListView(frame.Body));
                case ChatViewModel chat:
                    return new ChatPartView(chat, frame.AddChatView(frame.Body));
                case InputBoxModel input:
                    return new InputPartView(
                        input,
                        frame.AddInputBox(
                            frame.Body,
                            input.Placeholder,
                            new GameStyleInputOptions { Lines = input.Lines, SubmitOnEnter = input.SubmitOnEnter, MaxLength = input.MaxLength }));
                case ButtonRowModel buttons:
                    return new ButtonsPartView(buttons, frame);
                case TabStripModel tabs:
                    return new TabsPartView(tabs, frame.AddTabStrip(frame.Body));
                default:
                    throw new ArgumentException("no view for a " + model.GetType().Name, nameof(model));
            }
        }

        /// <summary>Puts the part where the layout says, or hides it.</summary>
        /// <param name="slot">Its slot.</param>
        public void Place(StackSlot slot)
        {
            IsPlacedVisible = slot.Visible;
            if (!slot.Visible)
            {
                OnHiding();
                if (Rect.gameObject.activeSelf)
                {
                    Rect.gameObject.SetActive(false);
                }

                return;
            }

            Rect.anchorMin = new Vector2(0f, 1f);
            Rect.anchorMax = new Vector2(1f, 1f);
            Rect.pivot = new Vector2(0.5f, 1f);
            Rect.offsetMin = new Vector2(0f, -(slot.Top + slot.Height));
            Rect.offsetMax = new Vector2(0f, -slot.Top);
            if (!Rect.gameObject.activeSelf)
            {
                Rect.gameObject.SetActive(true);
            }
        }

        /// <summary>Catches up with the model when it changed, then does the per-frame work.</summary>
        /// <param name="now">Monotonic seconds.</param>
        public void Tick(double now)
        {
            var version = Model.Version;
            if (version != _seenVersion)
            {
                _seenVersion = version;
                Apply(now);
            }

            Frame(now);
        }

        /// <summary>The model changed: redraw from it.</summary>
        /// <param name="now">Monotonic seconds.</param>
        protected abstract void Apply(double now);

        /// <summary>Per-frame work while shown.</summary>
        /// <param name="now">Monotonic seconds.</param>
        protected virtual void Frame(double now)
        {
        }

        /// <summary>The part is about to be hidden by the layout.</summary>
        protected virtual void OnHiding()
        {
        }
    }

    /// <summary>A text part on <see cref="GameStyleTextView"/>.</summary>
    internal sealed class TextPartView : PartView
    {
        private readonly TextViewModel _model;
        private readonly GameStyleTextView _view;
        private readonly Func<int, string> _lineAt;
        private string[] _lines = Array.Empty<string>();
        private int _appended;
        private long _contentVersion;

        public TextPartView(TextViewModel model, GameStyleTextView view)
            : base(model, view.Rect)
        {
            _model = model;
            _view = view;
            _lineAt = index => _lines[index];
        }

        protected override void Apply(double now)
        {
            var snapshot = _model.Take();
            _lines = snapshot.Lines;
            _appended += snapshot.Appended;
            _contentVersion = snapshot.ContentVersion;

            switch (_model.TakeScrollRequest())
            {
                case ScrollRequest.End:
                    _view.ScrollToEnd();
                    break;
                case ScrollRequest.Start:
                    _view.ScrollToStart();
                    break;
            }
        }

        protected override void Frame(double now)
        {
            _view.Render(_lines.Length, _appended, _contentVersion, _lineAt);
            _appended = 0;
            _model.ReportFollowing(_view.Offset == 0);
        }
    }

    /// <summary>A list part on <see cref="GameStyleListView"/>.</summary>
    internal sealed class ListPartView : PartView
    {
        private readonly ListViewModel _model;
        private readonly GameStyleListView _view;

        public ListPartView(ListViewModel model, GameStyleListView view)
            : base(model, view.Rect)
        {
            _model = model;
            _view = view;
            _view.ItemClicked += model.Click;
        }

        protected override void Apply(double now)
        {
            var items = _model.Snapshot(out var selected);
            _view.SetItems(items, selected);
            var reveal = _model.TakeScrollRequest();
            if (reveal >= 0)
            {
                _view.Reveal(reveal);
            }
        }

        protected override void Frame(double now) => _view.Refresh();
    }

    /// <summary>A chat part on <see cref="GameStyleChatView"/>.</summary>
    internal sealed class ChatPartView : PartView
    {
        private readonly ChatViewModel _model;
        private readonly GameStyleChatView _view;

        public ChatPartView(ChatViewModel model, GameStyleChatView view)
            : base(model, view.Rect)
        {
            _model = model;
            _view = view;
            _view.ButtonClicked += model.Click;
        }

        protected override void Apply(double now)
        {
            _view.Apply(_model.Snapshot(), now);
            if (_model.TakeScrollToEnd())
            {
                _view.ScrollToEnd();
            }
        }

        protected override void Frame(double now)
        {
            _view.Tick(now);
            _model.ReportFollowing(_view.IsFollowing);
        }
    }

    /// <summary>An input part on <see cref="GameStyleInputLine"/> in mod text-box mode.</summary>
    internal sealed class InputPartView : PartView
    {
        private readonly InputBoxModel _model;
        private readonly GameStyleInputLine _input;

        public InputPartView(InputBoxModel model, GameStyleInputLine input)
            : base(model, input.Rect)
        {
            _model = model;
            _input = input;
            _input.Visible = true;
            _input.Submitted += model.RaiseSubmitted;
        }

        /// <summary>True while this box is the selected object and takes typing.</summary>
        public bool HasKeyboard => _input.IsSelected && _input.Interactable;

        /// <summary>True while its input method composes (or just did).</summary>
        public bool ComposingRecently => _input.ComposingRecently;

        /// <summary>Gives the keyboard back now.</summary>
        public void ReleaseNow()
        {
            _input.Release();
            _model.Report(_input.Text, false, false);
        }

        protected override void Apply(double now)
        {
            if (_model.TakePendingText(out var text))
            {
                _input.SetText(text);
            }

            _input.Placeholder = _model.Placeholder;
            _input.Interactable = _model.Enabled;

            switch (_model.TakeFocusRequest())
            {
                case FocusRequest.Focus:
                    if (_model.Enabled)
                    {
                        _input.Focus();
                    }

                    break;
                case FocusRequest.Release:
                    _input.Release();
                    break;
            }
        }

        protected override void Frame(double now) =>
            _model.Report(_input.Text, _input.IsSelected, _input.ComposingRecently);

        protected override void OnHiding() => ReleaseNow();
    }

    /// <summary>A button row: game-style pills left to right, each as wide as its label needs.</summary>
    internal sealed class ButtonsPartView : PartView
    {
        private readonly ButtonRowModel _model;
        private readonly List<KeyValuePair<string, GameStyleButton>> _buttons = new List<KeyValuePair<string, GameStyleButton>>();
        private float _laidOutWidth = -1f;
        private bool _dirty = true;

        public ButtonsPartView(ButtonRowModel model, GameStyleWindow frame)
            : base(model, GameStyleUi.Child(frame.Body, "Buttons " + model.Id))
        {
            _model = model;
            foreach (var state in model.Snapshot())
            {
                var id = state.Id;
                var button = frame.AddButton(Rect, state.Label);
                button.Clicked += () => model.Click(id);
                _buttons.Add(new KeyValuePair<string, GameStyleButton>(id, button));
            }
        }

        protected override void Apply(double now)
        {
            var states = _model.Snapshot();
            for (var i = 0; i < states.Length && i < _buttons.Count; i++)
            {
                var button = _buttons[i].Value;
                button.Label = states[i].Label;
                button.Visible = states[i].Visible;
            }

            _dirty = true;
        }

        protected override void Frame(double now)
        {
            var width = Rect.rect.width;
            if (!_dirty && Mathf.Approximately(width, _laidOutWidth))
            {
                return;
            }

            _dirty = false;
            _laidOutWidth = width;
            var x = 0f;
            foreach (var pair in _buttons)
            {
                var button = pair.Value;
                if (!button.Visible)
                {
                    continue;
                }

                var w = button.PreferredWidth;
                var rect = button.Rect;
                rect.anchorMin = new Vector2(0f, 0f);
                rect.anchorMax = new Vector2(0f, 1f);
                rect.pivot = new Vector2(0f, 0.5f);
                rect.anchoredPosition = new Vector2(x, 0f);
                rect.sizeDelta = new Vector2(w, 0f);
                x += w + ButtonGap;
            }
        }

        private const float ButtonGap = 8f;
    }

    /// <summary>A tab strip on <see cref="GameStyleTabStrip"/>.</summary>
    internal sealed class TabsPartView : PartView
    {
        private readonly TabStripModel _model;
        private readonly GameStyleTabStrip _strip;
        private readonly string[] _labels;

        public TabsPartView(TabStripModel model, GameStyleTabStrip strip)
            : base(model, strip.Rect)
        {
            _model = model;
            _strip = strip;
            _labels = new string[model.Labels.Count];
            for (var i = 0; i < _labels.Length; i++)
            {
                _labels[i] = model.Labels[i];
            }

            _strip.TabClicked += model.Pick;
        }

        protected override void Apply(double now) => _strip.SetTabs(_labels, _model.Selected);

        protected override void Frame(double now) => _strip.Layout();
    }
}
