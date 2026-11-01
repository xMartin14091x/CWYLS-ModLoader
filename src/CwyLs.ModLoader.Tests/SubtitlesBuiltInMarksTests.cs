using System.Linq;
using CwyLs.ModLoader.Core.Subtitles;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// The built-in Thai mark table: embedded in Core, so Thai tone marks sit above their vowels with no
    /// language pack installed. The Unity half (installing the records into Base_TH/Dynamic_TH at
    /// RoomReady) is covered in game by <c>s.ui.thai</c> and <c>pb.language.fonts</c>.
    /// </summary>
    public class SubtitlesBuiltInMarksTests
    {
        private const string Family = "IBM Plex Sans Thai Looped";

        [Fact]
        public void The_embedded_Thai_table_loads_for_the_game_s_Thai_font()
        {
            BuiltInMarkTables.Reset();
            var table = BuiltInMarkTables.Thai;

            Assert.Null(BuiltInMarkTables.LoadError);
            Assert.NotNull(table);
            Assert.Equal(Family, table!.Family);
            Assert.Equal("Medium", table.Style);
            Assert.Equal(1000, table.UnitsPerEm);
            Assert.Equal(new[] { "Base_TH", "Dynamic_TH" }, BuiltInMarkTables.ThaiFontAssets);
        }

        [Theory]
        [InlineData(0x0E31)] // ั mai han-akat
        [InlineData(0x0E34)] // ิ
        [InlineData(0x0E35)] // ี
        [InlineData(0x0E36)] // ึ
        [InlineData(0x0E37)] // ื
        public void Every_tone_mark_after_an_upper_vowel_has_a_pair(int vowel)
        {
            var table = BuiltInMarkTables.Thai!;
            foreach (var tone in new[] { 0x0E48, 0x0E49, 0x0E4A, 0x0E4B })
            {
                Assert.Contains(table.Pairs, p => p.Preceding == vowel && p.Mark == tone && p.Dy > 0);
            }
        }

        [Theory]
        [InlineData(0x0E1B)] // ป
        [InlineData(0x0E1D)] // ฝ
        [InlineData(0x0E1F)] // ฟ
        [InlineData(0x0E2C)] // ฬ
        public void Tall_consonants_move_the_mark_beside_them(int consonant)
        {
            Assert.Contains(BuiltInMarkTables.Thai!.Pairs, p => p.Preceding == consonant && p.Mark == 0x0E48);
        }

        [Fact]
        public void The_game_s_Thai_font_gets_a_record_for_every_pair()
        {
            var table = BuiltInMarkTables.Thai!;

            // Dynamic_TH adds characters only as text is drawn, so the table's own glyph ids are what it uses.
            var plan = MarkPlacement.Plan(table, Family, 20f, _ => null, MarkPlacementMode.Full);

            Assert.Equal(table.Pairs.Count, plan.Records.Count);

            // ที่: the tone mark after sara ii is raised clear of the vowel instead of drawn inside it.
            Assert.True(table.TryGetGlyphId(0x0E35, out var saraIi));
            Assert.True(table.TryGetGlyphId(0x0E48, out var maiEk));
            var record = plan.Records.Single(r => r.FirstGlyph == saraIi && r.SecondGlyph == maiEk);
            Assert.True(record.YPlacement > 0);
        }

        [Fact]
        public void Another_font_gets_nothing_rather_than_wrong_offsets()
        {
            var plan = MarkPlacement.Plan(BuiltInMarkTables.Thai!, "Noto Sans Thai", 20f, _ => null, MarkPlacementMode.Full);

            Assert.Empty(plan.Records);
        }
    }
}
