using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using CwyLs.ModLoader.Core.Data;
using CwyLs.ModLoader.Core.Json;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// <c>helper.Data.ReadJson</c> then <c>helper.Data.WriteJson</c> must give back the same JSON.
    /// The facet (<c>ModData</c>) lives in the Unity plugin assembly this host cannot load, so the
    /// tests drive the exact two Core calls it makes, <see cref="JsonReader.Parse"/> and
    /// <see cref="JsonWriter.Write"/>, and a source pin keeps the facet on those two calls.
    /// Before the 0.9.2 fix a parsed <see cref="JsonObject"/> (generic <c>IDictionary</c> only) was
    /// written as an array of <c>{"key":..,"value":..}</c> pairs.
    /// </summary>
    public class DataJsonRoundTripTests
    {
        /// <summary>What the facet does: <c>ReadJson</c> (parse) followed by <c>WriteJson</c> (write).</summary>
        private static string RoundTrip(string json) => JsonWriter.Write(JsonReader.Parse(json));

        [Theory]
        [InlineData("{\"x\":1}")]
        [InlineData("{}")]
        [InlineData("[{\"a\":{\"b\":[1,2]}},{}]")]
        [InlineData("[{\"id\":1,\"name\":\"satone\"},{\"id\":2,\"name\":\"kai\"}]")]
        [InlineData("{\"outer\":{\"inner\":{\"deep\":true}},\"list\":[{\"k\":null}]}")]
        public void Read_then_write_gives_back_the_same_json(string json)
        {
            Assert.Equal(json, RoundTrip(json));
        }

        [Fact]
        public void An_object_keeps_document_order_and_scalar_types()
        {
            const string json = "{\"z\":\"last first\",\"a\":2,\"m\":1.5,\"b\":false,\"n\":null}";
            Assert.Equal(json, RoundTrip(json));
        }

        [Fact]
        public void Any_string_keyed_generic_dictionary_is_an_object()
        {
            var map = new GenericOnlyMap { ["one"] = 1, ["two"] = 2 };
            Assert.Equal("{\"one\":1,\"two\":2}", JsonWriter.Write(map));
        }

        [Fact]
        public void A_json_object_that_contains_itself_is_cut_with_the_cycle_marker()
        {
            var root = new JsonObject();
            root["self"] = root;
            Assert.Equal("{\"self\":\"" + JsonWriter.CycleMarker + "\"}", JsonWriter.Write(root));
        }

        [Fact]
        public void The_facet_still_reads_and_writes_through_the_tested_calls()
        {
            var root = TestRepo.Root;
            Assert.NotNull(root);
            var source = File.ReadAllText(Path.Combine(root!, "src", "CwyLs.ModLoader", "Mods", "ModFacets.Framework.cs"));

            Assert.Contains("return text is null ? null : JsonReader.Parse(text);", source);
            Assert.Contains("var json = JsonWriter.Write(value,", source);
        }

        /// <summary>Implements only the generic interface, like <see cref="JsonObject"/>.</summary>
        private sealed class GenericOnlyMap : IDictionary<string, int>
        {
            private readonly List<KeyValuePair<string, int>> _items = new List<KeyValuePair<string, int>>();

            public int this[string key]
            {
                get => throw new NotSupportedException();
                set => _items.Add(new KeyValuePair<string, int>(key, value));
            }

            public ICollection<string> Keys => throw new NotSupportedException();

            public ICollection<int> Values => throw new NotSupportedException();

            public int Count => _items.Count;

            public bool IsReadOnly => false;

            public void Add(string key, int value) => this[key] = value;

            public void Add(KeyValuePair<string, int> item) => _items.Add(item);

            public void Clear() => _items.Clear();

            public bool Contains(KeyValuePair<string, int> item) => _items.Contains(item);

            public bool ContainsKey(string key) => throw new NotSupportedException();

            public void CopyTo(KeyValuePair<string, int>[] array, int arrayIndex) => _items.CopyTo(array, arrayIndex);

            public IEnumerator<KeyValuePair<string, int>> GetEnumerator() => _items.GetEnumerator();

            public bool Remove(string key) => throw new NotSupportedException();

            public bool Remove(KeyValuePair<string, int> item) => _items.Remove(item);

            public bool TryGetValue(string key, out int value) => throw new NotSupportedException();

            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        }
    }
}
