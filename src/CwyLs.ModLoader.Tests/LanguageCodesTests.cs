using CwyLs.ModLoader.Core.Language;
using CwyLs.ModLoader.Core.Subtitles;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// The map between the game's own <c>GameLanguageType</c> numbers and the codes packs are
    /// written in. The numbers are the game's own <c>Bulbul.GameLanguageType</c> values (game 1.17.3).
    /// Pack languages are not in this table: they come from a <see cref="LanguageRegistry"/>.
    /// </summary>
    public class LanguageCodesTests
    {
        [Theory]
        [InlineData(0, "")]
        [InlineData(1, "ja")]
        [InlineData(2, "en")]
        [InlineData(3, "zh-hans")]
        [InlineData(4, "zh-hant")]
        [InlineData(5, "pt-br")]
        [InlineData(6, "ko")]
        [InlineData(7, "ru")]
        public void Every_game_value_has_a_code(int value, string code)
        {
            Assert.Equal(code, LanguageCodes.ToCode(value));
        }

        [Theory]
        [InlineData(42)]
        [InlineData(GameLanguages.FirstPackValue)]
        public void A_value_the_game_does_not_define_has_no_code(int value)
        {
            Assert.Equal(LanguageCodes.Unknown, LanguageCodes.ToCode(value));
        }

        [Theory]
        [InlineData("ja", 1)]
        [InlineData("jp", 1)]
        [InlineData("ja-JP", 1)]
        [InlineData("EN", 2)]
        [InlineData("zh", 3)]
        [InlineData("zh-TW", 4)]
        [InlineData("pt", 5)]
        [InlineData("Korean", 6)]
        [InlineData("ru", 7)]
        public void Codes_and_their_aliases_map_back_to_a_value(string code, int value)
        {
            Assert.True(LanguageCodes.TryFromCode(code, out var found));
            Assert.Equal(value, found);
        }

        [Theory]
        [InlineData("")]
        [InlineData(null)]
        [InlineData("  ")]
        [InlineData("th")]
        [InlineData("klingon")]
        public void A_code_that_is_not_one_of_the_game_s_own_maps_to_nothing(string? code)
        {
            Assert.False(LanguageCodes.TryFromCode(code, out var found));
            Assert.Equal(GameLanguages.None, found);
        }

        [Fact]
        public void Only_the_seven_game_values_are_in_game()
        {
            Assert.False(LanguageCodes.IsInGame(GameLanguages.None));
            Assert.False(LanguageCodes.IsInGame(GameLanguages.FirstPackValue));
            for (var value = GameLanguages.Japanese; value <= GameLanguages.Russian; value++)
            {
                Assert.True(LanguageCodes.IsInGame(value));
            }
        }

        [Fact]
        public void The_codes_agree_with_the_subtitle_codes()
        {
            // One spelling per language across the whole framework: a pack's data/subtitles/en.json,
            // a fallback chain and a config value mean the same thing.
            Assert.Equal(SubtitleLanguages.Japanese, LanguageCodes.ToCode(GameLanguages.Japanese));
            Assert.Equal(SubtitleLanguages.English, LanguageCodes.ToCode(GameLanguages.English));

            foreach (var code in SubtitleLanguages.InGame)
            {
                Assert.True(LanguageCodes.TryFromCode(code, out var value), code);
                Assert.True(LanguageCodes.IsInGame(value), code);
            }
        }

        [Theory]
        [InlineData(2, "English")]
        [InlineData(0, "None")]
        [InlineData(42, "42")]
        public void Names_are_readable_in_a_log(int value, string name)
        {
            Assert.Equal(name, LanguageCodes.ToName(value));
        }

        [Fact]
        public void Pack_values_start_above_any_future_official_language()
        {
            Assert.True(GameLanguages.FirstPackValue > GameLanguages.Russian + 1);
            Assert.True(GameLanguages.IsPackRange(GameLanguages.FirstPackValue));
            Assert.False(GameLanguages.IsPackRange(GameLanguages.Russian));
        }
    }
}
