using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CwyLs.ModLoader.Core.Api;
using CwyLs.ModLoader.Core.Api.Maker;
using CwyLs.ModLoader.Core.Api.Maker.Editor;
using CwyLs.ModLoader.Core.Api.Maker.Studio;
using CwyLs.ModLoader.Core.Json;
using CwyLs.ModLoader.Core.Packs;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>M5d (W2b-03): publish/unpublish, voice-provenance.json, shared-clip aliases, the live apply seam.</summary>
    public sealed class MakerTakePublishTests
    {
        private const string Clip = "Voice_Shared_001";
        private const string Runtime = "audio/voice/th/" + Clip + ".wav";

        [Fact]
        public async Task Publish_needs_consent_and_a_current_render_and_a_published_take_cannot_be_deleted()
        {
            using var rig = new Rig();
            var created = await rig.Editor.CreateTakeAsync(rig.Create("one", 1, confirmed: false), default);
            var takeId = created.Value!.Take!.Id;
            var page = await rig.Page("one");
            Assert.Equal(MakerStudioErrorCodes.ConsentRequired, (await rig.Publish("one", 2, takeId, page)).Error);

            var attested = await rig.Editor.UpdateTakeAsync(new MakerStudioUpdateTakeRequest
            {
                PackId = "sample.pack", Lang = "th", Id = "one", TakeId = takeId, ExpectedRevision = page.Revision, OperationId = rig.Op(3),
                Consent = new MakerStudioConsent { Confirmed = true, StatementVersion = MakerStudioContract.ConsentVersion },
            }, default);
            page = await rig.Page("one");
            Assert.Equal(MakerStudioErrorCodes.RenderRequired, (await rig.Publish("one", 4, takeId, page)).Error); // no audio at all
            await rig.Upload("one", 5, takeId, "source", TestWav.Tone(0.5, 24000, 1));
            page = await rig.Page("one");
            Assert.Equal(MakerStudioErrorCodes.RenderRequired, (await rig.Publish("one", 6, takeId, page)).Error); // source only
            await rig.Upload("one", 7, takeId, "render", TestWav.Tone(0.5, 48000, 1));
            page = await rig.Page("one");

            var published = await rig.Publish("one", 8, takeId, page);

            Assert.True(published.Ok, published.Message);
            Assert.Equal(takeId, published.Value!.PublishedTakeId);
            Assert.Equal(Runtime, published.Value.Path);
            Assert.Equal(rig.Files.ReadBytes(MakerStudioPaths.TakeAudio("th", "one", takeId, "render")), rig.Files.ReadBytes(Runtime));
            page = await rig.Page("one");
            Assert.Equal(takeId, page.PublishedTakeId);
            Assert.Equal(MakerStudioErrorCodes.TakeInUse, (await rig.Editor.DeleteTakeAsync(rig.Delete("one", 9, page.Revision, takeId), default)).Error);
            Assert.True(attested.Ok);
        }

        [Fact]
        public async Task Voice_provenance_holds_only_published_clip_disclosures_matching_the_runtime_wav()
        {
            using var rig = new Rig();
            var takeId = await rig.Rendered("one", 1);
            await rig.Rendered("two", 10, clip: "Voice_Unpublished_002"); // a take that is never published

            Assert.True((await rig.Publish("one", 20, takeId, await rig.Page("one"))).Ok);

            var text = File.ReadAllText(Path.Combine(rig.Fx.Root, MakerStudioPaths.Provenance));
            Assert.True(MakerVoiceProvenance.TryRead(text, out var entries, out var problem), problem);
            var entry = Assert.Single(entries);
            Assert.Equal(("th", Clip, "own", MakerStudioContract.ConsentVersion), (entry.Lang, entry.Clip, entry.Origin, entry.StatementVersion));
            Assert.Equal(MakerVoiceProvenance.Sha256(rig.Files.ReadBytes(Runtime)!), entry.Sha256);
            Assert.False(string.IsNullOrEmpty(entry.ConfirmedAt));
            Assert.False(string.IsNullOrEmpty(entry.PublishedAt));
            var root = JsonReader.ParseObject(text);
            Assert.Equal(new[] { "schema", "clips" }, root.Keys);
            var clip = Assert.IsType<JsonObject>(Assert.Single((IList<object?>)root["clips"]!));
            Assert.Equal(new[] { "lang", "clip", "origin", "statementVersion", "confirmedAt", "sha256", "publishedAt" }, clip.Keys);
            Assert.DoesNotContain(takeId, text);                // no take ids
            Assert.DoesNotContain(".maker", text);              // no creator paths
            Assert.DoesNotContain(rig.Fx.Root, text);           // no machine paths
            Assert.DoesNotContain("Voice_Unpublished_002", text);
            Assert.Equal(PackContentKind.Other, PackContentIndex.Classify(MakerStudioPaths.Provenance));
        }

        [Fact]
        public async Task Draft_edits_after_publication_leave_the_published_wav_and_provenance_pinned()
        {
            using var rig = new Rig();
            var takeId = await rig.Rendered("one", 1);
            Assert.True((await rig.Publish("one", 5, takeId, await rig.Page("one"))).Ok);
            var runtime = rig.Files.ReadBytes(Runtime);
            var provenance = rig.Files.ReadBytes(MakerStudioPaths.Provenance);

            var page = await rig.Page("one");
            var trimmed = await rig.Editor.UpdateTakeAsync(new MakerStudioUpdateTakeRequest
            {
                PackId = "sample.pack", Lang = "th", Id = "one", TakeId = takeId, ExpectedRevision = page.Revision, OperationId = rig.Op(6),
                HasTrimStartSec = true, TrimStartSec = 0.1,
            }, default);
            Assert.True(trimmed.Ok, trimmed.Message);
            await rig.Upload("one", 7, takeId, "source", TestWav.Tone(0.7, 24000, 1));

            page = await rig.Page("one");
            Assert.False(page.Items[0].RenderReady);       // the draft needs a new render
            Assert.Equal(takeId, page.PublishedTakeId);     // the snapshot stays published
            Assert.Equal(runtime, rig.Files.ReadBytes(Runtime));
            Assert.Equal(provenance, rig.Files.ReadBytes(MakerStudioPaths.Provenance));
            Assert.Equal(MakerStudioErrorCodes.RenderRequired, (await rig.Publish("one", 8, takeId, page)).Error);
        }

        /// <summary>IG-01: the Save origin action (origin + fresh consent) on a published take relabels its disclosure.</summary>
        [Fact]
        public async Task Correcting_a_published_takes_origin_with_fresh_consent_relabels_its_disclosure_and_export()
        {
            using var rig = new Rig();
            var takeId = await rig.Rendered("one", 1);
            Assert.True((await rig.Publish("one", 5, takeId, await rig.Page("one"))).Ok);
            Assert.Equal("own", rig.Disclosure().Origin);

            var corrected = await rig.SetOrigin("one", 6, takeId, "ai-generated", consent: true);

            Assert.True(corrected.Ok, corrected.Message);
            var entry = rig.Disclosure();
            Assert.Equal("ai-generated", entry.Origin);
            Assert.Equal(corrected.Value!.Take!.Consent.ConfirmedAt, entry.ConfirmedAt);
            Assert.Equal(MakerVoiceProvenance.Sha256(rig.Files.ReadBytes(Runtime)!), entry.Sha256); // the WAV is untouched
            var check = rig.ProvenanceCheck();
            Assert.Equal((MakerExportSeverity.Ok, "provCount"), (check.Severity, check.Code));
        }

        /// <summary>IG-01: an origin change without fresh consent, or withdrawn consent, blocks export of that publication.</summary>
        [Theory]
        [InlineData("origin")]
        [InlineData("withdraw")]
        public async Task A_published_take_without_consent_after_a_change_is_disclosed_unconfirmed_and_export_refuses_it(string change)
        {
            using var rig = new Rig();
            var takeId = await rig.Rendered("one", 1);
            Assert.True((await rig.Publish("one", 5, takeId, await rig.Page("one"))).Ok);

            var page = await rig.Page("one");
            var updated = await rig.Editor.UpdateTakeAsync(new MakerStudioUpdateTakeRequest
            {
                PackId = "sample.pack", Lang = "th", Id = "one", TakeId = takeId, ExpectedRevision = page.Revision, OperationId = rig.Op(6),
                Origin = change == "origin" ? "ai-generated" : null,
                Consent = change == "withdraw" ? new MakerStudioConsent { Confirmed = false, StatementVersion = MakerStudioContract.ConsentVersion } : null,
            }, default);

            Assert.True(updated.Ok, updated.Message);
            var entry = rig.Disclosure();
            Assert.Equal(change == "origin" ? "ai-generated" : "own", entry.Origin);
            Assert.Equal(MakerVoiceProvenance.Unconfirmed, entry.ConfirmedAt);
            var check = rig.ProvenanceCheck();
            Assert.Equal((MakerExportSeverity.Error, "provConsent"), (check.Severity, check.Code));
            Assert.Equal(new[] { Runtime }, check.Paths.ToArray());

            // Confirming again (no republish) clears the block with the take's current origin.
            var confirmed = await rig.SetOrigin("one", 7, takeId, change == "origin" ? "ai-generated" : "own", consent: true);
            Assert.True(confirmed.Ok, confirmed.Message);
            Assert.Equal(confirmed.Value!.Take!.Consent.ConfirmedAt, rig.Disclosure().ConfirmedAt);
            Assert.Equal(MakerExportSeverity.Ok, rig.ProvenanceCheck().Severity);
        }

        [Fact]
        public async Task Lines_sharing_a_clip_share_its_revision_and_publication()
        {
            using var rig = new Rig();
            var a = await rig.Rendered("line_a", 1);
            var b = await rig.Rendered("line_b", 10);
            var pageA = await rig.Page("line_a");
            var pageB = await rig.Page("line_b");
            Assert.Equal(pageA.ClipRevision, pageB.ClipRevision);
            Assert.NotNull(pageA.ClipRevision);

            Assert.True((await rig.Publish("line_a", 20, a, pageA)).Ok);
            var staleB = await rig.Publish("line_b", 21, b, pageB);
            Assert.Equal(MakerStudioErrorCodes.Conflict, staleB.Error);

            pageA = await rig.Page("line_a");
            pageB = await rig.Page("line_b");
            Assert.Equal(pageA.ClipRevision, pageB.ClipRevision);
            Assert.NotNull(pageB.ClipRevision);
            Assert.True((await rig.Publish("line_b", 22, b, pageB)).Ok);

            pageA = await rig.Page("line_a");
            pageB = await rig.Page("line_b");
            Assert.Equal(pageA.ClipRevision, pageB.ClipRevision);
            Assert.Null(pageA.PublishedTakeId);  // line_b's take replaced it
            Assert.Equal(b, pageB.PublishedTakeId);
            Assert.True((await rig.Editor.DeleteTakeAsync(rig.Delete("line_a", 23, pageA.Revision, a), default)).Ok);
            var record = new MakerClipStore(rig.Fx.Store).Read("th", Clip).Publication!;
            Assert.Equal(("line_b", b), (record.LineId, record.TakeId));
        }

        [Fact]
        public async Task Publish_moves_a_pre_existing_dub_aside_and_unpublish_restores_it_without_a_disclosure()
        {
            using var rig = new Rig();
            var original = new byte[] { 0x4f, 0x67, 0x67, 0x53, 1, 2, 3, 4 }; // a creator's own .ogg, made before Studio
            rig.Fx.Store.Files.WriteAtomic("audio/voice/th/" + Clip + ".ogg", original);
            var takeId = await rig.Rendered("one", 1);

            var published = await rig.Publish("one", 5, takeId, await rig.Page("one"));

            Assert.True(published.Ok, published.Message);
            Assert.False(rig.Files.Exists("audio/voice/th/" + Clip + ".ogg"));
            Assert.Equal(original, rig.Files.ReadBytes("_maker_backup/audio/voice/th/" + Clip + ".ogg"));
            Assert.True(rig.Files.Exists(Runtime));

            var unpublished = await rig.Unpublish("one", 6, await rig.Page("one"));

            Assert.True(unpublished.Ok, unpublished.Message);
            Assert.Equal(original, rig.Files.ReadBytes("audio/voice/th/" + Clip + ".ogg"));
            Assert.False(rig.Files.Exists(Runtime));
            Assert.False(rig.Files.Exists("_maker_backup/audio/voice/th/" + Clip + ".ogg"));
            Assert.True(MakerVoiceProvenance.TryRead(File.ReadAllText(Path.Combine(rig.Fx.Root, MakerStudioPaths.Provenance)), out var entries, out _));
            Assert.Empty(entries); // the restored pre-Studio file is never attributed to consent-1
            Assert.Null((await rig.Page("one")).PublishedTakeId);
            Assert.False(rig.Files.Exists(MakerStudioPaths.Clip("th", Clip)));
        }

        [Fact]
        public async Task An_edit_outside_Studio_advances_the_clip_revision_and_unpublish_moves_that_dub_aside()
        {
            using var rig = new Rig();
            var takeId = await rig.Rendered("one", 1);
            Assert.True((await rig.Publish("one", 5, takeId, await rig.Page("one"))).Ok);
            var before = await rig.Page("one");
            var outside = TestWav.Tone(0.9, 22050, 1);
            File.WriteAllBytes(Path.Combine(rig.Fx.Root, Runtime), outside); // a v1 upload or a hand-copied file

            var after = await rig.Page("one");
            Assert.NotEqual(before.ClipRevision, after.ClipRevision);
            Assert.Null(after.PublishedTakeId);            // external/unknown, not Studio's take
            Assert.Equal(MakerStudioErrorCodes.Conflict, (await rig.Unpublish("one", 6, before)).Error);

            var removed = await rig.Unpublish("one", 7, after); // W3 parity gap b: v2 removes a dub made outside Studio

            Assert.True(removed.Ok, removed.Message);
            Assert.False(rig.Files.Exists(Runtime));
            Assert.Equal(outside, rig.Files.ReadBytes("_maker_backup/" + Runtime)); // moved aside, never deleted
            Assert.True(MakerVoiceProvenance.TryRead(File.ReadAllText(Path.Combine(rig.Fx.Root, MakerStudioPaths.Provenance)), out var entries, out _));
            Assert.Empty(entries);
        }

        [Fact]
        public async Task V1_voice_save_over_a_publication_drops_its_disclosure_and_record()
        {
            using var rig = new Rig();
            var takeId = await rig.Rendered("one", 1);
            Assert.True((await rig.Publish("one", 5, takeId, await rig.Page("one"))).Ok);
            MakerVoiceStore.Save(rig.Files, "th", Clip, TestWav.Tone(0.4, 16000, 1)); // what the v1 route writes

            rig.Editor.ForgetV1Voice("sample.pack", "th", Clip);

            Assert.True(MakerVoiceProvenance.TryRead(File.ReadAllText(Path.Combine(rig.Fx.Root, MakerStudioPaths.Provenance)), out var entries, out _));
            Assert.Empty(entries);
            Assert.False(rig.Files.Exists(MakerStudioPaths.Clip("th", Clip)));
            Assert.Null((await rig.Page("one")).PublishedTakeId);
        }

        [Fact]
        public async Task Publish_applies_the_voice_live_finalises_the_receipt_and_replays_without_applying_again()
        {
            using var rig = new Rig();
            var takeId = await rig.Rendered("one", 1);
            var events = new List<(string Type, IReadOnlyDictionary<string, object?> Data)>();
            rig.Events = events;
            var page = await rig.Page("one");
            var request = rig.PublishRequest("one", 5, takeId, page);

            var first = await rig.Editor.PublishAsync(request, default);
            var replay = await rig.Editor.PublishAsync(rig.PublishRequest("one", 5, takeId, page), default);

            Assert.True(first.Ok, first.Message);
            Assert.True(first.Value!.Live);
            Assert.Equal("sample.pack", first.Value.EffectivePackId);
            Assert.Equal(1, rig.LiveApplies);
            Assert.Equal(rig.Files.ReadBytes(Runtime), rig.LastLiveBytes);
            Assert.True(replay.Ok, replay.Message);
            Assert.Equal(1, rig.LiveApplies); // a replay never re-applies live state
            Assert.True(replay.Value!.Live);    // the finalised receipt
            Assert.Equal(first.Value.Revision, replay.Value.Revision);
            var published = Assert.Single(events, e => e.Type == "maker.take.published");
            Assert.Equal(new[] { "packId", "lang", "id", "clip", "takeId", "revision", "live" }, published.Data.Keys);
            Assert.Equal(Clip, published.Data["clip"]);
        }

        [Theory]
        [InlineData("publish")]
        [InlineData("unpublish")]
        public async Task Crash_at_every_boundary_of_publish_or_unpublish_recovers_and_the_retry_completes(string flow)
        {
            var points = new List<string>();
            using (var probe = new Rig())
            {
                await probe.Prepare(flow);
                Seam(probe, points.Add);
                Assert.True((await probe.Flow(flow)).Ok);
            }

            Assert.Contains("before-intent", points);
            foreach (var point in points.Distinct())
            {
                using var rig = new Rig();
                var original = await rig.Prepare(flow);
                Seam(rig, p => { if (p == point) throw new InvalidOperationException(point); });
                await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Flow(flow));
                Seam(rig, null);

                Assert.True(rig.Fx.Store.RecoverPending().Ok, point);
                var retry = await rig.Flow(flow);
                Assert.True(retry.Ok, point + ": " + retry.Message);
                var ogg = "audio/voice/th/" + Clip + ".ogg";
                if (flow == "publish")
                {
                    Assert.True(rig.Files.Exists(Runtime), point);
                    Assert.Equal(original, rig.Files.ReadBytes("_maker_backup/" + ogg));
                }
                else
                {
                    Assert.False(rig.Files.Exists(Runtime), point);
                    Assert.Equal(original, rig.Files.ReadBytes(ogg));
                }
            }
        }

        [Fact]
        public async Task A_blocked_publish_resolves_with_keep_current()
        {
            using var rig = new Rig();
            var takeId = await rig.Rendered("one", 1);
            Seam(rig, p => { if (p == "after-intent") throw new InvalidOperationException("crash"); });
            await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Publish("one", 5, takeId, null));
            Seam(rig, null);
            var outside = TestWav.Tone(0.3, 16000, 1);
            rig.Files.WriteAtomic(Runtime, outside); // an external file appears in the clip's place
            var blocked = rig.Fx.Store.RecoverPending();
            Assert.Equal("recovery_required", blocked.Error);

            var recovery = new MakerRecoveryStore(rig.Fx.Store);
            var preview = recovery.Preview(blocked.Details!.BlockedOperationId);
            Assert.True(preview.Ok, preview.Message);
            var resolved = recovery.Resolve(new MakerRecoveryRequest
            {
                PackId = "sample.pack", BlockedOperationId = blocked.Details.BlockedOperationId, OperationId = rig.Op(90),
                ExpectedRecoveryRevision = preview.Value!.RecoveryRevision, Strategy = "keep-current",
            });

            Assert.True(resolved.Ok, resolved.Message);
            Assert.Equal(outside, rig.Files.ReadBytes(Runtime)); // current files kept
            Assert.True(rig.Fx.Store.RecoverPending().Ok);
            Assert.Null((await rig.Page("one")).PublishedTakeId);
        }

        [Fact]
        public async Task Router_publish_answers_studio_saved_and_capabilities_advertise_dubs()
        {
            using var rig = new Rig();
            var takeId = await rig.Rendered("one", 1);
            var page = await rig.Page("one");
            var http = new MakerRig(studio: () => new MakerStudioBackends(dubs: rig.Editor));
            var features = (IList<object?>)ApiTestKit.Json(http.Send(MakerHttp.Get("/v1/maker/studio/capabilities", http.Token)))["features"]!;
            Assert.Contains("dubs", features);
            var body = "{\"expectedRevision\":\"" + page.Revision + "\",\"expectedClipRevision\":\"" + page.ClipRevision
                + "\",\"operationId\":\"" + rig.Op(5) + "\",\"takeId\":\"" + takeId + "\"}";

            var reply = http.Send(MakerHttp.Json("POST", "/v1/maker/studio/packs/sample.pack/languages/th/lines/one/publish", http.Token, body));

            Assert.Equal(200, reply.Status);
            Assert.Equal(new[] { "revision", "operationId", "saved", "live", "path", "durationSec", "note", "publishedTakeId", "effectivePackId", "restartRequired" },
                ApiTestKit.Json(reply).Keys);
            var takes = ApiTestKit.Json(http.Send(MakerHttp.Get("/v1/maker/studio/packs/sample.pack/languages/th/lines/one/takes", http.Token)));
            Assert.Equal(new[] { "revision", "favouriteTakeId", "publishedTakeId", "clipRevision", "items" }, takes.Keys);
            Assert.Equal(takeId, takes["publishedTakeId"]);
        }

        private static void Seam(Rig rig, Action<string>? boundary) =>
            typeof(MakerProjectStore).GetField("_commitBoundary", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .SetValue(rig.Fx.Store, boundary);

        /// <summary>A pack with lines one, line_a and line_b sharing <see cref="Clip"/>, and a recording live-apply seam.</summary>
        private sealed class Rig : IDisposable
        {
            private readonly Dictionary<string, string> _clips = new Dictionary<string, string>
            {
                ["one"] = Clip, ["line_a"] = Clip, ["line_b"] = Clip,
            };

            internal Rig()
            {
                Fx = new MakerProjectFixture();
                Editor = new MakerStudioEditor((_, _) => MakerResult<MakerProjectStore>.Success(Fx.Store),
                    (_, _, _, _, _) => new MakerSubtitleLiveResult(),
                    publish: (type, data) => Events?.Add((type, data)),
                    lineVoice: (_, id, _) => Task.FromResult(_clips.TryGetValue(id, out var clip)
                        ? MakerResult<string?>.Success(clip) : MakerResult<string?>.Fail(MakerErrorCodes.NotFound, "no such line")),
                    applyVoice: (pack, lang, clip, write, bytes, _) =>
                    {
                        LiveApplies++;
                        LastLiveBytes = bytes;
                        return Task.FromResult(new MakerVoiceLiveResult
                        {
                            Saved = new MakerSaved { Live = true, Path = write.Path, Note = "applied" }, EffectivePackId = pack,
                        });
                    });
            }

            internal MakerProjectFixture Fx { get; }
            internal MakerStudioEditor Editor { get; }
            internal MakerPackFiles Files => Fx.Store.Files;
            internal List<(string Type, IReadOnlyDictionary<string, object?> Data)>? Events { get; set; }
            internal int LiveApplies { get; private set; }
            internal byte[]? LastLiveBytes { get; private set; }
            private string? _take;
            private byte[]? _original;
            private MakerStudioTakePage? _page;

            internal string Op(int n) => Fx.Operation(n);

            internal MakerStudioCreateTakeRequest Create(string id, int op, bool confirmed = true) => new MakerStudioCreateTakeRequest
            {
                PackId = "sample.pack", Lang = "th", Id = id, ExpectedRevision = Fx.Store.ReadLine("th", id).Value!.Revision, OperationId = Op(op),
                Origin = "own", Consent = new MakerStudioConsent { Confirmed = confirmed, StatementVersion = MakerStudioContract.ConsentVersion },
            };

            internal MakerStudioDeleteTakeRequest Delete(string id, int op, string revision, string takeId) => new MakerStudioDeleteTakeRequest
            {
                PackId = "sample.pack", Lang = "th", Id = id, TakeId = takeId, ExpectedRevision = revision, OperationId = Op(op),
            };

            internal async Task<MakerStudioTakePage> Page(string id)
            {
                var page = await Editor.TakesAsync("sample.pack", "th", id, default);
                Assert.True(page.Ok, page.Message);
                return page.Value!;
            }

            internal async Task Upload(string id, int op, string takeId, string kind, byte[] wav)
            {
                var page = await Page(id);
                var saved = await Editor.SaveTakeAudioAsync(new MakerStudioTakeAudioRequest
                {
                    PackId = "sample.pack", Lang = "th", Id = id, TakeId = takeId, Kind = kind, ExpectedRevision = page.Revision, OperationId = Op(op),
                }, wav, default);
                Assert.True(saved.Ok, saved.Message);
            }

            internal async Task<string> Rendered(string id, int op, string? clip = null)
            {
                if (clip != null) _clips[id] = clip;
                var created = await Editor.CreateTakeAsync(Create(id, op), default);
                Assert.True(created.Ok, created.Message);
                var takeId = created.Value!.Take!.Id;
                await Upload(id, op + 1, takeId, "source", TestWav.Tone(0.5, 24000, 1));
                await Upload(id, op + 2, takeId, "render", TestWav.Tone(0.5, 48000, 1));
                return takeId;
            }

            internal MakerStudioPublishRequest PublishRequest(string id, int op, string takeId, MakerStudioTakePage page) => new MakerStudioPublishRequest
            {
                PackId = "sample.pack", Lang = "th", Id = id, TakeId = takeId, ExpectedRevision = page.Revision,
                ExpectedClipRevision = page.ClipRevision!, OperationId = Op(op),
            };

            internal async Task<MakerResult<MakerStudioSaved>> Publish(string id, int op, string takeId, MakerStudioTakePage? page) =>
                await Editor.PublishAsync(PublishRequest(id, op, takeId, page ?? await Page(id)), default);

            internal async Task<MakerResult<MakerStudioSaved>> Unpublish(string id, int op, MakerStudioTakePage page) =>
                await Editor.UnpublishAsync(new MakerStudioUnpublishRequest
                {
                    PackId = "sample.pack", Lang = "th", Id = id, ExpectedRevision = page.Revision, ExpectedClipRevision = page.ClipRevision!, OperationId = Op(op),
                }, default);

            internal async Task<MakerResult<MakerStudioTakeSaved>> SetOrigin(string id, int op, string takeId, string origin, bool consent)
            {
                var page = await Page(id);
                return await Editor.UpdateTakeAsync(new MakerStudioUpdateTakeRequest
                {
                    PackId = "sample.pack", Lang = "th", Id = id, TakeId = takeId, ExpectedRevision = page.Revision, OperationId = Op(op),
                    Origin = origin, Consent = consent ? new MakerStudioConsent { Confirmed = true, StatementVersion = MakerStudioContract.ConsentVersion } : null,
                }, default);
            }

            /// <summary>The single disclosure in <c>voice-provenance.json</c>.</summary>
            internal MakerProvenanceEntry Disclosure()
            {
                Assert.True(MakerVoiceProvenance.TryRead(File.ReadAllText(Path.Combine(Fx.Root, MakerStudioPaths.Provenance)), out var entries, out var problem), problem);
                return Assert.Single(entries);
            }

            /// <summary>The export's provenance check on the pack folder as it is now.</summary>
            internal MakerExportCheck ProvenanceCheck() =>
                MakerPackChecks.Run(Files, "sample.pack", MakerPackFileSet.Walk(Fx.Root), null, out _).Single(c => c.Id == "provenance");

            /// <summary>A pre-Studio .ogg in the clip's place and a rendered take; for unpublish, also published.</summary>
            internal async Task<byte[]> Prepare(string flow)
            {
                _original = new byte[] { 0x4f, 0x67, 0x67, 0x53, 9, 8, 7 };
                Fx.Store.Files.WriteAtomic("audio/voice/th/" + Clip + ".ogg", _original);
                _take = await Rendered("one", 1);
                if (flow == "unpublish") Assert.True((await Publish("one", 5, _take, null)).Ok);
                return _original;
            }

            /// <summary>The flow under test, retried with the same operation id after a crash.</summary>
            internal async Task<MakerResult<MakerStudioSaved>> Flow(string flow)
            {
                var page = _page ??= await Page("one"); // the retry repeats the original request exactly
                return flow == "publish"
                    ? await Editor.PublishAsync(PublishRequest("one", 40, _take!, page), default)
                    : await Unpublish("one", 40, page);
            }

            public void Dispose() => Fx.Dispose();
        }
    }
}
