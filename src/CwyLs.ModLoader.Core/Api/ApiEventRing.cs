using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using CwyLs.ModLoader.Core.Json;

namespace CwyLs.ModLoader.Core.Api
{
    /// <summary>One event as the API publishes it: sequence number, time, type and data, serialised once. Immutable.</summary>
    public sealed class ApiEvent
    {
        internal ApiEvent(long seq, DateTimeOffset time, string type, string json)
        {
            Seq = seq;
            Time = time;
            Type = type;
            Json = json;
        }

        /// <summary>The sequence number, 1 for the first event after the API started.</summary>
        public long Seq { get; }

        /// <summary>When it was recorded.</summary>
        public DateTimeOffset Time { get; }

        /// <summary>The event type, e.g. <c>music.changed</c>.</summary>
        public string Type { get; }

        /// <summary>The complete event object: <c>{"seq":…,"time":"…","type":"…","data":{…}}</c>.</summary>
        public string Json { get; }
    }

    /// <summary>One read of the event stream. Immutable.</summary>
    public sealed class ApiEventPage
    {
        internal ApiEventPage(
            string epoch, IReadOnlyList<ApiEvent> events, long next, long latest, long oldest, long dropped, bool reset, bool timedOut)
        {
            Epoch = epoch;
            Events = events;
            Next = next;
            Latest = latest;
            Oldest = oldest;
            Dropped = dropped;
            Reset = reset;
            TimedOut = timedOut;
        }

        /// <summary>The stream's id; it changes every time the API starts, and sequence numbers restart with it.</summary>
        public string Epoch { get; }

        /// <summary>The events, oldest first.</summary>
        public IReadOnlyList<ApiEvent> Events { get; }

        /// <summary>The <c>after</c> value for the next read: the last delivered sequence number, or the one asked for.</summary>
        public long Next { get; }

        /// <summary>The newest sequence number recorded (0 before the first event).</summary>
        public long Latest { get; }

        /// <summary>The oldest sequence number still held.</summary>
        public long Oldest { get; }

        /// <summary>Events after <c>after</c> that were overwritten before this read and are lost to this reader.</summary>
        public long Dropped { get; }

        /// <summary>True when <c>after</c> was beyond <see cref="Latest"/> (a stale reader from an earlier start): the read starts from the oldest held event.</summary>
        public bool Reset { get; }

        /// <summary>True when the read waited the whole <c>wait</c> and nothing arrived.</summary>
        public bool TimedOut { get; }

        /// <summary>The response body of <c>GET /v1/events</c>.</summary>
        public string ToJson()
        {
            var sb = new StringBuilder(128 + Events.Count * 160);
            sb.Append("{\"epoch\":").Append(MiniJson.String(Epoch)).Append(",\"events\":[");
            for (var i = 0; i < Events.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(',');
                }

                sb.Append(Events[i].Json);
            }

            sb.Append("],\"next\":").Append(Next.ToString(CultureInfo.InvariantCulture))
              .Append(",\"latest\":").Append(Latest.ToString(CultureInfo.InvariantCulture))
              .Append(",\"oldest\":").Append(Oldest.ToString(CultureInfo.InvariantCulture))
              .Append(",\"dropped\":").Append(Dropped.ToString(CultureInfo.InvariantCulture))
              .Append(",\"reset\":").Append(MiniJson.Bool(Reset))
              .Append(",\"timedOut\":").Append(MiniJson.Bool(TimedOut))
              .Append('}');
            return sb.ToString();
        }
    }

    /// <summary>
    /// The bounded event stream behind <c>GET /v1/events?after&amp;wait</c>: every event gets the
    /// next sequence number; the newest <see cref="Capacity"/> are kept; a reader that falls
    /// further behind is told how many it lost (<see cref="ApiEventPage.Dropped"/>). Readers can
    /// block until something newer than their <c>after</c> arrives (long-poll). Thread-safe:
    /// events are appended on the main thread, read on request threads.
    /// </summary>
    public sealed class ApiEventRing
    {
        private readonly object _gate = new object();
        private readonly ApiEvent?[] _slots;
        private readonly Func<DateTimeOffset> _now;
        private long _latest;
        private int _count;
        private long _droppedTotal;
        private bool _closed;

        /// <summary>Creates an empty ring.</summary>
        /// <param name="capacity">Events kept; at least 1.</param>
        /// <param name="now">Wall clock for event times; null means <see cref="DateTimeOffset.Now"/>.</param>
        /// <param name="epoch">The stream id; null mints a random one.</param>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="capacity"/> is below 1.</exception>
        public ApiEventRing(int capacity, Func<DateTimeOffset>? now = null, string? epoch = null)
        {
            if (capacity < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "the ring needs room for at least one event");
            }

            _slots = new ApiEvent?[capacity];
            _now = now ?? (() => DateTimeOffset.Now);
            Epoch = epoch ?? NewEpoch();
        }

        /// <summary>Events kept.</summary>
        public int Capacity => _slots.Length;

        /// <summary>The stream id (see <see cref="ApiEventPage.Epoch"/>).</summary>
        public string Epoch { get; }

        /// <summary>The newest sequence number (0 before the first event).</summary>
        public long Latest
        {
            get { lock (_gate) { return _latest; } }
        }

        /// <summary>The oldest sequence number held (<see cref="Latest"/> + 1 when empty).</summary>
        public long Oldest
        {
            get { lock (_gate) { return OldestLocked(); } }
        }

        /// <summary>Events overwritten since the ring was created.</summary>
        public long DroppedTotal
        {
            get { lock (_gate) { return _droppedTotal; } }
        }

        /// <summary>True after <see cref="Close"/>.</summary>
        public bool IsClosed
        {
            get { lock (_gate) { return _closed; } }
        }

        /// <summary>Records an event and wakes every waiting reader. Ignored after <see cref="Close"/>.</summary>
        /// <param name="type">The event type, e.g. <c>music.changed</c>.</param>
        /// <param name="data">Its data; null means an empty object.</param>
        /// <returns>The sequence number, or 0 when the ring is closed.</returns>
        public long Append(string type, JsonObject? data)
        {
            if (string.IsNullOrEmpty(type))
            {
                throw new ArgumentException("an event needs a type", nameof(type));
            }

            var time = _now();
            var dataJson = ApiJson.Write(data ?? new JsonObject());
            lock (_gate)
            {
                if (_closed)
                {
                    return 0;
                }

                var seq = _latest + 1;
                var json = "{\"seq\":" + seq.ToString(CultureInfo.InvariantCulture)
                    + ",\"time\":" + MiniJson.String(ApiJson.Time(time))
                    + ",\"type\":" + MiniJson.String(type)
                    + ",\"data\":" + dataJson + "}";
                _slots[(int)((seq - 1) % _slots.Length)] = new ApiEvent(seq, time, type, json);
                _latest = seq;
                if (_count < _slots.Length)
                {
                    _count++;
                }
                else
                {
                    _droppedTotal++;
                }

                Monitor.PulseAll(_gate);
                return seq;
            }
        }

        /// <summary>Reads what is there now, without waiting.</summary>
        /// <param name="after">Return events with a sequence number above this.</param>
        /// <param name="limit">Most events to return (clamped to 1..<see cref="ApiDefaults.MaxEventsPerRead"/>).</param>
        public ApiEventPage Read(long after, int limit) => Wait(after, limit, TimeSpan.Zero);

        /// <summary>
        /// Reads events after <paramref name="after"/>, waiting up to <paramref name="wait"/> for
        /// the first one when there is none yet. Returns at once when events are there, when the
        /// reader is stale (<see cref="ApiEventPage.Reset"/>) or when the ring is closed.
        /// </summary>
        /// <param name="after">Return events with a sequence number above this (negative counts as 0).</param>
        /// <param name="limit">Most events to return (clamped to 1..<see cref="ApiDefaults.MaxEventsPerRead"/>).</param>
        /// <param name="wait">How long to wait for the first event; zero or less does not wait.</param>
        public ApiEventPage Wait(long after, int limit, TimeSpan wait)
        {
            after = Math.Max(0, after);
            limit = Math.Max(1, Math.Min(ApiDefaults.MaxEventsPerRead, limit));
            var waited = false;
            lock (_gate)
            {
                if (after <= _latest && wait > TimeSpan.Zero && !_closed && _latest == after)
                {
                    var deadline = DateTime.UtcNow + wait;
                    while (!_closed && _latest == after)
                    {
                        var remaining = deadline - DateTime.UtcNow;
                        if (remaining <= TimeSpan.Zero)
                        {
                            break;
                        }

                        waited = true;
                        Monitor.Wait(_gate, remaining);
                    }
                }

                return PageLocked(after, limit, waited);
            }
        }

        /// <summary>Stops recording and wakes every waiting reader (the API is stopping).</summary>
        public void Close()
        {
            lock (_gate)
            {
                _closed = true;
                Monitor.PulseAll(_gate);
            }
        }

        private ApiEventPage PageLocked(long after, int limit, bool waited)
        {
            var oldest = OldestLocked();
            var reset = after > _latest;
            var from = reset ? oldest - 1 : after;
            var dropped = reset ? 0 : Math.Max(0, (oldest - 1) - after);
            var start = Math.Max(from + 1, oldest);
            var end = Math.Min(_latest, start + limit - 1);
            var list = new List<ApiEvent>(end >= start ? (int)(end - start + 1) : 0);
            for (var seq = start; seq <= end; seq++)
            {
                var slot = _slots[(int)((seq - 1) % _slots.Length)];
                if (slot != null && slot.Seq == seq)
                {
                    list.Add(slot);
                }
            }

            var next = list.Count > 0 ? list[list.Count - 1].Seq : (reset ? _latest : after);
            return new ApiEventPage(Epoch, list, next, _latest, oldest, dropped, reset, waited && list.Count == 0);
        }

        private long OldestLocked() => _count == 0 ? _latest + 1 : _latest - _count + 1;

        private static string NewEpoch()
        {
            var bytes = new byte[4];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(bytes);
            }

            return BitConverter.ToString(bytes).Replace("-", string.Empty).ToLowerInvariant();
        }
    }
}
