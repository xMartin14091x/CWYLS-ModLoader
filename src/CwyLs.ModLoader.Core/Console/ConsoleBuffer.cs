using System;
using System.Collections.Generic;

namespace CwyLs.ModLoader.Core.Console
{
    /// <summary>What a console line is, which is how it is coloured.</summary>
    public enum ConsoleLineKind
    {
        /// <summary>A command's result.</summary>
        Output,

        /// <summary>The command the user typed, echoed back.</summary>
        Echo,

        /// <summary>A mirrored Info log line.</summary>
        Info,

        /// <summary>A mirrored Warning log line.</summary>
        Warning,

        /// <summary>A mirrored Error or Fatal log line, or a command that failed.</summary>
        Error
    }

    /// <summary>One line in the console's output.</summary>
    public sealed class ConsoleLine
    {
        /// <summary>Creates a line.</summary>
        /// <param name="text">The text; null becomes empty.</param>
        /// <param name="kind">What kind of line it is.</param>
        /// <param name="sequence">Its position in the session's total output.</param>
        public ConsoleLine(string? text, ConsoleLineKind kind, long sequence)
        {
            Text = text ?? string.Empty;
            Kind = kind;
            Sequence = sequence;
        }

        /// <summary>The text of the line. Never null.</summary>
        public string Text { get; }

        /// <summary>What kind of line it is.</summary>
        public ConsoleLineKind Kind { get; }

        /// <summary>
        /// A number that only ever grows, counting every line written this session. The renderer
        /// compares the newest sequence against what it drew last frame to decide whether to
        /// scroll to the bottom.
        /// </summary>
        public long Sequence { get; }

        /// <inheritdoc />
        public override string ToString() => Text;
    }

    /// <summary>
    /// The console's scrollback: a ring of the last <see cref="Capacity"/> lines. Unity-free and
    /// thread-safe, because BepInEx log events arrive on whatever thread logged them while the
    /// renderer reads on the main thread.
    /// </summary>
    public sealed class ConsoleBuffer
    {
        /// <summary>Lines kept when no capacity is given.</summary>
        public const int DefaultCapacity = 300;

        private readonly object _gate = new object();
        private readonly Queue<ConsoleLine> _lines;

        private long _written;

        /// <summary>Creates a buffer.</summary>
        /// <param name="capacity">How many lines to keep. Must be positive.</param>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="capacity"/> is not positive.</exception>
        public ConsoleBuffer(int capacity = DefaultCapacity)
        {
            if (capacity <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "a buffer keeps at least one line");
            }

            Capacity = capacity;
            _lines = new Queue<ConsoleLine>(Math.Min(capacity, 64));
        }

        /// <summary>How many lines this buffer keeps.</summary>
        public int Capacity { get; }

        /// <summary>How many lines it currently holds.</summary>
        public int Count
        {
            get { lock (_gate) { return _lines.Count; } }
        }

        /// <summary>How many lines have been written this session, including those dropped.</summary>
        public long TotalWritten
        {
            get { lock (_gate) { return _written; } }
        }

        /// <summary>Sequence number of the newest line, or 0 when the buffer is empty.</summary>
        public long LastSequence
        {
            get { lock (_gate) { return _written; } }
        }

        /// <summary>
        /// Appends one line. Text containing newlines is split, so a multi-line command result
        /// scrolls and colours like the rest of the output.
        /// </summary>
        /// <param name="text">The text; null or empty appends one empty line.</param>
        /// <param name="kind">What kind of line it is.</param>
        public void Add(string? text, ConsoleLineKind kind = ConsoleLineKind.Output)
        {
            if (string.IsNullOrEmpty(text))
            {
                Append(string.Empty, kind);
                return;
            }

            if (text!.IndexOf('\n') < 0 && text.IndexOf('\r') < 0)
            {
                Append(text, kind);
                return;
            }

            foreach (var line in text.Split('\n'))
            {
                Append(line.TrimEnd('\r'), kind);
            }
        }

        /// <summary>The lines it holds, oldest first. A detached copy. Never null.</summary>
        public IReadOnlyList<ConsoleLine> Snapshot()
        {
            lock (_gate)
            {
                return _lines.ToArray();
            }
        }

        /// <summary>The last <paramref name="count"/> lines, oldest first. Never null.</summary>
        /// <param name="count">How many lines to take; 0 or less yields none.</param>
        public IReadOnlyList<ConsoleLine> Tail(int count)
        {
            if (count <= 0)
            {
                return Array.Empty<ConsoleLine>();
            }

            lock (_gate)
            {
                if (_lines.Count <= count)
                {
                    return _lines.ToArray();
                }

                var all = _lines.ToArray();
                var tail = new ConsoleLine[count];
                Array.Copy(all, all.Length - count, tail, 0, count);
                return tail;
            }
        }

        /// <summary>The whole buffer as text, one line per line. Never null.</summary>
        public string ToText()
        {
            var lines = Snapshot();
            var sb = new System.Text.StringBuilder();
            foreach (var line in lines)
            {
                sb.Append(line.Text).Append('\n');
            }

            return sb.ToString();
        }

        /// <summary>Empties the buffer. The sequence counter keeps running.</summary>
        public void Clear()
        {
            lock (_gate)
            {
                _lines.Clear();
            }
        }

        private void Append(string text, ConsoleLineKind kind)
        {
            lock (_gate)
            {
                _written++;
                _lines.Enqueue(new ConsoleLine(text, kind, _written));
                while (_lines.Count > Capacity)
                {
                    _lines.Dequeue();
                }
            }
        }
    }
}
