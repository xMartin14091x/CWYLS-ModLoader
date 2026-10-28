using System.Collections.Generic;
using CwyLs.ModLoader.Core.Textures;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// P1-3: what happens when two packs want the same material slot. Load order decides, and the
    /// loser stays visible so a log line can say who lost to whom.
    /// </summary>
    public class TexturesRuleSetTests
    {
        private static TextureRuleSet Pack(string id, params string[] paths)
        {
            var rules = new List<TextureRule>();
            foreach (var path in paths)
            {
                var rule = TextureRuleParser.FromPath(id, path, out _);
                Assert.NotNull(rule);
                rules.Add(rule!);
            }

            return new TextureRuleSet(rules);
        }

        [Fact]
        public void A_later_pack_overrides_an_earlier_one_for_the_same_slot()
        {
            var merged = TextureRuleSet.Merge(
                Pack("a.pack", "textures/Body/_MainTex.png"),
                Pack("b.pack", "textures/Body/_MainTex.png"));

            Assert.Single(merged.Rules);
            Assert.Equal("b.pack", merged.Rules[0].PackId);
            Assert.Single(merged.Overridden);
            Assert.Equal("a.pack", merged.Overridden[0].PackId);
        }

        [Fact]
        public void An_alias_and_an_explicit_property_are_the_same_slot()
        {
            // a.pack writes base.png, b.pack writes _MainTex.png: same property, so b wins.
            var merged = TextureRuleSet.Merge(
                Pack("a.pack", "textures/Body/base.png"),
                Pack("b.pack", "textures/Body/_MainTex.png"));

            Assert.Single(merged.Rules);
            Assert.Equal("b.pack", merged.Rules[0].PackId);
            Assert.Equal("_MainTex", merged.Rules[0].PropertyName);
        }

        [Fact]
        public void Renderer_names_are_matched_case_insensitively()
        {
            var merged = TextureRuleSet.Merge(
                Pack("a.pack", "textures/Body/_MainTex.png"),
                Pack("b.pack", "textures/BODY/_MainTex.png"));

            Assert.Single(merged.Rules);
            Assert.Equal("b.pack", merged.Rules[0].PackId);
            Assert.Equal("BODY", merged.Rules[0].RendererName);
        }

        [Fact]
        public void Different_slots_and_renderers_all_survive()
        {
            var merged = TextureRuleSet.Merge(
                Pack("a.pack", "textures/Body/_MainTex.png", "textures/Face/_MainTex.png"),
                Pack("b.pack", "textures/Body/shadow.png", "textures/Hair/reflect.png"));

            Assert.Equal(4, merged.Count);
            Assert.Empty(merged.Overridden);
            Assert.Equal(new[] { "Body", "Face", "Hair" }, merged.RendererNames);
        }

        [Fact]
        public void The_winner_keeps_the_losers_position_so_the_log_order_is_stable()
        {
            var merged = TextureRuleSet.Merge(
                Pack("a.pack", "textures/Body/_MainTex.png", "textures/Face/_MainTex.png", "textures/Hair/_MainTex.png"),
                Pack("b.pack", "textures/Face/_MainTex.png"));

            Assert.Equal(3, merged.Count);
            Assert.Equal(new[] { "Body", "Face", "Hair" }, merged.RendererNames);
            Assert.Equal("b.pack", merged.Rules[1].PackId);
        }

        [Fact]
        public void A_different_material_index_is_a_different_slot()
        {
            var first = new TextureRuleSet(new[]
            {
                new TextureRule("a.pack", "Body", "_MainTex", "textures/Body/_MainTex.png"),
            });

            var second = new TextureRuleSet(new[]
            {
                new TextureRule("b.pack", "Body", "_MainTex", "textures/Body/alt.png", materialIndex: 1),
            });

            var merged = TextureRuleSet.Merge(first, second);

            Assert.Equal(2, merged.Count);
            Assert.Empty(merged.Overridden);
        }

        [Fact]
        public void Merging_carries_every_packs_problems_and_skips_nulls()
        {
            var withProblem = new TextureRuleSet(
                null,
                new[] { new TextureRuleProblem("a.pack", "textures/Body/glow.png", "unknown slot") });

            var merged = TextureRuleSet.Merge(withProblem, null, Pack("b.pack", "textures/Body/_MainTex.png"));

            Assert.Single(merged.Rules);
            Assert.Single(merged.Problems);
            Assert.Equal("a.pack/textures/Body/glow.png: unknown slot", merged.Problems[0].ToString());
        }

        [Fact]
        public void Merging_nothing_is_empty_and_summarises_itself()
        {
            var merged = TextureRuleSet.Merge((IEnumerable<TextureRuleSet?>?)null);

            Assert.True(merged.IsEmpty);
            Assert.Equal(
                "0 rule(s) over 0 renderer(s) and 0 material(s); 0 overridden; 0 problem(s)",
                merged.Summarize());
        }

        [Fact]
        public void For_renderer_filters_case_insensitively()
        {
            var merged = TextureRuleSet.Merge(
                Pack("a.pack", "textures/Body/_MainTex.png", "textures/Body/rim.png", "textures/Face/_MainTex.png"));

            Assert.Equal(2, merged.ForRenderer("body").Count);
            Assert.Single(merged.ForRenderer("Face"));
            Assert.Empty(merged.ForRenderer("Nope"));
            Assert.Empty(merged.ForRenderer(null));
        }
    }
}
