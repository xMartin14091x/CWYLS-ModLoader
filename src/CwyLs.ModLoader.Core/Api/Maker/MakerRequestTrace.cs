using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;

namespace CwyLs.ModLoader.Core.Api.Maker
{
    /// <summary>
    /// Per-request phase timings of one Pack Maker backend call (<c>maker trace on</c>). The router makes one per backend call
    /// and makes it the ambient trace of that call (it flows through the call's awaits); the backends mark their phases with
    /// <see cref="Phase"/>, which costs nothing when no trace is ambient. The router always reads it on a timeout (which phase
    /// was still running) and, with <see cref="Enabled"/>, for the per-request <c>MAKER-TRACE</c> line. Thread-safe: a phase
    /// may end on the main thread while the request thread reads the trace after a timeout.
    /// </summary>
    public sealed class MakerRequestTrace
    {
        private const int MaxPhases = 16;
        private const int MaxOpen = 6;

        private static readonly AsyncLocal<MakerRequestTrace?> Ambient = new AsyncLocal<MakerRequestTrace?>();
        private static readonly double TicksToMs = 1000d / Stopwatch.Frequency;
        private static int _enabled;

        private readonly object _lock = new object();
        private readonly long _started = Stopwatch.GetTimestamp();
        private readonly string[] _names = new string[MaxPhases];
        private readonly double[] _ms = new double[MaxPhases];
        private readonly string?[] _open = new string?[MaxOpen];
        private readonly long[] _openAt = new long[MaxOpen];
        private int _count;
        private int _depth;

        /// <summary>True while <c>maker trace on</c>: every Pack Maker request writes one <c>MAKER-TRACE</c> line.</summary>
        public static bool Enabled
        {
            get => Volatile.Read(ref _enabled) == 1;
            set => Volatile.Write(ref _enabled, value ? 1 : 0);
        }

        /// <summary>The trace of the backend call running on this flow, or null.</summary>
        public static MakerRequestTrace? Current => Ambient.Value;

        /// <summary>Milliseconds since the trace was made.</summary>
        public double ElapsedMs => (Stopwatch.GetTimestamp() - _started) * TicksToMs;

        /// <summary>Makes <paramref name="trace"/> the ambient trace of this flow; returns the previous one for <see cref="Restore"/>.</summary>
        /// <param name="trace">The trace, or null.</param>
        public static MakerRequestTrace? Use(MakerRequestTrace? trace)
        {
            var previous = Ambient.Value;
            Ambient.Value = trace;
            return previous;
        }

        /// <summary>Puts back the trace <see cref="Use"/> replaced.</summary>
        /// <param name="previous">What <see cref="Use"/> returned.</param>
        public static void Restore(MakerRequestTrace? previous) => Ambient.Value = previous;

        /// <summary>Starts a phase of the ambient trace; dispose to end it. Free (no allocation) when no trace is ambient.</summary>
        /// <param name="name">A short constant name, e.g. <c>catalog.gate</c>.</param>
        public static PhaseScope Phase(string name) => new PhaseScope(Ambient.Value, name);

        /// <summary>Records a finished phase of the ambient trace (no-op when none).</summary>
        /// <param name="name">The phase.</param>
        /// <param name="ms">Its duration.</param>
        public static void Note(string name, double ms) => Ambient.Value?.Add(name, ms);

        /// <summary>Stopwatch ticks in milliseconds.</summary>
        /// <param name="from">A <see cref="Stopwatch.GetTimestamp"/> value.</param>
        public static double MsSince(long from) => (Stopwatch.GetTimestamp() - from) * TicksToMs;

        /// <summary>Opens a phase on this trace (any thread).</summary>
        /// <param name="name">The phase.</param>
        public void Begin(string name)
        {
            lock (_lock)
            {
                if (_depth < MaxOpen)
                {
                    _open[_depth] = name;
                    _openAt[_depth] = Stopwatch.GetTimestamp();
                }

                _depth++;
            }
        }

        /// <summary>Closes the innermost open phase called <paramref name="name"/> and records its duration (any thread).</summary>
        /// <param name="name">The phase.</param>
        public void End(string name)
        {
            lock (_lock)
            {
                for (var i = Math.Min(_depth, MaxOpen) - 1; i >= 0; i--)
                {
                    if (!ReferenceEquals(_open[i], name) && _open[i] != name) continue;
                    AddLocked(name, (Stopwatch.GetTimestamp() - _openAt[i]) * TicksToMs);
                    for (var j = i; j < Math.Min(_depth, MaxOpen) - 1; j++)
                    {
                        _open[j] = _open[j + 1];
                        _openAt[j] = _openAt[j + 1];
                    }

                    _depth--;
                    if (_depth < MaxOpen) _open[_depth] = null;
                    return;
                }
            }
        }

        /// <summary>Records a finished phase (any thread). Repeated names add up.</summary>
        /// <param name="name">The phase.</param>
        /// <param name="ms">Its duration.</param>
        public void Add(string name, double ms)
        {
            lock (_lock)
            {
                AddLocked(name, ms);
            }
        }

        /// <summary>The phases still open, innermost last, with how long each has run: <c>catalog.view(2950.1)&gt;main.wait(2940.0)</c>; <c>-</c> when none.</summary>
        public string Running()
        {
            lock (_lock)
            {
                var n = Math.Min(_depth, MaxOpen);
                if (n == 0) return "-";
                var now = Stopwatch.GetTimestamp();
                var text = new StringBuilder();
                for (var i = 0; i < n; i++)
                {
                    if (i > 0) text.Append('>');
                    text.Append(_open[i]).Append('(').Append(Format((now - _openAt[i]) * TicksToMs)).Append(')');
                }

                return text.ToString();
            }
        }

        /// <summary>The finished phases in order: <c>slot=0.2 catalog.gate=0.0 main.wait=12.3 ...</c>; <c>-</c> when none.</summary>
        public string Phases()
        {
            lock (_lock)
            {
                if (_count == 0) return "-";
                var text = new StringBuilder();
                for (var i = 0; i < _count; i++)
                {
                    if (i > 0) text.Append(' ');
                    text.Append(_names[i]).Append('=').Append(Format(_ms[i]));
                }

                return text.ToString();
            }
        }

        /// <summary>Milliseconds with one decimal, invariant culture.</summary>
        /// <param name="ms">Milliseconds.</param>
        public static string Format(double ms) => Math.Round(ms, 1).ToString("0.0", CultureInfo.InvariantCulture);

        private void AddLocked(string name, double ms)
        {
            for (var i = 0; i < _count; i++)
            {
                if (_names[i] == name)
                {
                    _ms[i] += ms;
                    return;
                }
            }

            if (_count < MaxPhases)
            {
                _names[_count] = name;
                _ms[_count] = ms;
                _count++;
            }
        }

        /// <summary>One open phase; ends on dispose. A struct so an untraced call allocates nothing.</summary>
        public readonly struct PhaseScope : IDisposable
        {
            private readonly MakerRequestTrace? _trace;
            private readonly string _name;

            internal PhaseScope(MakerRequestTrace? trace, string name)
            {
                _trace = trace;
                _name = name;
                trace?.Begin(name);
            }

            /// <summary>Ends the phase.</summary>
            public void Dispose() => _trace?.End(_name);
        }
    }
}
