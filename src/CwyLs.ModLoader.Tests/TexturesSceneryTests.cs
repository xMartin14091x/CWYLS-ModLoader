using System;
using CwyLs.ModLoader.Core.Packs;
using CwyLs.ModLoader.Core.Textures;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// D-SCENERY: material name patterns, the scenery/desk families, the paid-content refusal, and
    /// the pure half of <c>tex scan</c> / <c>materials.json</c>.
    /// </summary>
    public class TexturesSceneryTests
    {
        [Theory]
        [InlineData("mt_env_*", "mt_env_sakura", true)]
        [InlineData("mt_env_*", "MT_ENV_SEA", true)]
        [InlineData("mt_env_*", "mt_envy", false)]
        [InlineData("mt_desk*", "mt_desk", true)]
        [InlineData("mt_desk*", "mt_desktop", true)]
        [InlineData("mt_desk_?", "mt_desk_1", true)]
        [InlineData("mt_desk_?", "mt_desk_10", false)]
        [InlineData("*_dark", "mt_desk_lamp_dark", true)]
        [InlineData("mt_*_lamp*", "mt_desk_lamp_dark", true)]
        [InlineData("Skybox", "skybox", true)]
        [InlineData("Skybox", "Skybox2", false)]
        [InlineData("", "mt_desk", false)]
        [InlineData("*", "", true)]
        public void Patterns_match_case_insensitively(string pattern, string name, bool expected)
        {
            Assert.Equal(expected, MaterialNamePattern.Matches(pattern, name));
        }

        [Fact]
        public void Pattern_detection()
        {
            Assert.True(MaterialNamePattern.IsPattern("mt_env_*"));
            Assert.True(MaterialNamePattern.IsPattern("mt_desk_?"));
            Assert.False(MaterialNamePattern.IsPattern("mt_desk"));
            Assert.False(MaterialNamePattern.IsPattern(null));
        }

        [Theory]
        [InlineData("mt_env_sakura", SceneryFamily.Scenery)]
        [InlineData("mt_combined_room_far", SceneryFamily.Scenery)]
        [InlineData("Skybox", SceneryFamily.Scenery)]
        [InlineData("mt_desk_1", SceneryFamily.Desk)]
        [InlineData("mt_desk_lamp_dark", SceneryFamily.Desk)]
        [InlineData("mt_window", SceneryFamily.Window)]
        [InlineData("mt_Body", SceneryFamily.Other)]
        [InlineData(null, SceneryFamily.Other)]
        public void Families_follow_the_plan(string? name, SceneryFamily family)
        {
            Assert.Equal(family, SceneryMaterials.Classify(name));
        }

        [Theory]
        [InlineData("mt_dlc_desk")]
        [InlineData("DLC_Room")]
        [InlineData("mt_shop_sign")]
        [InlineData("mt_purchase_icon")]
        [InlineData("mt_env_premium_sky")]
        public void Paid_content_names_are_refused_with_a_reason(string name)
        {
            Assert.True(PaidContentGuard.IsRefused(name, out var reason));
            Assert.Contains(name, reason!);
            Assert.Contains("paid content", reason!);
        }

        [Theory]
        [InlineData("mt_desk_1")]
        [InlineData("mt_env_sakura")]
        [InlineData("mt_combined_room_far")]
        [InlineData("Skybox")]
        [InlineData("mt_Tee_A")]
        [InlineData(null)]
        public void Free_content_is_not_refused(string? name)
        {
            Assert.False(PaidContentGuard.IsRefused(name, out var reason));
            Assert.Null(reason);
        }

        [Fact]
        public void A_property_rule_on_paid_content_is_refused_with_one_problem_naming_it()
        {
            var set = MaterialPropertyRuleParser.FromRulesJson(
                "p", "[{\"material\":\"mt_dlc_desk\",\"property\":\"_Color\",\"tint\":\"#fff\"}]");

            Assert.Empty(set.Rules);
            Assert.Single(set.Problems);
            Assert.Contains("mt_dlc_desk", set.Problems[0].Reason);
            Assert.Contains("paid content", set.Problems[0].Reason);
        }

        [Fact]
        public void A_texture_rule_on_paid_content_is_refused_from_a_folder_and_from_the_rules_file()
        {
            var fromFolder = TextureRuleParser.FromPath("p", "textures/@mt_dlc_outfit/_MainTex.png", out var problem);
            Assert.Null(fromFolder);
            Assert.Contains("paid content", problem!.Reason);

            var renderer = TextureRuleParser.FromPath("p", "textures/DLC_Body/_MainTex.png", out var rendererProblem);
            Assert.Null(renderer);
            Assert.Contains("paid content", rendererProblem!.Reason);

            var fromRules = TextureRuleParser.FromRulesJson(
                "p", "[{\"material\":\"mt_shop_poster\",\"property\":\"_MainTex\",\"file\":\"a.png\"}]");
            Assert.Empty(fromRules.Rules);
            Assert.Contains("paid content", fromRules.Problems[0].Reason);
        }

        [Fact]
        public void A_scan_summary_line_shows_family_shader_users_and_key_properties()
        {
            var entry = new MaterialScanEntry(
                "mt_desk_1",
                false,
                123,
                "lilToon",
                2000,
                new[] { new MaterialScanUser("map_model/model/Platform_PC/m_desk_mobile/m_desk", true), new MaterialScanUser("x/m_desk", false) },
                new[]
                {
                    new MaterialScanProperty("_Color", MaterialValueType.Color, 1, 0.9, 0.8, 1),
                    new MaterialScanProperty("_MainTex", "t_desk_base_0", 2048, 2048),
                    new MaterialScanProperty("_Cull", MaterialValueType.Float, 2),
                });

            var line = MaterialScan.SummaryLine(entry);

            Assert.StartsWith("mt_desk_1  [desk] lilToon  users 2 (1 active)", line);
            Assert.Contains("_Color=(1,0.9,0.8,1)", line);
            Assert.Contains("_MainTex=t_desk_base_0 2048x2048", line);
            Assert.DoesNotContain("_Cull", line);
        }

        [Fact]
        public void The_export_is_valid_json_with_every_property()
        {
            var entries = new[]
            {
                new MaterialScanEntry(
                    "mt_env_sea", false, 7, "lilToon", 2000,
                    new[] { new MaterialScanUser("Paremt/EnviromentObject/WindowView/Env_Horizon/Env_Sea/m_env_sea", true) },
                    new[]
                    {
                        new MaterialScanProperty("_Color", MaterialValueType.Color, 1, 1, 1, 1),
                        new MaterialScanProperty("_EmissionMap", null, 0, 0),
                        new MaterialScanProperty("_Cutoff", MaterialValueType.Range, 0.5),
                    }),
                new MaterialScanEntry("Skybox", true, -9, "Distant Lands/Cozy/URP/Stylized Sky Desktop", 1000, null, null),
            };

            var json = MaterialScan.ToJson(entries, new DateTimeOffset(2026, 10, 1, 23, 0, 0, TimeSpan.Zero), new[] { "mt_env_*", "Skybox" });

            Assert.True(Core.Json.JsonReader.TryParse(json, out var root, out var error), error);
            Assert.Equal(2L, Core.Json.JsonPath.Get(root, "count"));
            Assert.Equal("scenery", Core.Json.JsonPath.GetString(root, "materials[0].family"));
            Assert.Equal("range", Core.Json.JsonPath.GetString(root, "materials[0].properties._Cutoff.type"));
            Assert.Null(Core.Json.JsonPath.Get(root, "materials[0].properties._EmissionMap.value"));
            Assert.Equal(true, Core.Json.JsonPath.Get(root, "materials[1].instance"));
        }

        [Fact]
        public void An_empty_export_is_still_valid_json()
        {
            var json = MaterialScan.ToJson(new MaterialScanEntry[0], DateTimeOffset.UnixEpoch, new string[0]);
            Assert.True(Core.Json.JsonReader.TryParse(json, out _, out var error), error);
        }

        [Fact]
        public void The_scenery_reference_names_resolve_to_files_in_the_pack_index_convention()
        {
            // The rules file of a pack is still found where the index expects it.
            Assert.Equal("textures/_rules.json", TextureRuleParser.RulesPath);
            Assert.Equal(PackContentIndex.TexturesFolder, TextureRuleParser.TexturesFolder);
        }
    }
}
