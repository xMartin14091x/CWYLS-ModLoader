using System;
using CwyLs.ModLoader.Core.Json;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>P1-1: dotted lookups into a parsed JSON graph.</summary>
    public class JsonPathTests
    {
        private const string Document =
            "{\"id\":\"cwy.example\"," +
            "\"dependsOn\":[{\"id\":\"a\",\"minVersion\":\"1.2.3\"},{\"id\":\"b\"}]," +
            "\"nested\":{\"rows\":[[10,20],[30]]}," +
            "\"nothing\":null}";

        private static object? Root => JsonReader.Parse(Document);

        [Theory]
        [InlineData("id", "cwy.example")]
        [InlineData("dependsOn[0].id", "a")]
        [InlineData("dependsOn[0].minVersion", "1.2.3")]
        [InlineData("dependsOn[1].id", "b")]
        public void Reads_strings_at_a_path(string path, string expected) =>
            Assert.Equal(expected, JsonPath.GetString(Root, path));

        [Fact]
        public void Reads_through_chained_array_indices() =>
            Assert.Equal(30L, JsonPath.Get(Root, "nested.rows[1][0]"));

        [Fact]
        public void An_empty_path_returns_the_root()
        {
            var root = Root;

            Assert.Same(root, JsonPath.Get(root, string.Empty));
        }

        [Theory]
        [InlineData("missing")]
        [InlineData("dependsOn[9].id")]
        [InlineData("dependsOn[1].minVersion")]
        [InlineData("id.deeper")]
        [InlineData("nested.rows[0][5]")]
        public void A_missing_step_is_null_not_an_error(string path)
        {
            Assert.Null(JsonPath.Get(Root, path));
            Assert.False(JsonPath.TryGet(Root, path, out var value));
            Assert.Null(value);
        }

        [Fact]
        public void TryGet_separates_a_stored_null_from_an_absent_member()
        {
            Assert.True(JsonPath.TryGet(Root, "nothing", out var present));
            Assert.Null(present);

            Assert.False(JsonPath.TryGet(Root, "absent", out _));
        }

        [Fact]
        public void GetString_returns_null_when_the_value_is_not_a_string() =>
            Assert.Null(JsonPath.GetString(Root, "nested"));

        [Theory]
        [InlineData("dependsOn[0")]
        [InlineData("dependsOn[x]")]
        [InlineData("a..b")]
        public void A_malformed_path_is_our_bug_and_throws(string path) =>
            Assert.Throws<FormatException>(() => JsonPath.Get(Root, path));

        [Fact]
        public void A_null_path_throws() =>
            Assert.Throws<ArgumentNullException>(() => JsonPath.Get(Root, null!));
    }
}
