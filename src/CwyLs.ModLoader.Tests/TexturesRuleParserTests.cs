using System.Collections.Generic;
using CwyLs.ModLoader.Core.Packs;
using CwyLs.ModLoader.Core.Textures;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// P1-3: the folder convention <c>textures/&lt;renderer&gt;/&lt;slot&gt;.png</c>, decided from
    /// paths alone - the same reason <see cref="PackContentIndex"/> is path-only.
    /// </summary>
    public class TexturesRuleParserTests
    {
        private const string PackId = "cwy.example";

        [Theory]
        // An exact shader property, spelled the way lilToon spells it.
        [InlineData("textures/Body/_MainTex.png", "Body", "_MainTex")]
        [InlineData("textures/Face/_ShadowBorderMask.png", "Face", "_ShadowBorderMask")]
        // A friendly alias. base -> _MainTex is the one the example pack uses.
        [InlineData("textures/Body/base.png", "Body", "_MainTex")]
        [InlineData("textures/Hair/reflect.png", "Hair", "_ReflectionColorTex")]
        [InlineData("textures/Top1/shadowborder.png", "Top1", "_ShadowBorderMask")]
        // Aliases fold case; a renderer name keeps the author's spelling so the log reads right.
        [InlineData("textures/HairPin_Dot/SMOOTHNESS.png", "HairPin_Dot", "_SmoothnessTex")]
        [InlineData("textures/m_Headphone/emission.png", "m_Headphone", "_EmissionMap")]
        // Windows separators and a leading ./ normalise to the same rule.
        [InlineData(".\\textures\\Paka1\\normal.png", "Paka1", "_BumpMap")]
        public void Reads_the_folder_convention(string path, string renderer, string property)
        {
            var rule = TextureRuleParser.FromPath(PackId, path, out var problem);

            Assert.Null(problem);
            Assert.NotNull(rule);
            Assert.Equal(PackId, rule!.PackId);
            Assert.Equal(renderer, rule.RendererName);
            Assert.Equal(property, rule.PropertyName);
            Assert.Equal(0, rule.MaterialIndex);
            Assert.Equal(TextureRuleSource.Convention, rule.Source);
        }

        [Theory]
        // Not a PNG: the loader decodes nothing else, and packs keep source art here.
        [InlineData("textures/Body/_MainTex.psd")]
        [InlineData("textures/_rules.json")]
        [InlineData("textures/Body/notes.txt")]
        // Nested deeper than <renderer>/<slot>: a subfolder, not a rule.
        [InlineData("textures/src/Body/_MainTex.png")]
        [InlineData("textures/Body/wip/_MainTex.png")]
        // A "_" folder is the author's own: source art at the rule's own depth stays quiet.
        [InlineData("textures/_src/wip.png")]
        [InlineData("textures/_source/Body.png")]
        // Directly under textures/: no renderer named, so nothing to apply it to.
        [InlineData("textures/_MainTex.png")]
        // Outside textures/ entirely.
        [InlineData("audio/voice/A.ogg")]
        [InlineData("manifest.json")]
        public void Ignores_a_path_that_is_not_a_rule(string path)
        {
            var rule = TextureRuleParser.FromPath(PackId, path, out var problem);

            Assert.Null(rule);
            Assert.Null(problem);
        }

        [Fact]
        public void Reports_a_slot_it_does_not_understand()
        {
            var rule = TextureRuleParser.FromPath(PackId, "textures/Body/glow.png", out var problem);

            Assert.Null(rule);
            Assert.NotNull(problem);
            Assert.Equal(PackId, problem!.PackId);
            Assert.Equal("textures/Body/glow.png", problem.Path);
            Assert.Contains("glow", problem.Reason);
            Assert.Contains("_MainTex", problem.Reason);
        }

        [Fact]
        public void Reports_a_property_that_is_not_spelled_like_one()
        {
            var rule = TextureRuleParser.FromPath(PackId, "textures/Body/_Main Tex.png", out var problem);

            Assert.Null(rule);
            Assert.NotNull(problem);
            Assert.Contains("shader property name", problem!.Reason);
        }

        [Fact]
        public void Reads_a_whole_pack_listing_in_path_order()
        {
            var index = PackContentIndex.Build(new[]
            {
                "manifest.json",
                "README.md",
                "textures/_rules.json",
                "textures/Body/_MainTex.png",
                "textures/Face/base.png",
                "textures/Hair/reflect.png",
                "textures/_src/wip.png",
                "audio/voice/A.ogg",
            });

            var set = TextureRuleParser.FromIndex(PackId, index.Files);

            Assert.Equal(3, set.Count);
            Assert.Empty(set.Problems);
            Assert.Equal(new[] { "Body", "Face", "Hair" }, set.RendererNames);
            Assert.Equal("textures/Body/_MainTex.png", set.Rules[0].RelativePath);
            Assert.Equal("_MainTex", set.Rules[1].PropertyName);
            Assert.Equal("_ReflectionColorTex", set.Rules[2].PropertyName);
        }

        [Fact]
        public void Keeps_one_rule_when_two_files_in_a_pack_claim_the_same_slot()
        {
            // "_MainTex.png" and "base.png" resolve to the same property; only one can win.
            var set = TextureRuleParser.FromIndex(PackId, PackContentIndex.Build(new[]
            {
                "textures/Body/_MainTex.png",
                "textures/Body/base.png",
            }).Files);

            Assert.Single(set.Rules);
            Assert.Single(set.Problems);
            Assert.Contains("also sets Body[0]._MainTex", set.Problems[0].Reason);
        }

        [Fact]
        public void An_empty_or_null_listing_yields_nothing()
        {
            Assert.Equal(0, TextureRuleParser.FromIndex(PackId, null).Count);
            Assert.True(TextureRuleParser.FromIndex(PackId, new List<PackFile?>()).IsEmpty);
        }

        [Theory]
        // Verified against the game's lilToon shader and the mt_* materials that use it.
        [InlineData("base", "_MainTex")]
        [InlineData("main", "_MainTex")]
        [InlineData("shadow", "_ShadowColorTex")]
        [InlineData("shadowborder", "_ShadowBorderMask")]
        [InlineData("rim", "_RimColorTex")]
        [InlineData("backlight", "_BacklightColorTex")]
        [InlineData("reflect", "_ReflectionColorTex")]
        [InlineData("reflection", "_ReflectionColorTex")]
        [InlineData("emission", "_EmissionMap")]
        [InlineData("normal", "_BumpMap")]
        [InlineData("matcap2", "_MatCap2ndTex")]
        [InlineData("outline", "_OutlineTex")]
        [InlineData("_AnythingTheShaderHas", "_AnythingTheShaderHas")]
        public void Resolves_a_slot_alias(string slot, string property) =>
            Assert.Equal(property, TextureSlotAliases.Resolve(slot));

        [Theory]
        // A "@" folder names a MATERIAL asset, not a renderer. This is the only rule form that can
        // repaint a costume the game instantiated: its renderers are Top2/Top3/Body1, names a pack
        // cannot predict, but CostumeSkinDB's material names are stable.
        [InlineData("textures/@mt_Tee_A/_MainTex.png", "mt_Tee_A", "_MainTex")]
        [InlineData("textures/@mt_Polo_A/base.png", "mt_Polo_A", "_MainTex")]
        [InlineData("textures/@mt_Body/rim.png", "mt_Body", "_RimColorTex")]
        // Material asset names may contain spaces - "Base_TH Material" is a real one in this game.
        [InlineData("textures/@Base_TH Material/_MainTex.png", "Base_TH Material", "_MainTex")]
        [InlineData("textures/@mt_Hair/reflect.png", "mt_Hair", "_ReflectionColorTex")]
        [InlineData("textures/@mt_Outfit/shadowborder.png", "mt_Outfit", "_ShadowBorderMask")]
        public void Reads_an_at_material_folder(string path, string material, string property)
        {
            var rule = TextureRuleParser.FromPath(PackId, path, out var problem);

            Assert.Null(problem);
            Assert.NotNull(rule);
            Assert.Equal(TextureRuleTarget.Material, rule!.Target);
            Assert.Equal(material, rule.MaterialName);
            Assert.Equal(material, rule.TargetName);
            Assert.Equal(string.Empty, rule.RendererName);
            Assert.Equal(property, rule.PropertyName);
            Assert.Equal(0, rule.MaterialIndex);
            Assert.Equal(
                "@" + material.ToLowerInvariant() + "." + property.ToLowerInvariant(), rule.Key);
        }

        [Fact]
        public void An_at_folder_with_no_material_name_is_reported()
        {
            var rule = TextureRuleParser.FromPath(PackId, "textures/@/_MainTex.png", out var problem);

            Assert.Null(rule);
            Assert.NotNull(problem);
            Assert.Contains("must be followed by a material name", problem!.Reason);
        }

        [Fact]
        public void A_material_rule_and_a_renderer_rule_of_the_same_name_are_different_slots()
        {
            var merged = TextureRuleSet.Merge(
                new TextureRuleSet(new[]
                {
                    new TextureRule("a.pack", "mt_Body", "_MainTex", "textures/mt_Body/_MainTex.png"),
                    TextureRule.ForMaterial("a.pack", "mt_Body", "_MainTex", "textures/@mt_Body/_MainTex.png"),
                }));

            Assert.Equal(2, merged.Count);
            Assert.Empty(merged.Overridden);
            Assert.Equal(new[] { "mt_Body" }, merged.RendererNames);
            Assert.Equal(new[] { "mt_Body" }, merged.MaterialNames);
        }

        [Fact]
        public void Material_rules_are_counted_separately_in_the_summary()
        {
            var set = TextureRuleParser.FromIndex(PackId, PackContentIndex.Build(new[]
            {
                "textures/Body/_MainTex.png",
                "textures/@mt_Tee_A/_MainTex.png",
                "textures/@mt_Polo_A/_MainTex.png",
            }).Files);

            Assert.Equal(3, set.Count);
            Assert.Single(set.RendererNames);
            Assert.Equal(2, set.MaterialNames.Count);
            Assert.Contains("1 renderer(s) and 2 material(s)", set.Summarize());
        }

        [Fact]
        public void Only_MainTex_has_srp_mirrors()
        {
            // The game is URP and every mt_* material binds the same texture to all three, so a
            // write to _MainTex alone can be sampled from a mirror still holding the game's texture.
            Assert.Equal(new[] { "_BaseMap", "_BaseColorMap" }, TextureSlotAliases.MirrorsOf("_MainTex"));

            Assert.Empty(TextureSlotAliases.MirrorsOf("_BaseMap"));
            Assert.Empty(TextureSlotAliases.MirrorsOf("_RimColorTex"));
            Assert.Empty(TextureSlotAliases.MirrorsOf(null));

            // Case-sensitive on purpose: Unity's material property lookup is.
            Assert.Empty(TextureSlotAliases.MirrorsOf("_maintex"));
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("glow")]
        [InlineData("_")]
        [InlineData("_has spaces")]
        [InlineData("_has-a-dash")]
        public void Refuses_a_slot_that_is_neither_alias_nor_property(string slot) =>
            Assert.Null(TextureSlotAliases.Resolve(slot));
    }
}
