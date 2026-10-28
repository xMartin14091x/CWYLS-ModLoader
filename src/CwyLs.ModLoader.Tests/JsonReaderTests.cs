using System.Collections.Generic;
using CwyLs.ModLoader.Core.Json;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// P1-1: the JSON reader. The cases that matter are the ones a pack author will actually hit -
    /// Japanese and Thai text, a nested manifest, and every shape of typo.
    /// </summary>
    public class JsonReaderTests
    {
        [Theory]
        [InlineData("true", true)]
        [InlineData("false", false)]
        [InlineData(" \t\r\n true \n ", true)]
        public void Parses_booleans_and_surrounding_whitespace(string json, bool expected) =>
            Assert.Equal(expected, JsonReader.Parse(json));

        [Fact]
        public void Parses_null_as_a_null_reference() => Assert.Null(JsonReader.Parse("null"));

        [Theory]
        [InlineData("0", 0L)]
        [InlineData("-17", -17L)]
        [InlineData("9007199254740993", 9007199254740993L)]
        public void Parses_integers_as_long(string json, long expected)
        {
            var value = JsonReader.Parse(json);
            Assert.IsType<long>(value);
            Assert.Equal(expected, (long)value!);
        }

        [Theory]
        [InlineData("1.5", 1.5)]
        [InlineData("-0.25", -0.25)]
        [InlineData("2e3", 2000d)]
        [InlineData("1.17E-2", 0.0117d)]
        public void Parses_fractions_and_exponents_as_double(string json, double expected)
        {
            var value = JsonReader.Parse(json);
            Assert.IsType<double>(value);
            Assert.Equal(expected, (double)value!, 10);
        }

        [Fact]
        public void Parses_an_object_keeping_document_order()
        {
            var json = "{\"z\":1,\"a\":2,\"m\":3}";

            var obj = JsonReader.ParseObject(json);

            Assert.Equal(new[] { "z", "a", "m" }, obj.Keys);
            Assert.Equal(3, obj.Count);
        }

        [Fact]
        public void Parses_nested_objects_and_arrays()
        {
            var json = "{\"dependsOn\":[{\"id\":\"a\",\"minVersion\":\"1.2.3\"},\"b\"],\"n\":{\"deep\":{\"deeper\":[1,[2,[3]]]}}}";

            var root = JsonReader.ParseObject(json);

            Assert.Equal("a", JsonPath.Get(root, "dependsOn[0].id"));
            Assert.Equal("b", JsonPath.Get(root, "dependsOn[1]"));
            Assert.Equal(3L, JsonPath.Get(root, "n.deep.deeper[1][1][0]"));
        }

        [Fact]
        public void Parses_empty_containers()
        {
            Assert.Empty(JsonReader.ParseObject("{}"));
            Assert.Empty(Assert.IsType<List<object?>>(JsonReader.Parse("[]")));
            Assert.Equal(string.Empty, JsonReader.Parse("\"\""));
        }

        [Fact]
        public void Parses_japanese_text_verbatim()
        {
            var obj = JsonReader.ParseObject("{\"line\":\"こんにちは、さとねです。\"}");

            Assert.Equal("こんにちは、さとねです。", obj["line"]);
        }

        [Fact]
        public void Parses_thai_text_verbatim()
        {
            var obj = JsonReader.ParseObject("{\"line\":\"สวัสดีค่ะ\"}");

            Assert.Equal("สวัสดีค่ะ", obj["line"]);
        }

        [Fact]
        public void Parses_unicode_escapes_including_japanese()
        {
            // \u3055\u3068\u306d is "さとね".
            Assert.Equal("さとね", JsonReader.Parse("\"\\u3055\\u3068\\u306d\""));
        }

        [Fact]
        public void Parses_a_surrogate_pair_escape_into_one_astral_character()
        {
            var value = (string)JsonReader.Parse("\"\\uD83C\\uDFB5\"")!;

            Assert.Equal("\U0001F3B5", value);
            Assert.Equal(2, value.Length);
            Assert.True(char.IsSurrogatePair(value, 0));
        }

        [Fact]
        public void Parses_every_simple_escape()
        {
            Assert.Equal("\"\\/\b\f\n\r\t", JsonReader.Parse("\"\\\"\\\\\\/\\b\\f\\n\\r\\t\""));
        }

        [Fact]
        public void Skips_a_leading_byte_order_mark()
        {
            var obj = JsonReader.ParseObject("\uFEFF{\"id\":\"cwy.example\"}");

            Assert.Equal("cwy.example", obj["id"]);
        }

        [Theory]
        [InlineData("", "the document is empty")]
        [InlineData("   ", "the document is empty")]
        [InlineData("{", "not closed")]
        [InlineData("[1,2", "not closed")]
        [InlineData("{\"a\":1,}", "expected a quoted member name")]
        [InlineData("[1,]", "expected a value")]
        [InlineData("{'a':1}", "expected a quoted member name")]
        [InlineData("{a:1}", "expected a quoted member name")]
        [InlineData("{\"a\" 1}", "expected ':'")]
        [InlineData("01", "may not have a leading zero")]
        [InlineData("1 2", "after the top-level value")]
        [InlineData("-", "the number has no digits")]
        [InlineData("1.", "the fraction needs at least one digit")]
        [InlineData("1e", "the exponent needs at least one digit")]
        [InlineData("tru", "expected 'true'")]
        [InlineData("NaN", "expected a value")]
        [InlineData("{} {}", "after the top-level value")]
        [InlineData("\"\\q\"", "unknown escape")]
        [InlineData("\"\\u12\"", "four hex digits")]
        [InlineData("\"unterminated", "the string is not closed")]
        [InlineData("// comment\n1", "expected a value")]
        public void Rejects_malformed_input(string json, string expectedFragment)
        {
            var ex = Assert.Throws<JsonParseException>(() => JsonReader.Parse(json));

            Assert.Contains(expectedFragment, ex.Message);
        }

        [Fact]
        public void Rejects_a_duplicate_member_name()
        {
            var ex = Assert.Throws<JsonParseException>(() => JsonReader.Parse("{\"id\":\"a\",\"id\":\"b\"}"));

            Assert.Contains("duplicate member name \"id\"", ex.Message);
        }

        [Fact]
        public void Rejects_a_raw_control_character_in_a_string()
        {
            var ex = Assert.Throws<JsonParseException>(() => JsonReader.Parse("\"a\nb\""));

            Assert.Contains("must be escaped", ex.Message);
        }

        [Fact]
        public void Rejects_nesting_past_the_depth_limit()
        {
            var json = new string('[', JsonReader.MaxDepth + 1) + new string(']', JsonReader.MaxDepth + 1);

            var ex = Assert.Throws<JsonParseException>(() => JsonReader.Parse(json));

            Assert.Contains("nesting deeper than", ex.Message);
        }

        [Fact]
        public void Reports_the_line_and_column_of_the_offending_character()
        {
            var ex = Assert.Throws<JsonParseException>(() => JsonReader.Parse("{\n  \"a\": 1,\n  \"b\": x\n}"));

            Assert.Equal(3, ex.Line);
            Assert.Equal(8, ex.Column);
            Assert.StartsWith("3:8: ", ex.Message);
        }

        [Fact]
        public void TryParse_reports_failure_instead_of_throwing()
        {
            Assert.False(JsonReader.TryParse("{", out var value, out var error));
            Assert.Null(value);
            Assert.StartsWith("1:2: ", error!);

            Assert.True(JsonReader.TryParse("{\"a\":1}", out var good, out var noError));
            Assert.Null(noError);
            Assert.NotNull(good);
        }

        [Fact]
        public void ParseObject_refuses_a_non_object_document()
        {
            var ex = Assert.Throws<JsonParseException>(() => JsonReader.ParseObject("[1]"));

            Assert.Contains("must be an object, not an array", ex.Message);
        }
    }
}
