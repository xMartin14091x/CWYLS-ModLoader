using System.IO;
using CwyLs.ModLoader.Core.Api.Maker;
using CwyLs.ModLoader.Core.Api.Maker.Studio;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    public sealed class MakerTakeMissingTests
    {
        [Fact]
        public void External_source_deletion_clears_source_and_render()
        {
            using var fx = new MakerProjectFixture();
            var takes = new MakerTakeStore(fx.Store);
            var (takeId, revision) = MakerTakeReviewTests.Rendered(fx, takes);
            File.Delete(Path.Combine(fx.Root, MakerStudioPaths.TakeAudio("th", "one", takeId, "source")));

            var result = takes.ReadTakes("th", "one");

            Assert.True(result.Ok, result.Message);
            Assert.NotEqual(revision, result.Value!.Revision);
            var take = Assert.Single(result.Value.Items);
            Assert.False(take.SourcePresent);
            Assert.Null(take.SourceRevision);
            Assert.Null(take.SourceDurationSec);
            Assert.False(take.RenderReady);
            Assert.Null(take.RenderDurationSec);
            Assert.True(fx.Store.Files.Exists(MakerStudioPaths.TakeAudio("th", "one", takeId, "render")));
        }

        [Fact]
        public void External_non_wav_source_clears_source_and_render()
        {
            using var fx = new MakerProjectFixture();
            var takes = new MakerTakeStore(fx.Store);
            var (takeId, revision) = MakerTakeReviewTests.Rendered(fx, takes);
            File.WriteAllText(Path.Combine(fx.Root, MakerStudioPaths.TakeAudio("th", "one", takeId, "source")), "not a wav");

            var result = takes.ReadTakes("th", "one");

            Assert.True(result.Ok, result.Message);
            Assert.NotEqual(revision, result.Value!.Revision);
            var take = Assert.Single(result.Value.Items);
            Assert.False(take.SourcePresent);
            Assert.Null(take.SourceRevision);
            Assert.Null(take.SourceDurationSec);
            Assert.False(take.RenderReady);
            Assert.Null(take.RenderDurationSec);
        }

        [Fact]
        public void Favourite_rejects_unknown_take_and_missing_take_id_over_http()
        {
            using var fx = new MakerProjectFixture();
            var takes = new MakerTakeStore(fx.Store);
            var (takeId, revision) = MakerTakeReviewTests.Rendered(fx, takes);
            var rig = new MakerRig(studio: () => new MakerStudioBackends(dubs: MakerTakeReviewTests.Editor(fx)));
            const string path = "/v1/maker/studio/packs/sample.pack/languages/th/lines/one/favourite";
            var mutation = "\"expectedRevision\":\"" + revision + "\",\"operationId\":\"" + fx.Operation(9) + "\"";

            var unknown = rig.Send(MakerHttp.Json("PUT", path, rig.Token, "{" + mutation + ",\"takeId\":\"" + "cccccccccccccccccccccccccccccccc" + "\"}"));
            var missing = rig.Send(MakerHttp.Json("PUT", path, rig.Token, "{" + mutation + "}"));

            Assert.Equal(404, unknown.Status);
            Assert.Equal(MakerErrorCodes.NotFound, ApiTestKit.ErrorCode(unknown));
            Assert.Equal(400, missing.Status);
            Assert.Equal(MakerErrorCodes.BadRequest, ApiTestKit.ErrorCode(missing));
            var page = takes.ReadTakes("th", "one").Value!;
            Assert.Equal(revision, page.Revision);
            Assert.Null(page.FavouriteTakeId);
            Assert.Equal(takeId, Assert.Single(page.Items).Id);
        }

        [Fact]
        public void Shorter_source_clears_an_out_of_range_trim_window()
        {
            using var fx = new MakerProjectFixture();
            var takes = new MakerTakeStore(fx.Store);
            var created = takes.Create(MakerTakeReviewTests.Create(fx, 1, takes.ReadTakes("th", "one").Value!.Revision));
            Assert.True(created.Ok, created.Message);
            var takeId = created.Value!.Take!.Id;
            var source = takes.SaveAudio(MakerTakeReviewTests.Audio(fx, 2, created.Value.Revision, takeId, "source"), TestWav.Tone(2.0, 24000, 1));
            Assert.True(source.Ok, source.Message);
            var trimmed = takes.Update(new MakerStudioUpdateTakeRequest
            {
                PackId = "sample.pack", Lang = "th", Id = "one", TakeId = takeId,
                ExpectedRevision = source.Value!.Revision, OperationId = fx.Operation(3),
                HasTrimStartSec = true, TrimStartSec = 0.5, HasTrimEndSec = true, TrimEndSec = 1.5,
            });
            Assert.True(trimmed.Ok, trimmed.Message);

            var shorter = takes.SaveAudio(MakerTakeReviewTests.Audio(fx, 4, trimmed.Value!.Revision, takeId, "source"), TestWav.Tone(1.0, 24000, 1));

            Assert.True(shorter.Ok, shorter.Message);
            Assert.True(shorter.Value!.Take!.SourcePresent);
            Assert.Null(shorter.Value.Take.TrimStartSec);
            Assert.Null(shorter.Value.Take.TrimEndSec);
            var persisted = Assert.Single(takes.ReadTakes("th", "one").Value!.Items);
            Assert.Null(persisted.TrimStartSec);
            Assert.Null(persisted.TrimEndSec);
        }

        [Fact]
        public void Stale_revision_refuses_audio_delete_and_favourite_without_changing_the_take()
        {
            using var fx = new MakerProjectFixture();
            var takes = new MakerTakeStore(fx.Store);
            var (takeId, currentRevision) = MakerTakeReviewTests.Rendered(fx, takes);
            const string staleRevision = "stale";

            var audio = takes.SaveAudio(MakerTakeReviewTests.Audio(fx, 10, staleRevision, takeId, "source"), TestWav.Tone(0.8, 24000, 1));
            var delete = takes.Delete(MakerTakeReviewTests.Delete(fx, 11, staleRevision, takeId));
            var favourite = takes.SetFavourite(new MakerStudioFavouriteRequest
            {
                PackId = "sample.pack", Lang = "th", Id = "one", TakeId = takeId,
                ExpectedRevision = staleRevision, OperationId = fx.Operation(12),
            });

            Assert.Equal(MakerStudioErrorCodes.Conflict, audio.Error);
            Assert.Equal(MakerStudioErrorCodes.Conflict, delete.Error);
            Assert.Equal(MakerStudioErrorCodes.Conflict, favourite.Error);
            var page = takes.ReadTakes("th", "one").Value!;
            Assert.Equal(currentRevision, page.Revision);
            Assert.Null(page.FavouriteTakeId);
            var take = Assert.Single(page.Items);
            Assert.Equal(takeId, take.Id);
            Assert.True(take.SourcePresent);
            Assert.True(take.RenderReady);
        }
    }
}
