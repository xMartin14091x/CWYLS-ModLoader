using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using CwyLs.ModLoader.Core.Language;
using CwyLs.ModLoader.Core.Subtitles;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// The display-only Thai rewrite: a tone mark before ำ is drawn
    /// as ํ + tone + า, the split the game's font does itself (HarfBuzz shapes น้ำ as น ํ ้ า), so the
    /// mark table's (ํ, tone) pairs can lift the tone mark above the circle. The drawn line must turn
    /// back into the pack's spelling exactly, because the smoke checks and the subtitle hold compare
    /// the two.
    /// </summary>
    public partial class SubtitlesThaiShapeTests
    {
        [Theory]
        [InlineData("น้ำ", "นํ้า")]
        [InlineData("ค่ำ", "คํ่า")]
        [InlineData("วนซ้ำ", "วนซํ้า")]
        [InlineData("ต่ำ ๆ", "ตํ่า ๆ")]
        [InlineData("ำน้ำ", "ำนํ้า")] // a stray ำ first must not hide a later pair
        [InlineData("ฟู่", "ฟู่")]          // tall letter: the mark above goes before the vowel below
        [InlineData("ปุ่ม", "ปุ่ม")]
        [InlineData("ปุ๊บ", "ปุ๊บ")]
        [InlineData("ฝุ่นน้ำ", "ฝุ่นนํ้า")]
        [InlineData("ซ๊ำ", "ซํ๊า")]
        public void A_tone_mark_before_sara_am_is_split_as_the_font_does(string text, string drawn)
        {
            Assert.Equal(drawn, ThaiText.ShapeForDisplay(text));
            Assert.Equal(text, ThaiText.UnshapeForDisplay(drawn));
        }

        [Theory]
        [InlineData("ทำ")]                // sara am without a tone mark: the font draws it whole
        [InlineData("ดู่")]                // a vowel below and a tone on an ordinary letter: no row, no rewrite
        [InlineData("ที่นี่")]              // stacked marks are the mark table's job, not this one
        [InlineData("Hello")]
        [InlineData("")]
        public void Text_without_a_tone_mark_before_sara_am_is_returned_as_is(string text) =>
            Assert.Same(text, ThaiText.ShapeForDisplay(text));

        [Fact]
        public void Null_stays_null() => Assert.Null(ThaiText.ShapeForDisplay(null));

        [Fact]
        public void Soft_breaks_are_placed_on_the_canonical_spelling_first()
        {
            // ำ ends its syllable, so a break goes before the next consonant; after the split the
            // ending would be า, which does not end a syllable - the order keeps that break.
            var drawn = ThaiText.ForDisplay("น้ำตา");
            Assert.Equal("นํ้า​ตา", drawn);
            Assert.Equal("น้ำตา", ThaiText.FromDisplay(drawn));
        }
    }
}
