using System;
using System.IO;
using System.Linq;
using System.Text;
using CwyLs.ModLoader.Core.Api;
using CwyLs.ModLoader.Core.Api.Maker;
using CwyLs.ModLoader.Core.Api.Maker.Editor;
using CwyLs.ModLoader.Core.Api.Maker.Studio;
using CwyLs.ModLoader.Core.Json;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    public sealed class MakerM4cStorageTests
    {
        [Fact]
        public void External_edit_conflicts_then_refetched_revision_can_save_and_records_external_value()
        {
            using var fx = new MakerProjectFixture();
            var history = new MakerHistoryStore(fx.Store);
            var first = history.Save(Save(fx, 1, fx.Store.ReadLine("th", "one").Value!.Revision, "first")).Value!;
            fx.Write("data/subtitles/th.json", "{\"one\":\"external\"}");
            Assert.Equal("conflict", history.Save(Save(fx, 2, first.Saved.Revision, "next")).Error);
            var refreshed = history.Read("th", "one", 0, 20).Value!;
            Assert.NotEqual(first.Saved.Revision, refreshed.Revision);
            Assert.Equal(refreshed.Revision, history.Read("th", "one", 0, 20).Value!.Revision);
            var saved = history.Save(Save(fx, 3, refreshed.Revision, "next"));
            Assert.True(saved.Ok, saved.Message);
            Assert.Contains(history.Read("th", "one", 0, 20).Value!.Items, x => x.Source == "external" && x.Text == "external");
        }

        [Fact]
        public void Equal_text_revert_appends_and_replay_survives_eviction()
        {
            using var fx = new MakerProjectFixture();
            var history = new MakerHistoryStore(fx.Store);
            history.Save(Save(fx, 1, fx.Store.ReadLine("th", "one").Value!.Revision, "same"));
            var page = history.Read("th", "one", 0, 20).Value!;
            var revisionId = page.Items.Last().RevisionId;
            var request = new MakerStudioRevertRequest { PackId = "sample.pack", Lang = "th", Id = "one",
                ExpectedRevision = page.Revision, OperationId = fx.Operation(2), RevisionId = revisionId };
            var first = history.Revert(request).Value!;
            var after = history.Read("th", "one", 0, 20).Value!;
            Assert.Equal(page.Total + 1, after.Total);
            Assert.Equal("revert", after.Items.Last().Source);
            var revision = first.Saved.Revision;
            for (var i = 3; i < 25; i++) revision = history.Save(Save(fx, i, revision, "change " + i)).Value!.Saved.Revision;
            Assert.DoesNotContain(history.Read("th", "one", 0, 20).Value!.Items, x => x.RevisionId == revisionId);
            var replay = history.Revert(request);
            Assert.True(replay.Ok, replay.Message);
            Assert.True(replay.Value!.Replayed);
            Assert.Equal(first.Saved.Revision, replay.Value.Saved.Revision);
        }

        [Fact]
        public void Refetched_save_of_external_text_itself_persists_the_external_history_entry()
        {
            using var fx = new MakerProjectFixture();
            var history = new MakerHistoryStore(fx.Store);
            history.Save(Save(fx, 1, fx.Store.ReadLine("th", "one").Value!.Revision, "first"));
            fx.Write("data/subtitles/th.json", "{\"one\":\"external\"}");
            var refreshed = history.Read("th", "one", 0, 20).Value!;
            var externalId = refreshed.Items.Last().RevisionId;
            Assert.True(history.Save(Save(fx, 2, refreshed.Revision, "external")).Ok);
            var reopened = new MakerHistoryStore(new MakerProjectStore(fx.Root, fx.Clock)).Read("th", "one", 0, 20).Value!;
            Assert.Contains(reopened.Items, i => i.RevisionId == externalId && i.Source == "external");
        }

        [Fact]
        public void Corrupt_subtitle_table_is_refused_without_persisting_a_false_gap()
        {
            using var fx = new MakerProjectFixture();
            new MakerHistoryStore(fx.Store).Save(Save(fx, 1, fx.Store.ReadLine("th", "one").Value!.Revision, "valid"));
            var before = fx.Read(".maker/lines/th/one.json");
            fx.Write("data/subtitles/th.json", "{broken");
            Assert.Equal("metadata_unavailable", fx.Store.ReadLine("th", "one").Error);
            Assert.Equal(before, fx.Read(".maker/lines/th/one.json"));
        }

        [Theory]
        [InlineData(".maker/lines/TH/one.json")]
        [InlineData(".maker/Lines/th/one.json")]
        [InlineData(".MAKER/lines/th/one.json")]
        public void Every_maker_path_component_is_case_checked(string path)
        {
            using var fx = new MakerProjectFixture(); fx.Write(path, "{}");
            Assert.Equal("conflict", fx.Store.ReadLine("th", "one").Error);
        }

        [Fact]
        public void Supplementary_characters_count_as_one_code_point_in_both_editor_paths()
        {
            var text = string.Concat(Enumerable.Repeat("🙂", 2000));
            Assert.True(MakerEditRules.IsSubtitleText(text, out _));
            Assert.False(MakerEditRules.IsSubtitleText(text + "🙂", out _));
        }

        [Fact]
        public void Prune_retains_100_and_expires_using_created_time_even_after_copying_mtimes()
        {
            using var fx = new MakerProjectFixture();
            var ledger = new MakerOperationLedger(fx.Store);
            for (var i = 1; i <= 105; i++)
            {
                fx.Clock.Advance(1);
                var receipt = MakerOperationLedger.Receipt(fx.Operation(i), "finger", "test", "revision", true,
                    fx.Clock.Now, new JsonObject());
                var target = ledger.ReceiptTarget(receipt);
                fx.Store.Files.WriteAtomic(target.Path, target.Bytes);
            }
            ledger.Prune();
            Assert.Equal(100, fx.Store.Files.ListFiles(MakerStudioPaths.Transactions).Count);
            Assert.Equal("not_found", ledger.Read(fx.Operation(1)).Error);
            Assert.True(ledger.Read(fx.Operation(105)).Ok);
            fx.Clock.Advance(86401);
            foreach (var path in fx.Store.Files.ListFiles(MakerStudioPaths.Transactions))
                File.SetLastWriteTimeUtc(fx.Store.Files.Resolve(path), DateTime.UtcNow);
            Assert.Equal(MakerOperationCheckKind.New, ledger.Check(fx.Operation(105), "finger").Kind);
            Assert.Equal("not_found", ledger.Read(fx.Operation(105)).Error);
            ledger.Prune();
            Assert.Empty(fx.Store.Files.ListFiles(MakerStudioPaths.Transactions));
        }

        [Theory]
        [InlineData(0, "history")]
        [InlineData(1, "history")]
        [InlineData(2, "history")]
        [InlineData(3, "history")]
        [InlineData(0, "glossary")]
        [InlineData(1, "glossary")]
        [InlineData(2, "glossary")]
        [InlineData(3, "glossary")]
        [InlineData(0, "operation")]
        [InlineData(1, "operation")]
        [InlineData(2, "operation")]
        [InlineData(3, "operation")]
        public void All_read_surfaces_roll_forward_at_each_commit_boundary(int stopAfter, string firstReader)
        {
            using var fx = new MakerProjectFixture();
            var targets = PendingTargets(fx);
            Assert.Throws<InvalidOperationException>(() => fx.Store.Commit(fx.Operation(10), targets, "subtitle",
                i => { if (i == stopAfter) throw new InvalidOperationException("crash"); }));
            var firstRevision = firstReader switch
            {
                "history" => new MakerHistoryStore(fx.Store).Read("th", "one", 0, 20).Value?.Revision,
                "glossary" => new MakerGlossaryStore(fx.Store).Read("th").Value?.Revision,
                _ => new MakerOperationLedger(fx.Store).Read(fx.Operation(10)).Value?.Revision,
            };
            Assert.Equal("final", firstRevision);
            var history = new MakerHistoryStore(fx.Store).Read("th", "one", 0, 20);
            Assert.True(history.Ok, history.Message);
            Assert.Equal("final", history.Value!.Revision);
            Assert.Equal("final", new MakerGlossaryStore(fx.Store).Read("th").Value!.Revision);
            Assert.Equal("committed", new MakerOperationLedger(fx.Store).Read(fx.Operation(10)).Value!.State);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        public void Unrecoverable_reads_return_structured_503_identity_instead_of_partial_data(int stopAfter)
        {
            using var fx = new MakerProjectFixture();
            Assert.Throws<InvalidOperationException>(() => fx.Store.Commit(fx.Operation(10), PendingTargets(fx), "subtitle",
                i => { if (i == stopAfter) throw new InvalidOperationException("crash"); }));
            fx.Write("_maker_backup/data/subtitles/th.json", "external");
            var history = new MakerHistoryStore(fx.Store).Read("th", "one", 0, 20);
            var glossary = new MakerGlossaryStore(fx.Store).Read("th");
            var op = new MakerOperationLedger(fx.Store).Read(fx.Operation(10));
            Assert.Equal("recovery_required", history.Error);
            Assert.Equal("recovery_required", glossary.Error);
            Assert.Equal("recovery_required", op.Error);
            Assert.Equal(fx.Operation(10), history.Details!.BlockedOperationId);
            Assert.Equal("_maker_backup/data/subtitles/th.json", history.Details.Resource);
            var editor = new MakerStudioEditor((_, _) => MakerResult<MakerProjectStore>.Success(fx.Store),
                (_, _, _, _, _) => throw new Exception("must not apply"));
            var rig = new MakerRig(studio: () => new MakerStudioBackends(subtitles: editor));
            var response = rig.Send(MakerHttp.Get("/v1/maker/studio/packs/sample.pack/languages/th/lines/one/history", rig.Token));
            Assert.Equal(503, response.Status);
            var details = (JsonObject)((JsonObject)ApiTestKit.Json(response)["error"]!)["details"]!;
            Assert.Equal("sample.pack", details["packId"]);
            Assert.Equal(fx.Operation(10), details["blockedOperationId"]);
        }

        private static MakerCommitFile[] PendingTargets(MakerProjectFixture fx)
        {
            // Glossary is independent of a subtitle commit; every read must still recover the latter.
            fx.Write(".maker/glossary/th.json", "{\"schema\":1,\"revision\":\"final\",\"entries\":[]}");
            var meta = "{\"schema\":1,\"revision\":\"final\",\"subtitleFingerprint\":\"" + fx.Store.SubtitleFingerprint("new")
                + "\",\"workflow\":{\"status\":\"draft\",\"starred\":false},\"history\":[],\"historyGap\":false}";
            var receipt = MakerOperationLedger.Receipt(fx.Operation(10), "finger", "subtitle", "final", true, fx.Clock.Now, new JsonObject());
            return new[]
            {
                fx.Store.Target("data/subtitles/th.json", Encoding.UTF8.GetBytes("{\"one\":\"new\"}")),
                fx.Store.Target(".maker/lines/th/one.json", Encoding.UTF8.GetBytes(meta)),
                fx.Store.Target("_maker_backup/data/subtitles/th.json", Encoding.UTF8.GetBytes("{\"one\":\"before\"}")),
                new MakerOperationLedger(fx.Store).ReceiptTarget(receipt),
            };
        }

        private static MakerStudioSubtitleRequest Save(MakerProjectFixture fx, int op, string revision, string? text) =>
            new MakerStudioSubtitleRequest { PackId = "sample.pack", Lang = "th", Id = "one",
                OperationId = fx.Operation(op), ExpectedRevision = revision, Text = text };
    }
}
