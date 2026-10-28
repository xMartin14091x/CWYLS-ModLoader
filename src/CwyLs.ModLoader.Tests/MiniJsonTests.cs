using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using CwyLs.ModLoader.Core;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    public class MiniJsonTests
    {
        [Theory]
        [InlineData("plain", "plain")]
        [InlineData("say \"hi\"", "say \\\"hi\\\"")]
        [InlineData("back\\slash", "back\\\\slash")]
        [InlineData("line\nbreak", "line\\nbreak")]
        [InlineData("tab\there", "tab\\there")]
        public void Escape_handles_the_json_specials(string input, string expected)
        {
            Assert.Equal(expected, MiniJson.Escape(input));
        }

        [Theory]
        [InlineData(0x01, "\\u0001")]
        [InlineData(0x1f, "\\u001f")]
        [InlineData(0x7f, "\\u007f")]
        public void Escape_uses_unicode_form_for_other_control_characters(int code, string expected)
        {
            Assert.Equal(expected, MiniJson.Escape(((char)code).ToString()));
        }

        [Fact]
        public void Escape_leaves_non_ascii_alone()
        {
            // Game text is Japanese and Thai; escaping it would be legal JSON but unreadable.
            Assert.Equal("さとね", MiniJson.Escape("さとね"));
        }

        [Fact]
        public void Escape_of_null_or_empty_is_empty()
        {
            Assert.Equal(string.Empty, MiniJson.Escape(null));
            Assert.Equal(string.Empty, MiniJson.Escape(string.Empty));
        }

        [Fact]
        public void String_quotes_and_nulls()
        {
            Assert.Equal("\"a\"", MiniJson.String("a"));
            Assert.Equal("null", MiniJson.String(null));
        }

        [Fact]
        public void Object_composes_members_in_order()
        {
            var json = MiniJson.Object(
                MiniJson.Member("ticket", MiniJson.String("P0-1")),
                MiniJson.Member("ok", MiniJson.Bool(true)),
                MiniJson.Member("n", MiniJson.Number(3L)));

            Assert.Equal("{\"ticket\":\"P0-1\",\"ok\":true,\"n\":3}", json);
        }

        [Fact]
        public void Object_of_nothing_is_an_empty_object()
        {
            Assert.Equal("{}", MiniJson.Object(Array.Empty<KeyValuePair<string, string>>()));
        }

        [Fact]
        public void Object_escapes_keys_too()
        {
            Assert.Equal("{\"a\\\"b\":1}", MiniJson.Object(MiniJson.Member("a\"b", MiniJson.Number(1L))));
        }

        [Fact]
        public void Number_is_culture_invariant()
        {
            // Guards against a locale that formats 1.5 as "1,5" corrupting the smoke report.
            var previous = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                Assert.Equal("1.5", MiniJson.Number(1.5d));
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = previous;
            }
        }

        [Fact]
        public void Iso8601_round_trips()
        {
            var when = new DateTimeOffset(2026, 9, 22, 13, 45, 1, 123, TimeSpan.FromHours(9));
            var text = MiniJson.Iso8601(when);

            Assert.Equal("2026-09-22T13:45:01.123+09:00", text);
            Assert.Equal(when, DateTimeOffset.Parse(text, CultureInfo.InvariantCulture));
        }
    }
}
