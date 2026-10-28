using System;
using System.Collections.Generic;
using System.Globalization;

namespace CwyLs.ModLoader.Core.Voice
{
    /// <summary>
    /// One language's voice clips across every pack: the files a <see cref="LanguageVoicePlan"/>
    /// found under <c>audio/voice/&lt;code&gt;/</c>, one winning file per voice name.
    /// </summary>
    public sealed class LanguageVoiceSet
    {
        private readonly Dictionary<string, VoiceLoadStep> _byName;

        /// <summary>Creates a set.</summary>
        /// <param name="code">The canonical language code, e.g. <c>th</c>.</param>
        /// <param name="steps">One step per voice name, in load order.</param>
        /// <exception cref="ArgumentException"><paramref name="code"/> is blank.</exception>
        /// <exception cref="ArgumentNullException"><paramref name="steps"/> is null.</exception>
        public LanguageVoiceSet(string code, IReadOnlyList<VoiceLoadStep> steps)
        {
            if (string.IsNullOrWhiteSpace(code))
            {
                throw new ArgumentException("a language code is required", nameof(code));
            }

            Code = code;
            Steps = steps ?? throw new ArgumentNullException(nameof(steps));

            _byName = new Dictionary<string, VoiceLoadStep>(StringComparer.Ordinal);
            var packs = new List<string>();
            foreach (var step in steps)
            {
                _byName[step.Name] = step;
                if (!packs.Contains(step.PackId))
                {
                    packs.Add(step.PackId);
                }
            }

            PackIds = packs;
        }

        /// <summary>The canonical language code, e.g. <c>th</c>.</summary>
        public string Code { get; }

        /// <summary>The clips to load, in load order (pack order, then path).</summary>
        public IReadOnlyList<VoiceLoadStep> Steps { get; }

        /// <summary>Ids of the packs that contribute at least one clip, in load order.</summary>
        public IReadOnlyList<string> PackIds { get; }

        /// <summary>How many voice names the set covers.</summary>
        public int Count => Steps.Count;

        /// <summary>The step for a voice name, case-sensitively, or null when the set does not cover it.</summary>
        /// <param name="name">The voice name the game asks for.</param>
        public VoiceLoadStep? Find(string? name) =>
            !string.IsNullOrEmpty(name) && _byName.TryGetValue(name!, out var step) ? step : null;

        /// <inheritdoc />
        public override string ToString() =>
            Code + ": " + Count.ToString(CultureInfo.InvariantCulture) + " clip(s) from " + string.Join("+", PackIds);
    }
}
