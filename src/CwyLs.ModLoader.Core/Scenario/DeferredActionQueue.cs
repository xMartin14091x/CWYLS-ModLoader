using System;
using System.Collections.Generic;
using System.Globalization;

namespace CwyLs.ModLoader.Core.Scenario
{
    /// <summary>One action a scene asked to run after it ends (<c>cwy.defer</c>). Immutable.</summary>
    public sealed class DeferredAction
    {
        internal DeferredAction(long id, string groupId, string rowId, ScriptActionLine line, double queuedAt)
        {
            Id = id;
            GroupId = groupId;
            RowId = rowId;
            Line = line;
            QueuedAt = queuedAt;
        }

        /// <summary>Order of arrival, from 1.</summary>
        public long Id { get; }

        /// <summary>The scene that asked (the audit caller id).</summary>
        public string GroupId { get; }

        /// <summary>The <c>cwy.defer</c> row.</summary>
        public string RowId { get; }

        /// <summary>What to run.</summary>
        public ScriptActionLine Line { get; }

        /// <summary>When it was queued (seconds, the queue's clock).</summary>
        public double QueuedAt { get; }

        /// <summary><c>#3 music.play title=Rain (from cwy_gn_01/cwy_gn_014)</c>.</summary>
        public override string ToString() =>
            "#" + Id.ToString(CultureInfo.InvariantCulture) + " " + Line + " (from " + GroupId + "/" + RowId + ")";
    }

    /// <summary>What <see cref="DeferredActionQueue.Take"/> hands back. Immutable.</summary>
    public sealed class DeferredTake
    {
        internal static readonly DeferredTake Nothing = new DeferredTake(Array.Empty<DeferredAction>(), Array.Empty<DeferredAction>());

        internal DeferredTake(IReadOnlyList<DeferredAction> due, IReadOnlyList<DeferredAction> expired)
        {
            Due = due;
            Expired = expired;
        }

        /// <summary>Run these now, in this order.</summary>
        public IReadOnlyList<DeferredAction> Due { get; }

        /// <summary>Dropped: they waited longer than the queue's limit for a quiet room.</summary>
        public IReadOnlyList<DeferredAction> Expired { get; }
    }

    /// <summary>
    /// The actions scenes deferred (<c>cwy.defer</c>), released only when the room is quiet: no scene
    /// is playing (so the asking scene has ended), the room's main state is <c>Idle</c> (so a long
    /// story's fade and tidy are done), and both have held for <see cref="SettleSeconds"/> (so the
    /// game's own end-of-talk handling, one frame later, has run first). Pure: the game side says
    /// whether the room is quiet; time is passed in. Thread-safe.
    /// </summary>
    public sealed class DeferredActionQueue
    {
        /// <summary>Most actions one scene may defer.</summary>
        public const int MaxPerGroup = 16;

        /// <summary>Most actions waiting in total.</summary>
        public const int MaxTotal = 64;

        /// <summary>The default quiet time before release, in seconds.</summary>
        public const double DefaultSettleSeconds = 1.0;

        /// <summary>The default longest wait for a quiet room, in seconds (15 minutes).</summary>
        public const double DefaultMaxAgeSeconds = 900;

        private readonly object _gate = new object();
        private readonly List<DeferredAction> _pending = new List<DeferredAction>();
        private long _nextId = 1;
        private double? _quietSince;

        /// <summary>Creates a queue.</summary>
        /// <param name="settleSeconds">Quiet time before release; negative counts as zero.</param>
        /// <param name="maxAgeSeconds">Longest wait for a quiet room; at least one second.</param>
        public DeferredActionQueue(double settleSeconds = DefaultSettleSeconds, double maxAgeSeconds = DefaultMaxAgeSeconds)
        {
            SettleSeconds = Math.Max(0d, settleSeconds);
            MaxAgeSeconds = Math.Max(1d, maxAgeSeconds);
        }

        /// <summary>Quiet time before release, in seconds.</summary>
        public double SettleSeconds { get; }

        /// <summary>Longest wait for a quiet room, in seconds.</summary>
        public double MaxAgeSeconds { get; }

        /// <summary>How many actions wait.</summary>
        public int Count
        {
            get { lock (_gate) { return _pending.Count; } }
        }

        /// <summary>The waiting actions, oldest first.</summary>
        public IReadOnlyList<DeferredAction> Pending
        {
            get { lock (_gate) { return _pending.ToArray(); } }
        }

        /// <summary>Queues an action.</summary>
        /// <param name="groupId">The asking scene.</param>
        /// <param name="rowId">The <c>cwy.defer</c> row.</param>
        /// <param name="line">What to run.</param>
        /// <param name="now">The current time in seconds.</param>
        /// <returns>Accepted, or refused when the scene or the queue is full.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="line"/> is null.</exception>
        public ScenarioResult Enqueue(string groupId, string rowId, ScriptActionLine line, double now)
        {
            if (line is null)
            {
                throw new ArgumentNullException(nameof(line));
            }

            var group = groupId ?? string.Empty;
            lock (_gate)
            {
                if (_pending.Count >= MaxTotal)
                {
                    return ScenarioResult.Refused(MaxTotal + " deferred actions are already waiting; " + line + " is dropped");
                }

                var ofGroup = 0;
                foreach (var item in _pending)
                {
                    if (string.Equals(item.GroupId, group, StringComparison.Ordinal))
                    {
                        ofGroup++;
                    }
                }

                if (ofGroup >= MaxPerGroup)
                {
                    return ScenarioResult.Refused(group + " already deferred " + MaxPerGroup + " actions; " + line + " is dropped");
                }

                var action = new DeferredAction(_nextId++, group, rowId ?? string.Empty, line, now);
                _pending.Add(action);
                return ScenarioResult.Ok("deferred " + action);
            }
        }

        /// <summary>
        /// Called once a frame. Drops actions older than <see cref="MaxAgeSeconds"/>; while the room is
        /// quiet, releases every waiting action once it has been quiet for <see cref="SettleSeconds"/>.
        /// A frame that is not quiet restarts the settle time.
        /// </summary>
        /// <param name="quiet">No scene playing and the room Idle (the game side's probe).</param>
        /// <param name="now">The current time in seconds.</param>
        /// <returns>What to run now and what expired.</returns>
        public DeferredTake Take(bool quiet, double now)
        {
            lock (_gate)
            {
                if (_pending.Count == 0)
                {
                    _quietSince = quiet ? _quietSince ?? now : (double?)null;
                    return DeferredTake.Nothing;
                }

                List<DeferredAction>? expired = null;
                for (var i = _pending.Count - 1; i >= 0; i--)
                {
                    if (now - _pending[i].QueuedAt > MaxAgeSeconds)
                    {
                        expired ??= new List<DeferredAction>();
                        expired.Insert(0, _pending[i]);
                        _pending.RemoveAt(i);
                    }
                }

                if (!quiet)
                {
                    _quietSince = null;
                    return expired is null ? DeferredTake.Nothing : new DeferredTake(Array.Empty<DeferredAction>(), expired);
                }

                _quietSince ??= now;
                if (now - _quietSince.Value < SettleSeconds || _pending.Count == 0)
                {
                    return expired is null ? DeferredTake.Nothing : new DeferredTake(Array.Empty<DeferredAction>(), expired);
                }

                var due = _pending.ToArray();
                _pending.Clear();
                return new DeferredTake(due, (IReadOnlyList<DeferredAction>?)expired ?? Array.Empty<DeferredAction>());
            }
        }

        /// <summary>Drops every waiting action (the room unloaded, the feature turned off).</summary>
        /// <returns>How many were dropped.</returns>
        public int Clear()
        {
            lock (_gate)
            {
                var count = _pending.Count;
                _pending.Clear();
                _quietSince = null;
                return count;
            }
        }
    }
}
