using System;
using System.Collections.Generic;

namespace CwyLs.ModLoader.Core.Scenario
{
    /// <summary>How a held row stopped waiting.</summary>
    public enum ScenarioWaitEndKind
    {
        /// <summary>The check passed.</summary>
        Done = 0,

        /// <summary>The timeout passed first; the scene goes on with a warning.</summary>
        TimedOut = 1,

        /// <summary>The check threw; counted as done, with a warning.</summary>
        Threw = 2,

        /// <summary>The scene moved on without us (it ended, was skipped or replaced); nothing continues.</summary>
        Abandoned = 3,
    }

    /// <summary>One finished wait. Immutable.</summary>
    public sealed class ScenarioWaitEnd
    {
        internal ScenarioWaitEnd(long ticket, string label, object? cursor, ScenarioWaitEndKind kind, string detail, double waitedSeconds)
        {
            Ticket = ticket;
            Label = label;
            Cursor = cursor;
            Kind = kind;
            Detail = detail;
            WaitedSeconds = waitedSeconds;
        }

        /// <summary>The ticket <see cref="ScenarioWaitBook.Add"/> returned.</summary>
        public long Ticket { get; }

        /// <summary>What was waiting (the row).</summary>
        public string Label { get; }

        /// <summary>The caller's marker of where the scene was (the game side's row cursor).</summary>
        public object? Cursor { get; }

        /// <summary>How it ended.</summary>
        public ScenarioWaitEndKind Kind { get; }

        /// <summary>The exception text for <see cref="ScenarioWaitEndKind.Threw"/>; empty otherwise.</summary>
        public string Detail { get; }

        /// <summary>How long it held, in seconds.</summary>
        public double WaitedSeconds { get; }
    }

    /// <summary>
    /// The rows a scene is holding on (<see cref="IScenarioCommandContext.WaitFor"/>): each with its
    /// check, deadline and the caller's cursor. The game side polls once a frame and continues the
    /// scene for each finished entry. Time is passed in (seconds, any monotonic clock), so this is
    /// testable. Thread-safe; checks run on the polling thread outside the lock.
    /// </summary>
    public sealed class ScenarioWaitBook
    {
        private readonly object _gate = new object();
        private readonly List<Entry> _entries = new List<Entry>();
        private long _nextTicket = 1;

        /// <summary>How many rows are holding.</summary>
        public int Count
        {
            get { lock (_gate) { return _entries.Count; } }
        }

        /// <summary>Labels of the holding rows, oldest first.</summary>
        public IReadOnlyList<string> Labels
        {
            get
            {
                lock (_gate)
                {
                    var list = new List<string>(_entries.Count);
                    foreach (var entry in _entries)
                    {
                        list.Add(entry.Label);
                    }

                    return list;
                }
            }
        }

        /// <summary>Starts holding a row.</summary>
        /// <param name="label">What is waiting (for logs).</param>
        /// <param name="isDone">The check; polled once per <see cref="Poll"/>.</param>
        /// <param name="timeout">The longest hold; negative counts as zero.</param>
        /// <param name="now">The current time in seconds.</param>
        /// <param name="cursor">The caller's marker of where the scene is; handed back on the end.</param>
        /// <returns>A ticket.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="isDone"/> is null.</exception>
        public long Add(string label, Func<bool> isDone, TimeSpan timeout, double now, object? cursor)
        {
            if (isDone is null)
            {
                throw new ArgumentNullException(nameof(isDone));
            }

            var seconds = timeout < TimeSpan.Zero ? 0d : timeout.TotalSeconds;
            lock (_gate)
            {
                var ticket = _nextTicket++;
                _entries.Add(new Entry(ticket, label ?? string.Empty, isDone, now, now + seconds, cursor));
                return ticket;
            }
        }

        /// <summary>
        /// Checks every holding row once and removes the finished ones. Order per entry: an entry
        /// whose cursor <paramref name="stillValid"/> rejects is <see cref="ScenarioWaitEndKind.Abandoned"/>
        /// (its check is not run); then the check (true = <see cref="ScenarioWaitEndKind.Done"/>, a throw =
        /// <see cref="ScenarioWaitEndKind.Threw"/>); then the deadline (<see cref="ScenarioWaitEndKind.TimedOut"/>).
        /// </summary>
        /// <param name="now">The current time in seconds.</param>
        /// <param name="stillValid">True while the scene is still at the entry's cursor; null treats every entry as valid.</param>
        /// <returns>The finished entries, in the order they were added; empty when none finished.</returns>
        public IReadOnlyList<ScenarioWaitEnd> Poll(double now, Func<object?, bool>? stillValid = null)
        {
            Entry[] snapshot;
            lock (_gate)
            {
                if (_entries.Count == 0)
                {
                    return Array.Empty<ScenarioWaitEnd>();
                }

                snapshot = _entries.ToArray();
            }

            List<ScenarioWaitEnd>? ended = null;
            foreach (var entry in snapshot)
            {
                var end = Check(entry, now, stillValid);
                if (end is null)
                {
                    continue;
                }

                ended ??= new List<ScenarioWaitEnd>();
                ended.Add(end);
            }

            if (ended is null)
            {
                return Array.Empty<ScenarioWaitEnd>();
            }

            lock (_gate)
            {
                foreach (var end in ended)
                {
                    _entries.RemoveAll(e => e.Ticket == end.Ticket);
                }
            }

            return ended;
        }

        /// <summary>Drops every holding row (the room unloaded). Nothing continues.</summary>
        /// <returns>How many were dropped.</returns>
        public int Clear()
        {
            lock (_gate)
            {
                var count = _entries.Count;
                _entries.Clear();
                return count;
            }
        }

        private static ScenarioWaitEnd? Check(Entry entry, double now, Func<object?, bool>? stillValid)
        {
            var waited = Math.Max(0d, now - entry.Started);
            bool valid;
            try
            {
                valid = stillValid is null || stillValid(entry.Cursor);
            }
            catch (Exception ex)
            {
                return new ScenarioWaitEnd(entry.Ticket, entry.Label, entry.Cursor, ScenarioWaitEndKind.Abandoned,
                    "the scene-position check threw " + ex.GetType().Name + ": " + ex.Message, waited);
            }

            if (!valid)
            {
                return new ScenarioWaitEnd(entry.Ticket, entry.Label, entry.Cursor, ScenarioWaitEndKind.Abandoned, string.Empty, waited);
            }

            try
            {
                if (entry.IsDone())
                {
                    return new ScenarioWaitEnd(entry.Ticket, entry.Label, entry.Cursor, ScenarioWaitEndKind.Done, string.Empty, waited);
                }
            }
            catch (Exception ex)
            {
                return new ScenarioWaitEnd(entry.Ticket, entry.Label, entry.Cursor, ScenarioWaitEndKind.Threw,
                    ex.GetType().Name + ": " + ex.Message, waited);
            }

            return now >= entry.Deadline
                ? new ScenarioWaitEnd(entry.Ticket, entry.Label, entry.Cursor, ScenarioWaitEndKind.TimedOut, string.Empty, waited)
                : null;
        }

        private sealed class Entry
        {
            internal Entry(long ticket, string label, Func<bool> isDone, double started, double deadline, object? cursor)
            {
                Ticket = ticket;
                Label = label;
                IsDone = isDone;
                Started = started;
                Deadline = deadline;
                Cursor = cursor;
            }

            internal long Ticket { get; }

            internal string Label { get; }

            internal Func<bool> IsDone { get; }

            internal double Started { get; }

            internal double Deadline { get; }

            internal object? Cursor { get; }
        }
    }
}
