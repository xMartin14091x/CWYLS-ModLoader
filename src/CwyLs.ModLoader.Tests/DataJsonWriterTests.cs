using System;
using System.Collections.Generic;
using System.Text.Json;
using CwyLs.ModLoader.Core.Data;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// Pins the wire format of <see cref="JsonWriter"/>. Every assertion parses the output with a
    /// real JSON reader rather than comparing strings, so the tests say "a consumer can read this",
    /// which is the actual contract: the snapshot exists to be read by tools we do not own.
    /// </summary>
    public class DataJsonWriterTests
    {
        private static JsonElement Parse(object? value) =>
            JsonDocument.Parse(JsonWriter.Write(value)).RootElement.Clone();

        private sealed class Leaf
        {
            public string Name { get; init; } = string.Empty;

            public int Count { get; init; }
        }

        private sealed class Node
        {
            public string Label { get; init; } = string.Empty;

            public Node? Child { get; set; }
        }

        private sealed class Thrower
        {
            public string Fine => "ok";

            public string Broken => throw new InvalidOperationException("boom");
        }

        [Fact]
        public void Null_is_json_null()
        {
            Assert.Equal("null", JsonWriter.Write(null));
        }

        [Fact]
        public void Primitives_keep_their_json_types()
        {
            var root = Parse(new Dictionary<string, object?>
            {
                ["flag"] = true,
                ["count"] = 42,
                ["ratio"] = 0.5,
                ["name"] = "satone",
                ["missing"] = null,
            });

            Assert.Equal(JsonValueKind.True, root.GetProperty("flag").ValueKind);
            Assert.Equal(42, root.GetProperty("count").GetInt32());
            Assert.Equal(0.5, root.GetProperty("ratio").GetDouble());
            Assert.Equal("satone", root.GetProperty("name").GetString());
            Assert.Equal(JsonValueKind.Null, root.GetProperty("missing").ValueKind);
        }

        [Fact]
        public void Ulong_is_a_string_so_javascript_readers_keep_every_digit()
        {
            // The game mints ids from DateTime.Now.Ticks, far above the 2^53 a JSON float holds
            // exactly. Emitting them as numbers would silently corrupt every todo and note id.
            var id = 638_000_000_000_000_001UL;
            var root = Parse(new Dictionary<string, object?> { ["id"] = id });

            Assert.Equal(JsonValueKind.String, root.GetProperty("id").ValueKind);
            Assert.Equal("638000000000000001", root.GetProperty("id").GetString());
        }

        [Fact]
        public void Enums_are_written_by_name()
        {
            var root = Parse(new Dictionary<string, object?>
            {
                ["section"] = GameDataSection.Todos,
                ["state"] = TodoViewState.Complete,
            });

            Assert.Equal("Todos", root.GetProperty("section").GetString());
            Assert.Equal("Complete", root.GetProperty("state").GetString());
        }

        [Fact]
        public void Utc_and_local_and_unspecified_datetimes_each_carry_their_kind()
        {
            var root = Parse(new Dictionary<string, object?>
            {
                ["utc"] = new DateTime(2026, 9, 22, 13, 42, 1, 123, DateTimeKind.Utc),
                ["plain"] = new DateTime(2026, 9, 22, 13, 42, 1, 123, DateTimeKind.Unspecified),
            });

            Assert.Equal("2026-09-22T13:42:01.123Z", root.GetProperty("utc").GetString());
            Assert.Equal("2026-09-22T13:42:01.123", root.GetProperty("plain").GetString());
        }

        [Fact]
        public void DateTimeOffset_keeps_its_offset()
        {
            var when = new DateTimeOffset(2026, 9, 22, 13, 42, 1, 123, TimeSpan.FromHours(9));
            var root = Parse(new Dictionary<string, object?> { ["at"] = when });

            Assert.Equal("2026-09-22T13:42:01.123+09:00", root.GetProperty("at").GetString());
        }

        [Fact]
        public void Guid_and_timespan_become_strings()
        {
            var root = Parse(new Dictionary<string, object?>
            {
                ["id"] = new Guid("0f8fad5b-d9cb-469f-a165-70867728950e"),
                ["span"] = TimeSpan.FromMinutes(25),
            });

            Assert.Equal("0f8fad5b-d9cb-469f-a165-70867728950e", root.GetProperty("id").GetString());
            Assert.Equal("00:25:00", root.GetProperty("span").GetString());
        }

        [Fact]
        public void Non_finite_floats_become_null_because_nan_is_not_json()
        {
            var root = Parse(new Dictionary<string, object?>
            {
                ["nan"] = double.NaN,
                ["inf"] = float.PositiveInfinity,
                ["real"] = 1.5f,
            });

            Assert.Equal(JsonValueKind.Null, root.GetProperty("nan").ValueKind);
            Assert.Equal(JsonValueKind.Null, root.GetProperty("inf").ValueKind);
            Assert.Equal(1.5, root.GetProperty("real").GetDouble());
        }

        [Fact]
        public void Nested_lists_and_dictionaries_survive_intact()
        {
            var value = new Dictionary<string, object?>
            {
                ["lists"] = new List<object?>
                {
                    new Dictionary<string, object?> { ["items"] = new List<object?> { 1, 2, 3 } },
                    new Dictionary<string, object?> { ["items"] = new List<object?>() },
                },
            };

            var root = Parse(value);
            var lists = root.GetProperty("lists");

            Assert.Equal(2, lists.GetArrayLength());
            Assert.Equal(3, lists[0].GetProperty("items").GetArrayLength());
            Assert.Equal(2, lists[0].GetProperty("items")[1].GetInt32());
            Assert.Equal(0, lists[1].GetProperty("items").GetArrayLength());
        }

        [Fact]
        public void Dictionary_keys_are_stringified_including_ulong_ids()
        {
            var root = Parse(new Dictionary<ulong, string> { [638_000_000_000_000_001UL] = "page" });

            Assert.Equal("page", root.GetProperty("638000000000000001").GetString());
        }

        [Fact]
        public void Object_members_are_camel_cased()
        {
            var root = Parse(new Leaf { Name = "note", Count = 2 });

            Assert.True(root.TryGetProperty("name", out _));
            Assert.True(root.TryGetProperty("count", out _));
            Assert.False(root.TryGetProperty("Name", out _));
        }

        [Fact]
        public void Japanese_and_thai_text_round_trips_unescaped()
        {
            // The game ships Japanese; P2-4 adds Thai subtitles. Escaping these would be legal
            // JSON but would make the snapshot unreadable to a human debugging a prompt.
            const string japanese = "さとねと集中する";
            const string thai = "โฟกัสกับซาโตเนะ";

            var root = Parse(new Dictionary<string, object?> { ["ja"] = japanese, ["th"] = thai });

            Assert.Equal(japanese, root.GetProperty("ja").GetString());
            Assert.Equal(thai, root.GetProperty("th").GetString());
            Assert.Contains(japanese, JsonWriter.Write(japanese), StringComparison.Ordinal);
        }

        [Fact]
        public void Control_characters_in_player_text_stay_valid_json()
        {
            var root = Parse(new Dictionary<string, object?> { ["body"] = "line1\nline2\t\"quoted\"" });

            Assert.Equal("line1\nline2\t\"quoted\"", root.GetProperty("body").GetString());
        }

        [Fact]
        public void A_reference_cycle_is_replaced_not_followed()
        {
            var a = new Node { Label = "a" };
            var b = new Node { Label = "b" };
            a.Child = b;
            b.Child = a;

            var root = Parse(a);

            Assert.Equal("a", root.GetProperty("label").GetString());
            Assert.Equal("b", root.GetProperty("child").GetProperty("label").GetString());
            Assert.Equal(
                JsonWriter.CycleMarker,
                root.GetProperty("child").GetProperty("child").GetString());
        }

        [Fact]
        public void A_member_whose_getter_throws_is_reported_and_skipped()
        {
            var errors = new List<string>();
            var json = JsonWriter.Write(new Thrower(), errors.Add);
            var root = JsonDocument.Parse(json).RootElement;

            Assert.Equal("ok", root.GetProperty("fine").GetString());
            Assert.False(root.TryGetProperty("broken", out _));
            Assert.Single(errors);
            Assert.Contains("Broken", errors[0], StringComparison.Ordinal);
        }
    }
}
