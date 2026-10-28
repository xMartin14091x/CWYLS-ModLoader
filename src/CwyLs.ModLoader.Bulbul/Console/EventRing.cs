using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using CwyLs.ModLoader.Core.Events;

namespace CwyLs.ModLoader.Bulbul.Console
{
    /// <summary>
    /// The last few hundred game events, kept so the console's Stream tab and the
    /// <c>events last</c> command can show what just happened without re-reading the trace file.
    /// </summary>
    /// <remarks>
    /// Fed from <c>CWYEvents.SubscribeAny</c>, which delivers on the Unity main thread; the ring
    /// still locks, because the console renders from the same thread but a smoke check may read it
    /// from wherever it runs.
    /// </remarks>
    internal sealed class EventRing
    {
        /// <summary>How many events are kept.</summary>
        internal const int Capacity = 200;

        private readonly object _gate = new object();
        private readonly Queue<Entry> _entries = new Queue<Entry>(Capacity);

        private long _total;
        private bool _describeWarned;

        /// <summary>How many events have passed through this session, including those dropped.</summary>
        internal long Total
        {
            get { lock (_gate) { return _total; } }
        }

        /// <summary>How many events are held right now.</summary>
        internal int Count
        {
            get { lock (_gate) { return _entries.Count; } }
        }

        /// <summary>Records one event. Never throws - a bad <c>Describe</c> must not kill the bus.</summary>
        /// <param name="evt">The event as the catch-all channel delivered it.</param>
        internal void Add(AnyEvent evt)
        {
            if (evt is null)
            {
                return;
            }

            string text;
            try
            {
                text = evt.Args.Describe();
            }
            catch (Exception ex)
            {
                text = "<undescribable>";
                if (!_describeWarned)
                {
                    _describeWarned = true;
                    CwyLsModLoader.Log.LogWarning(
                        $"CONSOLE {evt.Name}.Describe() threw: {ex.GetType().Name}: {ex.Message}");
                }
            }

            lock (_gate)
            {
                _total++;
                _entries.Enqueue(new Entry(DateTime.Now, evt.Name, text));
                while (_entries.Count > Capacity)
                {
                    _entries.Dequeue();
                }
            }
        }

        /// <summary>The newest <paramref name="count"/> events, oldest first.</summary>
        /// <param name="count">How many to take; 0 or less yields none.</param>
        internal IReadOnlyList<Entry> Tail(int count)
        {
            if (count <= 0)
            {
                return Array.Empty<Entry>();
            }

            lock (_gate)
            {
                var all = _entries.ToArray();
                if (all.Length <= count)
                {
                    return all;
                }

                var tail = new Entry[count];
                Array.Copy(all, all.Length - count, tail, 0, count);
                return tail;
            }
        }

        /// <summary>The newest events as text, one per line, newest last.</summary>
        /// <param name="count">How many to show.</param>
        internal string Format(int count)
        {
            var entries = Tail(count);
            if (entries.Count == 0)
            {
                return "no events yet (" + Total + " seen)";
            }

            var sb = new StringBuilder();
            sb.Append(entries.Count).Append(" of ").Append(Count).Append(" held, ")
                .Append(Total).Append(" seen this session");

            foreach (var entry in entries)
            {
                sb.Append('\n').Append(entry.Format());
            }

            return sb.ToString();
        }

        /// <summary>One recorded event.</summary>
        internal readonly struct Entry
        {
            /// <summary>Records an event.</summary>
            /// <param name="time">When it arrived.</param>
            /// <param name="name">The channel name, e.g. <c>LineSpoken</c>.</param>
            /// <param name="text">Its one-line description.</param>
            internal Entry(DateTime time, string name, string text)
            {
                Time = time;
                Name = name ?? "?";
                Text = text ?? string.Empty;
            }

            /// <summary>When the event arrived.</summary>
            internal DateTime Time { get; }

            /// <summary>The channel name.</summary>
            internal string Name { get; }

            /// <summary>The event's own one-line description.</summary>
            internal string Text { get; }

            /// <summary><c>14:22:31.108 LineSpoken Voice_x [...]</c>.</summary>
            internal string Format() =>
                Time.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + " " + Name + " " + Text;
        }
    }
}
