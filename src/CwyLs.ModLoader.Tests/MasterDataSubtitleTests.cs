using CwyLs.ModLoader.Core.MasterData;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// P2-1: subtitle tables for languages the game's <c>LocalizationData</c> has no column for.
    /// </summary>
    public class MasterDataSubtitleTests
    {
        private const string File = "cwy.example/data/subtitles/th.json";

        [Theory]
        [InlineData("data/subtitles/th.json", "th")]
        [InlineData("data/subtitles/TH.json", "th")]
        [InlineData("data/subtitles/pt-br.json", "pt-br")]
        [InlineData("data\\subtitles\\th.json", "th")]
        [InlineData("data/subtitles/th.txt", null)]
        [InlineData("data/subtitles/nested/th.json", null)]
        [InlineData("data/subtitles/.json", null)]
        [InlineData("data/localization.json", null)]
        [InlineData("", null)]
        public void Reads_the_language_code_out_of_the_path(string path, string? expected) =>
            Assert.Equal(expected, SubtitleTables.LanguageOf(path));

        [Fact]
        public void Reads_a_thai_table()
        {
            var parsed = SubtitleTables.Parse(
                """{"cwy_example_01_001": "สวัสดีจาก Mod Loader"}""", File, "th");

            Assert.True(parsed.Clean);
            var entry = Assert.Single(parsed.Rows);
            Assert.Equal("th", entry.Language);
            Assert.Equal("cwy_example_01_001", entry.LocalizationId);
            Assert.Equal("สวัสดีจาก Mod Loader", entry.Text);
        }

        [Fact]
        public void Rejects_a_non_string_value_but_keeps_the_rest_of_the_table()
        {
            var parsed = SubtitleTables.Parse("""{"a": "ok", "b": 7, "c": "also ok"}""", File, "th");

            Assert.Equal(2, parsed.Rows.Count);
            var rejection = Assert.Single(parsed.Rejected);
            Assert.Equal("b", rejection.Member);
            Assert.Equal(1, rejection.Index);
            Assert.Contains("must be a string", rejection.Reason);
        }

        [Fact]
        public void Rejects_a_file_that_is_not_an_object_or_has_no_language()
        {
            var notObject = SubtitleTables.Parse("""["a"]""", File, "th");
            Assert.Empty(notObject.Rows);
            Assert.Contains("must be an object", Assert.Single(notObject.Rejected).Reason);

            var noLanguage = SubtitleTables.Parse("""{"a": "b"}""", File, null);
            Assert.Empty(noLanguage.Rows);
            Assert.Contains("is not a language code", Assert.Single(noLanguage.Rejected).Reason);
        }

        [Fact]
        public void Looks_a_line_up_case_insensitively_by_language_and_exactly_by_id()
        {
            var tables = SubtitleTables.Build(new[]
            {
                new SubtitleEntry("th", "line1", "หนึ่ง"),
                new SubtitleEntry("TH", "line2", "สอง")
            });

            Assert.Equal(2, tables.Count);
            Assert.Equal(2, tables.CountFor("th"));
            Assert.Equal(2, tables.CountFor("TH"));

            Assert.True(tables.TryGet("line1", "th", out var found));
            Assert.Equal("หนึ่ง", found);

            Assert.False(tables.TryGet("LINE1", "th", out _));
            Assert.False(tables.TryGet("line1", "ja", out _));
            Assert.False(tables.TryGet(null, "th", out _));
            Assert.False(tables.TryGet("line1", null, out _));
        }

        [Fact]
        public void An_empty_lookup_answers_nothing_without_throwing()
        {
            Assert.Equal(0, SubtitleTables.Empty.Count);
            Assert.Empty(SubtitleTables.Empty.Languages);
            Assert.Equal(0, SubtitleTables.Empty.CountFor("th"));
            Assert.False(SubtitleTables.Empty.TryGet("anything", "th", out var text));
            Assert.Null(text);
            Assert.Equal("no subtitles", SubtitleTables.Empty.ToString());
        }
    }
}
