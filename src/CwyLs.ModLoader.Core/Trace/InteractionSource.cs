using System;
using System.Collections.Generic;

namespace CwyLs.ModLoader.Core.Trace
{
    /// <summary>
    /// Tells a control the user moved apart from one the game moved.
    /// </summary>
    /// <remarks>
    /// UGUI cannot answer this on its own: <c>Slider.Set</c> and <c>Toggle.Set</c> look identical
    /// whether a finger or a coroutine caused them, which is why one code-driven progress bar wrote
    /// 4139 of the 95-second run's UI lines. The pointer and submit callbacks, though, only ever
    /// fire for a real interaction. So those record a timestamp per control here, and a later value
    /// change within the window is attributed to the user; everything else is the game.
    /// </remarks>
    public sealed class InteractionSource
    {
        /// <summary>Value written as <c>src</c> when the user caused the change.</summary>
        public const string User = "user";

        /// <summary>Value written as <c>src</c> when the game caused the change.</summary>
        public const string Code = "code";

        /// <summary>Controls remembered before the least recently touched is forgotten.</summary>
        private const int MaxKeys = 256;

        private readonly object _gate = new object();
        private readonly Dictionary<string, long> _lastIntentMs = new Dictionary<string, long>(StringComparer.Ordinal);
        private readonly Func<long> _nowMs;

        /// <summary>Creates a classifier.</summary>
        /// <param name="nowMilliseconds">Monotonic clock. Defaults to <see cref="Environment.TickCount"/>.</param>
        public InteractionSource(Func<long>? nowMilliseconds = null)
        {
            _nowMs = nowMilliseconds ?? (() => Environment.TickCount);
        }

        /// <summary>Controls currently remembered.</summary>
        public int TrackedKeys
        {
            get
            {
                lock (_gate)
                {
                    return _lastIntentMs.Count;
                }
            }
        }

        /// <summary>
        /// Records that the user just touched this control. Called from the pointer and submit
        /// callbacks, which the game never raises by itself.
        /// </summary>
        /// <param name="key">Identifies the control; normally its hierarchy path.</param>
        public void NoteUserIntent(string? key)
        {
            if (string.IsNullOrEmpty(key))
            {
                return;
            }

            var now = _nowMs();

            lock (_gate)
            {
                if (!_lastIntentMs.ContainsKey(key!) && _lastIntentMs.Count >= MaxKeys)
                {
                    ForgetOldestLocked();
                }

                _lastIntentMs[key!] = now;
            }
        }

        /// <summary>Whether the user touched this control within <paramref name="withinSeconds"/>.</summary>
        public bool WasUserIntent(string? key, double withinSeconds)
        {
            if (string.IsNullOrEmpty(key) || withinSeconds <= 0d)
            {
                return false;
            }

            var window = (long)(withinSeconds * 1000d);
            var now = _nowMs();

            lock (_gate)
            {
                return _lastIntentMs.TryGetValue(key!, out var last) && now - last <= window;
            }
        }

        /// <summary><see cref="User"/> or <see cref="Code"/> for a value change on this control.</summary>
        public string Classify(string? key, double withinSeconds) =>
            WasUserIntent(key, withinSeconds) ? User : Code;

        /// <summary>Forgets every control. Only used by tests.</summary>
        public void Clear()
        {
            lock (_gate)
            {
                _lastIntentMs.Clear();
            }
        }

        private void ForgetOldestLocked()
        {
            string? oldestKey = null;
            var oldest = long.MaxValue;

            foreach (var pair in _lastIntentMs)
            {
                if (pair.Value < oldest)
                {
                    oldest = pair.Value;
                    oldestKey = pair.Key;
                }
            }

            if (oldestKey != null)
            {
                _lastIntentMs.Remove(oldestKey);
            }
        }
    }
}
