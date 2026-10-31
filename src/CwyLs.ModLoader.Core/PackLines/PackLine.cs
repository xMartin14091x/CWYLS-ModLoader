using System;
using System.Collections.Generic;
using System.Globalization;
using CwyLs.ModLoader.Core.Subtitles;

namespace CwyLs.ModLoader.Core.PackLines
{
    /// <summary>
    /// One language-exclusive line a pack added: a scenario group that passed
    /// <see cref="PackLineScreen"/>, the pool it joins, its episode number and the languages it is
    /// offered in.
    /// </summary>
    public sealed class PackLine
    {
        /// <summary>Creates a line.</summary>
        /// <param name="groupId">The scenario group id. Required.</param>
        /// <param name="pool">The pool it joins. Required.</param>
        /// <param name="episode">Its episode number within the pool's scenario type (1000 or more).</param>
        /// <param name="languages">The language codes it is offered in; normalised here. Required, non-empty.</param>
        /// <param name="voices">
        /// The voice clip names its rows speak (from the pack's localization rows), so the game side can
        /// skip the line while a clip of its own is not loaded yet. Null means none known.
        /// </param>
        /// <param name="weight">
        /// The group's <c>Weight</c> member: the chance, 0 to 1, that this line takes a pick of its pool
        /// (<see cref="PackLineWeights"/>); null when absent (deck pools: one more card in the deck;
        /// startup pools: an equal share with the game's lines).
        /// </param>
        /// <param name="sourceGroupId">
        /// For a copy a spread <c>Pool</c> (<see cref="PackLinePools.TryGetSpread"/>) made: the group the
        /// pack wrote. Null or blank means this line is that group itself.
        /// </param>
        /// <param name="spread">The spread <c>Pool</c> value the copy came from (<c>gamestart.any</c>, <c>*</c>); null or blank for none.</param>
        /// <exception cref="ArgumentException"><paramref name="groupId"/> is blank or <paramref name="languages"/> holds no code.</exception>
        /// <exception cref="ArgumentNullException"><paramref name="pool"/> or <paramref name="languages"/> is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="weight"/> is not a number from 0 to 1.</exception>
        public PackLine(
            string groupId,
            PackLinePool pool,
            int episode,
            IEnumerable<string> languages,
            IEnumerable<string>? voices = null,
            double? weight = null,
            string? sourceGroupId = null,
            string? spread = null)
        {
            if (string.IsNullOrWhiteSpace(groupId))
            {
                throw new ArgumentException("a group id is required", nameof(groupId));
            }

            if (languages is null)
            {
                throw new ArgumentNullException(nameof(languages));
            }

            GroupId = groupId;
            Pool = pool ?? throw new ArgumentNullException(nameof(pool));
            Episode = episode;

            var codes = new List<string>();
            foreach (var language in languages)
            {
                var code = SubtitleLanguages.Normalize(language);
                if (code.Length > 0 && !codes.Contains(code))
                {
                    codes.Add(code);
                }
            }

            if (codes.Count == 0)
            {
                throw new ArgumentException("at least one language code is required", nameof(languages));
            }

            Languages = codes;

            var names = new List<string>();
            if (voices != null)
            {
                foreach (var voice in voices)
                {
                    if (!string.IsNullOrWhiteSpace(voice) && !names.Contains(voice))
                    {
                        names.Add(voice);
                    }
                }
            }

            Voices = names;

            if (weight.HasValue && (double.IsNaN(weight.Value) || weight.Value < 0d || weight.Value > 1d))
            {
                throw new ArgumentOutOfRangeException(nameof(weight), weight, "a weight is a number from 0 to 1");
            }

            Weight = weight;
            SourceGroupId = string.IsNullOrWhiteSpace(sourceGroupId) ? groupId : sourceGroupId!;
            Spread = string.IsNullOrWhiteSpace(spread) ? string.Empty : spread!.Trim();
        }

        /// <summary>The scenario group id, e.g. <c>mymod_click_work_001</c>; what the save filter holds back.</summary>
        public string GroupId { get; }

        /// <summary>The pool the line joins.</summary>
        public PackLinePool Pool { get; }

        /// <summary>Its episode number within <see cref="PackLinePool.Scenario"/>.</summary>
        public int Episode { get; }

        /// <summary>The normalised language codes it is offered in, e.g. <c>th</c>; never empty.</summary>
        public IReadOnlyList<string> Languages { get; }

        /// <summary>
        /// The distinct voice clip names its rows speak, in row order, as far as the pack's own
        /// localization rows tell (a row that reuses a game line's text is not listed: its clip is the
        /// game's). Empty for a text-only line.
        /// </summary>
        public IReadOnlyList<string> Voices { get; }

        /// <summary>
        /// The chance, 0 to 1, that this line takes a pick of its pool (1 = every pick); null when the
        /// pack wrote no <c>Weight</c>.
        /// </summary>
        public double? Weight { get; }

        /// <summary>True when the pack wrote a <c>Weight</c>.</summary>
        public bool IsWeighted => Weight.HasValue;

        /// <summary>
        /// The group the pack wrote: <see cref="GroupId"/> itself, or for a copy a spread made, the
        /// group it was copied from. What the no-repeat rule compares (<see cref="PackLineWeights"/>).
        /// </summary>
        public string SourceGroupId { get; }

        /// <summary>The spread <c>Pool</c> value this copy came from (<c>gamestart.any</c>, <c>*</c>); empty for a line written for its own pool.</summary>
        public string Spread { get; }

        /// <summary>True for a copy a spread <c>Pool</c> made.</summary>
        public bool IsCopy => Spread.Length > 0;

        /// <summary>True for a copy of a global line (<c>"Pool": "*"</c>).</summary>
        public bool IsGlobal => string.Equals(Spread, PackLinePools.Global, StringComparison.Ordinal);

        /// <summary>
        /// The gate: true when the game showing <paramref name="languageCode"/> may offer this line.
        /// Any spelling of a code is accepted (<c>TH</c>, <c>tha</c>, <c>th</c>); blank never matches.
        /// </summary>
        /// <param name="languageCode">The language the game is displaying, e.g. <c>LanguageFeature.CurrentCode</c>.</param>
        public bool IsOfferedIn(string? languageCode)
        {
            var code = SubtitleLanguages.Normalize(languageCode);
            if (code.Length == 0)
            {
                return false;
            }

            for (var i = 0; i < Languages.Count; i++)
            {
                if (string.Equals(Languages[i], code, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary><c>group (pool #episode, th)</c>.</summary>
        public override string ToString() =>
            GroupId + " (" + Pool.Id + " #" + Episode.ToString(CultureInfo.InvariantCulture) + ", " + string.Join("/", Languages) + ")";
    }
}
