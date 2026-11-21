using System;
using System.Text.RegularExpressions;
using CwyLs.ModLoader.Core.Satone.Say;

namespace CwyLs.ModLoader.Core.Api.Maker.Catalog
{
    /// <summary>The <c>filter</c> values of <c>GET /v1/maker/lines</c> (CONTRACT.md "Routes").</summary>
    public enum MakerLineFilter
    {
        /// <summary><c>all</c>: every line, omitted ones last.</summary>
        All,

        /// <summary><c>missing-subtitle</c>: lines with text that have no subtitle in the query language.</summary>
        MissingSubtitle,

        /// <summary><c>missing-dub</c>: voiced lines with no pack clip for the query language.</summary>
        MissingDub,

        /// <summary><c>done</c>: lines that need something and have all of it.</summary>
        Done,
    }

    /// <summary>
    /// The rules the Pack Maker catalog (ticket DS-02) applies to one line in one language: what it
    /// needs, what it has, which <c>filter</c> it belongs to, and whether a search matches it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Omitted lines.</b> The game data carries rows the game never speaks: ids ending in
    /// <see cref="OmitSuffix"/> (game 1.18.0 has 15, whose voice names have no clip), and any voiced
    /// row whose clip exists nowhere - not in the game's voice cache, not registered by a mod, not
    /// in any pack. They are listed under <c>all</c> (last, with <see cref="MakerLine.Group"/> set to
    /// <see cref="OmittedGroup"/> so a UI can hide them), need neither a subtitle nor a dub, and so
    /// never appear under <c>missing-subtitle</c>, <c>missing-dub</c> or <c>done</c>.
    /// </para>
    /// <para>
    /// <b>Needs.</b> A line needs a subtitle when the game has text for it (Japanese or English
    /// not blank) and a dub when it has a voice name. The game's wordless vocalisations (motion
    /// grunts: a voice, no text) therefore need a dub and no subtitle.
    /// </para>
    /// <para>
    /// <b>Has.</b> A subtitle counts when it is not empty: the game's text seam treats an empty
    /// table entry as no entry and falls back along the language's chain, so an empty entry would
    /// show the fallback text in the game.
    /// </para>
    /// <para>
    /// <b>Not listed at all:</b> rows <c>Satone.Say</c> adds at runtime (<see cref="IsRuntimeRow"/>).
    /// </para>
    /// </remarks>
    public static class MakerLineRules
    {
        /// <summary>The <c>filter</c> value for <see cref="MakerLineFilter.All"/>.</summary>
        public const string FilterAll = "all";

        /// <summary>The <c>filter</c> value for <see cref="MakerLineFilter.MissingSubtitle"/>.</summary>
        public const string FilterMissingSubtitle = "missing-subtitle";

        /// <summary>The <c>filter</c> value for <see cref="MakerLineFilter.MissingDub"/>.</summary>
        public const string FilterMissingDub = "missing-dub";

        /// <summary>The <c>filter</c> value for <see cref="MakerLineFilter.Done"/>.</summary>
        public const string FilterDone = "done";

        /// <summary>
        /// The <see cref="MakerLine.Group"/> of an omitted line. Parentheses keep it apart from every
        /// real scenario group id.
        /// </summary>
        public const string OmittedGroup = "(omitted)";

        /// <summary>The id suffix the game uses for rows it left out of the story.</summary>
        public const string OmitSuffix = "_omit";

        /// <summary>The smallest page (<c>limit</c>).</summary>
        public const int MinLimit = 1;

        /// <summary>The largest page (<c>limit</c>).</summary>
        public const int MaxLimit = 500;

        /// <summary>The <c>lang</c> segment rule of CONTRACT.md.</summary>
        public const string LangPattern = "^[a-z]{2,3}(-[a-z0-9]{2,8})?$";

        private static readonly Regex LangRegex = new Regex(LangPattern, RegexOptions.CultureInvariant);

        /// <summary>Parses a <c>filter</c> value; null or blank is <see cref="MakerLineFilter.All"/>.</summary>
        /// <param name="value">The query value.</param>
        /// <param name="filter">The filter.</param>
        /// <returns>False when the value is not one of the four the contract names.</returns>
        public static bool TryParseFilter(string? value, out MakerLineFilter filter)
        {
            filter = MakerLineFilter.All;
            if (string.IsNullOrWhiteSpace(value))
            {
                return true;
            }

            switch (value!.Trim())
            {
                case FilterAll:
                    filter = MakerLineFilter.All;
                    return true;
                case FilterMissingSubtitle:
                    filter = MakerLineFilter.MissingSubtitle;
                    return true;
                case FilterMissingDub:
                    filter = MakerLineFilter.MissingDub;
                    return true;
                case FilterDone:
                    filter = MakerLineFilter.Done;
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// The language code a query names, trimmed and lower-cased, when it matches
        /// <see cref="LangPattern"/>.
        /// </summary>
        /// <param name="value">The query value.</param>
        /// <param name="lang">The code, or empty.</param>
        /// <returns>False when it is missing or not a language code.</returns>
        public static bool TryParseLang(string? value, out string lang)
        {
            lang = string.Empty;
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            var candidate = value!.Trim().ToLowerInvariant();
            if (!LangRegex.IsMatch(candidate))
            {
                return false;
            }

            lang = candidate;
            return true;
        }

        /// <summary>True when the id itself says the game left the row out (<see cref="OmitSuffix"/>).</summary>
        /// <param name="id">A localisation id.</param>
        public static bool IsOmittedId(string? id) =>
            !string.IsNullOrEmpty(id) && id!.EndsWith(OmitSuffix, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// True for a row <c>Satone.Say</c> added at runtime (<see cref="SayIdGenerator.GroupPrefix"/>):
        /// a mod's passing line, not a game line, so the catalog does not list it.
        /// </summary>
        /// <param name="id">A localisation id.</param>
        public static bool IsRuntimeRow(string? id) =>
            !string.IsNullOrEmpty(id) && id!.StartsWith(SayIdGenerator.GroupPrefix, StringComparison.Ordinal);

        /// <summary>True when the line belongs to <paramref name="filter"/>.</summary>
        /// <param name="state">The line's state in the query language.</param>
        /// <param name="filter">The filter.</param>
        public static bool Matches(MakerLineState state, MakerLineFilter filter)
        {
            switch (filter)
            {
                case MakerLineFilter.MissingSubtitle:
                    return state.NeedsSubtitle && !state.HasSubtitle;
                case MakerLineFilter.MissingDub:
                    return state.NeedsDub && !state.HasDub;
                case MakerLineFilter.Done:
                    return (state.NeedsSubtitle || state.NeedsDub)
                        && (!state.NeedsSubtitle || state.HasSubtitle)
                        && (!state.NeedsDub || state.HasDub);
                default:
                    return true;
            }
        }

        /// <summary>
        /// True when <paramref name="needle"/> occurs in any of the fields, ignoring case (ordinal,
        /// so Thai and Japanese match exactly and Latin letters match either case).
        /// </summary>
        /// <param name="needle">The search text, already trimmed; empty matches everything.</param>
        /// <param name="fields">The id, voice name, JA, EN and subtitle; nulls are skipped.</param>
        public static bool MatchesSearch(string needle, params string?[] fields)
        {
            if (string.IsNullOrEmpty(needle))
            {
                return true;
            }

            foreach (var field in fields)
            {
                if (!string.IsNullOrEmpty(field) && field!.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>What one line needs and has in one language; see <see cref="MakerLineRules"/>.</summary>
    public readonly struct MakerLineState
    {
        /// <summary>Creates a state.</summary>
        /// <param name="omitted">The game never speaks the line.</param>
        /// <param name="needsSubtitle">The line has text to translate.</param>
        /// <param name="needsDub">The line has a voice to dub.</param>
        /// <param name="hasSubtitle">A pack has a non-empty subtitle for it.</param>
        /// <param name="hasDub">A pack has a clip for its voice in this language.</param>
        public MakerLineState(bool omitted, bool needsSubtitle, bool needsDub, bool hasSubtitle, bool hasDub)
        {
            Omitted = omitted;
            NeedsSubtitle = needsSubtitle;
            NeedsDub = needsDub;
            HasSubtitle = hasSubtitle;
            HasDub = hasDub;
        }

        /// <summary>The game never speaks the line; it needs nothing.</summary>
        public bool Omitted { get; }

        /// <summary>The line has Japanese or English text, so a subtitle is wanted.</summary>
        public bool NeedsSubtitle { get; }

        /// <summary>The line has a voice, so a dub is wanted.</summary>
        public bool NeedsDub { get; }

        /// <summary>A pack has a non-empty subtitle for it in this language.</summary>
        public bool HasSubtitle { get; }

        /// <summary>A pack has a clip for its voice in this language.</summary>
        public bool HasDub { get; }

        /// <summary>The state of one line.</summary>
        /// <param name="omitted">The game never speaks the line.</param>
        /// <param name="ja">The game's Japanese text.</param>
        /// <param name="en">The game's English text.</param>
        /// <param name="voice">The voice name, or null.</param>
        /// <param name="subtitle">The subtitle in the query language, or null.</param>
        /// <param name="dubPack">The pack with a clip in the query language, or null.</param>
        public static MakerLineState Of(bool omitted, string? ja, string? en, string? voice, string? subtitle, string? dubPack) =>
            new MakerLineState(
                omitted,
                !omitted && (!string.IsNullOrWhiteSpace(ja) || !string.IsNullOrWhiteSpace(en)),
                !omitted && !string.IsNullOrEmpty(voice),
                !string.IsNullOrEmpty(subtitle),
                !string.IsNullOrEmpty(dubPack));

        /// <inheritdoc />
        public override string ToString() =>
            Omitted
                ? "omitted"
                : "subtitle " + (NeedsSubtitle ? (HasSubtitle ? "done" : "missing") : "-")
                  + ", dub " + (NeedsDub ? (HasDub ? "done" : "missing") : "-");
    }
}
