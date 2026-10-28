using CwyLs.ModLoader.Core.Subtitles;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// B1: every line the framework causes to be shown stays readable for
    /// <c>max(2.5 s, characters / 15 per second)</c>, counting what a reader sees.
    /// </summary>
    public class SubtitlesReadingTimeTests
    {
        [Theory]
        [InlineData(null, 0)]
        [InlineData("", 0)]
        [InlineData("Hello", 5)]
        [InlineData("Hello there", 11)]                 // the space counts
        [InlineData("Okay, which task goes\nfirst today?", 33)] // the game's own line break does not
        [InlineData("<color=#ffcc00>Hi</color>", 2)]    // rich-text tags are invisible
        [InlineData("a < b", 5)]                         // a lone '<' is text
        [InlineData("\U0001F600!", 2)]                   // a surrogate pair is one character
        public void Visible_characters_are_counted(string? text, int expected)
        {
            Assert.Equal(expected, ReadingTime.CountVisible(text));
        }

        [Fact]
        public void Thai_combining_marks_and_soft_breaks_do_not_count()
        {
            // ที่นี่ = ท ี ่ น ี ่ : two letters a reader sees, four combining marks.
            Assert.Equal(2, ReadingTime.CountVisible("ที่นี่"));

            // ไป + ZWSP + ทำงาน: the zero-width space a line-break helper inserts is invisible.
            Assert.Equal(
                ReadingTime.CountVisible("ไปทำงาน"),
                ReadingTime.CountVisible("ไป​ทำงาน"));
        }

        [Theory]
        [InlineData("OK", 2.5)]
        [InlineData("", 2.5)]
        [InlineData("123456789012345678901234567890123456789012345", 3.0)] // 45 chars / 15
        [InlineData("123456789012345678901234567890123456789012345678901234567890", 4.0)]
        public void The_minimum_is_two_and_a_half_seconds_or_the_reading_time(string text, double seconds)
        {
            Assert.Equal(seconds, ReadingTime.MinimumFor(text), 6);
        }

        [Fact]
        public void The_remaining_hold_counts_down_from_when_the_line_reached_the_box()
        {
            const string Line = "Ready to focus?"; // 15 chars -> 2.5 s minimum

            Assert.Equal(2.5, ReadingTime.RemainingFor(Line, shownAtSeconds: 10, nowSeconds: 10), 6);
            Assert.Equal(1.0, ReadingTime.RemainingFor(Line, shownAtSeconds: 10, nowSeconds: 11.5), 6);
            Assert.Equal(0.0, ReadingTime.RemainingFor(Line, shownAtSeconds: 10, nowSeconds: 13), 6);
        }

        [Fact]
        public void A_clock_that_went_backwards_holds_the_full_minimum_not_more()
        {
            Assert.Equal(2.5, ReadingTime.RemainingFor("Hi", shownAtSeconds: 20, nowSeconds: 5), 6);
            Assert.Equal(2.5, ReadingTime.RemainingFor("Hi", shownAtSeconds: double.NaN, nowSeconds: 5), 6);
        }
    }
}
