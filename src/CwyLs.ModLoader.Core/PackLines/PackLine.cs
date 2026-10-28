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
        /// <exception cref="ArgumentException"><paramref name="groupId"/> is blank or <paramref name="languages"/> holds no code.</exception>
        /// <exception cref="ArgumentNullException"><paramref name="pool"/> or <paramref name="languages"/> is null.</exception>
        public PackLine(string groupId, PackLinePool pool, int episode, IEnumerable<string> languages, IEnumerable<string>? voices = null)
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
