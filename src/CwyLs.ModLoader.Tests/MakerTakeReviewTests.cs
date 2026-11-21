using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CwyLs.ModLoader.Core.Api;
using CwyLs.ModLoader.Core.Api.Maker;
using CwyLs.ModLoader.Core.Api.Maker.Studio;
using CwyLs.ModLoader.Core.Json;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>The fixes and missing tests of the M5c takes review (REVIEW-FIXES-M5c.md).</summary>
    public sealed class MakerTakeReviewTests
    {
        private const string LinePath = "/v1/maker/studio/packs/sample.pack/languages/th/lines/one";

        // Item 1 (major): a render is ready only for the source it was made from.
        [Fact]
        public void External_source_replacement_leaves_the_old_render_not_ready()
        {
            using var fx = new MakerProjectFixture();
            var takes = new MakerTakeStore(fx.Store);
            var (takeId, rendered) = Rendered(fx, takes);
            var source = Path.Combine(fx.Root, MakerStudioPaths.TakeAudio("th", "one", takeId, "source"));
            File.WriteAllBytes(source, TestWav.Tone(0.9, 24000, 1)); // re-recorded by hand, e.g. in an audio editor

            var page = takes.ReadTakes("th", "one").Value!;

            Assert.NotEqual(rendered, page.Revision);
            Assert.True(page.Items[0].SourcePresent);
            Assert.False(page.Items[0].RenderReady); // the render on disk was made from the old source
            Assert.Null(page.Items[0].RenderDurationSec);
            Assert.True(fx.Store.Files.Exists(MakerStudioPaths.TakeAudio("th", "one", takeId, "render"))); // kept, not adopted
        }

        [Fact]
        public void A_render_file_the_server_never_validated_is_not_adopted()
        {
            using var fx = new MakerProjectFixture();
            var takes = new MakerTakeStore(fx.Store);
            var rev = fx.Store.ReadLine("th", "one").Value!.Revision;
            var created = takes.Create(Create(fx, 1, rev)).Value!;
            var takeId = created.Take!.Id;
            takes.SaveAudio(Audio(fx, 2, created.Revision, takeId, "source"), TestWav.Tone(0.5, 24000, 1));
            fx.Store.Files.WriteAtomic(MakerStudioPaths.TakeAudio("th", "one", takeId, "render"), TestWav.Tone(0.5, 48000, 1));

            Assert.False(takes.ReadTakes("th", "one").Value!.Items[0].RenderReady);
        }

        // Item 2 (major): line reads stat take files instead of hashing them, never read an oversized file, and map I/O
        // errors to structured codes.
        [Fact]
        public void Oversized_take_file_is_treated_as_invalid_without_being_read()
        {
            using var fx = new MakerProjectFixture();
            var takes = new MakerTakeStore(fx.Store);
            var (takeId, _) = Rendered(fx, takes);
            var source = Path.Combine(fx.Root, MakerStudioPaths.TakeAudio("th", "one", takeId, "source"));
            using (var huge = new FileStream(source, FileMode.Create, FileAccess.Write))
            {
                huge.SetLength(2L * 1024 * 1024 * 1024 + 1); // sparse; reading it whole would throw "file too long"
            }

            try
            {
                var line = fx.Store.ReadLine("th", "one");
                Assert.True(line.Ok, line.Message);
                Assert.False(line.Value!.Takes[0].SourcePresent);
                Assert.False(line.Value.Takes[0].RenderReady);
            }
            finally
            {
                File.Delete(source);
            }
        }

        [Fact]
        public void Line_reads_after_an_upload_do_not_rehash_unchanged_take_audio()
        {
            using var fx = new MakerProjectFixture();
            var takes = new MakerTakeStore(fx.Store);
            var (takeId, _) = Rendered(fx, takes);
            var before = fx.Store.TakeAudioReads;
            for (var i = 0; i < 3; i++) Assert.True(fx.Store.ReadLine("th", "one").Ok);
            Assert.Equal(before, fx.Store.TakeAudioReads);

            var render = Path.Combine(fx.Root, MakerStudioPaths.TakeAudio("th", "one", takeId, "render"));
            File.WriteAllBytes(render, TestWav.Tone(0.75, 48000, 1)); // a real change is still noticed
            Assert.False(fx.Store.ReadLine("th", "one").Value!.Takes[0].RenderReady);
            Assert.True(fx.Store.TakeAudioReads > before);
        }

        // Item 3: a commit that frees space is never refused for the quota.
        [Fact]
        public void Deleting_a_take_near_the_quota_succeeds_because_it_frees_space()
        {
            using var fx = new MakerProjectFixture();
            var takes = new MakerTakeStore(fx.Store);
            var (takeId, rendered) = Rendered(fx, takes);
            var full = new MakerTakeStore(new MakerProjectStore(fx.Root, fx.Clock, quota: fx.Store.Files.BytesUnder(MakerStudioPaths.Root) + 64));

            var deleted = full.Delete(Delete(fx, 9, rendered, takeId));

            Assert.True(deleted.Ok, deleted.Message);
            Assert.False(fx.Store.Files.Exists(MakerStudioPaths.TakeAudio("th", "one", takeId, "source")));
        }

        // Item 4: creator takes are readable on a read-only pack; only reference packs never serve take audio.
        [Fact]
        public async Task Take_audio_is_served_from_a_read_only_pack_but_never_from_a_reference_pack()
        {
            using var fx = new MakerProjectFixture();
            var (takeId, _) = Rendered(fx, new MakerTakeStore(fx.Store));
            var readOnly = new MakerProjectStore(fx.Root, fx.Clock, recoveryWritable: false);
            var editor = new MakerStudioEditor((pack, editing) => editing
                    ? MakerResult<MakerProjectStore>.Fail(MakerErrorCodes.ReadOnlyPack, "Its folder is read-only.")
                    : MakerResult<MakerProjectStore>.Success(readOnly),
                (_, _, _, _, _) => new MakerSubtitleLiveResult());

            var audio = await editor.ReadAudioAsync("sample.pack", "th", "one", takeId, "source", default);
            Assert.True(audio.Ok, audio.Message);
            Assert.Equal(fx.Store.Files.ReadBytes(MakerStudioPaths.TakeAudio("th", "one", takeId, "source")), audio.Value!.Bytes);

            var reference = await editor.ReadAudioAsync("cwy.example", "th", "one", takeId, "source", default);
            Assert.Equal(MakerErrorCodes.ReadOnlyPack, reference.Error);
            Assert.Contains("reference pack", reference.Message);
        }

        // Item 5: list, audio, update, favourite and delete work on existing takes without the live catalog.
        [Fact]
        public async Task Takes_can_be_listed_played_and_deleted_after_the_line_left_the_catalog()
        {
            using var fx = new MakerProjectFixture();
            var inCatalog = true;
            var editor = new MakerStudioEditor((_, _) => MakerResult<MakerProjectStore>.Success(fx.Store),
                (_, _, _, _, _) => new MakerSubtitleLiveResult(),
                lineVoice: (_, _, _) => Task.FromResult(inCatalog
                    ? MakerResult<string?>.Success("Voice_One")
                    : MakerResult<string?>.Fail(MakerErrorCodes.NotFound, "no such line")));
            var (takeId, rendered) = Rendered(fx, new MakerTakeStore(fx.Store));
            inCatalog = false; // a game update removed the line

            var page = await editor.TakesAsync("sample.pack", "th", "one", default);
            Assert.True(page.Ok, page.Message);
            Assert.True((await editor.ReadAudioAsync("sample.pack", "th", "one", takeId, "source", default)).Ok);
            var deleted = await editor.DeleteTakeAsync(Delete(fx, 9, page.Value!.Revision, takeId), default);
            Assert.True(deleted.Ok, deleted.Message);

            // Creating a take, or uploading audio, still needs the line in the catalog.
            var create = await editor.CreateTakeAsync(Create(fx, 10, deleted.Value!.Revision), default);
            Assert.Equal(MakerErrorCodes.NotFound, create.Error);
            Assert.NotEqual(rendered, deleted.Value.Revision);
        }

        // Item 6: keep-current on a take intent does not touch subtitle history.
        [Fact]
        public void Keep_current_on_a_take_intent_leaves_subtitle_history_untouched()
        {
            using var fx = new MakerProjectFixture();
            var takes = new MakerTakeStore(fx.Store);
            var (takeId, _) = Rendered(fx, takes);
            var before = fx.Store.ReadLine("th", "one").Value!;
            var lineJson = JsonReader.ParseObject(fx.Read(MakerStudioPaths.Line("th", "one")));
            lineJson["revision"] = "intended";
            var sourceRel = MakerStudioPaths.TakeAudio("th", "one", takeId, "source");
            Assert.Throws<InvalidOperationException>(() => new MakerCommit(fx.Store.Files, "take",
                boundary: p => { if (p == "after-intent") throw new InvalidOperationException("crash"); })
                .Execute(fx.Operation(20), new[]
                {
                    fx.Store.Target(sourceRel, TestWav.Tone(0.6, 24000, 1)),
                    fx.Store.Target(MakerStudioPaths.Line("th", "one"), System.Text.Encoding.UTF8.GetBytes(ApiJson.Write(lineJson))),
                }));
            File.WriteAllBytes(Path.Combine(fx.Root, sourceRel), TestWav.Tone(0.8, 24000, 1)); // external edit blocks it
            var recovery = new MakerRecoveryStore(fx.Store);
            var preview = recovery.Preview(fx.Operation(20)).Value!;

            var resolved = recovery.Resolve(new MakerRecoveryRequest
            {
                PackId = "sample.pack", BlockedOperationId = fx.Operation(20), OperationId = fx.Operation(21),
                ExpectedRecoveryRevision = preview.RecoveryRevision, Strategy = "keep-current",
            });

            Assert.True(resolved.Ok, resolved.Message);
            var after = fx.Store.ReadLine("th", "one").Value!;
            Assert.Equal(before.History.Count, after.History.Count);
            Assert.Equal(before.HistoryGap, after.HistoryGap);
            Assert.False(after.Takes[0].RenderReady); // the source changed, so the old render is not ready (item 1)
        }

        // Item 7: an I/O failure while writing take audio is a structured 503, not a 500.
        [Fact]
        public async Task An_io_failure_while_writing_take_audio_answers_503_unavailable()
        {
            using var fx = new MakerProjectFixture();
            var editor = Editor(fx);
            var rev = fx.Store.ReadLine("th", "one").Value!.Revision;
            var created = (await editor.CreateTakeAsync(Create(fx, 1, rev), default)).Value!;
            typeof(MakerProjectStore).GetField("_commitBoundary", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .SetValue(fx.Store, (Action<string>)(p => { if (p.StartsWith("before-write:", StringComparison.Ordinal)) throw new IOException("injected: disk full"); }));

            var saved = await editor.SaveTakeAudioAsync(Audio(fx, 2, created.Revision, created.Take!.Id, "source"), TestWav.Tone(0.5, 24000, 1), default);

            Assert.Equal(MakerErrorCodes.Unavailable, saved.Error);
            Assert.Contains("disk full", saved.Message);
        }

        // Claude's ruling: a render uploaded before any source is 404 not_found ("upload the source first").
        [Fact]
        public void Render_before_source_is_not_found()
        {
            using var fx = new MakerProjectFixture();
            var takes = new MakerTakeStore(fx.Store);
            var created = takes.Create(Create(fx, 1, fx.Store.ReadLine("th", "one").Value!.Revision)).Value!;

            var render = takes.SaveAudio(Audio(fx, 2, created.Revision, created.Take!.Id, "render"), TestWav.Tone(0.5, 48000, 1));

            Assert.Equal(MakerErrorCodes.NotFound, render.Error);
            Assert.Contains("source", render.Message);
        }

        // Item 9: the missing tests.
        [Fact]
        public void Audio_update_delete_and_favourite_replay_their_receipts_and_refuse_a_reused_operation_id()
        {
            using var fx = new MakerProjectFixture();
            var (takeId, rendered) = Rendered(fx, new MakerTakeStore(fx.Store));
            var source = Audio(fx, 10, rendered, takeId, "source");
            var first = new MakerTakeStore(fx.Store).SaveAudio(source, TestWav.Tone(0.6, 24000, 1));
            Assert.True(first.Ok, first.Message);
            AssertReplay(fx, s => s.SaveAudio(source, TestWav.Tone(0.6, 24000, 1)), first.Value!.Revision,
                s => s.SaveAudio(source, TestWav.Tone(0.7, 24000, 1)));

            var update = new MakerStudioUpdateTakeRequest
            {
                PackId = "sample.pack", Lang = "th", Id = "one", TakeId = takeId, ExpectedRevision = first.Value.Revision,
                OperationId = fx.Operation(11), EvenOutVolume = true,
            };
            var updated = new MakerTakeStore(fx.Store).Update(update);
            Assert.True(updated.Ok, updated.Message);
            AssertReplay(fx, s => s.Update(update), updated.Value!.Revision, s => s.Update(new MakerStudioUpdateTakeRequest
            {
                PackId = "sample.pack", Lang = "th", Id = "one", TakeId = takeId, ExpectedRevision = first.Value.Revision,
                OperationId = fx.Operation(11), EvenOutVolume = false,
            }));

            var favourite = new MakerStudioFavouriteRequest
            {
                PackId = "sample.pack", Lang = "th", Id = "one", TakeId = takeId, ExpectedRevision = updated.Value.Revision, OperationId = fx.Operation(12),
            };
            var fav = new MakerTakeStore(fx.Store).SetFavourite(favourite);
            Assert.True(fav.Ok, fav.Message);
            AssertReplay(fx, s => s.SetFavourite(favourite), fav.Value!.Revision, s => s.SetFavourite(new MakerStudioFavouriteRequest
            {
                PackId = "sample.pack", Lang = "th", Id = "one", TakeId = null, ExpectedRevision = updated.Value.Revision, OperationId = fx.Operation(12),
            }));

            var delete = Delete(fx, 13, fav.Value.Revision, takeId);
            var deleted = new MakerTakeStore(fx.Store).Delete(delete);
            Assert.True(deleted.Ok, deleted.Message);
            var replayed = new MakerTakeStore(fx.Store);
            var again = replayed.Delete(delete);
            Assert.True(again.Ok, again.Message);
            Assert.True(replayed.LastReplayed);
            Assert.Equal(deleted.Value!.Revision, again.Value!.Revision);
            Assert.Equal(MakerStudioErrorCodes.Conflict, new MakerTakeStore(fx.Store).Delete(Delete(fx, 13, fav.Value.Revision, new string('b', 32))).Error);
        }

        [Theory]
        [InlineData("source")]
        [InlineData("delete")]
        public void Crash_at_every_boundary_of_a_real_upload_or_delete_recovers_and_the_retry_replays(string flow)
        {
            var points = new List<string>();
            using (var probe = new MakerProjectFixture())
            {
                var (takeId, rendered) = Rendered(probe, new MakerTakeStore(probe.Store));
                Seam(probe, points.Add);
                Assert.True(Run(probe, flow, takeId, rendered).Ok);
            }

            Assert.Contains("before-intent", points);
            foreach (var point in points.Distinct())
            {
                using var fx = new MakerProjectFixture();
                var (takeId, rendered) = Rendered(fx, new MakerTakeStore(fx.Store));
                Seam(fx, p => { if (p == point) throw new InvalidOperationException(point); });
                Assert.Throws<InvalidOperationException>(() => Run(fx, flow, takeId, rendered));
                Seam(fx, null);

                Assert.True(fx.Store.RecoverPending().Ok, point);
                var retry = Run(fx, flow, takeId, rendered); // the client retries the same request after an uncertain answer
                Assert.True(retry.Ok, point + ": " + retry.Message);
                var line = fx.Store.ReadLine("th", "one").Value!;
                var render = MakerStudioPaths.TakeAudio("th", "one", takeId, "render");
                Assert.False(fx.Store.Files.Exists(render), point);
                if (flow == "delete")
                {
                    Assert.Empty(line.Takes);
                    Assert.False(fx.Store.Files.Exists(MakerStudioPaths.TakeAudio("th", "one", takeId, "source")), point);
                }
                else
                {
                    Assert.False(line.Takes[0].RenderReady, point);
                    Assert.Equal(Retake, fx.Store.Files.ReadBytes(MakerStudioPaths.TakeAudio("th", "one", takeId, "source")));
                }
            }
        }

        [Fact]
        public void Deleting_an_unknown_take_is_not_found()
        {
            using var fx = new MakerProjectFixture();
            var takes = new MakerTakeStore(fx.Store);
            var (_, rendered) = Rendered(fx, takes);
            Assert.Equal(MakerErrorCodes.NotFound, takes.Delete(Delete(fx, 9, rendered, new string('c', 32))).Error);
        }

        [Fact]
        public void Case_colliding_take_folders_are_refused()
        {
            using var fx = new MakerProjectFixture();
            var takes = new MakerTakeStore(fx.Store);
            var (takeId, _) = Rendered(fx, takes);
            var folder = Path.Combine(fx.Root, ".maker", "takes", "th", "one");
            Directory.Move(folder, folder + "-moving");
            Directory.Move(folder + "-moving", Path.Combine(fx.Root, ".maker", "takes", "th", "One"));

            Assert.Equal(MakerStudioErrorCodes.Conflict, takes.ReadTakes("th", "one").Error);
            Assert.Equal(MakerStudioErrorCodes.Conflict, takes.ReadAudio("th", "one", takeId, "source").Error);
        }

        [Fact]
        public void Take_audio_responses_carry_no_store_and_nosniff_on_the_wire()
        {
            using var fx = new MakerProjectFixture();
            var (takeId, _) = Rendered(fx, new MakerTakeStore(fx.Store));
            var editor = Editor(fx);
            var run = SocketTestServer.Start(ApiTransportMode.Tcp, p => new MakerRig(port: p, studio: () => new MakerStudioBackends(dubs: editor)),
                (rig, request) => rig.Router.Handle(request));
            using var transport = run.Transport;
            foreach (var kind in new[] { "source", "render" })
            {
                var reply = RawHttp.Send(run.Port, "GET", LinePath + "/takes/" + takeId + "/audio?kind=" + kind,
                    new[] { new KeyValuePair<string, string>("Authorization", ApiTestKit.AuthValue(run.Rig.Token)) });
                Assert.Equal(200, reply.Status);
                Assert.Equal("audio/wav", reply.Headers.Get("Content-Type"));
                Assert.Equal("no-store", reply.Headers.Get("Cache-Control"));
                Assert.Equal("nosniff", reply.Headers.Get("X-Content-Type-Options"));
                Assert.Equal(fx.Store.Files.ReadBytes(MakerStudioPaths.TakeAudio("th", "one", takeId, kind)), reply.Body);
            }
        }

        [Fact]
        public void Router_refuses_bad_take_uploads_and_patch_bodies_before_the_backend()
        {
            using var fx = new MakerProjectFixture();
            var (takeId, rendered) = Rendered(fx, new MakerTakeStore(fx.Store));
            var rig = new MakerRig(studio: () => new MakerStudioBackends(dubs: Editor(fx)));
            var upload = LinePath + "/takes/" + takeId + "/audio?kind=source&expectedRevision=" + rendered + "&operationId=" + fx.Operation(40);
            Assert.Equal(415, rig.Send(MakerHttp.Build("PUT", upload, rig.Token, TestWav.Tone(0.2, 16000, 1), "application/json")).Status);
            Assert.Equal(415, rig.Send(MakerHttp.Build("PUT", upload, rig.Token, new byte[64], "audio/wav")).Status);
            Assert.Equal(413, rig.Send(MakerHttp.Build("PUT", upload, rig.Token, new byte[MakerDefaults.MaxWavBytes + 1], "audio/wav")).Status);
            Assert.Equal(415, rig.Send(MakerHttp.Build("PUT", upload, rig.Token, TestWav.Tone(0.2, 6000, 1), "audio/wav")).Status); // below 8 kHz
            Assert.Equal(413, rig.Send(MakerHttp.Build("PUT", upload, rig.Token, TestWav.Silence(61, 8000, 1), "audio/wav")).Status); // over 60 s

            var mutation = "\"expectedRevision\":\"" + rendered + "\",\"operationId\":\"" + fx.Operation(41) + "\",";
            foreach (var bad in new[]
            {
                "\"trimStartSec\":61", "\"trimStartSec\":-1", "\"trimStartSec\":\"1\"", "\"evenOutVolume\":\"yes\"",
                "\"consent\":{\"confirmed\":true,\"statementVersion\":\"consent-0\"}", "\"colour\":\"red\"", "\"origin\":\"robot\"",
            })
            {
                var reply = rig.Send(MakerHttp.Json("PATCH", LinePath + "/takes/" + takeId, rig.Token, "{" + mutation + bad + "}"));
                Assert.True(reply.Status == 400, bad + " answered " + reply.Status);
            }

            Assert.Equal(rendered, fx.Store.ReadLine("th", "one").Value!.Revision); // nothing reached the store
        }

        private static readonly byte[] Retake = TestWav.Tone(0.6, 24000, 1);

        private static MakerCommitResultLike Run(MakerProjectFixture fx, string flow, string takeId, string revision)
        {
            var takes = new MakerTakeStore(fx.Store);
            if (flow == "delete")
            {
                var d = takes.Delete(Delete(fx, 30, revision, takeId));
                return new MakerCommitResultLike(d.Ok, d.Message);
            }

            var s = takes.SaveAudio(Audio(fx, 30, revision, takeId, "source"), Retake);
            return new MakerCommitResultLike(s.Ok, s.Message);
        }

        private sealed class MakerCommitResultLike
        {
            internal MakerCommitResultLike(bool ok, string? message) { Ok = ok; Message = message; }
            internal bool Ok { get; }
            internal string? Message { get; }
        }

        private static void Seam(MakerProjectFixture fx, Action<string>? boundary) =>
            typeof(MakerProjectStore).GetField("_commitBoundary", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .SetValue(fx.Store, boundary);

        private static void AssertReplay<T>(MakerProjectFixture fx, Func<MakerTakeStore, MakerResult<T>> again, string revision,
            Func<MakerTakeStore, MakerResult<T>> otherPayload)
        {
            var store = new MakerTakeStore(fx.Store);
            var replay = again(store);
            Assert.True(replay.Ok, replay.Message);
            Assert.True(store.LastReplayed);
            Assert.Equal(revision, replay.Value switch
            {
                MakerStudioTakeSaved saved => saved.Revision,
                MakerStudioTakeRemoved removed => removed.Revision,
                _ => null,
            });
            Assert.Equal(MakerStudioErrorCodes.Conflict, otherPayload(new MakerTakeStore(fx.Store)).Error);
        }

        internal static (string TakeId, string Revision) Rendered(MakerProjectFixture fx, MakerTakeStore takes, int firstOp = 1)
        {
            var rev = fx.Store.ReadLine("th", "one").Value!.Revision;
            var created = takes.Create(Create(fx, firstOp, rev));
            Assert.True(created.Ok, created.Message);
            var takeId = created.Value!.Take!.Id;
            var source = takes.SaveAudio(Audio(fx, firstOp + 1, created.Value.Revision, takeId, "source"), TestWav.Tone(0.5, 24000, 1));
            Assert.True(source.Ok, source.Message);
            var render = takes.SaveAudio(Audio(fx, firstOp + 2, source.Value!.Revision, takeId, "render"), TestWav.Tone(0.5, 48000, 1));
            Assert.True(render.Ok, render.Message);
            return (takeId, render.Value!.Revision);
        }

        internal static MakerStudioEditor Editor(MakerProjectFixture fx) => new MakerStudioEditor(
            (_, _) => MakerResult<MakerProjectStore>.Success(fx.Store), (_, _, _, _, _) => new MakerSubtitleLiveResult(),
            lineVoice: (_, _, _) => Task.FromResult(MakerResult<string?>.Success("Voice_One")));

        internal static MakerStudioCreateTakeRequest Create(MakerProjectFixture fx, int op, string revision) => new MakerStudioCreateTakeRequest
        {
            PackId = "sample.pack", Lang = "th", Id = "one", ExpectedRevision = revision, OperationId = fx.Operation(op),
            Origin = "own", Consent = new MakerStudioConsent { Confirmed = true, StatementVersion = MakerStudioContract.ConsentVersion },
        };

        internal static MakerStudioTakeAudioRequest Audio(MakerProjectFixture fx, int op, string revision, string takeId, string kind) =>
            new MakerStudioTakeAudioRequest
            {
                PackId = "sample.pack", Lang = "th", Id = "one", TakeId = takeId, Kind = kind, ExpectedRevision = revision, OperationId = fx.Operation(op),
            };

        internal static MakerStudioDeleteTakeRequest Delete(MakerProjectFixture fx, int op, string revision, string takeId) =>
            new MakerStudioDeleteTakeRequest
            {
                PackId = "sample.pack", Lang = "th", Id = "one", TakeId = takeId, ExpectedRevision = revision, OperationId = fx.Operation(op),
            };
    }
}
