using CwyLs.ModLoader.Core.Packs;
using CwyLs.ModLoader.Core.Textures;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// P1-3: <c>textures/_rules.json</c>, the escape hatch for what the folder layout cannot say -
    /// a material index other than 0, or a file whose name is not the slot.
    /// </summary>
    public class TexturesRulesJsonTests
    {
        private const string PackId = "cwy.example";

        [Fact]
        public void Reads_the_documented_shape()
        {
            var set = TextureRuleParser.FromRulesJson(
                PackId,
                "[{\"renderer\":\"Body\",\"materialIndex\":0,\"property\":\"_MainTex\",\"file\":\"my.png\"}]");

            Assert.Empty(set.Problems);
            Assert.Single(set.Rules);

            var rule = set.Rules[0];
            Assert.Equal(PackId, rule.PackId);
            Assert.Equal("Body", rule.RendererName);
            Assert.Equal("_MainTex", rule.PropertyName);
            Assert.Equal(0, rule.MaterialIndex);
            Assert.Equal("textures/my.png", rule.RelativePath);
            Assert.Equal(TextureRuleSource.RulesFile, rule.Source);
        }

        [Fact]
        public void A_file_is_relative_to_the_textures_folder_and_an_index_defaults_to_zero()
        {
            var set = TextureRuleParser.FromRulesJson(
                PackId,
                "[{\"renderer\":\"Top1\",\"property\":\"base\",\"file\":\"alt/outfit_winter.png\"}," +
                " {\"renderer\":\"Body\",\"property\":\"rim\",\"file\":\"Body/rim.png\",\"materialIndex\":2}]");

            Assert.Empty(set.Problems);
            Assert.Equal(2, set.Count);
            Assert.Equal("textures/alt/outfit_winter.png", set.Rules[0].RelativePath);
            Assert.Equal("_MainTex", set.Rules[0].PropertyName);
            Assert.Equal(0, set.Rules[0].MaterialIndex);
            Assert.Equal(2, set.Rules[1].MaterialIndex);
            Assert.Equal("_RimColorTex", set.Rules[1].PropertyName);
        }

        [Fact]
        public void A_rules_entry_overrides_a_file_that_sits_in_the_matching_folder()
        {
            var files = PackContentIndex.Build(new[] { "textures/Body/_MainTex.png" }).Files;
            var json = "[{\"renderer\":\"Body\",\"property\":\"base\",\"file\":\"alt/summer.png\"}]";

            var set = TextureRuleParser.Build(PackId, files, json);

            Assert.Single(set.Rules);
            Assert.Equal("textures/alt/summer.png", set.Rules[0].RelativePath);
            Assert.Equal(TextureRuleSource.RulesFile, set.Rules[0].Source);
            Assert.Single(set.Overridden);
            Assert.Equal("textures/Body/_MainTex.png", set.Overridden[0].RelativePath);
        }

        [Theory]
        [InlineData("{\"renderer\":\"Body\"}", "must be a JSON array")]
        [InlineData("[{\"renderer\":\"Body\",", "invalid JSON at")]
        [InlineData("[\"Body\"]", "a rule must be an object")]
        [InlineData("[{\"property\":\"_MainTex\",\"file\":\"a.png\"}]", "\"renderer\" or \"material\" is required")]
        [InlineData("[{\"renderer\":\"Body\",\"file\":\"a.png\"}]", "\"property\" is required")]
        [InlineData("[{\"renderer\":\"Body\",\"property\":\"_MainTex\"}]", "\"file\" is required")]
        [InlineData("[{\"renderer\":\"\",\"property\":\"_MainTex\",\"file\":\"a.png\"}]", "non-empty string")]
        [InlineData("[{\"renderer\":\"Body\",\"property\":\"glow\",\"file\":\"a.png\"}]", "not a known slot alias")]
        [InlineData("[{\"renderer\":\"Body\",\"property\":\"base\",\"file\":\"a.psd\"}]", "must be a .png file")]
        [InlineData("[{\"renderer\":\"Body\",\"property\":\"base\",\"file\":\"../../evil.png\"}]", "not a path inside the pack")]
        [InlineData("[{\"renderer\":\"Body\",\"property\":\"base\",\"file\":\"a.png\",\"materialIndex\":-1}]", "not a material index")]
        [InlineData("[{\"renderer\":\"Body\",\"property\":\"base\",\"file\":\"a.png\",\"materialIndex\":\"0\"}]", "expected a whole number")]
        [InlineData("[{\"renderer\":\"Body\",\"property\":\"base\",\"file\":\"a.png\",\"slot\":\"x\"}]", "unknown member \"slot\"")]
        public void Reports_a_bad_file_or_entry_without_throwing(string json, string expected)
        {
            var set = TextureRuleParser.FromRulesJson(PackId, json);

            Assert.Empty(set.Rules);
            Assert.Single(set.Problems);
            Assert.Contains(expected, set.Problems[0].Reason);
            Assert.StartsWith(TextureRuleParser.RulesPath, set.Problems[0].Path);
        }

        [Fact]
        public void One_bad_entry_costs_only_that_entry()
        {
            var set = TextureRuleParser.FromRulesJson(
                PackId,
                "[{\"renderer\":\"Body\",\"property\":\"base\",\"file\":\"a.png\"}," +
                " {\"renderer\":\"Face\",\"property\":\"glow\",\"file\":\"b.png\"}," +
                " {\"renderer\":\"Hair\",\"property\":\"reflect\",\"file\":\"c.png\",\"x-note\":\"mine\"}]");

            Assert.Equal(2, set.Count);
            Assert.Single(set.Problems);
            Assert.Equal(TextureRuleParser.RulesPath + "[1]", set.Problems[0].Path);
            Assert.Equal("Hair", set.Rules[1].RendererName);
        }

        [Fact]
        public void Reads_a_material_entry()
        {
            var set = TextureRuleParser.FromRulesJson(
                PackId,
                "[{\"material\":\"mt_Tee_A\",\"property\":\"_MainTex\",\"file\":\"tee_white.png\"}]");

            Assert.Empty(set.Problems);
            Assert.Single(set.Rules);

            var rule = set.Rules[0];
            Assert.Equal(TextureRuleTarget.Material, rule.Target);
            Assert.Equal("mt_Tee_A", rule.MaterialName);
            Assert.Equal(string.Empty, rule.RendererName);
            Assert.Equal("_MainTex", rule.PropertyName);
            Assert.Equal("textures/tee_white.png", rule.RelativePath);
            Assert.Equal(TextureRuleSource.RulesFile, rule.Source);
        }

        [Fact]
        public void A_material_entry_takes_an_alias_too()
        {
            var set = TextureRuleParser.FromRulesJson(
                PackId,
                "[{\"material\":\"mt_Polo_A\",\"property\":\"base\",\"file\":\"@mt_Polo_A/white.png\"}]");

            Assert.Empty(set.Problems);
            Assert.Equal("_MainTex", set.Rules[0].PropertyName);
            Assert.Equal("textures/@mt_Polo_A/white.png", set.Rules[0].RelativePath);
        }

        [Theory]
        [InlineData(
            "[{\"renderer\":\"Top1\",\"material\":\"mt_Tee_A\",\"property\":\"base\",\"file\":\"a.png\"}]",
            "cannot both be set")]
        [InlineData("[{\"property\":\"base\",\"file\":\"a.png\"}]", "\"renderer\" or \"material\" is required")]
        [InlineData(
            "[{\"material\":\"mt_Tee_A\",\"property\":\"base\",\"file\":\"a.png\",\"materialIndex\":1}]",
            "has no meaning with \"material\"")]
        [InlineData("[{\"material\":\"\",\"property\":\"base\",\"file\":\"a.png\"}]", "non-empty string")]
        public void Reports_a_bad_material_entry(string json, string expected)
        {
            var set = TextureRuleParser.FromRulesJson(PackId, json);

            Assert.Empty(set.Rules);
            Assert.Single(set.Problems);
            Assert.Contains(expected, set.Problems[0].Reason);
        }

        [Fact]
        public void A_material_entry_overrides_an_at_folder_file_for_the_same_material()
        {
            var files = PackContentIndex.Build(new[] { "textures/@mt_Tee_A/_MainTex.png" }).Files;
            var json = "[{\"material\":\"mt_Tee_A\",\"property\":\"base\",\"file\":\"alt/tee_white.png\"}]";

            var set = TextureRuleParser.Build(PackId, files, json);

            Assert.Single(set.Rules);
            Assert.Equal(TextureRuleTarget.Material, set.Rules[0].Target);
            Assert.Equal("textures/alt/tee_white.png", set.Rules[0].RelativePath);
            Assert.Single(set.Overridden);
        }

        [Fact]
        public void No_rules_file_is_not_a_problem()
        {
            Assert.True(TextureRuleParser.FromRulesJson(PackId, null).IsEmpty);
            Assert.True(TextureRuleParser.FromRulesJson(PackId, "   ").IsEmpty);
            Assert.True(TextureRuleParser.FromRulesJson(PackId, "[]").IsEmpty);
        }
    }
}
