using System.Linq;
using CwyLs.ModLoader.Core.Api.Maker;
using CwyLs.ModLoader.Core.Api.Maker.Studio;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    public sealed class MakerHistoryTests
    {
        [Fact]
        public void Unicode_newlines_and_null_deletions_survive_history_and_revert()
        {
            using var fx = new MakerProjectFixture();
            fx.Write("data/subtitles/th.json", "{\"one\":\"เดิม\\n🙂\"}");
            var store = new MakerHistoryStore(fx.Store);
            var initial = fx.Store.ReadLine("th", "one").Value!;
            var save = store.Save(Request(fx, 1, initial.Revision, "ใหม่\n🙂"));
            var afterSave = store.Read("th", "one", 0, 20).Value!;
            var remove = store.Save(Request(fx, 2, save.Value!.Saved.Revision, null));
            var afterRemove = store.Read("th", "one", 0, 20).Value!;
            var baseline = afterRemove.Items.First(i => i.Source == "baseline");
            var revert = store.Revert(new MakerStudioRevertRequest
            {
                PackId = "sample.pack", Lang = "th", Id = "one", RevisionId = baseline.RevisionId,
                ExpectedRevision = remove.Value!.Saved.Revision, OperationId = fx.Operation(3),
            });

            Assert.Equal("เดิม\n🙂", afterSave.Items[0].Text);
            Assert.Equal("ใหม่\n🙂", afterSave.Items[1].Text);
            Assert.False(save.Value.Saved.Live);
            Assert.Null(afterRemove.Items.Last().Text);
            Assert.True(revert.Ok);
            Assert.Equal("เดิม\n🙂", fx.Store.CurrentSubtitle("th", "one"));
            Assert.Equal("revert", store.Read("th", "one", 0, 20).Value!.Items.Last().Source);
        }

        [Fact]
        public void History_keeps_only_twenty_revisions()
        {
            using var fx = new MakerProjectFixture();
            var store = new MakerHistoryStore(fx.Store);
            var revision = fx.Store.ReadLine("th", "one").Value!.Revision;
            for (var i = 1; i <= 22; i++)
            {
                var result = store.Save(Request(fx, i, revision, "ข้อความ " + i));
                Assert.True(result.Ok, result.Message);
                revision = result.Value!.Saved.Revision;
            }

            var history = store.Read("th", "one", 0, 20).Value!;
            Assert.Equal(20, history.Total);
            Assert.Equal("ข้อความ 3", history.Items[0].Text);
            Assert.Equal("ข้อความ 22", history.Items[^1].Text);
        }

        [Fact]
        public void Identical_save_is_a_no_op_and_same_operation_replays()
        {
            using var fx = new MakerProjectFixture();
            fx.Write("data/subtitles/th.json", "{\"one\":\"เดิม\"}");
            var store = new MakerHistoryStore(fx.Store);
            var initial = fx.Store.ReadLine("th", "one").Value!;
            var request = Request(fx, 1, initial.Revision, "เดิม");

            var first = store.Save(request);
            var replay = store.Save(request);

            Assert.False(first.Value!.Saved.Saved);
            Assert.Equal(initial.Revision, first.Value.Saved.Revision);
            Assert.True(replay.Value!.Replayed);
            Assert.Empty(store.Read("th", "one", 0, 20).Value!.Items);
        }

        [Fact]
        public void Manual_external_edit_conflicts_and_preserves_both_files()
        {
            using var fx = new MakerProjectFixture();
            var store = new MakerHistoryStore(fx.Store);
            var initial = fx.Store.ReadLine("th", "one").Value!;
            var first = store.Save(Request(fx, 1, initial.Revision, "studio"));
            fx.Write("data/subtitles/th.json", "{\"one\":\"manual\"}");

            var conflict = store.Save(Request(fx, 2, first.Value!.Saved.Revision, "next"));

            Assert.Equal("conflict", conflict.Error);
            Assert.Equal("manual", fx.Store.CurrentSubtitle("th", "one"));
            Assert.True(store.Read("th", "one", 0, 20).Value!.HistoryGap);
        }

        [Fact]
        public void First_transactional_edit_keeps_the_creator_table_backup()
        {
            using var fx = new MakerProjectFixture();
            const string Original = "{\n  \"one\": \"mine\"\n}\n";
            fx.Write("data/subtitles/th.json", Original);
            var initial = fx.Store.ReadLine("th", "one").Value!;

            var result = new MakerHistoryStore(fx.Store).Save(Request(fx, 1, initial.Revision, "edited"));

            Assert.True(result.Ok);
            Assert.Equal(Original, fx.Read("_maker_backup/data/subtitles/th.json"));
        }

        [Fact]
        public void V1_capture_is_best_effort_and_marks_a_preexisting_gap()
        {
            using var fx = new MakerProjectFixture();
            var store = new MakerHistoryStore(fx.Store);
            var initial = fx.Store.ReadLine("th", "one").Value!;
            var first = store.Save(Request(fx, 1, initial.Revision, "studio"));
            fx.Write("data/subtitles/th.json", "{\"one\":\"v1\"}");

            store.CaptureV1("th", "one", "manual-between", "v1");

            var history = store.Read("th", "one", 0, 20).Value!;
            Assert.True(history.HistoryGap);
            Assert.Equal("v1", history.Items.Last().Text);
            Assert.Equal("v1", history.Items.Last().Source);
            Assert.NotEqual(initial.Revision, first.Value!.Saved.Revision);
        }

        [Fact]
        public void First_v1_capture_uses_its_known_previous_text_without_inventing_a_gap()
        {
            using var fx = new MakerProjectFixture();
            fx.Write("data/subtitles/th.json", "{\"one\":\"after\"}");

            new MakerHistoryStore(fx.Store).CaptureV1("th", "one", "before", "after");

            var history = new MakerHistoryStore(fx.Store).Read("th", "one", 0, 20).Value!;
            Assert.False(history.HistoryGap);
            Assert.Equal(new[] { "before", "after" }, history.Items.Select(i => i.Text));
        }

        private static MakerStudioSubtitleRequest Request(MakerProjectFixture fx, int op, string revision, string? text) =>
            new MakerStudioSubtitleRequest
            {
                PackId = "sample.pack", Lang = "th", Id = "one", ExpectedRevision = revision,
                OperationId = fx.Operation(op), Text = text,
            };
    }
}
