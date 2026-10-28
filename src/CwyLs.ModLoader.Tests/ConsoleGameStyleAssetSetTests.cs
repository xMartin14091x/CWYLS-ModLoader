using System;
using System.Collections.Generic;
using CwyLs.ModLoader.Core.Console;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// B-UI: the fallback decision's input - whether the game's UI assets found at runtime are
    /// enough for a game-style window, and what to say when they are not.
    /// </summary>
    public class ConsoleGameStyleAssetSetTests
    {
        private static List<string> Everything()
        {
            var all = new List<string>(GameStyleAssetSet.Required);
            all.AddRange(GameStyleAssetSet.Optional);
            return all;
        }

        [Fact]
        public void Everything_found_builds_with_nothing_to_report()
        {
            var report = GameStyleAssetSet.Evaluate(Everything());

            Assert.True(report.CanBuild);
            Assert.Empty(report.MissingRequired);
            Assert.Empty(report.MissingOptional);
            Assert.Equal("all game UI assets found", report.Describe());
        }

        [Fact]
        public void A_missing_optional_asset_still_builds_and_names_the_part()
        {
            var found = Everything();
            found.Remove(GameStyleAssetSet.ScrollHandle);

            var report = GameStyleAssetSet.Evaluate(found);

            Assert.True(report.CanBuild);
            Assert.Equal(new[] { GameStyleAssetSet.ScrollHandle }, report.MissingOptional);
            Assert.Contains(GameStyleAssetSet.ScrollHandle, report.Describe());
        }

        [Theory]
        [InlineData(GameStyleAssetSet.Font)]
        [InlineData(GameStyleAssetSet.WindowBase)]
        [InlineData(GameStyleAssetSet.Separator)]
        [InlineData(GameStyleAssetSet.Close)]
        public void A_missing_required_asset_means_fall_back_and_says_which(string missing)
        {
            var found = Everything();
            found.Remove(missing);

            var report = GameStyleAssetSet.Evaluate(found);

            Assert.False(report.CanBuild);
            Assert.Equal(new[] { missing }, report.MissingRequired);
            Assert.StartsWith("missing " + missing, report.Describe());
        }

        [Fact]
        public void Nothing_found_before_the_room_loads_means_fall_back()
        {
            var report = GameStyleAssetSet.Evaluate(null);

            Assert.False(report.CanBuild);
            Assert.Equal(GameStyleAssetSet.Required.Count, report.MissingRequired.Count);
            Assert.Equal(GameStyleAssetSet.Optional.Count, report.MissingOptional.Count);
        }

        [Fact]
        public void Names_are_matched_exactly()
        {
            var found = new List<string> { "base_jp", "T_UI_CLOSE", GameStyleAssetSet.WindowBase, GameStyleAssetSet.Separator };

            var report = GameStyleAssetSet.Evaluate(found);

            Assert.Contains(GameStyleAssetSet.Font, report.MissingRequired);
            Assert.Contains(GameStyleAssetSet.Close, report.MissingRequired);
        }

        [Fact]
        public void The_sprite_list_is_every_asset_but_the_font_once()
        {
            var sprites = GameStyleAssetSet.SpriteNames();

            Assert.DoesNotContain(GameStyleAssetSet.Font, sprites);
            Assert.Equal(GameStyleAssetSet.Required.Count - 1 + GameStyleAssetSet.Optional.Count, sprites.Count);
            Assert.Equal(sprites.Count, new HashSet<string>(sprites, StringComparer.Ordinal).Count);
        }
    }
}
