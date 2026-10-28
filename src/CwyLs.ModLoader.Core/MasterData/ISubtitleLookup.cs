using System.Collections.Generic;

namespace CwyLs.ModLoader.Core.MasterData
{
    /// <summary>
    /// Text for a localization id in a language the game itself does not have.
    /// <para>
    /// The game's own <c>LocalizationData</c> has exactly seven text columns (Ja, En, ZhHans,
    /// ZhHant, Pt, Ko, Ru - plus the voice name) and no room for another, so a language a pack
    /// declares cannot live there. The pack ships <c>data/subtitles/&lt;lang&gt;.json</c> instead,
    /// and the text seam asks this lookup first, falling back along the language's declared chain
    /// when there is no entry.
    /// </para>
    /// </summary>
    public interface ISubtitleLookup
    {
        /// <summary>Language codes this lookup holds, lower-cased and ordinally sorted.</summary>
        IReadOnlyList<string> Languages { get; }

        /// <summary>Total number of entries across every language.</summary>
        int Count { get; }

        /// <summary>The text for one line in one language.</summary>
        /// <param name="localizationId">The id of the line, as used in the master data.</param>
        /// <param name="language">A language code, e.g. <c>th</c>; matched case-insensitively.</param>
        /// <param name="text">The text, or null when there is no entry.</param>
        /// <returns>True when an entry was found.</returns>
        bool TryGet(string? localizationId, string? language, out string? text);

        /// <summary>How many entries one language has.</summary>
        /// <param name="language">A language code; matched case-insensitively.</param>
        int CountFor(string? language);

        /// <summary>The localization ids one language has an entry for, ordinally sorted. Never null.</summary>
        /// <param name="language">A language code; matched case-insensitively.</param>
        IReadOnlyList<string> IdsFor(string? language);
    }
}
