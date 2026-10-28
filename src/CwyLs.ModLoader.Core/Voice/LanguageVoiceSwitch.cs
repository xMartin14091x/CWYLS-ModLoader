using System;
using CwyLs.ModLoader.Core.Subtitles;

namespace CwyLs.ModLoader.Core.Voice
{
    /// <summary>
    /// Turns "the game's language is now X" observations into set changes: which language set is in
    /// force, and when that changes.
    /// </summary>
    /// <remarks>
    /// The language in force has a set only when a pack ships <c>audio/voice/&lt;code&gt;/</c> for
    /// it; any other language means "no set" (the game's own clips, plus global overrides). So a
    /// switch from Japanese to English is no change here, while Thai to English leaves the Thai set.
    /// Observing the same language again is cheap and reports nothing, so callers may observe on
    /// every voice lookup and on a timer.
    /// </remarks>
    public sealed class LanguageVoiceSwitch
    {
        private readonly Func<string, bool> _hasSet;

        /// <summary>Creates a switch.</summary>
        /// <param name="hasSet">Whether a canonical language code has a set.</param>
        /// <exception cref="ArgumentNullException"><paramref name="hasSet"/> is null.</exception>
        public LanguageVoiceSwitch(Func<string, bool> hasSet)
        {
            _hasSet = hasSet ?? throw new ArgumentNullException(nameof(hasSet));
        }

        /// <summary>The code of the set in force, or empty when none applies.</summary>
        public string Active { get; private set; } = string.Empty;

        /// <summary>The last language observed (canonical), with or without a set; empty before the first.</summary>
        public string LastObserved { get; private set; } = string.Empty;

        /// <summary>How many times the set in force changed.</summary>
        public int Changes { get; private set; }

        /// <summary>Records the language in force.</summary>
        /// <param name="currentCode">The game's current language code, any accepted spelling; empty when unknown.</param>
        /// <param name="previous">The set that was in force before, or empty.</param>
        /// <returns>True when the set in force changed; <see cref="Active"/> is the new one.</returns>
        public bool Observe(string? currentCode, out string previous)
        {
            var code = SubtitleLanguages.Normalize(currentCode);
            LastObserved = code;

            var next = code.Length > 0 && _hasSet(code) ? code : string.Empty;
            previous = Active;
            if (string.Equals(next, Active, StringComparison.Ordinal))
            {
                return false;
            }

            Active = next;
            Changes++;
            return true;
        }

        /// <summary>
        /// Re-evaluates the set in force after the sets themselves changed (a pack rescan), against
        /// the last observed language.
        /// </summary>
        /// <param name="previous">The set that was in force before, or empty.</param>
        /// <returns>True when the set in force changed.</returns>
        public bool Refresh(out string previous) => Observe(LastObserved, out previous);
    }
}
