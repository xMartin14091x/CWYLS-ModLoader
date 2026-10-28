using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CwyLs.ModLoader.Core.Subtitles;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// B3: the mark-positioning table - parsing a language pack's mark table (measured from the
    /// font's own GPOS data), and planning the pair-adjustment records one font asset needs. The
    /// Unity half (inserting the records into a TMP asset) is covered by <c>pb.language.fonts</c> in
    /// game.
    /// </summary>
    public partial class SubtitlesMarkPlacementTests
    {
        private const string Family = "IBM Plex Sans Thai Looped";

        private const string Small =
            "{\"schema\":1,\"font\":{\"family\":\"" + Family + "\",\"style\":\"Medium\",\"file\":\"x.otf\",\"unitsPerEm\":1000},"
            + "\"source\":{\"table\":\"GPOS\"},\"fallbackGap\":30,"
            + "\"marks\":[{\"code\":\"0E35\",\"gid\":463},{\"code\":\"0E48\",\"gid\":457},{\"code\":\"0E34\",\"gid\":460}],"
            + "\"pairs\":[{\"preceding\":\"0E35\",\"mark\":\"0E48\",\"dx\":-4,\"dy\":293},"
            + "{\"preceding\":\"0E34\",\"mark\":\"0E48\",\"dx\":-4,\"dy\":249}]}";

        private static MarkTable Table() => MarkTable.Parse(Small, "test.json");

        [Fact]
        public void Parses_the_font_the_pairs_and_the_glyph_ids()
        {
            var table = Table();

            Assert.Equal(Family, table.Family);
            Assert.Equal("Medium", table.Style);
            Assert.Equal(1000, table.UnitsPerEm);
            Assert.Equal(2, table.Pairs.Count);
            Assert.Equal(0x0E35, table.Pairs[0].Preceding);
            Assert.Equal(0x0E48, table.Pairs[0].Mark);
            Assert.Equal(-4, table.Pairs[0].Dx);
            Assert.Equal(293, table.Pairs[0].Dy);
            Assert.True(table.TryGetGlyphId(0x0E48, out var gid));
            Assert.Equal(457u, gid);
            Assert.False(table.TryGetGlyphId(0x0E49, out _));
        }

        [Theory]
        [InlineData("", "the file is empty")]
        [InlineData("[]", "must be an object")]
        [InlineData("{\"schema\":2,\"font\":{},\"pairs\":[]}", "this framework reads 1")]
        [InlineData("{\"schema\":1,\"pairs\":[]}", "\"font\" must be an object")]
        [InlineData("{\"schema\":1,\"font\":{\"unitsPerEm\":1000},\"pairs\":[]}", "font.family\" is required")]
        [InlineData("{\"schema\":1,\"font\":{\"family\":\"F\",\"unitsPerEm\":0},\"pairs\":[]}", "must be positive")]
        [InlineData("{\"schema\":1,\"font\":{\"family\":\"F\",\"unitsPerEm\":1000},\"pairs\":[]}", "non-empty array")]
        [InlineData("{\"schema\":1,\"font\":{\"family\":\"F\",\"unitsPerEm\":1000},\"pairs\":[{\"preceding\":\"zz\",\"mark\":\"0E48\",\"dx\":0,\"dy\":1}]}", "hexadecimal code point")]
        [InlineData("{\"schema\":1,\"font\":{\"family\":\"F\",\"unitsPerEm\":1000},\"pairs\":[{\"preceding\":\"0E35\",\"mark\":\"0E48\",\"dx\":0.5,\"dy\":1}]}", "\"dx\" must be an integer")]
        [InlineData("{\"schema\":1,\"font\":{\"family\":\"F\",\"unitsPerEm\":1000},\"pairs\":[{\"preceding\":\"0E35\",\"mark\":\"0E48\",\"dx\":0,\"dy\":1}],\"extra\":1}", "unknown member \"extra\"")]
        public void Refuses_a_bad_table_naming_the_member(string json, string expected)
        {
            var ex = Assert.Throws<FormatException>(() => MarkTable.Parse(json, "bad.json"));
            Assert.Contains(expected, ex.Message);
            Assert.False(MarkTable.TryParse(json, "bad.json", out var table, out var error));
            Assert.Null(table);
            Assert.Contains(expected, error);
        }

        [Fact]
        public void Offsets_are_scaled_to_the_asset_s_point_size_and_only_the_second_glyph_moves()
        {
            // Base_TH: 20-point static asset, same family, its own character table holds the glyphs.
            var plan = MarkPlacement.Plan(Table(), Family, 20f, AssetGlyphs(0x0E35, 463, 0x0E48, 457, 0x0E34, 460), MarkPlacementMode.Full);

            Assert.Equal(2, plan.Records.Count);
            var first = plan.Records[0];
            Assert.Equal(463u, first.FirstGlyph);
            Assert.Equal(457u, first.SecondGlyph);
            Assert.Equal(293 * 0.02f, first.YPlacement, 4);
            Assert.Equal(-4 * 0.02f, first.XPlacement, 4);
            Assert.Equal((457u << 16) | 463u, first.Key);   // GlyphPairKey: (second << 16) | first
            Assert.Equal(0, plan.Skipped);
        }

        [Fact]
        public void Vertical_only_drops_the_sideways_shift()
        {
            var plan = MarkPlacement.Plan(Table(), Family, 90f, AssetGlyphs(0x0E35, 463, 0x0E48, 457, 0x0E34, 460), MarkPlacementMode.VerticalOnly);

            Assert.All(plan.Records, r => Assert.Equal(0f, r.XPlacement));
            Assert.Equal(293 * 0.09f, plan.Records[0].YPlacement, 4);
            Assert.Contains("vertical only", plan.Detail);
        }

        [Fact]
        public void Off_plans_nothing()
        {
            var plan = MarkPlacement.Plan(Table(), Family, 20f, null, MarkPlacementMode.Off);

            Assert.Empty(plan.Records);
            Assert.Contains("off", plan.Detail);
        }

        [Theory]
        [InlineData("Noto Sans Thai")]
        [InlineData("")]
        [InlineData(null)]
        public void An_asset_of_another_family_gets_nothing(string? family)
        {
            var plan = MarkPlacement.Plan(Table(), family, 20f, null, MarkPlacementMode.Full);

            Assert.Empty(plan.Records);
            Assert.Contains("is not the table's", plan.Detail);
        }

        [Fact]
        public void The_family_match_ignores_case_and_surrounding_spaces()
        {
            Assert.True(MarkPlacement.SameFamily(Family, "  ibm plex sans thai looped "));
            Assert.False(MarkPlacement.SameFamily(Family, "IBM Plex Sans Thai Looped Medm"));
        }

        [Fact]
        public void A_dynamic_asset_with_an_empty_character_table_uses_the_table_s_glyph_ids()
        {
            // Dynamic_TH adds characters only as text is drawn; same family, same font file.
            var plan = MarkPlacement.Plan(Table(), Family, 20f, _ => null, MarkPlacementMode.Full);

            Assert.Equal(2, plan.Records.Count);
            Assert.Equal(460u, plan.Records[1].FirstGlyph);
        }

        [Fact]
        public void A_pair_whose_glyph_cannot_be_found_is_skipped()
        {
            var json = Small.Replace("{\"code\":\"0E34\",\"gid\":460}", "{\"code\":\"0E34\"}");
            var plan = MarkPlacement.Plan(MarkTable.Parse(json, "t.json"), Family, 20f, _ => null, MarkPlacementMode.Full);

            Assert.Single(plan.Records);
            Assert.Equal(1, plan.Skipped);
            Assert.Contains("1 pair(s) skipped", plan.Detail);
        }

        [Fact]
        public void A_glyph_index_beyond_the_16_bit_key_is_skipped()
        {
            var plan = MarkPlacement.Plan(Table(), Family, 20f, AssetGlyphs(0x0E35, 70000, 0x0E48, 457, 0x0E34, 460), MarkPlacementMode.Full);

            Assert.Single(plan.Records);
            Assert.Equal(460u, plan.Records[0].FirstGlyph);
        }

        [Fact]
        public void An_asset_without_a_point_size_gets_nothing()
        {
            Assert.Empty(MarkPlacement.Plan(Table(), Family, 0f, null, MarkPlacementMode.Full).Records);
            Assert.Empty(MarkPlacement.Plan(Table(), Family, float.NaN, null, MarkPlacementMode.Full).Records);
        }

        private static Func<int, uint?> AssetGlyphs(params int[] pairs)
        {
            var map = new Dictionary<int, uint>();
            for (var i = 0; i < pairs.Length; i += 2)
            {
                map[pairs[i]] = (uint)pairs[i + 1];
            }

            return code => map.TryGetValue(code, out var glyph) ? glyph : (uint?)null;
        }
    }
}
