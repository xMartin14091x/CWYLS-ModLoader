using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CwyLs.ModLoader.Core.Api.Maker;
using CwyLs.ModLoader.Core.Api.Maker.Studio;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    public sealed class MakerTakeTests
    {
        [Fact]
        public void Create_list_update_favourite_and_delete_round_trip_and_respect_three_slot_cap()
        {
            using var fx = new MakerProjectFixture();
            var takes = new MakerTakeStore(fx.Store);

            var initial = takes.ReadTakes("th", "one").Value!;
            Assert.Empty(initial.Items);
            Assert.Null(initial.FavouriteTakeId);
            Assert.Null(initial.PublishedTakeId);

            // Create 3 takes (even empty reserved slots count toward TakesPerLine = 3).
            var c1 = takes.Create(CreateReq(fx, 1, initial.Revision, "own", confirmed: true));
            Assert.True(c1.Ok, c1.Message);
            Assert.NotNull(c1.Value!.Take);
            Assert.Equal("own", c1.Value.Take!.Origin);
            Assert.True(c1.Value.Take.Consent.Confirmed);
            Assert.Equal(MakerStudioContract.ConsentVersion, c1.Value.Take.Consent.StatementVersion);
            Assert.NotNull(c1.Value.Take.Consent.ConfirmedAt);
            Assert.False(c1.Value.Take.SourcePresent);
            Assert.False(c1.Value.Take.RenderReady);

            var c2 = takes.Create(CreateReq(fx, 2, c1.Value.Revision, "ai-assisted", confirmed: false));
            Assert.True(c2.Ok, c2.Message);
            Assert.Null(c2.Value!.Take!.Consent.ConfirmedAt);

            var c3 = takes.Create(CreateReq(fx, 3, c2.Value.Revision, "ai-generated", confirmed: true));
            Assert.True(c3.Ok, c3.Message);

            // 4th take is refused with take_limit (no automatic eviction).
            var c4 = takes.Create(CreateReq(fx, 4, c3.Value!.Revision, "own", confirmed: true));
            Assert.False(c4.Ok);
            Assert.Equal(MakerStudioErrorCodes.TakeLimit, c4.Error);

            var page = takes.ReadTakes("th", "one").Value!;
            Assert.Equal(3, page.Items.Count);
            Assert.Equal(c1.Value.Take.Id, page.Items[0].Id);
            Assert.Equal(c2.Value.Take.Id, page.Items[1].Id);
            Assert.Equal(c3.Value.Take!.Id, page.Items[2].Id);

            // Favourite selection is independent of publication and can be cleared with null.
            var fav = takes.SetFavourite(new MakerStudioFavouriteRequest
            {
                PackId = "sample.pack",
                Lang = "th",
                Id = "one",
                ExpectedRevision = page.Revision,
                OperationId = fx.Operation(5),
                TakeId = c2.Value.Take.Id,
            });
            Assert.True(fav.Ok, fav.Message);
            Assert.Equal(c2.Value.Take.Id, fav.Value!.FavouriteTakeId);
            Assert.Null(takes.ReadTakes("th", "one").Value!.PublishedTakeId);

            var cleared = takes.SetFavourite(new MakerStudioFavouriteRequest
            {
                PackId = "sample.pack", Lang = "th", Id = "one", ExpectedRevision = fav.Value.Revision, OperationId = fx.Operation(15), TakeId = null,
            });
            Assert.True(cleared.Ok, cleared.Message);
            Assert.Null(cleared.Value!.FavouriteTakeId);
            Assert.Null(takes.ReadTakes("th", "one").Value!.FavouriteTakeId);
            fav = takes.SetFavourite(new MakerStudioFavouriteRequest
            {
                PackId = "sample.pack", Lang = "th", Id = "one", ExpectedRevision = cleared.Value.Revision, OperationId = fx.Operation(16), TakeId = c2.Value.Take.Id,
            });
            Assert.True(fav.Ok, fav.Message);

            // Update: an origin change without a new attestation clears the consent.
            var updated = takes.Update(new MakerStudioUpdateTakeRequest
            {
                PackId = "sample.pack", Lang = "th", Id = "one", TakeId = c1.Value.Take.Id, ExpectedRevision = fav.Value!.Revision,
                OperationId = fx.Operation(17), Origin = "ai-assisted",
            });
            Assert.True(updated.Ok, updated.Message);
            Assert.Equal("ai-assisted", updated.Value!.Take!.Origin);
            Assert.False(updated.Value.Take.Consent.Confirmed);
            fav = takes.SetFavourite(new MakerStudioFavouriteRequest
            {
                PackId = "sample.pack", Lang = "th", Id = "one", ExpectedRevision = updated.Value.Revision, OperationId = fx.Operation(18), TakeId = c2.Value.Take.Id,
            });
            Assert.True(fav.Ok, fav.Message);

            // Deleting the favourite take clears favouriteTakeId and frees a slot for a new take.
            var del = takes.Delete(new MakerStudioDeleteTakeRequest
            {
                PackId = "sample.pack",
                Lang = "th",
                Id = "one",
                TakeId = c2.Value.Take.Id,
                ExpectedRevision = fav.Value!.Revision,
                OperationId = fx.Operation(6),
            });
            Assert.True(del.Ok, del.Message);
            Assert.True(del.Value!.Removed);

            var afterDelete = takes.ReadTakes("th", "one").Value!;
            Assert.Equal(2, afterDelete.Items.Count);
            Assert.Null(afterDelete.FavouriteTakeId);

            var replacement = takes.Create(CreateReq(fx, 7, afterDelete.Revision, "own", confirmed: true));
            Assert.True(replacement.Ok, replacement.Message);
            Assert.Equal(3, takes.ReadTakes("th", "one").Value!.Items.Count);
        }

        [Fact]
        public async System.Threading.Tasks.Task Deleting_a_published_take_is_refused_with_take_in_use()
        {
            using var fx = new MakerProjectFixture();
            var editor = MakerTakeReviewTests.Editor(fx);
            var (takeId, _) = MakerTakeReviewTests.Rendered(fx, new MakerTakeStore(fx.Store));
            var page = (await editor.TakesAsync("sample.pack", "th", "one", default)).Value!;
            var published = await editor.PublishAsync(new MakerStudioPublishRequest
            {
                PackId = "sample.pack", Lang = "th", Id = "one", TakeId = takeId, ExpectedRevision = page.Revision,
                ExpectedClipRevision = page.ClipRevision!, OperationId = fx.Operation(8),
            }, default);
            Assert.True(published.Ok, published.Message);
            var takes = new MakerTakeStore(fx.Store);

            var refusal = takes.Delete(MakerTakeReviewTests.Delete(fx, 9, takes.ReadTakes("th", "one").Value!.Revision, takeId));

            Assert.False(refusal.Ok);
            Assert.Equal(MakerStudioErrorCodes.TakeInUse, refusal.Error);
            Assert.Single(takes.ReadTakes("th", "one").Value!.Items);

            // A publishedTakeId written by hand, with no publication behind it, does not pin the take.
            var lineJson = CwyLs.ModLoader.Core.Json.JsonReader.ParseObject(fx.Read(MakerStudioPaths.Line("th", "one")));
            lineJson["publishedClip"] = "Voice_Other";
            fx.Write(MakerStudioPaths.Line("th", "one"), CwyLs.ModLoader.Core.Api.ApiJson.Write(lineJson));
            Assert.Null(takes.ReadTakes("th", "one").Value!.PublishedTakeId);
        }

        [Fact]
        public void Changing_trim_even_out_volume_or_origin_invalidates_render_or_consent_and_deletes_render_wav()
        {
            using var fx = new MakerProjectFixture();
            var takes = new MakerTakeStore(fx.Store);
            var rev = fx.Store.ReadLine("th", "one").Value!.Revision;
            var created = takes.Create(CreateReq(fx, 1, rev, "own", confirmed: true)).Value!;
            var takeId = created.Take!.Id;

            var sourceUp = takes.SaveAudio(new MakerStudioTakeAudioRequest
            {
                PackId = "sample.pack",
                Lang = "th",
                Id = "one",
                TakeId = takeId,
                Kind = "source",
                ExpectedRevision = created.Revision,
                OperationId = fx.Operation(2),
            }, TestWav.Tone(2.0, 24000, 1)).Value!;
            Assert.True(sourceUp.Take!.SourcePresent);
            Assert.False(sourceUp.Take.RenderReady);

            var renderUp = takes.SaveAudio(new MakerStudioTakeAudioRequest
            {
                PackId = "sample.pack",
                Lang = "th",
                Id = "one",
                TakeId = takeId,
                Kind = "render",
                ExpectedRevision = sourceUp.Revision,
                OperationId = fx.Operation(3),
            }, TestWav.Tone(1.5, 48000, 1)).Value!;
            Assert.True(renderUp.Take!.RenderReady);
            var renderRel = MakerStudioPaths.TakeAudio("th", "one", takeId, "render");
            Assert.True(fx.Store.Files.Exists(renderRel));

            // Updating trimStartSec/trimEndSec invalidates renderReady and deletes render.wav on disk.
            var trimmed = takes.Update(new MakerStudioUpdateTakeRequest
            {
                PackId = "sample.pack",
                Lang = "th",
                Id = "one",
                TakeId = takeId,
                ExpectedRevision = renderUp.Revision,
                OperationId = fx.Operation(4),
                HasTrimStartSec = true,
                TrimStartSec = 0.25,
                HasTrimEndSec = true,
                TrimEndSec = 1.75,
            }).Value!;
            Assert.False(trimmed.Take!.RenderReady);
            Assert.Null(trimmed.Take.RenderDurationSec);
            Assert.Equal(0.25, trimmed.Take.TrimStartSec);
            Assert.Equal(1.75, trimmed.Take.TrimEndSec);
            Assert.False(fx.Store.Files.Exists(renderRel));

            // Re-upload render, then clear trimStartSec with explicit null -> invalidates render again.
            var renderUp2 = takes.SaveAudio(new MakerStudioTakeAudioRequest
            {
                PackId = "sample.pack",
                Lang = "th",
                Id = "one",
                TakeId = takeId,
                Kind = "render",
                ExpectedRevision = trimmed.Revision,
                OperationId = fx.Operation(5),
            }, TestWav.Tone(1.5, 48000, 1)).Value!;
            Assert.True(renderUp2.Take!.RenderReady);
            Assert.True(fx.Store.Files.Exists(renderRel));

            var clearedTrim = takes.Update(new MakerStudioUpdateTakeRequest
            {
                PackId = "sample.pack",
                Lang = "th",
                Id = "one",
                TakeId = takeId,
                ExpectedRevision = renderUp2.Revision,
                OperationId = fx.Operation(6),
                HasTrimStartSec = true,
                TrimStartSec = null,
            }).Value!;
            Assert.Null(clearedTrim.Take!.TrimStartSec);
            Assert.Equal(1.75, clearedTrim.Take.TrimEndSec);
            Assert.False(clearedTrim.Take.RenderReady);
            Assert.False(fx.Store.Files.Exists(renderRel));

            // Re-upload render, then toggle evenOutVolume -> invalidates render again.
            var renderUp3 = takes.SaveAudio(new MakerStudioTakeAudioRequest
            {
                PackId = "sample.pack",
                Lang = "th",
                Id = "one",
                TakeId = takeId,
                Kind = "render",
                ExpectedRevision = clearedTrim.Revision,
                OperationId = fx.Operation(7),
            }, TestWav.Tone(1.5, 48000, 1)).Value!;
            Assert.True(renderUp3.Take!.RenderReady);

            var evenOut = takes.Update(new MakerStudioUpdateTakeRequest
            {
                PackId = "sample.pack",
                Lang = "th",
                Id = "one",
                TakeId = takeId,
                ExpectedRevision = renderUp3.Revision,
                OperationId = fx.Operation(8),
                EvenOutVolume = true,
            }).Value!;
            Assert.True(evenOut.Take!.EvenOutVolume);
            Assert.False(evenOut.Take.RenderReady);
            Assert.False(fx.Store.Files.Exists(renderRel));

            // Changing origin without supplying consent invalidates consent confirmation.
            var originChanged = takes.Update(new MakerStudioUpdateTakeRequest
            {
                PackId = "sample.pack",
                Lang = "th",
                Id = "one",
                TakeId = takeId,
                ExpectedRevision = evenOut.Revision,
                OperationId = fx.Operation(9),
                Origin = "ai-assisted",
            }).Value!;
            Assert.Equal("ai-assisted", originChanged.Take!.Origin);
            Assert.False(originChanged.Take.Consent.Confirmed);
            Assert.Null(originChanged.Take.Consent.ConfirmedAt);

            // Re-confirming consent stamps confirmedAt.
            var reconfirmed = takes.Update(new MakerStudioUpdateTakeRequest
            {
                PackId = "sample.pack",
                Lang = "th",
                Id = "one",
                TakeId = takeId,
                ExpectedRevision = originChanged.Revision,
                OperationId = fx.Operation(10),
                Consent = new MakerStudioConsent
                {
                    Confirmed = true,
                    StatementVersion = MakerStudioContract.ConsentVersion,
                },
            }).Value!;
            Assert.True(reconfirmed.Take!.Consent.Confirmed);
            Assert.NotNull(reconfirmed.Take.Consent.ConfirmedAt);
        }

        [Fact]
        public void Invalid_trim_bounds_are_rejected_with_bad_request()
        {
            using var fx = new MakerProjectFixture();
            var takes = new MakerTakeStore(fx.Store);
            var rev = fx.Store.ReadLine("th", "one").Value!.Revision;
            var created = takes.Create(CreateReq(fx, 1, rev, "own", confirmed: true)).Value!;
            var takeId = created.Take!.Id;
            var sourceUp = takes.SaveAudio(new MakerStudioTakeAudioRequest
            {
                PackId = "sample.pack",
                Lang = "th",
                Id = "one",
                TakeId = takeId,
                Kind = "source",
                ExpectedRevision = created.Revision,
                OperationId = fx.Operation(2),
            }, TestWav.Tone(2.0, 16000, 1)).Value!;

            // trimEndSec <= trimStartSec
            var inverted = takes.Update(new MakerStudioUpdateTakeRequest
            {
                PackId = "sample.pack",
                Lang = "th",
                Id = "one",
                TakeId = takeId,
                ExpectedRevision = sourceUp.Revision,
                OperationId = fx.Operation(3),
                HasTrimStartSec = true,
                TrimStartSec = 1.0,
                HasTrimEndSec = true,
                TrimEndSec = 1.0,
            });
            Assert.False(inverted.Ok);
            Assert.Equal(MakerErrorCodes.BadRequest, inverted.Error);

            // trimEndSec > sourceDurationSec (2.0 s)
            var pastEnd = takes.Update(new MakerStudioUpdateTakeRequest
            {
                PackId = "sample.pack",
                Lang = "th",
                Id = "one",
                TakeId = takeId,
                ExpectedRevision = sourceUp.Revision,
                OperationId = fx.Operation(4),
                HasTrimEndSec = true,
                TrimEndSec = 2.5,
            });
            Assert.False(pastEnd.Ok);
            Assert.Equal(MakerErrorCodes.BadRequest, pastEnd.Error);
        }

        [Fact]
        public void Take_mutations_replay_receipts_without_duplicate_events_and_reject_mismatched_operation_id()
        {
            using var fx = new MakerProjectFixture();
            var events = new List<(string Type, IReadOnlyDictionary<string, object?> Data)>();
            var editor = new MakerStudioEditor(
                (_, _) => MakerResult<MakerProjectStore>.Success(fx.Store),
                (_, _, _, _, _) => new MakerSubtitleLiveResult(),
                (type, data) => events.Add((type, data)));
            var rig = new MakerRig(studio: () => new MakerStudioBackends(dubs: editor));

            var rev = fx.Store.ReadLine("th", "one").Value!.Revision;
            var createBody = "{\"expectedRevision\":\"" + rev + "\",\"operationId\":\"" + fx.Operation(1)
                + "\",\"origin\":\"own\",\"consent\":{\"confirmed\":true,\"statementVersion\":\"consent-1\"}}";

            var r1 = rig.Send(MakerHttp.Json("POST", "/v1/maker/studio/packs/sample.pack/languages/th/lines/one/takes", rig.Token, createBody));
            var r2 = rig.Send(MakerHttp.Json("POST", "/v1/maker/studio/packs/sample.pack/languages/th/lines/one/takes", rig.Token, createBody));
            Assert.Equal(201, r1.Status);
            Assert.Equal(201, r2.Status);
            Assert.Equal(r1.Body, r2.Body);
            Assert.Single(events);
            Assert.Equal("maker.take.changed", events[0].Type);
            Assert.Equal("created", events[0].Data["change"]);

            // Reusing the same operationId with a different origin returns 409 conflict.
            var conflictBody = "{\"expectedRevision\":\"" + rev + "\",\"operationId\":\"" + fx.Operation(1)
                + "\",\"origin\":\"ai-generated\",\"consent\":{\"confirmed\":true,\"statementVersion\":\"consent-1\"}}";
            var conflict = rig.Send(MakerHttp.Json("POST", "/v1/maker/studio/packs/sample.pack/languages/th/lines/one/takes", rig.Token, conflictBody));
            Assert.Equal(409, conflict.Status);
            Assert.Equal(MakerStudioErrorCodes.Conflict, ApiTestKit.ErrorCode(conflict));
        }

        private static MakerStudioCreateTakeRequest CreateReq(MakerProjectFixture fx, int op, string expectedRevision, string origin, bool confirmed) =>
            new MakerStudioCreateTakeRequest
            {
                PackId = "sample.pack",
                Lang = "th",
                Id = "one",
                ExpectedRevision = expectedRevision,
                OperationId = fx.Operation(op),
                Origin = origin,
                Consent = new MakerStudioConsent
                {
                    Confirmed = confirmed,
                    StatementVersion = MakerStudioContract.ConsentVersion,
                },
            };
    }
}
