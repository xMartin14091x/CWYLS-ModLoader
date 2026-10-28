using System;

namespace CwyLs.ModLoader.Core.Events
{
    /// <summary>
    /// What the game's scenario reader was about to speak, captured the moment it started reading
    /// a row and before any clip existed to guess from.
    /// </summary>
    /// <remarks>
    /// Engine-free on purpose: the adapter fills it from <c>Bulbul.MasterData.NovelData</c> plus the
    /// matching <c>LocalizationData</c>, and <see cref="SpeechAttribution"/> then decides from this
    /// struct alone, which is what makes the decision unit-testable without Unity or the game.
    /// </remarks>
    public readonly struct ScenarioRowHint : IEquatable<ScenarioRowHint>
    {
        /// <summary>The hint that says "no row is being read".</summary>
        public static readonly ScenarioRowHint None = default;

        /// <summary>Creates a hint.</summary>
        /// <param name="localizationId">The row id from the <c>NovelData</c> being read; null or empty means no hint.</param>
        /// <param name="scenarioGroupId">The scenario group the row belongs to, or null.</param>
        /// <param name="voice">The voice clip name on the matching localization row, or null when it has none.</param>
        /// <param name="capturedFrame">The Unity frame the hint was captured on, for diagnostics.</param>
        public ScenarioRowHint(string? localizationId, string? scenarioGroupId, string? voice, int capturedFrame)
        {
            LocalizationId = string.IsNullOrEmpty(localizationId) ? null : localizationId;
            ScenarioGroupId = string.IsNullOrEmpty(scenarioGroupId) ? null : scenarioGroupId;
            Voice = voice;
            CapturedFrame = capturedFrame;
        }

        /// <summary>Row id the reader is reading, or null when there is no hint.</summary>
        public string? LocalizationId { get; }

        /// <summary>Scenario group the row belongs to, or null.</summary>
        public string? ScenarioGroupId { get; }

        /// <summary>
        /// Voice clip name on that row. Null when the row has none (a text-only line) and null when
        /// the localization row could not be resolved at capture time.
        /// </summary>
        public string? Voice { get; }

        /// <summary>Frame the hint was captured on. Diagnostics only; the voice guard is the correctness gate.</summary>
        public int CapturedFrame { get; }

        /// <summary>True when a row id was captured.</summary>
        public bool IsSet => LocalizationId != null;

        /// <inheritdoc />
        public bool Equals(ScenarioRowHint other) =>
            string.Equals(LocalizationId, other.LocalizationId, StringComparison.Ordinal)
            && string.Equals(ScenarioGroupId, other.ScenarioGroupId, StringComparison.Ordinal)
            && string.Equals(Voice, other.Voice, StringComparison.Ordinal)
            && CapturedFrame == other.CapturedFrame;

        /// <inheritdoc />
        public override bool Equals(object? obj) => obj is ScenarioRowHint other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode()
        {
            unchecked
            {
                var hash = LocalizationId?.GetHashCode() ?? 0;
                hash = (hash * 397) ^ (ScenarioGroupId?.GetHashCode() ?? 0);
                hash = (hash * 397) ^ (Voice?.GetHashCode() ?? 0);
                return (hash * 397) ^ CapturedFrame;
            }
        }

        /// <inheritdoc />
        public override string ToString() =>
            IsSet
                ? LocalizationId + " (group=" + (ScenarioGroupId ?? "-") + ", voice=" + (Voice ?? "-")
                  + ", frame=" + CapturedFrame + ")"
                : "(no row)";
    }
}
