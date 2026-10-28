using System;
using System.Collections.Generic;
using System.Threading;

namespace CwyLs.ModLoader.Core.Ui
{
    /// <summary>
    /// The state behind one part of a mod window: what mods change (from any thread) and what the
    /// game adapter's view draws (on the main thread, when <see cref="Version"/> moved). Framework
    /// plumbing - mods get these as their <c>ICwy*</c> interfaces.
    /// </summary>
    public abstract class PartModel : ICwyPart
    {
        private int _version;
        private int _visible = 1;

        /// <summary>Creates the model of a part.</summary>
        /// <param name="spec">The part's spec.</param>
        /// <param name="run">Runs the mod's event handlers with its attribution.</param>
        protected PartModel(CwyPartSpec spec, UiEventRunner run)
        {
            if (spec is null)
            {
                throw new ArgumentNullException(nameof(spec));
            }

            Id = spec.Id;
            Kind = spec.Kind;
            Tab = spec.Tab;
            FixedHeight = CwyWindowSpecRules.FixedHeight(spec);
            Run = run ?? throw new ArgumentNullException(nameof(run));
        }

        /// <inheritdoc />
        public string Id { get; }

        /// <inheritdoc />
        public CwyPartKind Kind { get; }

        /// <summary>The tab the part shows on, or -1 for every tab.</summary>
        public int Tab { get; }

        /// <summary>The part's fixed height, or 0 when it shares the leftover room.</summary>
        public float FixedHeight { get; }

        /// <inheritdoc />
        public bool Visible
        {
            get => Volatile.Read(ref _visible) == 1;
            set
            {
                var wanted = value ? 1 : 0;
                if (Interlocked.Exchange(ref _visible, wanted) != wanted)
                {
                    Touch();
                }
            }
        }

        /// <summary>Moves on every change; the view redraws when it differs from what it drew.</summary>
        public int Version => Volatile.Read(ref _version);

        /// <summary>The lock every member of this part takes.</summary>
        internal object SyncRoot { get; } = new object();

        /// <summary>Runs the mod's event handlers with its attribution.</summary>
        protected UiEventRunner Run { get; }

        /// <summary>Marks the part changed.</summary>
        protected internal void Touch() => Interlocked.Increment(ref _version);

        /// <summary>Raises an event through <see cref="Run"/> when it has handlers. Never throws.</summary>
        /// <param name="label">What the event is, for the log.</param>
        /// <param name="handlers">The event's handlers, or null.</param>
        /// <param name="invoke">Calls the handlers.</param>
        protected void Raise<T>(string label, T? handlers, Action<T> invoke)
            where T : Delegate
        {
            if (handlers is null)
            {
                return;
            }

            Run(Id + " " + label, () => invoke(handlers));
        }
    }

    /// <summary>What a text view draws: the lines, how many were appended since the last snapshot, and the content version.</summary>
    public readonly struct TextSnapshot
    {
        /// <summary>Creates a snapshot.</summary>
        /// <param name="lines">The lines.</param>
        /// <param name="appended">Lines appended since the last snapshot (keeps a scrolled-up view in place).</param>
        /// <param name="contentVersion">Moves when existing lines changed (replaced, cleared, oldest dropped).</param>
        public TextSnapshot(string[] lines, int appended, long contentVersion)
        {
            Lines = lines;
            Appended = appended;
            ContentVersion = contentVersion;
        }

        /// <summary>The lines.</summary>
        public string[] Lines { get; }

        /// <summary>Lines appended since the last snapshot.</summary>
        public int Appended { get; }

        /// <summary>Moves when existing lines changed.</summary>
        public long ContentVersion { get; }
    }

    /// <summary>A pending scroll request of a view.</summary>
    public enum ScrollRequest
    {
        /// <summary>None.</summary>
        None = 0,

        /// <summary>Jump to the end and follow.</summary>
        End = 1,

        /// <summary>Jump to the start.</summary>
        Start = 2,
    }

    /// <summary>The state of a text part (<see cref="ICwyTextView"/>). Thread-safe.</summary>
    public sealed class TextViewModel : PartModel, ICwyTextView
    {
        private readonly List<string> _lines = new List<string>();
        private readonly int _max;
        private int _appended;
        private long _contentVersion;
        private int _scroll;
        private volatile bool _following = true;

        /// <summary>Creates the model.</summary>
        /// <param name="spec">The part's spec.</param>
        /// <param name="run">Runs event handlers.</param>
        public TextViewModel(CwyTextSpec spec, UiEventRunner run)
            : base(spec, run)
        {
            _max = Math.Max(1, spec.MaxLines);
        }

        /// <inheritdoc />
        public int Count
        {
            get
            {
                lock (SyncRoot)
                {
                    return _lines.Count;
                }
            }
        }

        /// <inheritdoc />
        public bool IsFollowing => _following;

        /// <inheritdoc />
        public void SetLines(IReadOnlyList<string>? lines)
        {
            lock (SyncRoot)
            {
                _lines.Clear();
                if (lines != null)
                {
                    foreach (var line in lines)
                    {
                        AddSplit(line);
                    }
                }

                Trim();
                _appended = 0;
                _contentVersion++;
            }

            Touch();
        }

        /// <inheritdoc />
        public void AppendLine(string line)
        {
            lock (SyncRoot)
            {
                _appended += AddSplit(line);
                Trim();
            }

            Touch();
        }

        /// <inheritdoc />
        public void Clear() => SetLines(null);

        /// <inheritdoc />
        public void ScrollToEnd()
        {
            Interlocked.Exchange(ref _scroll, (int)ScrollRequest.End);
            Touch();
        }

        /// <inheritdoc />
        public void ScrollToStart()
        {
            Interlocked.Exchange(ref _scroll, (int)ScrollRequest.Start);
            Touch();
        }

        /// <summary>The lines to draw; resets the appended count. View side.</summary>
        public TextSnapshot Take()
        {
            lock (SyncRoot)
            {
                var snapshot = new TextSnapshot(_lines.ToArray(), _appended, _contentVersion);
                _appended = 0;
                return snapshot;
            }
        }

        /// <summary>The pending scroll request, cleared. View side.</summary>
        public ScrollRequest TakeScrollRequest() => (ScrollRequest)Interlocked.Exchange(ref _scroll, 0);

        /// <summary>Records whether the view follows the newest line. View side.</summary>
        /// <param name="following">True when following.</param>
        public void ReportFollowing(bool following) => _following = following;

        private int AddSplit(string? line)
        {
            var text = (line ?? string.Empty).Replace("\r", string.Empty);
            if (text.IndexOf('\n') < 0)
            {
                _lines.Add(text);
                return 1;
            }

            var parts = text.Split('\n');
            _lines.AddRange(parts);
            return parts.Length;
        }

        private void Trim()
        {
            if (_lines.Count <= _max)
            {
                return;
            }

            // The appended count stays: a view scrolled up keeps its distance from the newest line,
            // and the content version tells it the indices moved (as the console's scrollback does).
            _lines.RemoveRange(0, _lines.Count - _max);
            _contentVersion++;
        }
    }

    /// <summary>The state of a list part (<see cref="ICwyListView"/>). Thread-safe.</summary>
    public sealed class ListViewModel : PartModel, ICwyListView
    {
        private string[] _items = Array.Empty<string>();
        private int _selected = -1;
        private int _scrollTo = -1;

        /// <summary>Creates the model.</summary>
        /// <param name="spec">The part's spec.</param>
        /// <param name="run">Runs event handlers.</param>
        public ListViewModel(CwyListSpec spec, UiEventRunner run)
            : base(spec, run)
        {
        }

        /// <inheritdoc />
        public event Action<int>? ItemClicked;

        /// <inheritdoc />
        public int Count
        {
            get
            {
                lock (SyncRoot)
                {
                    return _items.Length;
                }
            }
        }

        /// <inheritdoc />
        public IReadOnlyList<string> Items
        {
            get
            {
                lock (SyncRoot)
                {
                    return (string[])_items.Clone();
                }
            }
        }

        /// <inheritdoc />
        public int Selected
        {
            get
            {
                lock (SyncRoot)
                {
                    return _selected;
                }
            }

            set
            {
                lock (SyncRoot)
                {
                    _selected = value >= 0 && value < _items.Length ? value : -1;
                }

                Touch();
            }
        }

        /// <inheritdoc />
        public void SetItems(IReadOnlyList<string>? items)
        {
            var copy = new string[items?.Count ?? 0];
            for (var i = 0; i < copy.Length; i++)
            {
                copy[i] = (items![i] ?? string.Empty).Replace("\r", " ").Replace("\n", " ");
            }

            lock (SyncRoot)
            {
                _items = copy;
                _selected = -1;
            }

            Touch();
        }

        /// <inheritdoc />
        public void ScrollTo(int index)
        {
            if (index < 0)
            {
                return;
            }

            Interlocked.Exchange(ref _scrollTo, index);
            Touch();
        }

        /// <summary>The items and the selection to draw. View side.</summary>
        /// <param name="selected">The selection.</param>
        public string[] Snapshot(out int selected)
        {
            lock (SyncRoot)
            {
                selected = _selected;
                return _items;
            }
        }

        /// <summary>The item to scroll to, or -1; cleared. View side.</summary>
        public int TakeScrollRequest() => Interlocked.Exchange(ref _scrollTo, -1);

        /// <summary>The player clicked an item: it is selected and <see cref="ItemClicked"/> runs. View side.</summary>
        /// <param name="index">The item.</param>
        public void Click(int index)
        {
            lock (SyncRoot)
            {
                if (index < 0 || index >= _items.Length)
                {
                    return;
                }

                _selected = index;
            }

            Touch();
            Raise("ItemClicked", ItemClicked, handlers => handlers(index));
        }
    }

    /// <summary>What a text box should do with the keyboard at the next frame.</summary>
    public enum FocusRequest
    {
        /// <summary>Nothing.</summary>
        None = 0,

        /// <summary>Take the keyboard.</summary>
        Focus = 1,

        /// <summary>Give it back.</summary>
        Release = 2,
    }

    /// <summary>The state of an input box (<see cref="ICwyInputBox"/>). Thread-safe.</summary>
    public sealed class InputBoxModel : PartModel, ICwyInputBox
    {
        private string _text = string.Empty;
        private string? _pendingText;
        private string _placeholder;
        private volatile bool _enabled = true;
        private volatile bool _focused;
        private volatile bool _composing;
        private int _focusRequest;

        /// <summary>Creates the model.</summary>
        /// <param name="spec">The part's spec.</param>
        /// <param name="run">Runs event handlers.</param>
        public InputBoxModel(CwyInputSpec spec, UiEventRunner run)
            : base(spec, run)
        {
            Lines = Math.Max(1, spec.Lines);
            MaxLength = Math.Max(0, spec.MaxLength);
            SubmitOnEnter = spec.SubmitOnEnter;
            ClearOnSubmit = spec.ClearOnSubmit;
            _placeholder = spec.Placeholder ?? string.Empty;
        }

        /// <inheritdoc />
        public event Action<string>? Submitted;

        /// <summary>Visible lines.</summary>
        public int Lines { get; }

        /// <summary>Most characters, 0 for no limit.</summary>
        public int MaxLength { get; }

        /// <summary>Enter sends (true) or breaks the line.</summary>
        public bool SubmitOnEnter { get; }

        /// <summary>The box empties itself after sending.</summary>
        public bool ClearOnSubmit { get; }

        /// <inheritdoc />
        public string Text
        {
            get
            {
                lock (SyncRoot)
                {
                    return _pendingText ?? _text;
                }
            }

            set
            {
                lock (SyncRoot)
                {
                    _pendingText = value ?? string.Empty;
                }

                Touch();
            }
        }

        /// <inheritdoc />
        public string Placeholder
        {
            get
            {
                lock (SyncRoot)
                {
                    return _placeholder;
                }
            }

            set
            {
                lock (SyncRoot)
                {
                    _placeholder = value ?? string.Empty;
                }

                Touch();
            }
        }

        /// <inheritdoc />
        public bool Enabled
        {
            get => _enabled;
            set
            {
                if (_enabled != value)
                {
                    _enabled = value;
                    Touch();
                }
            }
        }

        /// <inheritdoc />
        public bool IsFocused => _focused;

        /// <inheritdoc />
        public bool IsComposing => _composing;

        /// <inheritdoc />
        public void Focus()
        {
            Interlocked.Exchange(ref _focusRequest, (int)FocusRequest.Focus);
            Touch();
        }

        /// <inheritdoc />
        public void Release()
        {
            Interlocked.Exchange(ref _focusRequest, (int)FocusRequest.Release);
            Touch();
        }

        /// <summary>Text a mod set that the box has not shown yet; cleared. View side.</summary>
        /// <param name="text">The text.</param>
        public bool TakePendingText(out string text)
        {
            lock (SyncRoot)
            {
                if (_pendingText is null)
                {
                    text = string.Empty;
                    return false;
                }

                text = _pendingText;
                _text = _pendingText;
                _pendingText = null;
                return true;
            }
        }

        /// <summary>The pending keyboard request, cleared. View side.</summary>
        public FocusRequest TakeFocusRequest() => (FocusRequest)Interlocked.Exchange(ref _focusRequest, 0);

        /// <summary>Records what the box shows and whether it has the keyboard. View side, once a frame.</summary>
        /// <param name="text">The box's text.</param>
        /// <param name="focused">It has the keyboard.</param>
        /// <param name="composing">Its input method is composing.</param>
        public void Report(string text, bool focused, bool composing)
        {
            lock (SyncRoot)
            {
                if (_pendingText is null)
                {
                    _text = text ?? string.Empty;
                }
            }

            _focused = focused;
            _composing = composing;
        }

        /// <summary>The player sent the text: <see cref="Submitted"/> runs. View side.</summary>
        /// <param name="text">The text sent.</param>
        public void RaiseSubmitted(string text)
        {
            var sent = text ?? string.Empty;
            if (ClearOnSubmit)
            {
                Text = string.Empty;
            }

            Raise("Submitted", Submitted, handlers => handlers(sent));
        }
    }

    /// <summary>One button of a row as drawn: id, label, shown.</summary>
    public readonly struct ButtonState
    {
        /// <summary>Creates a state.</summary>
        /// <param name="id">The button's id.</param>
        /// <param name="label">Its label.</param>
        /// <param name="visible">True when shown.</param>
        public ButtonState(string id, string label, bool visible)
        {
            Id = id;
            Label = label;
            Visible = visible;
        }

        /// <summary>The button's id.</summary>
        public string Id { get; }

        /// <summary>Its label.</summary>
        public string Label { get; }

        /// <summary>True when shown.</summary>
        public bool Visible { get; }
    }

    /// <summary>The state of a button row (<see cref="ICwyButtonRow"/>). Thread-safe.</summary>
    public sealed class ButtonRowModel : PartModel, ICwyButtonRow
    {
        private readonly ButtonState[] _buttons;

        /// <summary>Creates the model.</summary>
        /// <param name="spec">The part's spec.</param>
        /// <param name="run">Runs event handlers.</param>
        public ButtonRowModel(CwyButtonsSpec spec, UiEventRunner run)
            : base(spec, run)
        {
            _buttons = new ButtonState[spec.Buttons.Count];
            for (var i = 0; i < _buttons.Length; i++)
            {
                _buttons[i] = new ButtonState(spec.Buttons[i].Id, spec.Buttons[i].Label, true);
            }
        }

        /// <inheritdoc />
        public event Action<string>? Clicked;

        /// <inheritdoc />
        public IReadOnlyList<CwyButtonSpec> Buttons
        {
            get
            {
                lock (SyncRoot)
                {
                    var list = new CwyButtonSpec[_buttons.Length];
                    for (var i = 0; i < list.Length; i++)
                    {
                        list[i] = new CwyButtonSpec(_buttons[i].Id, _buttons[i].Label);
                    }

                    return list;
                }
            }
        }

        /// <inheritdoc />
        public bool SetLabel(string id, string label) =>
            Change(id, state => new ButtonState(state.Id, label ?? string.Empty, state.Visible));

        /// <inheritdoc />
        public bool SetButtonVisible(string id, bool visible) =>
            Change(id, state => new ButtonState(state.Id, state.Label, visible));

        /// <summary>The buttons to draw. View side.</summary>
        public ButtonState[] Snapshot()
        {
            lock (SyncRoot)
            {
                return (ButtonState[])_buttons.Clone();
            }
        }

        /// <summary>The player clicked a button: <see cref="Clicked"/> runs. View side.</summary>
        /// <param name="id">The button's id.</param>
        public void Click(string id) => Raise("Clicked", Clicked, handlers => handlers(id));

        private bool Change(string id, Func<ButtonState, ButtonState> change)
        {
            var found = false;
            lock (SyncRoot)
            {
                for (var i = 0; i < _buttons.Length; i++)
                {
                    if (string.Equals(_buttons[i].Id, id, StringComparison.OrdinalIgnoreCase))
                    {
                        _buttons[i] = change(_buttons[i]);
                        found = true;
                    }
                }
            }

            if (found)
            {
                Touch();
            }

            return found;
        }
    }

    /// <summary>The state of a tab strip (<see cref="ICwyTabStrip"/>). Thread-safe.</summary>
    public sealed class TabStripModel : PartModel, ICwyTabStrip
    {
        private readonly string[] _labels;
        private int _selected;

        /// <summary>Creates the model.</summary>
        /// <param name="spec">The part's spec.</param>
        /// <param name="run">Runs event handlers.</param>
        public TabStripModel(CwyTabsSpec spec, UiEventRunner run)
            : base(spec, run)
        {
            _labels = new string[spec.Labels.Count];
            for (var i = 0; i < _labels.Length; i++)
            {
                _labels[i] = spec.Labels[i] ?? string.Empty;
            }

            _selected = spec.Selected >= 0 && spec.Selected < _labels.Length ? spec.Selected : 0;
        }

        /// <inheritdoc />
        public event Action<int>? SelectedChanged;

        /// <inheritdoc />
        public IReadOnlyList<string> Labels => (string[])_labels.Clone();

        /// <inheritdoc />
        public int Selected
        {
            get => Volatile.Read(ref _selected);
            set
            {
                if (value < 0 || value >= _labels.Length)
                {
                    return;
                }

                if (Interlocked.Exchange(ref _selected, value) != value)
                {
                    Touch();
                }
            }
        }

        /// <summary>The player picked a tab: it is selected and <see cref="SelectedChanged"/> runs. View side.</summary>
        /// <param name="index">The tab.</param>
        public void Pick(int index)
        {
            if (index < 0 || index >= _labels.Length || index == Selected)
            {
                return;
            }

            Selected = index;
            Raise("SelectedChanged", SelectedChanged, handlers => handlers(index));
        }
    }
}
