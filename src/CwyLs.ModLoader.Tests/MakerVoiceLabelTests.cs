using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CwyLs.ModLoader.Core.Api;
using CwyLs.ModLoader.Core.Api.Maker;
using CwyLs.ModLoader.Core.Api.Maker.Editor;
using CwyLs.ModLoader.Core.Api.Maker.Studio;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// D-148 (audit F5 + IG-04, T-111): export refuses a dub without a valid voice label, and one click labels every such dub
    /// with the pack's voice default. The pack, its lines and its audio are invented (generated tones).
    /// </summary>
    public sealed class MakerVoiceLabelTests
    {
        private const string Pack = "sample.pack";
        private const string StudioClip = "Voice_Studio_001";
        private const string StudioPath = "audio/voice/th/" + StudioClip + ".wav";
        private const string HandPath = "audio/voice/th/Voice_Hand_001.wav";

        [Fact]
        public void A_hand_dropped_wav_blocks_export_naming_the_count_and_the_file()
        {
            using var rig = new Rig();
            rig.Fx.Store.Files.WriteAtomic(HandPath, TestWav.Tone(0.4, 48000, 1));

            var check = rig.ProvenanceCheck();

            Assert.Equal((MakerExportSeverity.Error, "provUnlabelled"), (check.Severity, check.Code));
            Assert.Equal(1L, check.Params["n"]);
            Assert.Equal(new[] { HandPath }, check.Paths.ToArray());
            Assert.Contains("1 dub(s)", check.Message);
        }

        [Fact]
        public void A_hand_dropped_ogg_blocks_export_too()
        {
            using var rig = new Rig();
            rig.Fx.Store.Files.WriteAtomic("audio/voice/th/Voice_Hand_002.ogg", new byte[] { 0x4f, 0x67, 0x67, 0x53, 1, 2, 3 });

            var check = rig.ProvenanceCheck();

            Assert.Equal((MakerExportSeverity.Error, "provUnlabelled"), (check.Severity, check.Code));
            Assert.Equal(new[] { "audio/voice/th/Voice_Hand_002.ogg" }, check.Paths.ToArray());
        }

        /// <summary>IG-04 / F5(a): an entry with an empty or unknown origin, an old statement or no confirmation is no label.</summary>
        [Theory]
        [InlineData("", "consent-1", "2026-11-17T00:00:00Z", true)]
        [InlineData("human", "consent-1", "2026-11-17T00:00:00Z", true)]
        [InlineData("own", "consent-0", "2026-11-17T00:00:00Z", true)]
        [InlineData("own", "consent-1", "", true)]
        [InlineData("own", "consent-1", "2026-11-17T00:00:00Z", false)]
        public void An_entry_with_an_invalid_origin_statement_confirmation_or_hash_blocks_export(string origin, string statement, string confirmedAt, bool sameBytes)
        {
            using var rig = new Rig();
            var wav = TestWav.Tone(0.4, 48000, 1);
            rig.Fx.Store.Files.WriteAtomic(HandPath, wav);
            rig.WriteLabel("Voice_Hand_001", origin, statement, confirmedAt, MakerVoiceProvenance.Sha256(sameBytes ? wav : new byte[] { 1 }));

            var check = rig.ProvenanceCheck();

            Assert.Equal((MakerExportSeverity.Error, "provUnlabelled"), (check.Severity, check.Code));
            Assert.Equal(new[] { HandPath }, check.Paths.ToArray());
        }

        [Fact]
        public void A_valid_label_on_a_hand_dropped_wav_passes()
        {
            using var rig = new Rig();
            var wav = TestWav.Tone(0.4, 48000, 1);
            rig.Fx.Store.Files.WriteAtomic(HandPath, wav);
            rig.WriteLabel("Voice_Hand_001", "ai-assisted", "consent-1", "2026-11-17T00:00:00Z", MakerVoiceProvenance.Sha256(wav));

            Assert.Equal((MakerExportSeverity.Ok, "provCount"), (rig.ProvenanceCheck().Severity, rig.ProvenanceCheck().Code));
        }

        [Fact]
        public async Task A_dub_saved_through_the_v1_voice_route_over_a_studio_publication_blocks_export()
        {
            using var rig = new Rig();
            await rig.PublishStudio();
            Assert.Equal(MakerExportSeverity.Ok, rig.ProvenanceCheck().Severity);

            // The v1 voice route: the host writes the WAV, then drops the Studio disclosure that no longer describes it.
            MakerStores.SaveVoice(rig.Fx.Store.Files, "th", StudioClip, TestWav.Tone(0.9, 48000, 1));
            rig.Editor.ForgetV1Voice(Pack, "th", StudioClip);

            var check = rig.ProvenanceCheck();
            Assert.Equal((MakerExportSeverity.Error, "provUnlabelled"), (check.Severity, check.Code));
            Assert.Equal(new[] { StudioPath }, check.Paths.ToArray());
        }

        [Fact]
        public async Task One_click_labels_every_unlabelled_dub_with_the_pack_default_and_export_passes()
        {
            using var rig = new Rig();
            await rig.PublishStudio();
            var studioLabel = rig.Entries().Single();
            var files = rig.Fx.Store.Files;
            files.WriteAtomic(HandPath, TestWav.Tone(0.4, 48000, 1));                                  // hand-dropped WAV
            files.WriteAtomic("audio/voice/th/Voice_Hand_002.ogg", MakerAudioFixtures.OggVorbis(48000, 24000)); // hand-dropped OGG
            var v1 = TestWav.Tone(0.6, 48000, 1);
            MakerStores.SaveVoice(files, "th", "Voice_V1_003", v1);                                    // v1 voice route
            rig.Editor.ForgetV1Voice(Pack, "th", "Voice_V1_003");
            var empty = TestWav.Tone(0.5, 48000, 1);
            files.WriteAtomic("audio/voice/th/Voice_Empty_004.wav", empty);                            // empty origin (IG-04)
            var entries = rig.Entries();
            entries.Add(new MakerProvenanceEntry
            {
                Lang = "th", Clip = "Voice_Empty_004", Origin = "", StatementVersion = "consent-1", ConfirmedAt = "x",
                Sha256 = MakerVoiceProvenance.Sha256(empty), PublishedAt = "x",
            });
            files.WriteAtomic(MakerStudioPaths.Provenance, MakerVoiceProvenance.Write(entries));
            Assert.Equal(4L, rig.ProvenanceCheck().Params["n"]);
            var vd = await rig.SetDefault("ai-assisted");
            rig.Fx.Clock.Advance(300);
            rig.Events = new List<(string, IReadOnlyDictionary<string, object?>)>();

            var labelled = await rig.Label(vd.Revision, "ai-assisted", 50);

            Assert.True(labelled.Ok, labelled.Message);
            Assert.Equal(4, labelled.Value!.Labelled);
            Assert.Equal(new[] { "audio/voice/th/Voice_Empty_004.wav", "audio/voice/th/Voice_Hand_001.wav", "audio/voice/th/Voice_Hand_002.ogg", "audio/voice/th/Voice_V1_003.wav" },
                labelled.Value.Paths.OrderBy(p => p, StringComparer.Ordinal).ToArray());
            var after = rig.Entries();
            Assert.Equal(5, after.Count);
            foreach (var entry in after.Where(e => e.Clip != StudioClip))
            {
                Assert.Equal(("ai-assisted", MakerStudioContract.ConsentVersion), (entry.Origin, entry.StatementVersion));
                Assert.Equal(MakerProjectRevision.At(rig.Fx.Clock.Now), entry.ConfirmedAt); // the click is the confirmation
            }
            Assert.Equal(MakerVoiceProvenance.Sha256(v1), after.Single(e => e.Clip == "Voice_V1_003").Sha256);
            var studio = after.Single(e => e.Clip == StudioClip);                              // the Studio publication follows its take
            Assert.Equal((studioLabel.Origin, studioLabel.ConfirmedAt, studioLabel.Sha256), (studio.Origin, studio.ConfirmedAt, studio.Sha256));
            Assert.Equal((MakerExportSeverity.Ok, "provCount"), (rig.ProvenanceCheck().Severity, rig.ProvenanceCheck().Code));
            Assert.Contains(rig.Events, e => e.Item1 == "maker.pack.changed" && (string?)e.Item2["reason"] == "voice-label");

            // The whole export goes through.
            rig.Fx.Write("manifest.json", "{\"id\":\"sample.pack\",\"name\":\"Sample\",\"version\":\"0.1.0\",\"author\":\"Lab\",\"loaderVersion\":\"0.9.2\"}");
            var exports = Path.Combine(Path.GetDirectoryName(rig.Fx.Root)!, "exports");
            var exporter = new MakerPackExporter((_, _) => MakerResult<MakerProjectStore>.Success(rig.Fx.Store), _ => null, () => exports);
            var preview = await exporter.CheckAsync(Pack, default);
            Assert.True(preview.Ok, preview.Message);
            Assert.True(preview.Value!.ErrorCount == 0, string.Join("; ", preview.Value.Checks.Where(c => c.Severity == MakerExportSeverity.Error).Select(c => c.Id + ": " + c.Message)));
            var exported = await exporter.ExportAsync(new MakerExportRequest
            {
                PackId = Pack, OperationId = rig.Op(60), ExpectedVersion = "0.1.0", Bump = "none",
            }, default);
            Assert.True(exported.Ok, exported.Message);

            // A second click finds nothing left to label and writes nothing.
            var before = File.ReadAllBytes(Path.Combine(rig.Fx.Root, MakerStudioPaths.Provenance));
            var again = await rig.Label(vd.Revision, "ai-assisted", 61);
            Assert.True(again.Ok, again.Message);
            Assert.Equal(0, again.Value!.Labelled);
            Assert.Equal(before, File.ReadAllBytes(Path.Combine(rig.Fx.Root, MakerStudioPaths.Provenance)));
        }

        [Fact]
        public async Task Without_a_pack_default_nothing_is_labelled_silently()
        {
            using var rig = new Rig();
            rig.Fx.Store.Files.WriteAtomic(HandPath, TestWav.Tone(0.4, 48000, 1));
            var none = await rig.Editor.VoiceDefaultAsync(Pack, default);
            Assert.True(none.Ok, none.Message);
            Assert.Null(none.Value!.Origin);

            var refused = await rig.Label(none.Value.Revision, "own", 10);

            Assert.Equal(MakerStudioErrorCodes.ConsentRequired, refused.Error);
            Assert.False(File.Exists(Path.Combine(rig.Fx.Root, MakerStudioPaths.Provenance)));
            Assert.Equal("provUnlabelled", rig.ProvenanceCheck().Code);
        }

        [Fact]
        public async Task Labelling_refuses_a_stale_or_different_default_and_an_unconfirmed_click()
        {
            using var rig = new Rig();
            rig.Fx.Store.Files.WriteAtomic(HandPath, TestWav.Tone(0.4, 48000, 1));
            var vd = await rig.SetDefault("own");

            Assert.Equal(MakerStudioErrorCodes.Conflict, (await rig.Label("stale-revision", "own", 20)).Error);
            Assert.Equal(MakerStudioErrorCodes.Conflict, (await rig.Label(vd.Revision, "ai-generated", 21)).Error); // the page showed another origin
            Assert.Equal(MakerErrorCodes.BadRequest, (await rig.Label(vd.Revision, "own", 22, confirmed: false)).Error);
            Assert.Equal(MakerErrorCodes.BadRequest, (await rig.Label(vd.Revision, "", 23)).Error);
            Assert.False(File.Exists(Path.Combine(rig.Fx.Root, MakerStudioPaths.Provenance)));
        }

        [Fact]
        public async Task A_studio_publication_whose_take_lost_consent_is_not_relabelled_with_the_default()
        {
            using var rig = new Rig();
            var takeId = await rig.PublishStudio();
            var page = await rig.Page();
            var withdrawn = await rig.Editor.UpdateTakeAsync(new MakerStudioUpdateTakeRequest
            {
                PackId = Pack, Lang = "th", Id = "one", TakeId = takeId, ExpectedRevision = page.Revision, OperationId = rig.Op(30),
                Consent = new MakerStudioConsent { Confirmed = false, StatementVersion = MakerStudioContract.ConsentVersion },
            }, default);
            Assert.True(withdrawn.Ok, withdrawn.Message);
            var vd = await rig.SetDefault("ai-generated");

            var labelled = await rig.Label(vd.Revision, "ai-generated", 31);

            Assert.True(labelled.Ok, labelled.Message);
            Assert.Equal(0, labelled.Value!.Labelled);
            Assert.Equal(("own", MakerVoiceProvenance.Unconfirmed), (rig.Entries().Single().Origin, rig.Entries().Single().ConfirmedAt));
            Assert.Equal((MakerExportSeverity.Error, "provConsent"), (rig.ProvenanceCheck().Severity, rig.ProvenanceCheck().Code));
        }

        [Fact]
        public async Task A_retried_label_with_the_same_operation_replays_the_answer()
        {
            using var rig = new Rig();
            rig.Fx.Store.Files.WriteAtomic(HandPath, TestWav.Tone(0.4, 48000, 1));
            var vd = await rig.SetDefault("own");
            var first = await rig.Label(vd.Revision, "own", 40);
            Assert.True(first.Ok, first.Message);
            var bytes = File.ReadAllBytes(Path.Combine(rig.Fx.Root, MakerStudioPaths.Provenance));
            rig.Fx.Clock.Advance(60);

            var retry = await rig.Label(vd.Revision, "own", 40);

            Assert.True(retry.Ok, retry.Message);
            Assert.Equal(1, retry.Value!.Labelled);
            Assert.Equal(new[] { HandPath }, retry.Value.Paths.ToArray());
            Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(rig.Fx.Root, MakerStudioPaths.Provenance)));
        }

        [Fact]
        public async Task Two_files_for_one_clip_block_export_and_are_not_labelled()
        {
            using var rig = new Rig();
            var files = rig.Fx.Store.Files;
            files.WriteAtomic(HandPath, TestWav.Tone(0.4, 48000, 1));
            files.WriteAtomic("audio/voice/th/Voice_Hand_001.ogg", new byte[] { 0x4f, 0x67, 0x67, 0x53, 1 });
            var vd = await rig.SetDefault("own");

            var labelled = await rig.Label(vd.Revision, "own", 45);

            Assert.True(labelled.Ok, labelled.Message);
            Assert.Equal(new[] { HandPath }, labelled.Value!.Paths.ToArray()); // the WAV is the clip's labelled file
            var check = rig.ProvenanceCheck();
            Assert.Equal((MakerExportSeverity.Error, "provDuplicate"), (check.Severity, check.Code));
            Assert.Equal(new[] { "audio/voice/th/Voice_Hand_001.ogg" }, check.Paths.ToArray());
        }

        [Fact]
        public async Task The_label_route_is_advertised_and_answers_over_http()
        {
            using var rig = new Rig();
            rig.Fx.Store.Files.WriteAtomic(HandPath, TestWav.Tone(0.4, 48000, 1));
            var vd = await rig.SetDefault("own");
            var http = new MakerRig(studio: () => new MakerStudioBackends(voiceDefaults: rig.Editor, voiceLabels: rig.Editor));
            var features = (IList<object?>)ApiTestKit.Json(http.Send(MakerHttp.Get("/v1/maker/studio/capabilities", http.Token)))["features"]!;
            Assert.Contains("voiceLabel", features);
            const string Route = "/v1/maker/studio/packs/sample.pack/voice/label";
            string Body(string op, string consent) => "{\"expectedRevision\":\"" + vd.Revision + "\",\"operationId\":\"" + op + "\",\"origin\":\"own\",\"consent\":" + consent + "}";

            Assert.Equal(400, http.Send(MakerHttp.Json("POST", Route, http.Token, Body(rig.Op(70), "{\"confirmed\":false,\"statementVersion\":\"consent-1\"}"))).Status);
            Assert.Equal(400, http.Send(MakerHttp.Json("POST", Route, http.Token, "{\"expectedRevision\":\"" + vd.Revision + "\",\"operationId\":\"" + rig.Op(71) + "\",\"origin\":\"own\"}")).Status);
            Assert.Equal(404, http.Send(MakerHttp.Get(Route, http.Token)).Status); // the router answers a wrong method with no_route
            var reply = http.Send(MakerHttp.Json("POST", Route, http.Token, Body(rig.Op(72), "{\"confirmed\":true,\"statementVersion\":\"consent-1\"}")));

            Assert.Equal(200, reply.Status);
            var json = ApiTestKit.Json(reply);
            Assert.Equal(new[] { "operationId", "revision", "origin", "labelled", "paths" }, json.Keys);
            Assert.Equal(1L, json["labelled"]);
            Assert.Equal(MakerExportSeverity.Ok, rig.ProvenanceCheck().Severity);
            Assert.False(new MakerStudioBackends(voiceDefaults: rig.Editor).Features.Contains("voiceLabel"));
        }

        /// <summary>A pack with line <c>one</c> (voice <see cref="StudioClip"/>) and a Studio editor over it.</summary>
        private sealed class Rig : IDisposable
        {
            internal Rig()
            {
                Fx = new MakerProjectFixture();
                Editor = new MakerStudioEditor((_, _) => MakerResult<MakerProjectStore>.Success(Fx.Store),
                    (_, _, _, _, _) => new MakerSubtitleLiveResult(),
                    publish: (type, data) => Events?.Add((type, data)),
                    lineVoice: (_, id, _) => Task.FromResult(id == "one"
                        ? MakerResult<string?>.Success(StudioClip) : MakerResult<string?>.Fail(MakerErrorCodes.NotFound, "no such line")),
                    applyVoice: (pack, _, _, write, _, _) => Task.FromResult(new MakerVoiceLiveResult
                    {
                        Saved = new MakerSaved { Live = true, Path = write.Path, Note = "applied" }, EffectivePackId = pack,
                    }));
            }

            internal MakerProjectFixture Fx { get; }
            internal MakerStudioEditor Editor { get; }
            internal List<(string, IReadOnlyDictionary<string, object?>)>? Events { get; set; }

            internal string Op(int n) => Fx.Operation(n);

            internal async Task<MakerStudioTakePage> Page()
            {
                var page = await Editor.TakesAsync(Pack, "th", "one", default);
                Assert.True(page.Ok, page.Message);
                return page.Value!;
            }

            /// <summary>Creates, renders and publishes an own-voice take of line one; returns its id.</summary>
            internal async Task<string> PublishStudio()
            {
                var created = await Editor.CreateTakeAsync(new MakerStudioCreateTakeRequest
                {
                    PackId = Pack, Lang = "th", Id = "one", ExpectedRevision = Fx.Store.ReadLine("th", "one").Value!.Revision, OperationId = Op(1),
                    Origin = "own", Consent = new MakerStudioConsent { Confirmed = true, StatementVersion = MakerStudioContract.ConsentVersion },
                }, default);
                Assert.True(created.Ok, created.Message);
                var takeId = created.Value!.Take!.Id;
                var op = 2;
                foreach (var (kind, rate) in new[] { ("source", 24000), ("render", 48000) })
                {
                    var page = await Page();
                    var saved = await Editor.SaveTakeAudioAsync(new MakerStudioTakeAudioRequest
                    {
                        PackId = Pack, Lang = "th", Id = "one", TakeId = takeId, Kind = kind, ExpectedRevision = page.Revision, OperationId = Op(op++),
                    }, TestWav.Tone(0.5, rate, 1), default);
                    Assert.True(saved.Ok, saved.Message);
                }

                var current = await Page();
                var published = await Editor.PublishAsync(new MakerStudioPublishRequest
                {
                    PackId = Pack, Lang = "th", Id = "one", TakeId = takeId, ExpectedRevision = current.Revision,
                    ExpectedClipRevision = current.ClipRevision!, OperationId = Op(5),
                }, default);
                Assert.True(published.Ok, published.Message);
                return takeId;
            }

            internal async Task<MakerStudioVoiceDefault> SetDefault(string origin)
            {
                var current = await Editor.VoiceDefaultAsync(Pack, default);
                Assert.True(current.Ok, current.Message);
                var saved = await Editor.SaveVoiceDefaultAsync(new MakerStudioVoiceDefaultRequest
                {
                    PackId = Pack, ExpectedRevision = current.Value!.Revision, OperationId = Guid.NewGuid().ToString("N"), Origin = origin,
                    Consent = new MakerStudioConsent { Confirmed = true, StatementVersion = MakerStudioContract.ConsentVersion },
                }, default);
                Assert.True(saved.Ok, saved.Message);
                return saved.Value!;
            }

            internal Task<MakerResult<MakerStudioVoiceLabelled>> Label(string revision, string origin, int op, bool confirmed = true) =>
                Editor.LabelDubsAsync(new MakerStudioVoiceLabelRequest
                {
                    PackId = Pack, ExpectedRevision = revision, OperationId = Op(op), Origin = origin,
                    Consent = new MakerStudioConsent { Confirmed = confirmed, StatementVersion = MakerStudioContract.ConsentVersion },
                }, default);

            internal void WriteLabel(string clip, string origin, string statement, string confirmedAt, string sha) =>
                Fx.Store.Files.WriteAtomic(MakerStudioPaths.Provenance, MakerVoiceProvenance.Write(new[]
                {
                    new MakerProvenanceEntry
                    {
                        Lang = "th", Clip = clip, Origin = origin, StatementVersion = statement, ConfirmedAt = confirmedAt, Sha256 = sha,
                        PublishedAt = "2026-11-17T00:00:00Z",
                    },
                }));

            internal List<MakerProvenanceEntry> Entries()
            {
                var path = Path.Combine(Fx.Root, MakerStudioPaths.Provenance);
                if (!File.Exists(path)) return new List<MakerProvenanceEntry>();
                Assert.True(MakerVoiceProvenance.TryRead(File.ReadAllText(path), out var entries, out var problem), problem);
                return entries;
            }

            internal MakerExportCheck ProvenanceCheck() =>
                MakerPackChecks.Run(Fx.Store.Files, Pack, MakerPackFileSet.Walk(Fx.Root), null, out _).Single(c => c.Id == "provenance");

            public void Dispose() => Fx.Dispose();
        }
    }
}
