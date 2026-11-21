using System;
using System.IO;
using CwyLs.ModLoader.Core.Api.Maker;
using CwyLs.ModLoader.Core.Api.Maker.Editor;
using CwyLs.ModLoader.Core.Api.Maker.Studio;
using CwyLs.ModLoader.Core.Packs;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    public sealed class MakerTakeAudioTests
    {
        [Fact]
        public void Source_accepts_v1_wav_range_canonicalises_headers_and_invalidates_previous_render()
        {
            using var fx = new MakerProjectFixture();
            var takes = new MakerTakeStore(fx.Store);
            var rev = fx.Store.ReadLine("th", "one").Value!.Revision;
            var created = takes.Create(CreateReq(fx, 1, rev)).Value!;
            var takeId = created.Take!.Id;

            // Upload source with a padded JUNK chunk -> stored and served as canonical 44-byte header WAV.
            var padded = TestWav.PaddedTo(17000);
            var source1 = takes.SaveAudio(AudioReq(fx, 2, created.Revision, takeId, "source"), padded).Value!;
            Assert.True(source1.Take!.SourcePresent);
            Assert.NotNull(source1.Take.SourceRevision);
            Assert.Equal(1.0, source1.Take.SourceDurationSec!.Value, 3);

            var readSource1 = takes.ReadAudio("th", "one", takeId, "source").Value!;
            Assert.Equal("source", readSource1.Kind);
            var expectedCanonical = MakerWav.Canonical(padded, MakerWav.Inspect(padded));
            Assert.Equal(expectedCanonical, readSource1.Bytes);

            // Upload 48 kHz mono render.
            var renderWav = TestWav.Tone(0.8, 48000, 1);
            var render1 = takes.SaveAudio(AudioReq(fx, 3, source1.Revision, takeId, "render"), renderWav).Value!;
            Assert.True(render1.Take!.RenderReady);
            Assert.Equal(0.8, render1.Take.RenderDurationSec!.Value, 3);
            var renderRel = MakerStudioPaths.TakeAudio("th", "one", takeId, "render");
            Assert.True(fx.Store.Files.Exists(renderRel));

            // Re-uploading source (24 kHz stereo) invalidates renderReady and deletes render.wav.
            var stereoSource = TestWav.Tone(1.25, 24000, 2);
            var source2 = takes.SaveAudio(AudioReq(fx, 4, render1.Revision, takeId, "source"), stereoSource).Value!;
            Assert.True(source2.Take!.SourcePresent);
            Assert.NotEqual(source1.Take.SourceRevision, source2.Take.SourceRevision);
            Assert.False(source2.Take.RenderReady);
            Assert.Null(source2.Take.RenderDurationSec);
            Assert.False(fx.Store.Files.Exists(renderRel));
            Assert.Equal(MakerErrorCodes.NotFound, takes.ReadAudio("th", "one", takeId, "render").Error);
        }

        [Fact]
        public void Render_requires_existing_source_and_strictly_enforces_48khz_mono_pcm16()
        {
            using var fx = new MakerProjectFixture();
            var takes = new MakerTakeStore(fx.Store);
            var rev = fx.Store.ReadLine("th", "one").Value!.Revision;
            var created = takes.Create(CreateReq(fx, 1, rev)).Value!;
            var takeId = created.Take!.Id;

            // Uploading render before source is refused with 404 not_found (Claude's M5c ruling: "upload the source first").
            var noSource = takes.SaveAudio(AudioReq(fx, 2, created.Revision, takeId, "render"), TestWav.Tone(0.5, 48000, 1));
            Assert.False(noSource.Ok);
            Assert.Equal(MakerErrorCodes.NotFound, noSource.Error);

            // Upload source first.
            var withSource = takes.SaveAudio(AudioReq(fx, 3, created.Revision, takeId, "source"), TestWav.Tone(0.5, 16000, 1)).Value!;

            // Render at 44.1 kHz mono is refused with 415 unsupported_media_type.
            var wrongRate = takes.SaveAudio(AudioReq(fx, 4, withSource.Revision, takeId, "render"), TestWav.Tone(0.5, 44100, 1));
            Assert.False(wrongRate.Ok);
            Assert.Equal(MakerErrorCodes.UnsupportedMedia, wrongRate.Error);

            // Render at 48 kHz stereo is refused with 415 unsupported_media_type.
            var stereo = takes.SaveAudio(AudioReq(fx, 5, withSource.Revision, takeId, "render"), TestWav.Tone(0.5, 48000, 2));
            Assert.False(stereo.Ok);
            Assert.Equal(MakerErrorCodes.UnsupportedMedia, stereo.Error);

            // Render over 60 seconds is refused with 413 too_large.
            var tooLong = takes.SaveAudio(AudioReq(fx, 6, withSource.Revision, takeId, "render"), TestWav.Header(1, 1, 48000, 16, 48000 * 2 * 61));
            Assert.False(tooLong.Ok);
            Assert.Equal(MakerErrorCodes.TooLarge, tooLong.Error);

            // Valid 48 kHz mono render succeeds.
            var validRender = takes.SaveAudio(AudioReq(fx, 7, withSource.Revision, takeId, "render"), TestWav.Tone(0.5, 48000, 1));
            Assert.True(validRender.Ok, validRender.Message);
            Assert.True(validRender.Value!.Take!.RenderReady);
        }

        [Fact]
        public void Get_take_audio_serves_only_editable_pack_creator_files_and_never_runtime_or_reference_audio()
        {
            using var fx = new MakerProjectFixture();
            var editor = new MakerStudioEditor(
                (packId, editing) =>
                {
                    if (packId == "reference.pack")
                        return editing
                            ? MakerResult<MakerProjectStore>.Fail(MakerErrorCodes.ReadOnlyPack, "reference pack")
                            : MakerResult<MakerProjectStore>.Success(new MakerProjectStore(fx.Root, fx.Clock, recoveryWritable: false));
                    return MakerResult<MakerProjectStore>.Success(fx.Store);
                },
                (_, _, _, _, _) => new MakerSubtitleLiveResult());
            var rig = new MakerRig(studio: () => new MakerStudioBackends(dubs: editor));

            // Write a runtime voice file under audio/voice/th/one.wav and verify GET T/audio never reads it.
            var runtimeWav = TestWav.Tone(0.5, 48000, 1);
            fx.Store.Files.WriteAtomic("audio/voice/th/one.wav", runtimeWav);

            var rev = fx.Store.ReadLine("th", "one").Value!.Revision;
            var createBody = "{\"expectedRevision\":\"" + rev + "\",\"operationId\":\"" + fx.Operation(1)
                + "\",\"origin\":\"own\",\"consent\":{\"confirmed\":true,\"statementVersion\":\"consent-1\"}}";
            var created = rig.Send(MakerHttp.Json("POST", "/v1/maker/studio/packs/sample.pack/languages/th/lines/one/takes", rig.Token, createBody));
            Assert.Equal(201, created.Status);
            var createdJson = ApiTestKit.Json(created);
            var takeObj = Assert.IsType<CwyLs.ModLoader.Core.Json.JsonObject>(createdJson["take"]);
            var takeId = Assert.IsType<string>(takeObj["id"]);
            var nextRev = Assert.IsType<string>(createdJson["revision"]);

            // Before uploading source, GET T/audio returns 404 even though audio/voice/th/one.wav exists on disk.
            var missingGet = rig.Send(MakerHttp.Get(
                "/v1/maker/studio/packs/sample.pack/languages/th/lines/one/takes/" + takeId + "/audio?kind=source", rig.Token));
            Assert.Equal(404, missingGet.Status);

            // Upload creator source WAV and verify GET T/audio returns the exact canonical bytes with audio/wav.
            var creatorWav = TestWav.Tone(0.75, 24000, 1);
            var putUrl = "/v1/maker/studio/packs/sample.pack/languages/th/lines/one/takes/" + takeId
                + "/audio?kind=source&expectedRevision=" + nextRev + "&operationId=" + fx.Operation(2);
            var uploaded = rig.Send(MakerHttp.Build("PUT", putUrl, rig.Token, creatorWav, "audio/wav"));
            Assert.Equal(200, uploaded.Status);

            var getOk = rig.Send(MakerHttp.Get(
                "/v1/maker/studio/packs/sample.pack/languages/th/lines/one/takes/" + takeId + "/audio?kind=source", rig.Token));
            Assert.Equal(200, getOk.Status);
            Assert.Equal("audio/wav", getOk.MediaType);
            Assert.Equal(creatorWav, getOk.Bytes);

            // A real reference pack (MakerEditRules.IsReferencePack: cwy.example, cwy.demo.*, cwy.sample.*) never serves
            // take audio; a read-only pack that is not a reference pack does (REVIEW-FIXES-M5c item 4).
            foreach (var reference in new[] { "cwy.example", "cwy.demo.audio", "cwy.sample.hello" })
            {
                var refGet = rig.Send(MakerHttp.Get(
                    "/v1/maker/studio/packs/" + reference + "/languages/th/lines/one/takes/" + takeId + "/audio?kind=source", rig.Token));
                Assert.Equal(403, refGet.Status);
                Assert.Equal(MakerErrorCodes.ReadOnlyPack, ApiTestKit.ErrorCode(refGet));
            }

            var readOnlyGet = rig.Send(MakerHttp.Get(
                "/v1/maker/studio/packs/reference.pack/languages/th/lines/one/takes/" + takeId + "/audio?kind=source", rig.Token));
            Assert.Equal(200, readOnlyGet.Status);
            Assert.Equal(creatorWav, readOnlyGet.Bytes);
        }

        [Fact]
        public void External_modification_or_deletion_of_take_wav_is_reconciled_and_invalidates_stale_revision()
        {
            using var fx = new MakerProjectFixture();
            var takes = new MakerTakeStore(fx.Store);
            var rev = fx.Store.ReadLine("th", "one").Value!.Revision;
            var created = takes.Create(CreateReq(fx, 1, rev)).Value!;
            var takeId = created.Take!.Id;

            var s1 = takes.SaveAudio(AudioReq(fx, 2, created.Revision, takeId, "source"), TestWav.Tone(1.0, 24000, 1)).Value!;
            var r1 = takes.SaveAudio(AudioReq(fx, 3, s1.Revision, takeId, "render"), TestWav.Tone(1.0, 48000, 1)).Value!;

            // Delete render.wav externally outside Studio -> next ReadTakes reports renderReady:false and a new revision.
            File.Delete(Path.Combine(fx.Root, MakerStudioPaths.TakeAudio("th", "one", takeId, "render")));
            var afterExternalDelete = takes.ReadTakes("th", "one").Value!;
            Assert.NotEqual(r1.Revision, afterExternalDelete.Revision);
            Assert.False(afterExternalDelete.Items[0].RenderReady);
            Assert.Null(afterExternalDelete.Items[0].RenderDurationSec);

            // Stale expectedRevision mutation fails with 409 conflict.
            var staleUpdate = takes.Update(new MakerStudioUpdateTakeRequest
            {
                PackId = "sample.pack",
                Lang = "th",
                Id = "one",
                TakeId = takeId,
                ExpectedRevision = r1.Revision,
                OperationId = fx.Operation(4),
                EvenOutVolume = true,
            });
            Assert.False(staleUpdate.Ok);
            Assert.Equal(MakerStudioErrorCodes.Conflict, staleUpdate.Error);
        }

        [Fact]
        public void Take_audio_quota_refusal_returns_storage_limit_without_deleting_existing_files()
        {
            using var fx = new MakerProjectFixture();
            var wav = TestWav.Tone(1.0, 48000, 1);
            // Allow enough peak quota (stage + target = 2x wav) for the first WAV, but not a second WAV alongside it (3x wav).
            var constrained = new MakerProjectStore(fx.Root, fx.Clock, quota: (long)(2.5 * wav.Length));
            var takes = new MakerTakeStore(constrained);

            var rev = constrained.ReadLine("th", "one").Value!.Revision;
            var created = takes.Create(CreateReq(fx, 1, rev)).Value!;
            var takeId = created.Take!.Id;

            var s1 = takes.SaveAudio(AudioReq(fx, 2, created.Revision, takeId, "source"), wav);
            Assert.True(s1.Ok, s1.Message);

            var r1 = takes.SaveAudio(AudioReq(fx, 3, s1.Value!.Revision, takeId, "render"), wav);
            Assert.False(r1.Ok);
            Assert.Equal(MakerStudioErrorCodes.StorageLimit, r1.Error);

            // Existing source.wav is untouched.
            Assert.True(constrained.Files.Exists(MakerStudioPaths.TakeAudio("th", "one", takeId, "source")));
            Assert.False(constrained.Files.Exists(MakerStudioPaths.TakeAudio("th", "one", takeId, "render")));
        }

        [Fact]
        public void Pack_content_index_ignores_maker_take_wavs_and_never_indexes_them_as_runtime_voice()
        {
            using var fx = new MakerProjectFixture();
            var takeId = new string('a', 32);
            var sourceRel = MakerStudioPaths.TakeAudio("th", "one", takeId, "source");
            var renderRel = MakerStudioPaths.TakeAudio("th", "one", takeId, "render");
            fx.Store.Files.WriteAtomic(sourceRel, TestWav.Tone(0.5, 24000, 1));
            fx.Store.Files.WriteAtomic(renderRel, TestWav.Tone(0.5, 48000, 1));

            var sourceClass = PackContentIndex.Classify(sourceRel);
            var renderClass = PackContentIndex.Classify(renderRel);
            var runtimeClass = PackContentIndex.Classify("audio/voice/one.wav");

            Assert.Equal(PackContentKind.Other, sourceClass);
            Assert.Equal(PackContentKind.Other, renderClass);
            Assert.Equal(PackContentKind.Voice, runtimeClass);

            var scanned = CwyLs.ModLoader.Core.Api.Maker.Catalog.MakerPackScanner.Scan(new[]
            {
                new CwyLs.ModLoader.Core.Api.Maker.Catalog.MakerPackSource("sample.pack", "Sample", "0.1.0", fx.Root, false),
            });
            Assert.Null(scanned.FindDub("th", "one"));
        }

        private static MakerStudioCreateTakeRequest CreateReq(MakerProjectFixture fx, int op, string expectedRevision) =>
            new MakerStudioCreateTakeRequest
            {
                PackId = "sample.pack",
                Lang = "th",
                Id = "one",
                ExpectedRevision = expectedRevision,
                OperationId = fx.Operation(op),
                Origin = "own",
                Consent = new MakerStudioConsent
                {
                    Confirmed = true,
                    StatementVersion = MakerStudioContract.ConsentVersion,
                },
            };

        private static MakerStudioTakeAudioRequest AudioReq(MakerProjectFixture fx, int op, string expectedRevision, string takeId, string kind) =>
            new MakerStudioTakeAudioRequest
            {
                PackId = "sample.pack",
                Lang = "th",
                Id = "one",
                TakeId = takeId,
                Kind = kind,
                ExpectedRevision = expectedRevision,
                OperationId = fx.Operation(op),
            };
    }
}
