using System;
using System.Collections.Generic;
using CwyLs.ModLoader.Core.MasterData;
using CwyLs.ModLoader.Core.PackLines;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>TD-33 / T-083: <c>p2.masterdata.save_filter</c> reports the master-data save filter's state (it was log-only).</summary>
    public class PackSaveFilterSmokeTests
    {
        private const string PlainPack = "mymod_smalltalk_scene_1";
        private const string GameOwn = "smalltalk_03";
        private const string PackLine = "cwy_th_small_001";

        private static readonly MasterDataBaseline Game =
            MasterDataBaseline.Build(new[] { "x" }, new[] { "x" }, new[] { GameOwn, "main_01" });

        private static PackSaveHoldBack HoldBack(bool installed = true)
        {
            var added = new PackAddedGroups();
            added.Record(PackLineRows.Set(new[] { PackLineRows.Group(PlainPack, 23, 9001) }).ScenarioGroups, Game);
            var holdBack = new PackSaveHoldBack(added);
            if (installed) holdBack.MarkInstalled();
            else holdBack.MarkMissing("PATCH TARGET MISSING: Bulbul.SaveDataManager.SaveScenarioProgressData()");
            return holdBack;
        }

        [Fact]
        public void A_missing_save_hook_fails_and_names_the_problem()
        {
            var result = PackSaveFilterSmoke.Evaluate(true, string.Empty, HoldBack(installed: false), new List<string> { GameOwn });

            Assert.Equal(PackSaveFilterSmoke.Id, result.Id);
            Assert.False(result.Ok);
            Assert.False(result.Informational);
            Assert.Contains("PATCH TARGET MISSING", result.Detail);
        }

        [Fact]
        public void Master_data_off_is_skipped_and_an_unloaded_save_is_not_yet_observed()
        {
            var off = PackSaveFilterSmoke.Evaluate(false, "MasterData.Enabled=false", HoldBack(), null);
            var unloaded = PackSaveFilterSmoke.Evaluate(true, string.Empty, HoldBack(), null);

            Assert.True(off.Ok);
            Assert.Contains("MasterData.Enabled=false", off.Detail);
            Assert.False(unloaded.Ok);
            Assert.True(unloaded.Informational);
        }

        [Fact]
        public void An_installed_filter_passes_with_its_evidence_and_leaves_the_played_list_alone()
        {
            var holdBack = HoldBack();
            Assert.True(holdBack.JoinPackLines(new HashSet<string>(StringComparer.Ordinal) { PackLine }, null));
            var played = new List<string> { GameOwn, PlainPack, PackLine };

            var result = PackSaveFilterSmoke.Evaluate(true, string.Empty, holdBack, played);

            Assert.True(result.Ok, result.Detail);
            Assert.Contains("installed", result.Detail);
            Assert.Contains("1 pack-added group(s)", result.Detail);
            Assert.Contains("pack lines joined", result.Detail);
            Assert.Contains("a save would hold back 2 of 3 played id(s)", result.Detail);
            Assert.Equal(new[] { GameOwn, PlainPack, PackLine }, played);
        }

        [Fact]
        public void A_pack_added_group_the_rule_would_let_into_the_save_fails()
        {
            // A rule that would let a recorded pack-added group into the file is the leak this check exists to catch.
            var played = new List<string> { GameOwn, PlainPack };
            var leaky = PackSaveFilterSmoke.Evaluate(true, string.Empty, HoldBack(), played, rule: _ => false);

            Assert.False(leaky.Ok);
            Assert.False(leaky.Informational);
            Assert.Contains("1 pack-added group(s) in memory", leaky.Detail);
        }
    }
}
