using System;
using CwyLs.ModLoader.Core.Grading;
using CwyLs.ModLoader.Core.Textures;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// D-SCENERY: material property rules in <c>textures/_rules.json</c> (colour, tint, float, vector
    /// on a material name or pattern), living beside the texture entries of the same file.
    /// </summary>
    public class TexturesPropertyRuleTests
    {
        private const string PackId = "cwy.demo.scenery";

        [Fact]
        public void Reads_every_kind_of_value()
        {
            var set = MaterialPropertyRuleParser.FromRulesJson(
                PackId,
                "[{\"material\":\"mt_desk_1\",\"property\":\"_Color\",\"tint\":\"#E8C9A8\"}," +
                " {\"material\":\"mt_env_*\",\"property\":\"color\",\"color\":[0.9,0.95,1.0]}," +
                " {\"material\":\"mt_env_buil\",\"property\":\"emission\",\"color\":\"#FFD9A0\",\"intensity\":2}," +
                " {\"material\":\"mt_env_aurora\",\"property\":\"_Contrast\",\"float\":0.8}," +
                " {\"material\":\"mt_desk_1\",\"property\":\"hsvg\",\"vector\":[0.05,1.1]}]");

            Assert.Empty(set.Problems);
            Assert.Equal(5, set.Count);

            var tint = set.Rules[0];
            Assert.Equal(MaterialPropertyKind.Tint, tint.Kind);
            Assert.Equal(0xE8 / 255.0, tint.Value.X, 9);
            Assert.Equal(1.0, tint.Value.W, 9);
            Assert.Equal("textures/_rules.json[0]", tint.Entry);

            Assert.Equal("_Color", set.Rules[1].PropertyName);
            Assert.Equal(1.0, set.Rules[1].Value.W, 9);

            var emission = set.Rules[2];
            Assert.Equal("_EmissionColor", emission.PropertyName);
            Assert.Equal(2.0, emission.Value.X, 9);

            Assert.Equal(MaterialPropertyKind.Float, set.Rules[3].Kind);
            Assert.Equal(0.8, set.Rules[3].Value.X, 9);

            Assert.Equal("_MainTexHSVG", set.Rules[4].PropertyName);
            Assert.Equal(new Float4(0.05, 1.1, 0, 0), set.Rules[4].Value);
        }

        [Fact]
        public void A_tint_multiplies_the_original_and_a_colour_replaces_it()
        {
            var tint = new MaterialPropertyRule(PackId, "mt_desk", "_Color", MaterialPropertyKind.Tint, new Float4(0.5, 1, 0.25, 1), "x");
            var color = new MaterialPropertyRule(PackId, "mt_desk", "_Color", MaterialPropertyKind.Color, new Float4(0.1, 0.2, 0.3, 1), "x");
            var original = new Float4(0.8, 0.6, 0.4, 1);

            Assert.Equal(new Float4(0.4, 0.6, 0.1, 1), tint.Compute(original));
            Assert.Equal(new Float4(0.1, 0.2, 0.3, 1), color.Compute(original));
        }

        [Theory]
        [InlineData("#fff", 1, 1, 1, 1)]
        [InlineData("#000000", 0, 0, 0, 1)]
        [InlineData("80FF0040", 128 / 255.0, 1, 0, 64 / 255.0)]
        public void Hex_colours_are_read(string hex, double r, double g, double b, double a)
        {
            Assert.True(MaterialPropertyRuleParser.TryColor(hex, out var c, out _));
            Assert.Equal(new Float4(r, g, b, a), c);
        }

        [Theory]
        [InlineData("[{\"renderer\":\"m_desk\",\"property\":\"_Color\",\"color\":\"#fff\"}]", "not a renderer")]
        [InlineData("[{\"property\":\"_Color\",\"color\":\"#fff\"}]", "\"material\" is required")]
        [InlineData("[{\"material\":\"mt_desk\",\"color\":\"#fff\"}]", "\"property\" is required")]
        [InlineData("[{\"material\":\"mt_desk\",\"property\":\"Color Tint\",\"color\":\"#fff\"}]", "neither a shader property")]
        [InlineData("[{\"material\":\"mt_desk\",\"property\":\"_Color\",\"color\":\"#ff\"}]", "is not a #RGB")]
        [InlineData("[{\"material\":\"mt_desk\",\"property\":\"_Color\",\"color\":[1,-1,0]}]", "0 or more")]
        [InlineData("[{\"material\":\"mt_desk\",\"property\":\"_Color\",\"color\":\"#fff\",\"tint\":\"#fff\"}]", "cannot both be set")]
        [InlineData("[{\"material\":\"mt_desk\",\"property\":\"_X\",\"float\":\"1\"}]", "expected a number")]
        [InlineData("[{\"material\":\"mt_desk\",\"property\":\"_X\",\"float\":1,\"intensity\":2}]", "only goes with")]
        [InlineData("[{\"material\":\"mt_desk\",\"property\":\"_X\",\"vector\":[]}]", "1 to 4 numbers")]
        [InlineData("[{\"material\":\"mt_desk\",\"property\":\"_X\",\"float\":1,\"slot\":2}]", "unknown member \"slot\"")]
        public void A_bad_entry_costs_only_that_entry(string json, string expected)
        {
            var set = MaterialPropertyRuleParser.FromRulesJson(PackId, json);

            Assert.Empty(set.Rules);
            Assert.Single(set.Problems);
            Assert.Contains(expected, set.Problems[0].Reason);
        }

        [Fact]
        public void Texture_and_property_entries_share_the_file_without_tripping_each_other()
        {
            var json = "[{\"material\":\"mt_Tee_A\",\"property\":\"_MainTex\",\"file\":\"tee.png\"}," +
                       " {\"material\":\"mt_desk_1\",\"property\":\"_Color\",\"tint\":\"#ccbbaa\",\"x-note\":\"warmer desk\"}]";

            var textures = TextureRuleParser.FromRulesJson(PackId, json);
            var properties = MaterialPropertyRuleParser.FromRulesJson(PackId, json);

            Assert.Empty(textures.Problems);
            Assert.Single(textures.Rules);
            Assert.Empty(properties.Problems);
            Assert.Single(properties.Rules);
        }

        [Fact]
        public void A_texture_entry_without_a_file_or_a_value_says_both_options()
        {
            var set = TextureRuleParser.FromRulesJson(PackId, "[{\"material\":\"mt_desk\",\"property\":\"_Color\"}]");

            Assert.Single(set.Problems);
            Assert.Contains("\"file\" is required", set.Problems[0].Reason);
            Assert.Contains("\"tint\"", set.Problems[0].Reason);
        }

        [Fact]
        public void A_later_pack_overrides_the_same_material_and_property()
        {
            var a = MaterialPropertyRuleParser.FromRulesJson("a", "[{\"material\":\"mt_desk\",\"property\":\"_Color\",\"tint\":\"#fff\"}]");
            var b = MaterialPropertyRuleParser.FromRulesJson("b", "[{\"material\":\"MT_DESK\",\"property\":\"_Color\",\"color\":\"#000\"}]");

            var merged = MaterialPropertyRuleSet.Merge(a, b);

            Assert.Single(merged.Rules);
            Assert.Equal("b", merged.Rules[0].PackId);
            Assert.Single(merged.Overridden);
        }

        [Fact]
        public void The_same_target_twice_in_one_file_is_reported()
        {
            var set = MaterialPropertyRuleParser.FromRulesJson(
                PackId,
                "[{\"material\":\"mt_desk\",\"property\":\"_Color\",\"tint\":\"#fff\"}," +
                " {\"material\":\"mt_desk\",\"property\":\"_Color\",\"tint\":\"#eee\"}]");

            Assert.Single(set.Rules);
            Assert.Single(set.Problems);
            Assert.Contains("later entry", set.Problems[0].Reason);
        }

        [Fact]
        public void Colour_mirrors_follow_lilToon_and_urp()
        {
            Assert.Equal(new[] { "_BaseColor" }, MaterialPropertyRuleParser.MirrorsOf("_Color"));
            Assert.Equal(new[] { "_Color" }, MaterialPropertyRuleParser.MirrorsOf("_BaseColor"));
            Assert.Empty(MaterialPropertyRuleParser.MirrorsOf("_EmissionColor"));
        }

        [Fact]
        public void A_rule_matches_by_pattern()
        {
            var rule = new MaterialPropertyRule(PackId, "mt_env_*", "_Color", MaterialPropertyKind.Tint, new Float4(1, 1, 1, 1), "x");

            Assert.True(rule.Matches("mt_env_sakura"));
            Assert.False(rule.Matches("mt_desk"));
            Assert.Equal("@mt_env_*._color", rule.Key);
        }

        [Fact]
        public void A_rule_needs_an_underscore_property()
        {
            Assert.Throws<ArgumentException>(() =>
                new MaterialPropertyRule(PackId, "mt_desk", "Color", MaterialPropertyKind.Color, default, "x"));
        }

        [Fact]
        public void No_rules_file_is_no_rules()
        {
            Assert.Equal(0, MaterialPropertyRuleParser.FromRulesJson(PackId, null).Count);
            Assert.Equal(0, MaterialPropertyRuleParser.FromRulesJson(PackId, "{not json").Count);
        }
    }
}
