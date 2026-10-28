using System;

namespace CwyLs.ModLoader.Core.Events
{
    /// <summary>
    /// How a spoken line's text and master-data id were resolved - the answer to "do I trust this
    /// id?".
    /// </summary>
    /// <remarks>
    /// The framework has two ways to name the row behind a voice clip. The reliable one is the row
    /// the game's own <c>ScenarioReader</c> is reading right now; the fallback is a reverse index
    /// from voice name to row, which is ambiguous whenever two rows name the same clip. Publishing
    /// which one answered is what makes a wrong id diagnosable instead of merely surprising.
    /// </remarks>
    public static class LineAttribution
    {
        /// <summary>The row the scenario reader was reading. Unambiguous.</summary>
        public const string Row = "row";

        /// <summary>The reverse voice-name index. Correct for a clip no scenario started, ambiguous on a shared name.</summary>
        public const string VoiceIndex = "voiceIndex";

        /// <summary>No row was found at all: the clip has no entry in the game's master data.</summary>
        public const string None = "none";

        /// <summary>The publisher did not say. Only a default for callers that do not care.</summary>
        public const string Unknown = "unknown";

        /// <summary>True when <paramref name="value"/> is one of the four defined values.</summary>
        /// <param name="value">The attribution string from a <see cref="LineSpoken"/> payload.</param>
        public static bool IsDefined(string? value) =>
            string.Equals(value, Row, StringComparison.Ordinal)
            || string.Equals(value, VoiceIndex, StringComparison.Ordinal)
            || string.Equals(value, None, StringComparison.Ordinal)
            || string.Equals(value, Unknown, StringComparison.Ordinal);
    }
}
