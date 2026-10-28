using System;
using System.IO;
using System.Linq;
using CwyLs.ModLoader.Core.Grading;
using CwyLs.ModLoader.Core.Textures;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// D-SCENERY: what <c>grade apply &lt;name&gt;</c> means when the player and several packs ship
    /// presets, and the checked-in demo pack parses cleanly.
    /// </summary>
    public class GradingCatalogTests
    {
        private static GradingCatalog Catalog() =>
            new GradingCatalog(new[]
            {
                new GradingCatalogEntry("pack.a", "warm-film", "/a/warm-film.json", 0),
                new GradingCatalogEntry("pack.b", "warm-film", "/b/warm-film.json", 1),
                new GradingCatalogEntry("pack.a", "rainy", "/a/rainy.json", 0),
                new GradingCatalogEntry(GradingCatalogEntry.UserSource, "mine", "/u/mine.json", 0),
                new GradingCatalogEntry(GradingCatalogEntry.UserSource, "rainy", "/u/rainy.json", 0),
            });

        [Fact]
        public void A_plain_name_takes_the_later_pack()
        {
            var entry = Catalog().Find("warm-film", out var shadowed);

            Assert.Equal("pack.b", entry!.Source);
            Assert.Single(shadowed);
            Assert.Equal("pack.a", shadowed[0].Source);
        }

        [Fact]
        public void The_players_own_preset_wins_a_plain_name()
        {
            var entry = Catalog().Find("RAINY.json", out var shadowed);

            Assert.True(entry!.IsUser);
            Assert.Single(shadowed);
        }

        [Fact]
        public void A_qualified_name_picks_exactly_that_file()
        {
            Assert.Equal("/a/warm-film.json", Catalog().Find("pack.a:warm-film", out _)!.Path);
            Assert.Equal("/a/rainy.json", Catalog().Find("Pack.A:Rainy", out _)!.Path);
            Assert.Null(Catalog().Find("pack.c:warm-film", out _));
        }

        [Fact]
        public void Unknown_names_find_nothing()
        {
            Assert.Null(Catalog().Find("nope", out var shadowed));
            Assert.Empty(shadowed);
            Assert.Null(Catalog().Find(" ", out _));
        }

        [Fact]
        public void The_list_shows_user_presets_first_then_packs_in_load_order()
        {
            var names = Catalog().Entries.Select(e => e.QualifiedName).ToArray();

            Assert.Equal(new[] { "user:mine", "user:rainy", "pack.a:rainy", "pack.a:warm-film", "pack.b:warm-film" }, names);
        }

        [Fact]
        public void Entries_need_all_their_parts()
        {
            Assert.Throws<ArgumentException>(() => new GradingCatalogEntry("", "x", "/x", 0));
        }

        [Fact]
        public void The_demo_pack_presets_parse_without_errors()
        {
            var root = TestRepo.Root;
            Assert.NotNull(root);
            var folder = Path.Combine(root!, "packs", "cwy.demo.scenery", "grading");
            var files = Directory.GetFiles(folder, "*.json");

            Assert.True(files.Length >= 2, "the demo pack ships two presets");
            foreach (var file in files)
            {
                var result = GradingPresetParser.Parse(File.ReadAllText(file));
                Assert.True(result.Ok, file);
                Assert.True(result.Problems.Count == 0, file + ": " + string.Join("; ", result.Problems));
                Assert.False(GradingMapper.Map(result.Preset!.Resolve(GradingTimeOfDay.Day).ApplyBlend()).IsEmpty, file);
            }
        }

        [Fact]
        public void The_demo_pack_rules_parse_without_problems()
        {
            var root = TestRepo.Root;
            Assert.NotNull(root);
            var json = File.ReadAllText(Path.Combine(root!, "packs", "cwy.demo.scenery", "textures", "_rules.json"));

            var properties = MaterialPropertyRuleParser.FromRulesJson("cwy.demo.scenery", json);
            var textures = TextureRuleParser.FromRulesJson("cwy.demo.scenery", json);

            Assert.Empty(properties.Problems);
            Assert.Empty(textures.Problems);
            Assert.True(properties.Count >= 1);
        }
    }
}
