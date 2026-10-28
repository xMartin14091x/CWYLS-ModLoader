using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;

namespace CwyLs.ModLoader.Core.Ui
{
    /// <summary>One bubble as the view draws it.</summary>
    public readonly struct ChatBubbleState
    {
        /// <summary>Creates a state.</summary>
        /// <param name="id">The bubble's id.</param>
        /// <param name="role">Whose bubble.</param>
        /// <param name="text">Its text.</param>
        /// <param name="typing">The typing indicator is on.</param>
        /// <param name="buttons">Its buttons.</param>
        /// <param name="version">Moves whenever the bubble changed.</param>
        public ChatBubbleState(long id, CwyChatRole role, string text, bool typing, CwyButtonSpec[] buttons, int version)
        {
            Id = id;
            Role = role;
            Text = text;
            Typing = typing;
            Buttons = buttons;
            Version = version;
        }

        /// <summary>The bubble's id.</summary>
        public long Id { get; }

        /// <summary>Whose bubble.</summary>
        public CwyChatRole Role { get; }

        /// <summary>Its text.</summary>
        public string Text { get; }

        /// <summary>The typing indicator is on.</summary>
        public bool Typing { get; }

        /// <summary>Its buttons.</summary>
        public CwyButtonSpec[] Buttons { get; }

        /// <summary>Moves whenever the bubble changed.</summary>
        public int Version { get; }
    }

    /// <summary>
    /// The state of a chat part (<see cref="ICwyChatView"/>): the bubbles, oldest first, capped at the
    /// part's <c>MaxBubbles</c>. Thread-safe: one lock for the part and its bubbles.
    /// </summary>
    public sealed class ChatViewModel : PartModel, ICwyChatView
    {
        /// <summary>Most characters one bubble holds; text past it is dropped (<see cref="ICwyChatBubble.IsTruncated"/>).</summary>
        public const int MaxBubbleChars = 8000;

        /// <summary>Most buttons on one bubble.</summary>
        public const int MaxBubbleButtons = 6;

        private readonly List<ChatBubble> _bubbles = new List<ChatBubble>();
        private readonly int _max;
        private long _nextId;
        private int _scrollToEnd;
        private volatile bool _following = true;

        /// <summary>Creates the model.</summary>
        /// <param name="spec">The part's spec.</param>
        /// <param name="run">Runs event handlers.</param>
        public ChatViewModel(CwyChatSpec spec, UiEventRunner run)
            : base(spec, run)
        {
            _max = Math.Max(1, spec.MaxBubbles);
        }

        /// <inheritdoc />
        public event Action<ICwyChatBubble, string>? ButtonClicked;

        /// <inheritdoc />
        public int Count
        {
            get
            {
                lock (SyncRoot)
                {
                    return _bubbles.Count;
                }
            }
        }

        /// <inheritdoc />
        public IReadOnlyList<ICwyChatBubble> Bubbles
        {
            get
            {
                lock (SyncRoot)
                {
                    return _bubbles.ToArray();
                }
            }
        }

        /// <inheritdoc />
        public bool IsFollowing => _following;

        /// <inheritdoc />
        public ICwyChatBubble Add(CwyChatRole role, string? text = null)
        {
            ChatBubble bubble;
            lock (SyncRoot)
            {
                bubble = new ChatBubble(this, ++_nextId, role, text);
                _bubbles.Add(bubble);
                while (_bubbles.Count > _max)
                {
                    _bubbles[0].Kill();
                    _bubbles.RemoveAt(0);
                }
            }

            if (role == CwyChatRole.Player)
            {
                // The player just wrote: show it, whatever they were reading.
                Interlocked.Exchange(ref _scrollToEnd, 1);
            }

            Touch();
            return bubble;
        }

        /// <inheritdoc />
        public void Clear()
        {
            lock (SyncRoot)
            {
                foreach (var bubble in _bubbles)
                {
                    bubble.Kill();
                }

                _bubbles.Clear();
            }

            Touch();
        }

        /// <inheritdoc />
        public void ScrollToEnd()
        {
            Interlocked.Exchange(ref _scrollToEnd, 1);
            Touch();
        }

        /// <summary>Every bubble as it should be drawn, oldest first. View side.</summary>
        public ChatBubbleState[] Snapshot()
        {
            lock (SyncRoot)
            {
                var states = new ChatBubbleState[_bubbles.Count];
                for (var i = 0; i < states.Length; i++)
                {
                    states[i] = _bubbles[i].StateLocked();
                }

                return states;
            }
        }

        /// <summary>True once per <see cref="ScrollToEnd"/> (or player bubble). View side.</summary>
        public bool TakeScrollToEnd() => Interlocked.Exchange(ref _scrollToEnd, 0) == 1;

        /// <summary>Records whether the view follows the newest bubble. View side.</summary>
        /// <param name="following">True when following.</param>
        public void ReportFollowing(bool following) => _following = following;

        /// <summary>The player clicked a bubble's button: <see cref="ButtonClicked"/> runs. View side.</summary>
        /// <param name="bubbleId">The bubble.</param>
        /// <param name="buttonId">The button.</param>
        public void Click(long bubbleId, string buttonId)
        {
            ChatBubble? bubble = null;
            lock (SyncRoot)
            {
                foreach (var candidate in _bubbles)
                {
                    if (candidate.Id == bubbleId)
                    {
                        bubble = candidate;
                        break;
                    }
                }
            }

            if (bubble is null)
            {
                return;
            }

            Raise("ButtonClicked", ButtonClicked, handlers => handlers(bubble, buttonId));
        }

        /// <summary>A bubble changed.</summary>
        internal void BubbleChanged() => Touch();

        /// <summary>One bubble; every member takes the chat part's lock.</summary>
        private sealed class ChatBubble : ICwyChatBubble
        {
            private readonly ChatViewModel _owner;
            private readonly StringBuilder _text = new StringBuilder();
            private readonly List<CwyButtonSpec> _buttons = new List<CwyButtonSpec>();
            private string? _cache;
            private bool _typing;
            private bool _alive = true;
            private bool _truncated;
            private object? _tag;
            private int _version;

            internal ChatBubble(ChatViewModel owner, long id, CwyChatRole role, string? text)
            {
                _owner = owner;
                Id = id;
                Role = role;
                AppendLocked(text);
            }

            public long Id { get; }

            public CwyChatRole Role { get; }

            public string Text
            {
                get
                {
                    lock (_owner.SyncRoot)
                    {
                        return _cache ??= _text.ToString();
                    }
                }
            }

            public bool Typing
            {
                get
                {
                    lock (_owner.SyncRoot)
                    {
                        return _typing;
                    }
                }

                set
                {
                    Change(() =>
                    {
                        if (_typing == value)
                        {
                            return false;
                        }

                        _typing = value;
                        return true;
                    });
                }
            }

            public bool IsAlive
            {
                get
                {
                    lock (_owner.SyncRoot)
                    {
                        return _alive;
                    }
                }
            }

            public bool IsTruncated
            {
                get
                {
                    lock (_owner.SyncRoot)
                    {
                        return _truncated;
                    }
                }
            }

            public object? Tag
            {
                get
                {
                    lock (_owner.SyncRoot)
                    {
                        return _tag;
                    }
                }

                set
                {
                    lock (_owner.SyncRoot)
                    {
                        _tag = value;
                    }
                }
            }

            public IReadOnlyList<CwyButtonSpec> Buttons
            {
                get
                {
                    lock (_owner.SyncRoot)
                    {
                        return _buttons.ToArray();
                    }
                }
            }

            public void Append(string text)
            {
                if (string.IsNullOrEmpty(text))
                {
                    return;
                }

                Change(() => AppendLocked(text));
            }

            public void SetText(string? text) =>
                Change(() =>
                {
                    _text.Clear();
                    _truncated = false;
                    AppendLocked(text);
                    return true;
                });

            public void AddButton(string id, string label)
            {
                if (!CwyWindowSpecRules.IsValidId(id))
                {
                    throw new ArgumentException(
                        "a bubble button id must be 1-48 letters, digits, '.', '_' or '-', not starting with '.'", nameof(id));
                }

                var refused = false;
                Change(() =>
                {
                    for (var i = 0; i < _buttons.Count; i++)
                    {
                        if (string.Equals(_buttons[i].Id, id, StringComparison.OrdinalIgnoreCase))
                        {
                            _buttons[i] = new CwyButtonSpec(_buttons[i].Id, label ?? string.Empty);
                            return true;
                        }
                    }

                    if (_buttons.Count >= MaxBubbleButtons)
                    {
                        refused = true;
                        return false;
                    }

                    _buttons.Add(new CwyButtonSpec(id, label ?? string.Empty));
                    return true;
                });

                if (refused)
                {
                    throw new InvalidOperationException("a bubble has at most " + MaxBubbleButtons + " buttons");
                }
            }

            public bool RemoveButton(string id)
            {
                var removed = false;
                Change(() =>
                {
                    removed = _buttons.RemoveAll(b => string.Equals(b.Id, id, StringComparison.OrdinalIgnoreCase)) > 0;
                    return removed;
                });

                return removed;
            }

            public override string ToString() => "bubble " + Id + " (" + Role + ")";

            /// <summary>Marks the bubble dropped. Caller holds the lock.</summary>
            internal void Kill() => _alive = false;

            /// <summary>The bubble as drawn. Caller holds the lock.</summary>
            internal ChatBubbleState StateLocked() =>
                new ChatBubbleState(Id, Role, _cache ??= _text.ToString(), _typing, _buttons.ToArray(), _version);

            /// <summary>Applies a change under the lock when the bubble is alive; tells the view when it changed something.</summary>
            private void Change(Func<bool> change)
            {
                bool changed;
                lock (_owner.SyncRoot)
                {
                    if (!_alive)
                    {
                        return;
                    }

                    changed = change();
                    if (changed)
                    {
                        _cache = null;
                        _version++;
                    }
                }

                if (changed)
                {
                    _owner.BubbleChanged();
                }
            }

            /// <summary>Adds text up to the limit. Caller holds the lock (or is the constructor).</summary>
            private bool AppendLocked(string? text)
            {
                if (string.IsNullOrEmpty(text))
                {
                    return false;
                }

                var room = MaxBubbleChars - _text.Length;
                if (room <= 0)
                {
                    _truncated = true;
                    return false;
                }

                var piece = text!.Replace("\r", string.Empty);
                if (piece.Length > room)
                {
                    // Never split a surrogate pair at the limit.
                    var cut = room;
                    if (cut > 0 && char.IsHighSurrogate(piece[cut - 1]))
                    {
                        cut--;
                    }

                    piece = piece.Substring(0, cut);
                    _truncated = true;
                }

                _text.Append(piece);
                return piece.Length > 0;
            }
        }
    }
}
