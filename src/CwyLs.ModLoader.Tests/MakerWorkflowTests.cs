using CwyLs.ModLoader.Core.Api.Maker;
using CwyLs.ModLoader.Core.Api.Maker.Studio;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    public sealed class MakerWorkflowTests
    {
        [Fact]
        public void Workflow_survives_restart_and_is_isolated_by_pack_language_and_line()
        {
            using var a = new MakerProjectFixture();
            using var b = new MakerProjectFixture();
            var initial = a.Store.ReadLine("th", "one").Value!;
            var saved = new MakerWorkflowStore(a.Store).Update(Request(a, 1, "th", "one", initial.Revision, "draft", true));

            var reopened = new MakerProjectStore(a.Root, a.Clock).ReadLine("th", "one").Value!;

            Assert.True(saved.Ok);
            Assert.Equal("draft", reopened.Workflow.Status);
            Assert.True(reopened.Workflow.Starred);
            Assert.Equal("todo", a.Store.ReadLine("en", "one").Value!.Workflow.Status);
            Assert.Equal("todo", a.Store.ReadLine("th", "two").Value!.Workflow.Status);
            Assert.Equal("todo", b.Store.ReadLine("th", "one").Value!.Workflow.Status);
        }

        [Fact]
        public void No_op_keeps_revision_and_same_operation_replays()
        {
            using var fx = new MakerProjectFixture();
            var initial = fx.Store.ReadLine("th", "one").Value!;
            var request = Request(fx, 1, "th", "one", initial.Revision, "todo", false);
            var store = new MakerWorkflowStore(fx.Store);

            var first = store.Update(request);
            var replay = store.Update(request);

            Assert.Equal(initial.Revision, first.Value!.Revision);
            Assert.Equal(initial.Revision, replay.Value!.Revision);
            Assert.Equal("committed", new MakerOperationLedger(fx.Store).Read(request.OperationId).Value!.State);
        }

        [Fact]
        public void Stale_tabs_and_reused_operation_ids_conflict()
        {
            using var fx = new MakerProjectFixture();
            var initial = fx.Store.ReadLine("th", "one").Value!;
            var store = new MakerWorkflowStore(fx.Store);
            var first = store.Update(Request(fx, 1, "th", "one", initial.Revision, "draft", null));

            var stale = store.Update(Request(fx, 2, "th", "one", initial.Revision, "done", null));
            var reused = store.Update(Request(fx, 1, "th", "one", first.Value!.Revision, "done", null));

            Assert.Equal("conflict", stale.Error);
            Assert.Equal("conflict", reused.Error);
        }

        [Fact]
        public void Broken_line_metadata_is_never_replaced_with_defaults()
        {
            using var fx = new MakerProjectFixture();
            fx.Write(".maker/lines/th/one.json", "{broken");
            var result = new MakerWorkflowStore(fx.Store).Update(Request(fx, 1, "th", "one", "anything", "done", null));

            Assert.Equal("metadata_unavailable", result.Error);
            Assert.Equal("{broken", fx.Read(".maker/lines/th/one.json"));
        }

        private static MakerStudioWorkflowRequest Request(
            MakerProjectFixture fx, int op, string lang, string id, string revision, string? status, bool? starred) =>
            new MakerStudioWorkflowRequest
            {
                PackId = "sample.pack", Lang = lang, Id = id, ExpectedRevision = revision,
                OperationId = fx.Operation(op), Status = status, Starred = starred,
            };
    }
}
