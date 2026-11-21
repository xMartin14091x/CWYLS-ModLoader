using System;
using System.Collections.Generic;

namespace CwyLs.ModLoader.Core.Bus
{
    /// <summary>One structured event published by any plugin for the diagnostic stream.</summary>
    public readonly struct BusEvent
    {
        /// <summary>Subsystem tag written as <c>sys</c> in the trace line (e.g. "event", "data", "satone", "health", "unity").</summary>
        public string Sys { get; }
        /// <summary>Event name written as <c>m</c> (e.g. "LineSpoken", "Changed", "exception").</summary>
        public string Name { get; }
        /// <summary>Flat payload; keys become JSON members. Values: string, bool, numbers, string[]/lists, or nested dictionaries.</summary>
        public IReadOnlyDictionary<string, object?> Payload { get; }
        /// <summary>Wall-clock time of the emit.</summary>
        public DateTimeOffset Time { get; }

        public BusEvent(string sys, string name, IReadOnlyDictionary<string, object?> payload, DateTimeOffset time)
        {
            Sys = sys; Name = name; Payload = payload; Time = time;
        }
    }

    /// <summary>
    /// In-process, Unity-free publish channel that feeds the diagnostic stream.
    /// Producers (event bus, data bus, textures, voice, mods) call <see cref="Emit"/>.
    /// <para>
    /// Two kinds of reader. The trace file's consumer <see cref="Attach"/>es: there is one of it,
    /// and events emitted before it attaches are buffered up to <see cref="BufferCapacity"/>
    /// (oldest dropped, counted in <see cref="Dropped"/>) and replayed carrying their original
    /// <see cref="BusEvent.Time"/>. Everything else <see cref="Subscribe"/>s: any number of
    /// observers, each of which sees every event. The second kind exists so the console sink can
    /// print on the calling thread, in event order, while the file copy stays on the writer thread.
    /// </para>
    /// Never throws into a producer.
    /// </summary>
    public static class TraceBus
    {
        /// <summary>Maximum events held while no consumer is attached.</summary>
        public const int BufferCapacity = 2000;

        private static readonly TraceBusChannel Default = new TraceBusChannel();

        /// <summary>Events dropped because the pre-attach buffer overflowed.</summary>
        public static long Dropped => Default.Dropped;
        /// <summary>Total events emitted since process start.</summary>
        public static long Emitted => Default.Emitted;
        /// <summary>Events currently waiting for a consumer.</summary>
        public static int Buffered => Default.Buffered;
        /// <summary>True when a consumer is attached.</summary>
        public static bool HasConsumer => Default.HasConsumer;

        /// <summary>How many observers are subscribed (the console sink is one of them).</summary>
        public static int Observers => Default.Observers;

        /// <summary>Where consumer exceptions are reported (the Trace plugin wires this to its logger). Null = swallowed.</summary>
        public static void SetErrorSink(Action<Exception>? sink) => Default.SetErrorSink(sink);

        /// <summary>Publish an event. Safe from any thread; never throws.</summary>
        public static void Emit(string sys, string name) => Default.Emit(sys, name);

        /// <summary>Publish an event with a flat payload. Safe from any thread; never throws.</summary>
        public static void Emit(string sys, string name, IReadOnlyDictionary<string, object?>? payload) => Default.Emit(sys, name, payload);

        /// <summary>Convenience: build the payload from name/value pairs (distinct name avoids overload ambiguity with null).</summary>
        public static void EmitFields(string sys, string name, params (string Key, object? Value)[] fields) => Default.EmitFields(sys, name, fields);

        /// <summary>
        /// Attach the single consumer. Buffered events are delivered first, in order, on the caller's thread.
        /// A later Attach replaces the consumer. Dispose the result to detach (events buffer again).
        /// </summary>
        public static IDisposable Attach(Action<BusEvent> consumer) => Default.Attach(consumer);

        /// <summary>
        /// Adds an observer that sees every event from now on, plus whatever is still in the
        /// pre-attach buffer (each buffered event carries its ORIGINAL <see cref="BusEvent.Time"/>,
        /// so a replayed line is stamped when it happened, not when it was replayed).
        /// Dispose the result to stop observing.
        /// </summary>
        /// <param name="observer">Called on the publishing thread. Must not block and must not throw.</param>
        /// <returns>A handle that removes the observer.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="observer"/> is null.</exception>
        public static IDisposable Subscribe(Action<BusEvent> observer) => Default.Subscribe(observer);

        /// <summary>Test/dev helper: forget the consumer, the observers and buffered events; counters reset.</summary>
        public static void Reset() => Default.Reset();
    }

    /// <summary>
    /// Isolated instance implementation behind <see cref="TraceBus"/>; tests instantiate their own
    /// channel so parallel runs never observe events from other suites.
    /// </summary>
    internal sealed class TraceBusChannel
    {
        private readonly object _gate = new object();
        private readonly Queue<BusEvent> _buffer = new Queue<BusEvent>();
        private Action<BusEvent>? _consumer;
        private Action<BusEvent>[] _observers = Array.Empty<Action<BusEvent>>();
        private long _dropped;
        private long _emitted;
        private Action<Exception>? _errorSink;

        public long Dropped { get { lock (_gate) return _dropped; } }
        public long Emitted { get { lock (_gate) return _emitted; } }
        public int Buffered { get { lock (_gate) return _buffer.Count; } }
        public bool HasConsumer { get { lock (_gate) return _consumer != null; } }
        public int Observers { get { lock (_gate) return _observers.Length; } }

        public void SetErrorSink(Action<Exception>? sink) { lock (_gate) _errorSink = sink; }

        public void Emit(string sys, string name) => Emit(sys, name, (IReadOnlyDictionary<string, object?>?)null);

        public void Emit(string sys, string name, IReadOnlyDictionary<string, object?>? payload)
        {
            try
            {
                var evt = new BusEvent(sys ?? "?", name ?? "?", payload ?? Empty, DateTimeOffset.Now);
                Action<BusEvent>? consumer;
                Action<BusEvent>[] observers;
                lock (_gate)
                {
                    _emitted++;
                    consumer = _consumer;
                    observers = _observers;
                    if (consumer == null)
                    {
                        // Buffered for the file consumer only: observers are live or they are not.
                        if (_buffer.Count >= TraceBus.BufferCapacity) { _buffer.Dequeue(); _dropped++; }
                        _buffer.Enqueue(evt);
                    }
                }

                if (consumer != null)
                {
                    try { consumer(evt); }
                    catch (Exception ex) { Report(ex); }
                }

                foreach (var observer in observers)
                {
                    try { observer(evt); }
                    catch (Exception ex) { Report(ex); }
                }
            }
            catch (Exception ex) { Report(ex); }
        }

        public void EmitFields(string sys, string name, params (string Key, object? Value)[] fields)
        {
            var dict = new Dictionary<string, object?>(fields.Length);
            foreach (var (k, v) in fields) dict[k] = v;
            Emit(sys, name, dict);
        }

        public IDisposable Attach(Action<BusEvent> consumer)
        {
            if (consumer == null) throw new ArgumentNullException(nameof(consumer));
            BusEvent[] pending;
            lock (_gate)
            {
                _consumer = consumer;
                pending = _buffer.ToArray();
                _buffer.Clear();
            }
            foreach (var evt in pending)
            {
                try { consumer(evt); }
                catch (Exception ex) { Report(ex); }
            }
            return new Detacher(this, consumer);
        }

        public IDisposable Subscribe(Action<BusEvent> observer)
        {
            if (observer == null) throw new ArgumentNullException(nameof(observer));

            // The replay runs under the gate on purpose: it is the only way a new observer can be
            // guaranteed to see every buffered event exactly once, in order, with no event slipping
            // past between the snapshot and the subscription. Safe because no CWY log sink
            // publishes on this bus, so an observer can never re-enter Emit.
            lock (_gate)
            {
                var grown = new Action<BusEvent>[_observers.Length + 1];
                Array.Copy(_observers, grown, _observers.Length);
                grown[_observers.Length] = observer;
                _observers = grown;

                foreach (var evt in _buffer)
                {
                    try { observer(evt); }
                    catch (Exception ex) { Report(ex); }
                }
            }

            return new Unsubscriber(this, observer);
        }

        public void Reset()
        {
            lock (_gate)
            {
                _consumer = null;
                _observers = Array.Empty<Action<BusEvent>>();
                _buffer.Clear();
                _dropped = 0;
                _emitted = 0;
            }
        }

        private static readonly IReadOnlyDictionary<string, object?> Empty = new Dictionary<string, object?>();

        private void Report(Exception ex)
        {
            Action<Exception>? sink; lock (_gate) sink = _errorSink;
            try { sink?.Invoke(ex); } catch { /* the sink is the last resort; nothing else to do */ }
        }

        private sealed class Detacher : IDisposable
        {
            private readonly TraceBusChannel _bus;
            private readonly Action<BusEvent> _mine;
            public Detacher(TraceBusChannel bus, Action<BusEvent> mine) { _bus = bus; _mine = mine; }
            public void Dispose() { lock (_bus._gate) { if (ReferenceEquals(_bus._consumer, _mine)) _bus._consumer = null; } }
        }

        private sealed class Unsubscriber : IDisposable
        {
            private readonly TraceBusChannel _bus;
            private readonly Action<BusEvent> _mine;

            public Unsubscriber(TraceBusChannel bus, Action<BusEvent> mine) { _bus = bus; _mine = mine; }

            public void Dispose()
            {
                lock (_bus._gate)
                {
                    var index = Array.IndexOf(_bus._observers, _mine);
                    if (index < 0) return;

                    var shrunk = new Action<BusEvent>[_bus._observers.Length - 1];
                    Array.Copy(_bus._observers, shrunk, index);
                    Array.Copy(_bus._observers, index + 1, shrunk, index, _bus._observers.Length - index - 1);
                    _bus._observers = shrunk;
                }
            }
        }
    }
}
