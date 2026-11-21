using System;
using CwyLs.ModLoader.Core.Api;

namespace CwyLs.ModLoader.Core.Api.Maker
{
    /// <summary>
    /// Thread-safe cached game activity for Studio v2. Unity callbacks and the main-thread busy poll write it;
    /// HTTP workers only take snapshots. Focus, pause and busy remain independent nullable facts.
    /// </summary>
    public sealed class MakerGameActivity
    {
        private readonly object _gate = new object();
        private readonly IApiClock _clock;
        private readonly double _staleSeconds;

        private bool? _focused;
        private bool? _paused;
        private bool? _busy;
        private string? _busyReason;
        private double? _observedMono;
        private DateTimeOffset? _observedAt;

        /// <summary>Creates an unknown, stale snapshot.</summary>
        /// <param name="clock">Monotonic and wall clock.</param>
        /// <param name="staleAfter">Age after which <see cref="MakerGameActivitySnapshot.Stale"/> is true.</param>
        public MakerGameActivity(IApiClock clock, TimeSpan staleAfter)
        {
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            if (staleAfter <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(staleAfter), "the stale age must be positive");
            }

            _staleSeconds = staleAfter.TotalSeconds;
        }

        /// <summary>Records a Unity focus callback. Returns true only when the focus value changed.</summary>
        public bool ObserveFocus(bool focused) => Observe(ref _focused, focused);

        /// <summary>Records a Unity pause callback. Returns true only when the pause value changed.</summary>
        public bool ObservePause(bool paused) => Observe(ref _paused, paused);

        /// <summary>
        /// Records the main-thread playback poll. Null means busy is unknown; <paramref name="reason"/> may explain why.
        /// Returns true when the busy fact or its reason changed.
        /// </summary>
        public bool ObserveBusy(bool? busy, string? reason)
        {
            lock (_gate)
            {
                var clean = string.IsNullOrWhiteSpace(reason) ? null : reason;
                var changed = _busy != busy || !string.Equals(_busyReason, clean, StringComparison.Ordinal);
                _busy = busy;
                _busyReason = clean;
                Touch();
                return changed;
            }
        }

        /// <summary>Refreshes the observation age without changing any fact or publishing a transition.</summary>
        public void Heartbeat()
        {
            lock (_gate)
            {
                Touch();
            }
        }

        /// <summary>Returns a copy for any thread. Staleness never rewrites known facts into a guess.</summary>
        public MakerGameActivitySnapshot Snapshot()
        {
            lock (_gate)
            {
                return new MakerGameActivitySnapshot
                {
                    Focused = _focused,
                    Paused = _paused,
                    Busy = _busy,
                    Reason = Reason(_focused, _paused, _busy, _busyReason),
                    ObservedAt = _observedAt,
                    Stale = !_observedMono.HasValue || _clock.MonotonicSeconds - _observedMono.Value > _staleSeconds,
                };
            }
        }

        private bool Observe(ref bool? field, bool value)
        {
            lock (_gate)
            {
                var changed = field != value;
                field = value;
                Touch();
                return changed;
            }
        }

        private void Touch()
        {
            _observedMono = _clock.MonotonicSeconds;
            _observedAt = _clock.Now;
        }

        private static string? Reason(bool? focused, bool? paused, bool? busy, string? busyReason)
        {
            if (paused == true)
            {
                return "game_paused";
            }

            if (busy == true)
            {
                return busyReason ?? "game_busy";
            }

            if (focused == false)
            {
                return "game_unfocused";
            }

            return busyReason;
        }
    }

    /// <summary>One immutable-by-convention copy of <see cref="MakerGameActivity"/>.</summary>
    public sealed class MakerGameActivitySnapshot
    {
        public bool? Focused { get; set; }
        public bool? Paused { get; set; }
        public bool? Busy { get; set; }
        public string? Reason { get; set; }
        public DateTimeOffset? ObservedAt { get; set; }
        public bool Stale { get; set; }
    }
}
