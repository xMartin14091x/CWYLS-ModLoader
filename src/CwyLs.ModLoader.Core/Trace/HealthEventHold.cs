using System.Collections.Generic;

namespace CwyLs.ModLoader.Core.Trace
{
    /// <summary>One health event waiting to be attributed and emitted.</summary>
    public sealed class HeldHealthEvent
    {
        /// <summary>Creates a held event.</summary>
        /// <param name="name">Bus event name, e.g. <c>spike</c>.</param>
        /// <param name="atSeconds">When the reading was taken, in seconds since start-up.</param>
        /// <param name="fields">The payload as measured; the cause is added on release.</param>
        public HeldHealthEvent(string name, double atSeconds, (string Key, object? Value)[] fields)
        {
            Name = name;
            AtSeconds = atSeconds;
            Fields = fields;
        }

        /// <summary>Bus event name, e.g. <c>spike</c> or <c>lowFps</c>.</summary>
        public string Name { get; }

        /// <summary>When the reading was taken, in seconds since start-up.</summary>
        public double AtSeconds { get; }

        /// <summary>The payload as measured, without the attribution fields.</summary>
        public (string Key, object? Value)[] Fields { get; }
    }

    /// <summary>
    /// Holds a health event for <see cref="FocusWindow.HoldSeconds"/> before it is emitted, so it
    /// can be attributed with information that does not exist yet when it is measured.
    /// <para>
    /// Unity raises <c>focusChanged</c> <em>after</em> the stall the switch caused: in the 17:41
    /// session every ~1.1 s spike was followed within 20 ms by <c>focused=false</c>. Emitting the
    /// spike the instant it is measured therefore always tagged it <c>unfocused=false</c> and
    /// mirrored it to the console - the exact noise the user asked to be rid of. Waiting a
    /// fraction of a second costs nothing and makes the tag true.
    /// </para>
    /// Pure and clock-agnostic; the caller passes <c>Time.realtimeSinceStartup</c>.
    /// </summary>
    public sealed class HealthEventHold
    {
        /// <summary>
        /// Events held at once before the oldest is dropped. A 250 ms spike threshold caps the
        /// real rate at about four a second, so three is the realistic depth; this is a bound
        /// against a pathological stall storm, not a working limit.
        /// </summary>
        public const int Capacity = 64;

        private static readonly IReadOnlyList<HeldHealthEvent> None = new HeldHealthEvent[0];

        private readonly object _gate = new object();
        private readonly List<HeldHealthEvent> _pending = new List<HeldHealthEvent>(4);

        private long _dropped;

        /// <summary>Events waiting to be released.</summary>
        public int Count
        {
            get
            {
                lock (_gate)
                {
                    return _pending.Count;
                }
            }
        }

        /// <summary>Events lost to <see cref="Capacity"/>. Never silent: the caller reports it.</summary>
        public long Dropped
        {
            get
            {
                lock (_gate)
                {
                    return _dropped;
                }
            }
        }

        /// <summary>Holds one event.</summary>
        /// <param name="name">Bus event name, e.g. <c>spike</c>.</param>
        /// <param name="atSeconds">When the reading was taken, in seconds since start-up.</param>
        /// <param name="fields">The payload as measured.</param>
        public void Hold(string name, double atSeconds, params (string Key, object? Value)[] fields)
        {
            lock (_gate)
            {
                if (_pending.Count >= Capacity)
                {
                    _pending.RemoveAt(0);
                    _dropped++;
                }

                _pending.Add(new HeldHealthEvent(name, atSeconds, fields ?? new (string, object?)[0]));
            }
        }

        /// <summary>
        /// Every event whose hold has expired by <paramref name="nowSeconds"/>, oldest first, in
        /// the order they were measured. Returns an empty list rather than null when none are due,
        /// so the per-frame caller allocates nothing in the common case.
        /// </summary>
        /// <param name="nowSeconds">Now, in seconds since start-up.</param>
        public IReadOnlyList<HeldHealthEvent> Release(double nowSeconds)
        {
            lock (_gate)
            {
                if (_pending.Count == 0 || _pending[0].AtSeconds + FocusWindow.HoldSeconds > nowSeconds)
                {
                    // The list is in measurement order, so the head is always the first one due.
                    return None;
                }

                List<HeldHealthEvent>? due = null;
                while (_pending.Count > 0 && _pending[0].AtSeconds + FocusWindow.HoldSeconds <= nowSeconds)
                {
                    (due ??= new List<HeldHealthEvent>(_pending.Count)).Add(_pending[0]);
                    _pending.RemoveAt(0);
                }

                return (IReadOnlyList<HeldHealthEvent>?)due ?? None;
            }
        }

        /// <summary>Everything still held, released regardless of time. For shutdown.</summary>
        public IReadOnlyList<HeldHealthEvent> ReleaseAll()
        {
            lock (_gate)
            {
                if (_pending.Count == 0)
                {
                    return None;
                }

                var due = new List<HeldHealthEvent>(_pending);
                _pending.Clear();
                return due;
            }
        }
    }
}
