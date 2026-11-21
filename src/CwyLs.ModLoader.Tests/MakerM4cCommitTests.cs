using System;
using System.IO;
using System.Text;
using CwyLs.ModLoader.Core.Api.Maker.Studio;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    public sealed class MakerM4cCommitTests
    {
        [Fact]
        public void Later_target_conflict_cannot_modify_an_earlier_target()
        {
            using var fx = new MakerProjectFixture();
            fx.Write(".maker/lines/th/a.json", "old-a"); fx.Write(".maker/lines/th/b.json", "old-b");
            var targets = Targets(fx);
            fx.Write(".maker/lines/th/b.json", "external-b");
            var result = fx.Store.Commit(fx.Operation(1), targets, "subtitle");
            Assert.Equal("recovery_required", result.Error);
            Assert.Equal("old-a", fx.Read(".maker/lines/th/a.json"));
            Assert.Equal("external-b", fx.Read(".maker/lines/th/b.json"));
        }

        [Fact]
        public void Edit_in_the_replace_window_is_restored_from_the_displaced_destination()
        {
            using var fx = new MakerProjectFixture();
            fx.Write(".maker/lines/th/a.json", "old-a"); fx.Write(".maker/lines/th/b.json", "old-b");
            var targets = Targets(fx);
            var commit = new MakerCommit(fx.Store.Files, "subtitle", boundary: point =>
            {
                if (point == "before-write:0") fx.Write(".maker/lines/th/a.json", "external-in-window");
            });
            var result = commit.Execute(fx.Operation(1), targets);
            Assert.Equal("recovery_required", result.Error);
            Assert.Equal("external-in-window", fx.Read(".maker/lines/th/a.json"));
            Assert.Equal("old-b", fx.Read(".maker/lines/th/b.json"));
        }

        [Theory]
        [InlineData("before", 0)]
        [InlineData("after", 0)]
        [InlineData("before", 1)]
        [InlineData("after", 1)]
        public void Target_write_boundary_crashes_recover(string when, int index)
        {
            using var fx = new MakerProjectFixture();
            fx.Write(".maker/lines/th/a.json", "old-a"); fx.Write(".maker/lines/th/b.json", "old-b");
            var commit = new MakerCommit(fx.Store.Files, "subtitle", boundary: p =>
            {
                if (p == when + "-write:" + index) throw new InvalidOperationException("crash");
            });
            Assert.Throws<InvalidOperationException>(() => commit.Execute(fx.Operation(1), Targets(fx)));
            Assert.True(new MakerCommit(fx.Store.Files).Recover(fx.Operation(1)).Ok);
            Assert.Equal("new-a", fx.Read(".maker/lines/th/a.json"));
            Assert.Equal("new-b", fx.Read(".maker/lines/th/b.json"));
        }

        [Theory]
        [InlineData(false, 0)]
        [InlineData(true, 0)]
        [InlineData(false, 1)]
        [InlineData(true, 1)]
        [InlineData(false, 2)]
        [InlineData(true, 2)]
        [InlineData(false, 3)]
        [InlineData(true, 3)]
        [InlineData(false, 4)]
        [InlineData(true, 4)]
        public void Every_cleanup_boundary_recovers_without_requiring_deleted_stages(bool after, int index)
        {
            using var fx = new MakerProjectFixture();
            fx.Write(".maker/lines/th/a.json", "old-a"); fx.Write(".maker/lines/th/b.json", "old-b");
            var deletion = -1;
            var commit = new MakerCommit(fx.Store.Files, "subtitle", boundary: p =>
            {
                if (p.StartsWith(after ? "after-delete:" : "before-delete:", StringComparison.Ordinal)
                    && ++deletion == index) throw new InvalidOperationException("crash");
            });
            Assert.Throws<InvalidOperationException>(() => commit.Execute(fx.Operation(1), Targets(fx)));
            Assert.True(new MakerCommit(fx.Store.Files).Recover(fx.Operation(1)).Ok);
            Assert.Equal("new-a", fx.Read(".maker/lines/th/a.json"));
            Assert.Equal("new-b", fx.Read(".maker/lines/th/b.json"));
            Assert.Empty(fx.Store.Files.ListFiles(MakerStudioPaths.Transactions));
        }

        [Fact]
        public void Failed_stage_deletion_retires_marker_and_does_not_block_a_later_edit()
        {
            using var fx = new MakerProjectFixture();
            var op = fx.Operation(1);
            var stuck = MakerStudioPaths.Stage(op, 0);
            var commit = new MakerCommit(fx.Store.Files, "subtitle", boundary: p =>
            {
                if (p == "before-delete:" + stuck) throw new IOException("locked file");
            });
            Assert.True(commit.Execute(op, Targets(fx)).Ok);
            Assert.True(fx.Store.Files.Exists(stuck));
            Assert.False(fx.Store.Files.Exists(MakerStudioPaths.Stage(op, 1)));
            Assert.False(fx.Store.Files.Exists(MakerStudioPaths.Pending(op)));
            fx.Write(".maker/lines/th/a.json", "later edit");
            Assert.True(new MakerCommit(fx.Store.Files).Recover(op).Ok);
            Assert.Empty(fx.Store.Files.ListFiles(MakerStudioPaths.Transactions));
            Assert.Equal("later edit", fx.Read(".maker/lines/th/a.json"));
        }

        [Fact]
        public void Orphan_stages_are_scavenged_on_read_recovery()
        {
            using var fx = new MakerProjectFixture();
            fx.Store.Files.WriteAtomic(MakerStudioPaths.Stage(fx.Operation(1), 0), new byte[1000]);
            fx.Store.Files.WriteAtomic(MakerStudioPaths.Transactions + "/" + fx.Operation(1) + ".0.swap", new byte[1000]);
            Assert.True(new MakerGlossaryStore(fx.Store).Read("th").Ok);
            Assert.Empty(fx.Store.Files.ListFiles(MakerStudioPaths.Transactions));
        }

        [Fact]
        public void Quota_reserves_positive_target_growth_while_stages_exist()
        {
            using var fx = new MakerProjectFixture(quota: 4000);
            var result = fx.Store.Commit(fx.Operation(1), new[] { fx.Store.Target(".maker/lines/th/large.json", new byte[2048]) }, "subtitle");
            Assert.Equal("storage_limit", result.Error);
            Assert.False(fx.Store.Files.Exists(".maker/lines/th/large.json"));
            Assert.Empty(fx.Store.Files.ListFiles(MakerStudioPaths.Transactions));
        }

        private static MakerCommitFile[] Targets(MakerProjectFixture fx) => new[]
        {
            fx.Store.Target(".maker/lines/th/a.json", Encoding.UTF8.GetBytes("new-a")),
            fx.Store.Target(".maker/lines/th/b.json", Encoding.UTF8.GetBytes("new-b")),
        };
    }
}
