using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CwyLs.ModLoader.Core.Api.Maker;
using CwyLs.ModLoader.Core.Api.Maker.Studio;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    public sealed class MakerStudioEditorIntegrationTests
    {
        [Theory]
        [InlineData("tha")]
        [InlineData("th-th")]
        public void Alias_language_is_canonical_in_store_live_apply_and_replay(string lang)
        {
            using var fx = new MakerProjectFixture();
            string? applied = null;
            var editor = new MakerStudioEditor((_, _) => MakerResult<MakerProjectStore>.Success(fx.Store),
                (_, code, _, _, write) => { applied = code; return new MakerSubtitleLiveResult { Saved = new MakerSaved { Path = write.Path } }; });
            var rig = new MakerRig(studio: () => new MakerStudioBackends(subtitles: editor));
            var request = Body(fx, 1, fx.Store.ReadLine("th", "one").Value!.Revision, "hello");
            var response = rig.Send(MakerHttp.Json("PUT", Route(lang, "one", "subtitle"), rig.Token, request));
            Assert.Equal(200, response.Status);
            Assert.Equal("th", applied);
            Assert.True(File.Exists(Path.Combine(fx.Root, "data/subtitles/th.json")));
            Assert.False(File.Exists(Path.Combine(fx.Root, "data/subtitles/" + lang + ".json")));
        }

        [Theory]
        [InlineData("con", "one")]
        [InlineData("th", "nul")]
        [InlineData("th", "con")]
        public void Reserved_bare_segments_return_400_before_file_access(string lang, string id)
        {
            using var fx = new MakerProjectFixture();
            var editor = Editor(fx);
            var rig = new MakerRig(studio: () => new MakerStudioBackends(subtitles: editor));
            Assert.Equal(400, rig.Send(MakerHttp.Json("PUT", Route(lang, id, "subtitle"), rig.Token, Body(fx, 1, "initial", "text"))).Status);
            Assert.Empty(Directory.GetFiles(fx.Root, "*", SearchOption.AllDirectories));
        }

        [Fact]
        public void Read_only_packs_can_read_history_glossary_and_operations_but_cannot_mutate()
        {
            using var fx = new MakerProjectFixture();
            var editor = new MakerStudioEditor((_, editing) => editing
                    ? MakerResult<MakerProjectStore>.Fail(MakerErrorCodes.ReadOnlyPack, "reference pack")
                    : MakerResult<MakerProjectStore>.Success(fx.Store),
                (_, _, _, _, _) => throw new InvalidOperationException("must not apply"));
            var rig = new MakerRig(studio: () => new MakerStudioBackends(subtitles: editor, projects: editor, workflow: editor));
            Assert.Equal(200, rig.Send(MakerHttp.Get(Route("th", "one", "history"), rig.Token)).Status);
            Assert.Equal(200, rig.Send(MakerHttp.Get("/v1/maker/studio/packs/sample.pack/glossary/th", rig.Token)).Status);
            Assert.Equal(404, rig.Send(MakerHttp.Get("/v1/maker/studio/packs/sample.pack/operations/" + fx.Operation(1), rig.Token)).Status);
            Assert.Equal(403, rig.Send(MakerHttp.Json("PUT", Route("th", "one", "subtitle"), rig.Token, Body(fx, 1, "initial", "text"))).Status);
        }

        [Fact]
        public void Failure_to_finalize_receipt_does_not_fail_a_committed_live_save()
        {
            using var fx = new MakerProjectFixture();
            var editor = new MakerStudioEditor((_, _) => MakerResult<MakerProjectStore>.Success(fx.Store),
                (_, _, _, _, _) => new MakerSubtitleLiveResult { Saved = new MakerSaved { Live = true } },
                beforeReceiptWrite: () => throw new IOException("injected receipt failure"));
            var rig = new MakerRig(studio: () => new MakerStudioBackends(subtitles: editor));
            var body = Body(fx, 1, fx.Store.ReadLine("th", "one").Value!.Revision, "committed");
            var response = rig.Send(MakerHttp.Json("PUT", Route("th", "one", "subtitle"), rig.Token, body));
            Assert.Equal(200, response.Status);
            Assert.Equal(false, ApiTestKit.Json(response)["live"]);
            Assert.Equal(response.Body, rig.Send(MakerHttp.Json("PUT", Route("th", "one", "subtitle"), rig.Token, body)).Body);
            Assert.Equal("committed", fx.Store.CurrentSubtitle("th", "one"));
        }

        [Fact]
        public async Task V1_and_v2_share_commit_apply_and_event_order_under_the_pack_gate()
        {
            using var fx = new MakerProjectFixture();
            using var firstApply = new ManualResetEventSlim();
            using var secondOpened = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var order = new List<string>();
            var calls = 0;
            var editor = new MakerStudioEditor((_, _) =>
            {
                if (Interlocked.Increment(ref calls) == 2) secondOpened.Set();
                return MakerResult<MakerProjectStore>.Success(new MakerProjectStore(fx.Root, fx.Clock));
            }, (_, _, id, _, write) =>
            {
                order.Add("apply:" + id);
                if (id == "one") { firstApply.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(5))); }
                return new MakerSubtitleLiveResult { Saved = new MakerSaved { Live = true, Path = write.Path } };
            }, (type, _) => order.Add(type));
            var secondRevision = fx.Store.ReadLine("th", "two").Value!.Revision;
            var v1 = Task.Factory.StartNew(() => editor.SaveV1SubtitleAsync("sample.pack", "th", "one", "v1", default),
                default, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
            Assert.True(firstApply.Wait(TimeSpan.FromSeconds(2)));
            var v2 = Task.Factory.StartNew(() => editor.SaveSubtitleAsync(new MakerStudioSubtitleRequest
            {
                PackId = "sample.pack", Lang = "th", Id = "two", Text = "v2",
                OperationId = fx.Operation(2), ExpectedRevision = secondRevision,
            }, default), default, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
            try
            {
                Assert.True(secondOpened.Wait(TimeSpan.FromSeconds(2)));
                Assert.NotSame(v2, await Task.WhenAny(v2, Task.Delay(100)));
                Assert.Null(fx.Store.CurrentSubtitle("th", "two"));
            }
            finally { release.Set(); }
            Assert.True((await v1).Ok);
            Assert.True((await v2).Ok);
            Assert.Equal("v1", fx.Store.CurrentSubtitle("th", "one"));
            Assert.Equal("v2", fx.Store.CurrentSubtitle("th", "two"));
            Assert.Equal(new[] { "apply:one", "apply:two", "maker.history.changed" }, order);
        }

        private static MakerStudioEditor Editor(MakerProjectFixture fx) => new MakerStudioEditor(
            (_, _) => MakerResult<MakerProjectStore>.Success(fx.Store), (_, _, _, _, _) => new MakerSubtitleLiveResult());
        private static string Route(string lang, string id, string tail) =>
            "/v1/maker/studio/packs/sample.pack/languages/" + lang + "/lines/" + id + "/" + tail;
        private static string Body(MakerProjectFixture fx, int op, string revision, string text) =>
            "{\"expectedRevision\":\"" + revision + "\",\"operationId\":\"" + fx.Operation(op) + "\",\"text\":\"" + text + "\"}";

        [Theory]
        [InlineData("en")]
        [InlineData("ja")]
        public void Real_editor_refuses_builtin_subtitle_languages(string lang)
        {
            using var fx = new MakerProjectFixture();
            var editor = new MakerStudioEditor((_, _) => MakerResult<MakerProjectStore>.Success(fx.Store),
                (_, _, _, _, _) => new MakerSubtitleLiveResult { Saved = new MakerSaved { Live = true } });
            var rig = new MakerRig(studio: () => new MakerStudioBackends(subtitles: editor));
            var revision = fx.Store.ReadLine(lang, "one").Value!.Revision;
            var response = rig.Send(MakerHttp.Json("PUT",
                "/v1/maker/studio/packs/sample.pack/languages/" + lang + "/lines/one/subtitle", rig.Token,
                "{\"expectedRevision\":\"" + revision + "\",\"operationId\":\"" + fx.Operation(1) + "\",\"text\":\"hello\"}"));
            Assert.Equal(400, response.Status);
            Assert.False(File.Exists(Path.Combine(fx.Root, "data", "subtitles", lang + ".json")));
        }

        [Fact]
        public void Dropped_reply_replays_exact_final_live_outcome_without_applying_again()
        {
            using var fx = new MakerProjectFixture();
            var applies = 0;
            var editor = new MakerStudioEditor((_, _) => MakerResult<MakerProjectStore>.Success(fx.Store),
                (_, _, _, _, write) =>
                {
                    applies++;
                    Assert.Equal("new text", fx.Store.CurrentSubtitle("th", "one"));
                    return new MakerSubtitleLiveResult
                    {
                        EffectivePackId = "sample.pack",
                        Saved = new MakerSaved { Live = true, Path = write.Path, Note = "Applied in game." },
                    };
                });
            var rig = new MakerRig(studio: () => new MakerStudioBackends(subtitles: editor));
            var revision = fx.Store.ReadLine("th", "one").Value!.Revision;
            var body = "{\"expectedRevision\":\"" + revision + "\",\"operationId\":\"" + fx.Operation(1) + "\",\"text\":\"new text\"}";
            const string Path = "/v1/maker/studio/packs/sample.pack/languages/th/lines/one/subtitle";
            var first = rig.Send(MakerHttp.Json("PUT", Path, rig.Token, body));
            var retry = rig.Send(MakerHttp.Json("PUT", Path, rig.Token, body));
            Assert.Equal(200, first.Status);
            Assert.Equal(first.Body, retry.Body);
            Assert.Equal("sample.pack", ApiTestKit.Json(first)["effectivePackId"]);
            Assert.Equal(1, applies);
        }

        [Fact]
        public void Take_routes_enforce_line_existence_dubbability_and_canonical_language()
        {
            using var fx = new MakerProjectFixture();
            var editor = new MakerStudioEditor(
                (_, _) => MakerResult<MakerProjectStore>.Success(fx.Store),
                (_, _, _, _, _) => new MakerSubtitleLiveResult(),
                lineVoice: (lang, id, _) =>
                {
                    if (id == "missing_line")
                        return Task.FromResult(MakerResult<string?>.Fail(MakerErrorCodes.NotFound, "no such line"));
                    if (id == "text_only_line")
                        return Task.FromResult(MakerResult<string?>.Success(null));
                    return Task.FromResult(MakerResult<string?>.Success("Voice_" + id));
                });
            var rig = new MakerRig(studio: () => new MakerStudioBackends(dubs: editor));

            // Listing needs no catalog (REVIEW-FIXES-M5c item 5): a line without take records lists as empty.
            Assert.Equal(200, rig.Send(MakerHttp.Get("/v1/maker/studio/packs/sample.pack/languages/th/lines/missing_line/takes", rig.Token)).Status);
            var take = "{\"expectedRevision\":\"x\",\"operationId\":\"" + fx.Operation(9)
                + "\",\"origin\":\"own\",\"consent\":{\"confirmed\":true,\"statementVersion\":\"consent-1\"}}";

            // Creating a take on an unknown line -> 404 not_found
            var missing = rig.Send(MakerHttp.Json("POST", "/v1/maker/studio/packs/sample.pack/languages/th/lines/missing_line/takes", rig.Token, take));
            Assert.Equal(404, missing.Status);
            Assert.Equal(MakerErrorCodes.NotFound, ApiTestKit.ErrorCode(missing));

            // Creating a take on a text-only line (voice == null) -> 400 bad_request
            var textOnly = rig.Send(MakerHttp.Json("POST", "/v1/maker/studio/packs/sample.pack/languages/th/lines/text_only_line/takes", rig.Token, take));
            Assert.Equal(400, textOnly.Status);
            Assert.Equal(MakerErrorCodes.BadRequest, ApiTestKit.ErrorCode(textOnly));

            // Dubbable line with language alias "tha" -> canonicalised to "th" in .maker/lines/th/one.json
            var rev = fx.Store.ReadLine("th", "one").Value!.Revision;
            var createBody = "{\"expectedRevision\":\"" + rev + "\",\"operationId\":\"" + fx.Operation(1)
                + "\",\"origin\":\"own\",\"consent\":{\"confirmed\":true,\"statementVersion\":\"consent-1\"}}";
            var created = rig.Send(MakerHttp.Json("POST", "/v1/maker/studio/packs/sample.pack/languages/tha/lines/one/takes", rig.Token, createBody));
            Assert.Equal(201, created.Status);
            Assert.True(File.Exists(Path.Combine(fx.Root, ".maker/lines/th/one.json")));
            Assert.False(File.Exists(Path.Combine(fx.Root, ".maker/lines/tha/one.json")));

            // M5d: publish is installed; a made-up clip revision is a stale one (409 conflict, refetch).
            var takeObj = Assert.IsType<CwyLs.ModLoader.Core.Json.JsonObject>(ApiTestKit.Json(created)["take"]);
            var takeId = Assert.IsType<string>(takeObj["id"]);
            var pub = rig.Send(MakerHttp.Json("POST", "/v1/maker/studio/packs/sample.pack/languages/th/lines/one/publish", rig.Token,
                "{\"expectedRevision\":\"" + ApiTestKit.Json(created)["revision"] + "\",\"expectedClipRevision\":\"clip1\",\"operationId\":\""
                + fx.Operation(2) + "\",\"takeId\":\"" + takeId + "\"}"));
            Assert.Equal(409, pub.Status);
            Assert.Equal(MakerStudioErrorCodes.Conflict, ApiTestKit.ErrorCode(pub));
        }

        [Fact]
        public async Task Studio_play_handles_original_dub_override_preview_and_refusals()
        {
            using var fx = new MakerProjectFixture();
            var activity = new MakerGameActivitySnapshot { Focused = true, Paused = false, Busy = false };
            var effectivePack = "sample.pack";
            var playCalls = 0;
            MakerStudioPlayRequest? lastPlay = null;
            var editor = new MakerStudioEditor(
                (packId, _) => packId == "pending.pack"
                    ? MakerResult<MakerProjectStore>.Fail(MakerStudioErrorCodes.RestartRequired, "restart required")
                    : packId == "sample.pack"
                        ? MakerResult<MakerProjectStore>.Success(fx.Store)
                        : MakerResult<MakerProjectStore>.Fail(MakerErrorCodes.NotFound, "no such pack"),
                (_, _, _, _, _) => new MakerSubtitleLiveResult(),
                lineVoice: (_, id, _) =>
                {
                    if (id == "missing_line") return Task.FromResult(MakerResult<string?>.Fail(MakerErrorCodes.NotFound, "no such line"));
                    if (id == "text_only_line") return Task.FromResult(MakerResult<string?>.Success(null));
                    return Task.FromResult(MakerResult<string?>.Success("Voice_" + id));
                },
                applyVoice: (_, _, _, write, _, _) => Task.FromResult(new MakerVoiceLiveResult
                {
                    Saved = new MakerSaved { Live = true, Path = write.Path, Note = "Published." },
                    EffectivePackId = effectivePack,
                }),
                activity: () => activity,
                play: (req, ct) =>
                {
                    ct.ThrowIfCancellationRequested();
                    Interlocked.Increment(ref playCalls);
                    lastPlay = req;
                    var note = req.Variant == "original"
                        ? "Satone is saying the game's own line."
                        : effectivePack != req.PackId
                            ? "Satone is previewing " + req.PackId + "'s " + req.Lang + " dub in the game; " + effectivePack + " loads later and wins in normal play."
                            : "Satone is saying the " + req.Lang + " dub in the game.";
                    return Task.FromResult(MakerResult<MakerVoiceLiveResult>.Success(new MakerVoiceLiveResult
                    {
                        Saved = new MakerSaved { Live = true, Path = null, DurationSec = 1.25, Note = note },
                        EffectivePackId = effectivePack,
                    }));
                });
            var rig = new MakerRig(studio: () => new MakerStudioBackends(dubs: editor, playback: editor));

            var caps = ApiTestKit.Json(rig.Send(MakerHttp.Get("/v1/maker/studio/capabilities", rig.Token)));
            Assert.Contains("playback", Assert.IsAssignableFrom<IList<object?>>(caps["features"]));

            var rev0 = fx.Store.ReadLine("th", "one").Value!.Revision;

            // Dub play before any dub file exists in sample.pack -> 404 not_found
            var noDub = rig.Send(MakerHttp.Json("POST", "/v1/maker/studio/play", rig.Token,
                "{\"id\":\"one\",\"variant\":\"dub\",\"lang\":\"th\",\"packId\":\"sample.pack\",\"expectedRevision\":\"" + rev0 + "\"}"));
            Assert.Equal(404, noDub.Status);
            Assert.Equal(MakerErrorCodes.NotFound, ApiTestKit.ErrorCode(noDub));
            Assert.Equal(0, playCalls);

            // Create, upload source + render, and publish a take on line "one"
            const string lineBase = "/v1/maker/studio/packs/sample.pack/languages/th/lines/one";
            var created = ApiTestKit.Json(rig.Send(MakerHttp.Json("POST", lineBase + "/takes", rig.Token,
                "{\"expectedRevision\":\"" + rev0 + "\",\"operationId\":\"" + fx.Operation(1) + "\",\"origin\":\"own\",\"consent\":{\"confirmed\":true,\"statementVersion\":\"consent-1\"}}")));
            var takeId = (string)((CwyLs.ModLoader.Core.Json.JsonObject)created["take"]!)["id"]!;
            var src = ApiTestKit.Json(rig.Send(MakerHttp.Build("PUT", lineBase + "/takes/" + takeId + "/audio?kind=source&expectedRevision=" + created["revision"] + "&operationId=" + fx.Operation(2),
                rig.Token, TestWav.Tone(0.25, 24000, 1), "audio/wav")));
            var rnd = ApiTestKit.Json(rig.Send(MakerHttp.Build("PUT", lineBase + "/takes/" + takeId + "/audio?kind=render&expectedRevision=" + src["revision"] + "&operationId=" + fx.Operation(3),
                rig.Token, TestWav.Tone(0.25, 48000, 1), "audio/wav")));
            var takes = ApiTestKit.Json(rig.Send(MakerHttp.Get(lineBase + "/takes", rig.Token)));
            var published = ApiTestKit.Json(rig.Send(MakerHttp.Json("POST", lineBase + "/publish", rig.Token,
                "{\"expectedRevision\":\"" + takes["revision"] + "\",\"expectedClipRevision\":\"" + takes["clipRevision"] + "\",\"operationId\":\"" + fx.Operation(4) + "\",\"takeId\":\"" + takeId + "\"}")));
            var revPublished = (string)published["revision"]!;
            var txBefore = Directory.GetFiles(Path.Combine(fx.Root, ".maker/transactions")).Length;

            // Play dub with canonicalised alias language "tha"
            var playDub = rig.Send(MakerHttp.Json("POST", "/v1/maker/studio/play", rig.Token,
                "{\"id\":\"one\",\"variant\":\"dub\",\"lang\":\"tha\",\"packId\":\"sample.pack\",\"expectedRevision\":\"" + revPublished + "\"}"));
            Assert.Equal(200, playDub.Status);
            var dubJson = ApiTestKit.Json(playDub);
            Assert.Equal(revPublished, dubJson["revision"]);
            Assert.Equal(string.Empty, dubJson["operationId"]);
            Assert.Equal(false, dubJson["saved"]);
            Assert.Equal(true, dubJson["live"]);
            Assert.Null(dubJson["path"]);
            Assert.Equal(takeId, dubJson["publishedTakeId"]);
            Assert.Equal("sample.pack", dubJson["effectivePackId"]);
            Assert.Equal(false, dubJson["restartRequired"]);
            Assert.Equal("th", lastPlay!.Lang);
            Assert.Equal(txBefore, Directory.GetFiles(Path.Combine(fx.Root, ".maker/transactions")).Length);

            // Play original when unfocused with unknown pause (paused == null) succeeds; when another pack wins, effectivePackId + note name the winner
            effectivePack = "override.pack";
            activity = new MakerGameActivitySnapshot { Focused = false, Paused = null, Busy = false };
            var playOverride = rig.Send(MakerHttp.Json("POST", "/v1/maker/studio/play", rig.Token,
                "{\"id\":\"one\",\"variant\":\"dub\",\"lang\":\"th\",\"packId\":\"sample.pack\",\"expectedRevision\":\"" + revPublished + "\"}"));
            Assert.Equal(200, playOverride.Status);
            var overrideJson = ApiTestKit.Json(playOverride);
            Assert.Equal("override.pack", overrideJson["effectivePackId"]);
            Assert.Contains("override.pack", (string)overrideJson["note"]!);

            var playOriginal = rig.Send(MakerHttp.Json("POST", "/v1/maker/studio/play", rig.Token,
                "{\"id\":\"one\",\"variant\":\"original\",\"lang\":\"th\",\"packId\":\"sample.pack\",\"expectedRevision\":\"" + revPublished + "\"}"));
            Assert.Equal(200, playOriginal.Status);
            Assert.Equal(false, ApiTestKit.Json(playOriginal)["saved"]);

            // Refusals: stale expectedRevision -> 409 conflict; pending pack -> 409 restart_required; paused -> 409 game_paused; busy -> 409 not_ready; text-only -> 400 bad_request; missing -> 404 not_found
            var stale = rig.Send(MakerHttp.Json("POST", "/v1/maker/studio/play", rig.Token,
                "{\"id\":\"one\",\"variant\":\"dub\",\"lang\":\"th\",\"packId\":\"sample.pack\",\"expectedRevision\":\"" + rev0 + "\"}"));
            Assert.Equal(409, stale.Status);
            Assert.Equal(MakerStudioErrorCodes.Conflict, ApiTestKit.ErrorCode(stale));

            var pending = rig.Send(MakerHttp.Json("POST", "/v1/maker/studio/play", rig.Token,
                "{\"id\":\"one\",\"variant\":\"dub\",\"lang\":\"th\",\"packId\":\"pending.pack\"}"));
            Assert.Equal(409, pending.Status);
            Assert.Equal(MakerStudioErrorCodes.RestartRequired, ApiTestKit.ErrorCode(pending));

            var callsBeforePause = playCalls;
            activity = new MakerGameActivitySnapshot { Focused = false, Paused = true, Busy = false, Reason = "game_paused" };
            var paused = rig.Send(MakerHttp.Json("POST", "/v1/maker/studio/play", rig.Token,
                "{\"id\":\"one\",\"variant\":\"original\",\"lang\":\"th\"}"));
            Assert.Equal(409, paused.Status);
            Assert.Equal(MakerStudioErrorCodes.GamePaused, ApiTestKit.ErrorCode(paused));
            Assert.Equal(callsBeforePause, playCalls);

            activity = new MakerGameActivitySnapshot { Focused = true, Paused = false, Busy = true, Reason = "satone_speaking" };
            var busy = rig.Send(MakerHttp.Json("POST", "/v1/maker/studio/play", rig.Token,
                "{\"id\":\"one\",\"variant\":\"original\",\"lang\":\"th\"}"));
            Assert.Equal(409, busy.Status);
            Assert.Equal(MakerErrorCodes.NotReady, ApiTestKit.ErrorCode(busy));

            // Even while busy, a wrong line id is 404 and a text-only line is 400
            Assert.Equal(404, rig.Send(MakerHttp.Json("POST", "/v1/maker/studio/play", rig.Token,
                "{\"id\":\"missing_line\",\"variant\":\"original\",\"lang\":\"th\"}")).Status);
            Assert.Equal(400, rig.Send(MakerHttp.Json("POST", "/v1/maker/studio/play", rig.Token,
                "{\"id\":\"text_only_line\",\"variant\":\"original\",\"lang\":\"th\"}")).Status);

            // Cancelled request does not invoke host play
            activity = new MakerGameActivitySnapshot { Focused = true, Paused = false, Busy = false };
            var callsBeforeCancel = playCalls;
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            var cancelled = await editor.PlayAsync(new MakerStudioPlayRequest { Id = "one", Variant = "original", Lang = "th" }, cts.Token);
            Assert.False(cancelled.Ok);
            Assert.Equal(MakerErrorCodes.Unavailable, cancelled.Error);
            Assert.Equal(callsBeforeCancel, playCalls);
        }
    }
}
