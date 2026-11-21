using System;
using System.Text;
using CwyLs.ModLoader.Core.Api.Maker.Studio;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    public sealed class MakerCommitTests
    {
        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        public void Crash_after_any_target_boundary_is_completed_by_recovery(int stopAfter)
        {
            using var fx = new MakerProjectFixture();
            var op = fx.Operation(1);
            var targets = new[]
            {
                fx.Store.Target(".maker/lines/th/a.json", Encoding.UTF8.GetBytes("A")),
                fx.Store.Target(".maker/lines/th/b.json", Encoding.UTF8.GetBytes("B")),
                fx.Store.Target(".maker/lines/th/c.json", Encoding.UTF8.GetBytes("C")),
            };
            Assert.Throws<InvalidOperationException>(() => fx.Store.Commit(op, targets, "subtitle", i =>
            {
                if (i == stopAfter) throw new InvalidOperationException("simulated crash");
            }));

            var recovered = new MakerCommit(fx.Store.Files).Recover(op);

            Assert.True(recovered.Ok, recovered.Message);
            Assert.Equal("A", fx.Read(".maker/lines/th/a.json"));
            Assert.Equal("B", fx.Read(".maker/lines/th/b.json"));
            Assert.Equal("C", fx.Read(".maker/lines/th/c.json"));
            Assert.False(fx.Store.Files.Exists(MakerStudioPaths.Pending(op)));
        }

        [Fact]
        public void External_edit_during_pending_commit_requires_recovery_attention()
        {
            using var fx = new MakerProjectFixture();
            fx.Write(".maker/lines/th/a.json", "old-a");
            fx.Write(".maker/lines/th/b.json", "old-b");
            var op = fx.Operation(1);
            var targets = new[]
            {
                fx.Store.Target(".maker/lines/th/a.json", Encoding.UTF8.GetBytes("new-a")),
                fx.Store.Target(".maker/lines/th/b.json", Encoding.UTF8.GetBytes("new-b")),
            };
            Assert.Throws<InvalidOperationException>(() => fx.Store.Commit(op, targets, "subtitle", i =>
            {
                if (i == 0) throw new InvalidOperationException("simulated crash");
            }));
            fx.Write(".maker/lines/th/b.json", "external");

            var recovered = new MakerCommit(fx.Store.Files).Recover(op);

            Assert.False(recovered.Ok);
            Assert.Equal("recovery_required", recovered.Error);
            Assert.Contains("pack=sample.pack", recovered.Message);
            Assert.Contains("resource=.maker/lines/th/b.json", recovered.Message);
            Assert.Contains("operationId=" + op, recovered.Message);
            Assert.Equal("external", fx.Read(".maker/lines/th/b.json"));
        }

        [Fact]
        public void Quota_is_reserved_before_any_stage_or_target_write()
        {
            using var fx = new MakerProjectFixture(quota: 32);
            var result = fx.Store.Commit(fx.Operation(1), new[]
            {
                fx.Store.Target(".maker/lines/th/a.json", new byte[64]),
            }, "subtitle");

            Assert.False(result.Ok);
            Assert.Equal("storage_limit", result.Error);
            Assert.False(fx.Store.Files.Exists(".maker/lines/th/a.json"));
            Assert.False(fx.Store.Files.Exists(MakerStudioPaths.Pending(fx.Operation(1))));
        }

        [Fact]
        public void Operation_lookup_distinguishes_unknown_pending_and_committed()
        {
            using var fx = new MakerProjectFixture();
            var ledger = new MakerOperationLedger(fx.Store);
            Assert.Equal("not_found", ledger.Read(fx.Operation(9)).Error);

            var op = fx.Operation(1);
            var target = fx.Store.Target(".maker/lines/th/a.json", Encoding.UTF8.GetBytes("A"));
            Assert.Throws<InvalidOperationException>(() => fx.Store.Commit(op, new[] { target }, "subtitle", _ => throw new InvalidOperationException()));
            Assert.Equal("not_found", ledger.Read(op).Error); // Recovered, but this synthetic commit has no receipt.
            Assert.Equal("A", fx.Read(".maker/lines/th/a.json"));
            Assert.False(fx.Store.Files.Exists(MakerStudioPaths.Pending(op)));
        }
    }
}
