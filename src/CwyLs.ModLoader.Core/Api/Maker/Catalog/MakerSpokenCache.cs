using System;
using System.Collections.Generic;

namespace CwyLs.ModLoader.Core.Api.Maker.Catalog
{
    /// <summary>
    /// What the catalog learns from lines the game really speaks (<c>line.spoken</c>): the length of
    /// the game's own clip for a voice, and the scenario group or source a row was spoken from. The
    /// second source for <see cref="MakerLine.DurationSec"/> and <see cref="MakerLine.Group"/> when the
    /// snapshot does not know them. Safe from any thread.
    /// </summary>
    public sealed class MakerSpokenCache
    {
        /// <summary>The most voices or rows remembered; a full table stops taking new names.</summary>
        public const int MaxEntries = 20000;

        private readonly object _gate = new object();
        private readonly Dictionary<string, double> _durations = new Dictionary<string, double>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _groups = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>How many voice lengths are remembered.</summary>
        public int DurationCount
        {
            get { lock (_gate) { return _durations.Count; } }
        }

        /// <summary>
        /// Records one spoken line.
        /// </summary>
        /// <param name="voice">The voice name that played.</param>
        /// <param name="localizationId">The row it was spoken from, or null.</param>
        /// <param name="group">The scenario group, or (when none) the line source, e.g. <c>clickReaction</c>; null or blank skips it.</param>
        /// <param name="durationSec">The length of the clip that played.</param>
        /// <param name="gameClip">
        /// True when the clip that played was the game's own (no pack or mod clip answered). Only then is
        /// the length remembered: a dub's length is not the original's.
        /// </param>
        public void Note(string? voice, string? localizationId, string? group, double durationSec, bool gameClip)
        {
            lock (_gate)
            {
                if (gameClip && !string.IsNullOrEmpty(voice) && durationSec > 0d && !double.IsInfinity(durationSec)
                    && (_durations.Count < MaxEntries || _durations.ContainsKey(voice!)))
                {
                    _durations[voice!] = Math.Round(durationSec, 3);
                }

                if (!string.IsNullOrEmpty(localizationId) && !string.IsNullOrWhiteSpace(group)
                    && (_groups.Count < MaxEntries || _groups.ContainsKey(localizationId!)))
                {
                    _groups[localizationId!] = group!;
                }
            }
        }

        /// <summary>The length of the game's own clip seen for a voice.</summary>
        /// <param name="voice">The voice name.</param>
        /// <param name="seconds">The length in seconds.</param>
        public bool TryGetDuration(string? voice, out double seconds)
        {
            seconds = 0d;
            if (string.IsNullOrEmpty(voice))
            {
                return false;
            }

            lock (_gate)
            {
                return _durations.TryGetValue(voice!, out seconds);
            }
        }

        /// <summary>The scenario group (or source) a row was last spoken from.</summary>
        /// <param name="localizationId">The row id.</param>
        /// <param name="group">The group, or null.</param>
        public bool TryGetGroup(string? localizationId, out string? group)
        {
            group = null;
            if (string.IsNullOrEmpty(localizationId))
            {
                return false;
            }

            lock (_gate)
            {
                return _groups.TryGetValue(localizationId!, out group);
            }
        }
    }
}
