using CwyLs.ModLoader.Core.Language;
using CwyLs.ModLoader.Core.MasterData;
using CwyLs.ModLoader.Core.Subtitles;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// B2: script helpers are chosen by a declaration's script (or named explicitly), never by a
    /// language code; plus the text-table id listing the smoke checks sample from.
    /// </summary>
    public class LanguageLineBreakTests
    {
        [Theory]
        [InlineData("auto", "thai", LineBreakHelpers.Thai)]
        [InlineData(null, "Thai", LineBreakHelpers.Thai)]
        [InlineData("auto", "latn", "")]
        [InlineData("auto", "", "")]
        [InlineData("none", "thai", "")]
        [InlineData("thai", "", LineBreakHelpers.Thai)]   // named explicitly, whatever the script
        [InlineData("THAI", "latn", LineBreakHelpers.Thai)]
        [InlineData("dictionary", "thai", "")]            // unknown ids never run anything
        public void The_helper_is_resolved_from_the_script_or_named(string? lineBreaks, string? script, string expected)
        {
            Assert.Equal(expected, LineBreakHelpers.Resolve(lineBreaks, script));
        }

        [Fact]
        public void The_thai_helper_only_adds_zero_width_spaces()
        {
            const string Line = "ไปเที่ยว"; // ไปเที่ยว

            var marked = LineBreakHelpers.Apply(LineBreakHelpers.Thai, Line);

            Assert.NotEqual(Line, marked);
            Assert.Equal(Line, LineBreakHelpers.Strip(marked));
        }

        [Fact]
        public void No_helper_leaves_the_text_alone()
        {
            Assert.Equal("ไปเที่ยว", LineBreakHelpers.Apply(string.Empty, "ไปเที่ยว"));
            Assert.Equal("ไปเที่ยว", LineBreakHelpers.Apply("unknown", "ไปเที่ยว"));
            Assert.Equal(string.Empty, LineBreakHelpers.Apply(LineBreakHelpers.Thai, null));
        }

        [Theory]
        [InlineData("auto", true)]
        [InlineData("NONE", true)]
        [InlineData("thai", true)]
        [InlineData("", false)]
        [InlineData("khmer", false)]
        public void Known_ids_are_the_ones_a_declaration_may_name(string id, bool known)
        {
            Assert.Equal(known, LineBreakHelpers.IsKnown(id));
        }

        [Fact]
        public void A_text_table_lists_its_ids_per_language_in_order()
        {
            var tables = SubtitleTables.Build(new[]
            {
                new SubtitleEntry("th", "b_line", "บี"),
                new SubtitleEntry("th", "a_line", "เอ"),
                new SubtitleEntry("vi", "a_line", "A"),
            });

            Assert.Equal(new[] { "a_line", "b_line" }, tables.IdsFor("th"));
            Assert.Equal(new[] { "a_line" }, tables.IdsFor("VI"));
            Assert.Empty(tables.IdsFor("ko"));
            Assert.Empty(tables.IdsFor(null));
        }
    }
}
