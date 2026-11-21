using CwyLs.ModLoader.Core.Api.Maker.Editor;
using CwyLs.ModLoader.Core.Json;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// <see cref="MakerSubtitleFile"/>: a translator's table changes by exactly one member - order,
    /// spacing, line endings and escapes of everything else are byte-for-byte what they were.
    /// Sample ids and text are invented.
    /// </summary>
    public class MakerEditorSubtitleFileTests
    {
        private const string Table =
            "{\n" +
            "  \"cwy_demo_01_001\": \"First line\",\n" +
            "  \"cwy_demo_01_002\": \"Second line\",\n" +
            "  \"cwy_demo_01_003\": \"Third line\"\n" +
            "}\n";

        [Fact]
        public void Replacing_a_value_touches_nothing_else()
        {
            var edit = MakerSubtitleFile.Apply(Table, "cwy_demo_01_002", "สวัสดี ทดสอบ");

            Assert.True(edit.Ok);
            Assert.True(edit.Changed);
            Assert.Equal("Second line", edit.Previous);
            Assert.Equal(Table.Replace("\"Second line\"", "\"สวัสดี ทดสอบ\""), edit.Text);
        }

        [Fact]
        public void A_new_key_goes_last_in_the_files_own_style()
        {
            var edit = MakerSubtitleFile.Apply(Table, "cwy_demo_02_001", "New line");

            Assert.True(edit.Ok);
            Assert.Null(edit.Previous);
            Assert.Equal(
                "{\n" +
                "  \"cwy_demo_01_001\": \"First line\",\n" +
                "  \"cwy_demo_01_002\": \"Second line\",\n" +
                "  \"cwy_demo_01_003\": \"Third line\",\n" +
                "  \"cwy_demo_02_001\": \"New line\"\n" +
                "}\n",
                edit.Text);
        }

        [Fact]
        public void Key_order_is_kept_after_set_add_and_remove()
        {
            var text = MakerSubtitleFile.Apply(Table, "cwy_demo_01_003", "Changed").Text;
            text = MakerSubtitleFile.Apply(text, "cwy_demo_00_999", "Added").Text;
            text = MakerSubtitleFile.Apply(text, "cwy_demo_01_001", null).Text;

            var keys = JsonReader.ParseObject(text!).Keys;
            Assert.Equal(new[] { "cwy_demo_01_002", "cwy_demo_01_003", "cwy_demo_00_999" }, keys);
        }

        [Fact]
        public void Removing_a_middle_member_keeps_the_indentation_of_the_next()
        {
            var edit = MakerSubtitleFile.Apply(Table, "cwy_demo_01_002", null);

            Assert.True(edit.Changed);
            Assert.Equal("Second line", edit.Previous);
            Assert.Equal(
                "{\n" +
                "  \"cwy_demo_01_001\": \"First line\",\n" +
                "  \"cwy_demo_01_003\": \"Third line\"\n" +
                "}\n",
                edit.Text);
        }

        [Fact]
        public void Removing_the_last_member_takes_the_comma_before_it()
        {
            var edit = MakerSubtitleFile.Apply(Table, "cwy_demo_01_003", null);

            Assert.Equal(
                "{\n" +
                "  \"cwy_demo_01_001\": \"First line\",\n" +
                "  \"cwy_demo_01_002\": \"Second line\"\n" +
                "}\n",
                edit.Text);
        }

        [Fact]
        public void Removing_the_only_member_leaves_an_empty_object()
        {
            var edit = MakerSubtitleFile.Apply("{\n  \"a\": \"b\"\n}\n", "a", null);

            Assert.Equal("{}\n", edit.Text);
        }

        [Fact]
        public void Removing_a_missing_key_or_from_a_missing_file_changes_nothing()
        {
            var absent = MakerSubtitleFile.Apply(Table, "cwy_demo_09_999", null);
            Assert.True(absent.Ok);
            Assert.False(absent.Changed);

            var noFile = MakerSubtitleFile.Apply(null, "x", null);
            Assert.True(noFile.Ok);
            Assert.False(noFile.Changed);
            Assert.Null(noFile.Text);
        }

        [Fact]
        public void Setting_the_same_text_changes_nothing()
        {
            var edit = MakerSubtitleFile.Apply(Table, "cwy_demo_01_001", "First line");

            Assert.True(edit.Ok);
            Assert.False(edit.Changed);
        }

        [Fact]
        public void A_missing_file_is_created_with_two_space_indentation()
        {
            var edit = MakerSubtitleFile.Apply(null, "cwy_demo_01_001", "Hello");

            Assert.Equal("{\n  \"cwy_demo_01_001\": \"Hello\"\n}\n", edit.Text);
        }

        [Fact]
        public void An_empty_object_gets_its_first_member()
        {
            var edit = MakerSubtitleFile.Apply("{}", "k", "v");

            Assert.Equal("{\n  \"k\": \"v\"\n}", edit.Text);
        }

        [Fact]
        public void Crlf_line_endings_and_tab_indentation_are_kept()
        {
            const string crlf = "{\r\n\t\"a\": \"1\",\r\n\t\"b\": \"2\"\r\n}\r\n";

            var edit = MakerSubtitleFile.Apply(crlf, "c", "3");

            Assert.Equal("{\r\n\t\"a\": \"1\",\r\n\t\"b\": \"2\",\r\n\t\"c\": \"3\"\r\n}\r\n", edit.Text);
        }

        [Fact]
        public void A_compact_single_line_table_stays_compact()
        {
            var edit = MakerSubtitleFile.Apply("{\"a\":\"1\"}", "b", "2");

            Assert.Equal("{\"a\":\"1\",\"b\":\"2\"}", edit.Text);
        }

        [Fact]
        public void A_table_written_with_ascii_escapes_gets_escaped_text()
        {
            // As Python's json.dump writes by default: no raw Thai anywhere in the file.
            const string escaped = "{\n  \"a\": \"\\u0e2a\\u0e27\\u0e31\\u0e2a\"\n}\n";

            var edit = MakerSubtitleFile.Apply(escaped, "b", "ดี");

            Assert.Equal("{\n  \"a\": \"\\u0e2a\\u0e27\\u0e31\\u0e2a\",\n  \"b\": \"\\u0e14\\u0e35\"\n}\n", edit.Text);
            Assert.Equal("ดี", JsonReader.ParseObject(edit.Text!)["b"]);
        }

        [Fact]
        public void A_table_with_raw_text_gets_raw_text()
        {
            var edit = MakerSubtitleFile.Apply("{\n  \"a\": \"ไทย\"\n}\n", "b", "ดี");

            Assert.Contains("\"b\": \"ดี\"", edit.Text);
        }

        [Fact]
        public void Quotes_backslashes_and_newlines_in_text_are_escaped()
        {
            var edit = MakerSubtitleFile.Apply(Table, "cwy_demo_01_001", "She said \"hi\"\\\nnext");

            Assert.True(edit.Ok);
            Assert.Equal("She said \"hi\"\\\nnext", JsonReader.ParseObject(edit.Text!)["cwy_demo_01_001"]);
        }

        [Fact]
        public void A_key_that_needs_escaping_is_found_and_replaced()
        {
            const string odd = "{\n  \"a\\\"b\": \"x\",\n  \"c\": \"y\"\n}";

            var edit = MakerSubtitleFile.Apply(odd, "a\"b", "z");

            Assert.Equal("{\n  \"a\\\"b\": \"z\",\n  \"c\": \"y\"\n}", edit.Text);
        }

        [Fact]
        public void Values_that_are_not_text_elsewhere_in_the_file_are_left_alone()
        {
            const string mixed = "{\n  \"n\": 12,\n  \"o\": {\"x\": [1, \"}\"]},\n  \"s\": \"t\"\n}";

            var edit = MakerSubtitleFile.Apply(mixed, "s", "u");

            Assert.Equal("{\n  \"n\": 12,\n  \"o\": {\"x\": [1, \"}\"]},\n  \"s\": \"u\"\n}", edit.Text);
        }

        [Fact]
        public void A_broken_table_is_refused_not_repaired()
        {
            var trailingComma = MakerSubtitleFile.Apply("{\n  \"a\": \"b\",\n}", "a", "c");
            var notAnObject = MakerSubtitleFile.Apply("[\"a\"]", "a", "c");
            var duplicate = MakerSubtitleFile.Apply("{\"a\": \"1\", \"a\": \"2\"}", "a", "c");

            Assert.False(trailingComma.Ok);
            Assert.Contains("not valid JSON", trailingComma.Error);
            Assert.False(notAnObject.Ok);
            Assert.False(duplicate.Ok);
            Assert.Null(trailingComma.Text);
        }

        [Fact]
        public void An_entry_that_is_not_text_is_refused()
        {
            var edit = MakerSubtitleFile.Apply("{\"a\": 5}", "a", "five");

            Assert.False(edit.Ok);
            Assert.Contains("not text", edit.Error);
        }

        [Fact]
        public void Read_finds_one_entry_or_null()
        {
            Assert.Equal("Second line", MakerSubtitleFile.Read(Table, "cwy_demo_01_002"));
            Assert.Null(MakerSubtitleFile.Read(Table, "nope"));
            Assert.Null(MakerSubtitleFile.Read("{broken", "a"));
            Assert.Null(MakerSubtitleFile.Read(null, "a"));
        }
    }
}
