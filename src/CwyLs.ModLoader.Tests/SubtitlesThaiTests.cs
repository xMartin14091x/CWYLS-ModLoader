using CwyLs.ModLoader.Core.Subtitles;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    public class SubtitlesThaiTests
    {
        [Theory]
        [InlineData("ทดสอบคำบรรยายไทย", true)]          // the smoke check's probe string
        [InlineData("เวลา 12:30 น.", true)]              // Thai mixed with ASCII digits
        [InlineData("Hello there", false)]
        [InlineData("こんにちは", false)]                  // Japanese is not Thai
        [InlineData("안녕하세요", false)]                  // nor is Korean
        [InlineData("Привет", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void Thai_is_detected_by_its_unicode_block(string? text, bool expected) =>
            Assert.Equal(expected, ThaiText.Contains(text));

        [Fact]
        public void The_block_bounds_are_the_thai_block_and_nothing_else()
        {
            Assert.Equal('\u0E00', ThaiText.BlockStart);
            Assert.Equal('\u0E7F', ThaiText.BlockEnd);

            Assert.True(ThaiText.IsThai('\u0E01'));   // ko kai, the first Thai letter
            Assert.True(ThaiText.IsThai('\u0E5B'));   // khomut, the last assigned Thai character
            Assert.False(ThaiText.IsThai('\u0DFF'));  // just below the block
            Assert.False(ThaiText.IsThai('\u0E80'));  // Lao, just above the block
        }

        [Fact]
        public void A_break_is_marked_before_every_leading_vowel_inside_a_run()
        {
            // "ไปเที่ยวกันเถอะ" - let's go out. Leading vowels (เ) always start a syllable.
            var marked = ThaiText.AddSoftBreaks("\u0E44\u0E1B\u0E40\u0E17\u0E35\u0E48\u0E22\u0E27\u0E01\u0E31\u0E19\u0E40\u0E16\u0E2D\u0E30");

            Assert.Equal(
                "\u0E44\u0E1B\u200B\u0E40\u0E17\u0E35\u0E48\u0E22\u0E27\u0E01\u0E31\u0E19\u200B\u0E40\u0E16\u0E2D\u0E30",
                marked);
            Assert.Equal(2, CountBreaks(marked));
        }

        [Fact]
        public void A_break_is_marked_after_a_character_that_can_only_end_a_syllable()
        {
            // "ทำงาน" - work. Sara am (ำ) ends its syllable, so the next consonant starts one.
            Assert.Equal(
                "\u0E17\u0E33\u200B\u0E07\u0E32\u0E19",
                ThaiText.AddSoftBreaks("\u0E17\u0E33\u0E07\u0E32\u0E19"));
        }

        [Fact]
        public void A_consonant_cluster_and_its_marks_are_never_split()
        {
            // "กัน" - every character after the first is a mark or a final consonant.
            const string Syllable = "\u0E01\u0E31\u0E19";

            Assert.Equal(Syllable, ThaiText.AddSoftBreaks(Syllable));
        }

        [Fact]
        public void Nothing_is_marked_next_to_whitespace_or_an_existing_break()
        {
            // "เวลา 12:30 น." - a space is already a break opportunity.
            const string Line = "\u0E40\u0E27\u0E25\u0E32 12:30 \u0E19.";

            var once = ThaiText.AddSoftBreaks(Line);
            Assert.Equal(Line, once);

            // And marking twice changes nothing.
            var twice = ThaiText.AddSoftBreaks(ThaiText.AddSoftBreaks("\u0E17\u0E33\u0E07\u0E32\u0E19"));
            Assert.Equal(ThaiText.AddSoftBreaks("\u0E17\u0E33\u0E07\u0E32\u0E19"), twice);
        }

        [Fact]
        public void Text_without_thai_is_returned_untouched()
        {
            Assert.Equal("Hello there", ThaiText.AddSoftBreaks("Hello there"));
            Assert.Equal("\u3053\u3093\u306B\u3061\u306F", ThaiText.AddSoftBreaks("\u3053\u3093\u306B\u3061\u306F"));
            Assert.Null(ThaiText.AddSoftBreaks(null));
            Assert.Equal(string.Empty, ThaiText.AddSoftBreaks(string.Empty));
        }

        [Fact]
        public void Stripping_the_breaks_gives_the_original_line_back()
        {
            const string Line = "\u0E17\u0E33\u0E07\u0E32\u0E19";

            var marked = ThaiText.AddSoftBreaks(Line);

            Assert.True(ThaiText.HasSoftBreaks(marked));
            Assert.Equal(Line, ThaiText.StripSoftBreaks(marked));
            Assert.False(ThaiText.HasSoftBreaks(Line));
            Assert.Equal(Line, ThaiText.StripSoftBreaks(Line));
            Assert.Null(ThaiText.StripSoftBreaks(null));
        }

        private static int CountBreaks(string? text)
        {
            if (text is null)
            {
                return 0;
            }

            var count = 0;
            foreach (var value in text)
            {
                if (value == ThaiText.SoftBreak)
                {
                    count++;
                }
            }

            return count;
        }
    }
}
