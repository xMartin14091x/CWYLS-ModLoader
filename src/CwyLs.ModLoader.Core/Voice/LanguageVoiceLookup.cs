namespace CwyLs.ModLoader.Core.Voice
{
    /// <summary>What a language set says about one voice name, for the language in force.</summary>
    public enum LanguageVoiceOutcome
    {
        /// <summary>No pack ships a set for the language in force; language sets do not apply.</summary>
        NoSet = 0,

        /// <summary>The language has a set, but it does not cover this name.</summary>
        NotInSet,

        /// <summary>The set's clip is loaded and ready to play.</summary>
        Loaded,

        /// <summary>The set covers the name, but its clip is still waiting or loading.</summary>
        NotLoaded,

        /// <summary>The set covers the name, but its file did not load.</summary>
        Failed,
    }

    /// <summary>Where one clip of a language set is in its life.</summary>
    public enum LanguageVoiceState
    {
        /// <summary>Not requested yet.</summary>
        Pending = 0,

        /// <summary>A load is in flight.</summary>
        Loading,

        /// <summary>Loaded; the clip is held.</summary>
        Loaded,

        /// <summary>The load failed; the game's clip is used instead.</summary>
        Failed,
    }

    /// <summary>
    /// The answer of <see cref="LanguageVoiceTable.Lookup"/>: the outcome and, when loaded, the clip.
    /// </summary>
    public readonly struct LanguageVoiceLookup
    {
        /// <summary>Creates a lookup result.</summary>
        /// <param name="outcome">What the set says.</param>
        /// <param name="code">The language code in force (canonical), or empty.</param>
        /// <param name="name">The voice name asked for.</param>
        /// <param name="step">The set's file for the name, or null when it has none.</param>
        /// <param name="clip">The loaded clip, or null unless <paramref name="outcome"/> is <see cref="LanguageVoiceOutcome.Loaded"/>.</param>
        /// <param name="failure">Why the file did not load, or null.</param>
        public LanguageVoiceLookup(
            LanguageVoiceOutcome outcome, string code, string name, VoiceLoadStep? step, object? clip, string? failure)
        {
            Outcome = outcome;
            _code = code;
            _name = name;
            Step = step;
            Clip = clip;
            Failure = failure;
        }

        // Backing fields, so that default(LanguageVoiceLookup) - "no set" - reads empty strings, not null.
        private readonly string? _code;
        private readonly string? _name;

        /// <summary>What the set says.</summary>
        public LanguageVoiceOutcome Outcome { get; }

        /// <summary>The language code in force (canonical), or empty.</summary>
        public string Code => _code ?? string.Empty;

        /// <summary>The voice name asked for.</summary>
        public string Name => _name ?? string.Empty;

        /// <summary>The set's file for the name, or null.</summary>
        public VoiceLoadStep? Step { get; }

        /// <summary>The loaded clip (a <c>UnityEngine.AudioClip</c> at runtime), or null.</summary>
        public object? Clip { get; }

        /// <summary>Why the file did not load, or null.</summary>
        public string? Failure { get; }

        /// <summary>True when the set has a loaded clip for the name.</summary>
        public bool HasClip => Outcome == LanguageVoiceOutcome.Loaded && Clip != null;

        /// <summary>
        /// True when the set covers the name but cannot answer yet or at all, so the line plays
        /// another clip this time: the case the trace calls a fallback.
        /// </summary>
        public bool IsFallback => Outcome == LanguageVoiceOutcome.NotLoaded || Outcome == LanguageVoiceOutcome.Failed;

        /// <inheritdoc />
        public override string ToString() => Code + "/" + Name + ": " + Outcome;
    }
}
