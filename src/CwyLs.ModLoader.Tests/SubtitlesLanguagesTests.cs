using CwyLs.ModLoader.Core.Subtitles;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    public class SubtitlesLanguagesTests
    {
        [Theory]
        // The seven values of Bulbul.GameLanguageType other than None, read off the shipped
        // Assembly-CSharp (GameLanguageType.cs).
        [InlineData("ja", true)]
        [InlineData("jp", true)]
        [InlineData("en", true)]
        [InlineData("zh-hans", true)]
        [InlineData("zhcn", true)]
        [InlineData("zh-hant", true)]
        [InlineData("pt-br", true)]
        [InlineData("ko", true)]
        [InlineData("ru", true)]
        // Languages the game does not have: a pack may declare these (language.json).
        [InlineData("th", false)]
        [InlineData("tha", false)]
        [InlineData("vi", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void The_game_s_own_languages_are_known(string? code, bool expected) =>
            Assert.Equal(expected, SubtitleLanguages.IsInGame(code));

        [Theory]
        [InlineData("TH", "th")]
        [InlineData("th-TH", "th")]
        [InlineData("Thai", "th")]
        [InlineData(" JP ", "ja")]
        [InlineData("zh-TW", "zh-hant")]
        [InlineData("vi", "vi")]
        [InlineData("VI-vn", "vi-vn")]
        [InlineData(null, "")]
        public void Every_spelling_normalises_to_one_code(string? spelling, string code) =>
            Assert.Equal(code, SubtitleLanguages.Normalize(spelling));

        [Fact]
        public void Every_listed_in_game_code_is_already_normalised()
        {
            foreach (var code in SubtitleLanguages.InGame)
            {
                Assert.Equal(code, SubtitleLanguages.Normalize(code));
                Assert.True(SubtitleLanguages.IsInGame(code));
            }
        }
    }
}
