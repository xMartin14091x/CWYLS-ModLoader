using System;
using System.Linq;
using CwyLs.ModLoader.Core.Scenario;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>Save backups made before scene play previews an unseen game story.</summary>
    public class ScenePreviewBackupsTests
    {
        [Fact]
        public void The_folder_name_carries_the_time_and_a_safe_group_id() =>
            Assert.Equal("save-scene-20261003-143512-main_05_x", ScenePreviewBackups.FolderName(new DateTime(2026, 10, 3, 14, 35, 12), "main_05/x"));

        [Fact]
        public void Only_the_newest_ten_are_kept_and_other_folders_are_never_touched()
        {
            var names = Enumerable.Range(1, 13).Select(i => "save-scene-202610" + i.ToString("00") + "-120000-main_01")
                .Concat(new[] { "save-20261003-1222", "something-else" }).ToList();
            var prune = ScenePreviewBackups.ToPrune(names);
            Assert.Equal(new[] { "save-scene-20261001-120000-main_01", "save-scene-20261002-120000-main_01", "save-scene-20261003-120000-main_01" }, prune);
        }

        [Fact]
        public void Ten_or_fewer_prune_nothing() =>
            Assert.Empty(ScenePreviewBackups.ToPrune(Enumerable.Range(1, 10).Select(i => "save-scene-" + i)));
    }
}
