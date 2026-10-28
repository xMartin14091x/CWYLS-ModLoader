using System;

namespace CwyLs.ModLoader.Core.MasterData
{
    /// <summary>One line of one subtitle table: a localization id and its text in one language.</summary>
    public sealed class SubtitleEntry
    {
        /// <summary>Creates an entry.</summary>
        /// <param name="language">Language code, e.g. <c>th</c>. Stored lower-cased. Required.</param>
        /// <param name="localizationId">The localization id this translates. Required.</param>
        /// <param name="text">The text. May be empty, which deliberately blanks a line.</param>
        /// <exception cref="ArgumentException"><paramref name="language"/> or <paramref name="localizationId"/> is blank.</exception>
        /// <exception cref="ArgumentNullException"><paramref name="text"/> is null.</exception>
        public SubtitleEntry(string language, string localizationId, string text)
        {
            if (string.IsNullOrWhiteSpace(language))
            {
                throw new ArgumentException("a language code is required", nameof(language));
            }

            if (string.IsNullOrWhiteSpace(localizationId))
            {
                throw new ArgumentException("a localization id is required", nameof(localizationId));
            }

            Language = language.Trim().ToLowerInvariant();
            LocalizationId = localizationId;
            Text = text ?? throw new ArgumentNullException(nameof(text));
        }

        /// <summary>Language code, lower-cased.</summary>
        public string Language { get; }

        /// <summary>The localization id this entry translates.</summary>
        public string LocalizationId { get; }

        /// <summary>The text.</summary>
        public string Text { get; }

        /// <summary><c>lang/id</c>.</summary>
        public override string ToString() => Language + "/" + LocalizationId;
    }
}
