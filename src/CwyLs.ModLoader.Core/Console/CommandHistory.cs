using System;
using System.Collections.Generic;

namespace CwyLs.ModLoader.Core.Console
{
    /// <summary>
    /// The lines already typed, and the Up/Down cursor over them. A ring: once
    /// <see cref="Capacity"/> lines are held, the oldest is dropped.
    /// <para>
    /// The draft matters as much as the history: pressing Up must not lose the half-typed line,
    /// so the first Up remembers it and coming back down past the newest entry restores it.
    /// </para>
    /// </summary>
    public sealed class CommandHistory
    {
        /// <summary>Lines kept when no capacity is given.</summary>
        public const int DefaultCapacity = 100;

        private readonly object _gate = new object();
        private readonly List<string> _entries;

        // -1 = the user is on the live line they are typing; 0 = the newest entry, 1 the one
        // before it, and so on.
        private int _cursor = -1;
        private string _draft = string.Empty;

        /// <summary>Creates a history.</summary>
        /// <param name="capacity">How many lines to keep. Must be positive.</param>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="capacity"/> is not positive.</exception>
        public CommandHistory(int capacity = DefaultCapacity)
        {
            if (capacity <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "a history keeps at least one line");
            }

            Capacity = capacity;
            _entries = new List<string>(Math.Min(capacity, 32));
        }

        /// <summary>How many lines this history keeps.</summary>
        public int Capacity { get; }

        /// <summary>How many lines it currently holds.</summary>
        public int Count
        {
            get { lock (_gate) { return _entries.Count; } }
        }

        /// <summary>The lines it holds, oldest first. Never null.</summary>
        public IReadOnlyList<string> Entries
        {
            get { lock (_gate) { return _entries.ToArray(); } }
        }

        /// <summary>
        /// Records a submitted line and puts the cursor back on the live line. A blank line is
        /// ignored, and so is a line identical to the most recent one - holding Enter on the same
        /// command should not push everything else out of the ring.
        /// </summary>
        /// <param name="line">The line that was submitted.</param>
        /// <returns>True when the line was added.</returns>
        public bool Add(string? line)
        {
            lock (_gate)
            {
                _cursor = -1;
                _draft = string.Empty;

                if (string.IsNullOrWhiteSpace(line))
                {
                    return false;
                }

                if (_entries.Count > 0 &&
                    string.Equals(_entries[_entries.Count - 1], line, StringComparison.Ordinal))
                {
                    return false;
                }

                _entries.Add(line!);
                if (_entries.Count > Capacity)
                {
                    _entries.RemoveAt(0);
                }

                return true;
            }
        }

        /// <summary>
        /// Up: the entry before the one shown now. The first call remembers
        /// <paramref name="currentLine"/> as the draft. Returns null when there is nothing older,
        /// which the caller should read as "leave the input alone".
        /// </summary>
        /// <param name="currentLine">What is in the input box right now.</param>
        public string? Previous(string? currentLine)
        {
            lock (_gate)
            {
                if (_entries.Count == 0)
                {
                    return null;
                }

                if (_cursor < 0)
                {
                    _draft = currentLine ?? string.Empty;
                    _cursor = 0;
                }
                else if (_cursor + 1 >= _entries.Count)
                {
                    return _entries[0];
                }
                else
                {
                    _cursor++;
                }

                return _entries[_entries.Count - 1 - _cursor];
            }
        }

        /// <summary>
        /// Down: the entry after the one shown now, or the remembered draft once the cursor comes
        /// back past the newest entry. Returns null when the cursor is already on the live line.
        /// </summary>
        public string? Next()
        {
            lock (_gate)
            {
                if (_cursor < 0)
                {
                    return null;
                }

                _cursor--;
                if (_cursor < 0)
                {
                    var draft = _draft;
                    _draft = string.Empty;
                    return draft;
                }

                return _entries[_entries.Count - 1 - _cursor];
            }
        }

        /// <summary>Puts the cursor back on the live line and forgets the draft.</summary>
        public void ResetCursor()
        {
            lock (_gate)
            {
                _cursor = -1;
                _draft = string.Empty;
            }
        }

        /// <summary>Forgets every line.</summary>
        public void Clear()
        {
            lock (_gate)
            {
                _entries.Clear();
                _cursor = -1;
                _draft = string.Empty;
            }
        }
    }
}
