using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using CwyLs.ModLoader.Core.Api;
using CwyLs.ModLoader.Core.Api.Maker;
using CwyLs.ModLoader.Core.Api.Maker.Studio;
using CwyLs.ModLoader.Core.Json;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    public sealed class MakerRecoveryTests
    {
        [Fact]
        public void Keep_current_archives_both_versions_clears_the_intent_and_replays_without_live_apply()
        {
            using var fx = Blocked();
            var recovery = new MakerRecoveryStore(fx.Store);
            var preview = recovery.Preview(fx.Operation(10)).Value!;
            var request = Request(fx, preview);
            var result = recovery.Resolve(request);
            Assert.True(result.Ok, result.Message);
            Assert.False(result.Value!.Live);
            Assert.Equal("external", fx.Store.CurrentSubtitle("th", "one"));
            Assert.Contains("external", fx.Read(result.Value.ArchivePath + "/0.current.bin"));
            Assert.Contains("intended", fx.Read(result.Value.ArchivePath + "/0.intended.bin"));
            Assert.True(fx.Store.Files.Exists(result.Value.ArchivePath + "/intent.json"));
            Assert.False(fx.Store.Files.Exists(MakerStudioPaths.Pending(fx.Operation(10))));
            Assert.False(fx.Store.Files.Exists(MakerStudioPaths.Stage(fx.Operation(10), 0)));
            Assert.True(fx.Store.ReadLine("th", "one").Value!.HistoryGap);
            Assert.Equal(result.Value.Revision, recovery.Resolve(request).Value!.Revision);
            Assert.Equal("not_found", recovery.Preview(fx.Operation(10)).Error);
            Assert.Equal("conflict", recovery.Resolve(new MakerRecoveryRequest
            {
                PackId = request.PackId, BlockedOperationId = request.BlockedOperationId, OperationId = request.OperationId,
                ExpectedRecoveryRevision = new string('b', 32), Strategy = "keep-current",
            }).Error);
        }

        [Fact]
        public void Missing_current_file_is_archived_as_absent_and_stays_absent()
        {
            using var fx = Blocked();
            fx.Store.Files.Delete("data/subtitles/th.json");
            var recovery = new MakerRecoveryStore(fx.Store);
            var preview = recovery.Preview(fx.Operation(10)).Value!;
            Assert.False(preview.Resources[0].CurrentExists);
            var result = recovery.Resolve(Request(fx, preview));
            Assert.True(result.Ok, result.Message);
            Assert.False(fx.Store.Files.Exists("data/subtitles/th.json"));
            Assert.False(fx.Store.Files.Exists(result.Value!.ArchivePath + "/0.current.bin"));
            Assert.Contains("\"currentExists\":false", fx.Read(result.Value.ArchivePath + "/manifest.json"));
            Assert.True(fx.Store.ReadLine("th", "one").Ok);
            fx.Clock.Advance(24 * 60 * 60);
            Assert.Equal("not_found", recovery.Resolve(Request(fx, preview)).Error);
        }

        [Fact]
        public void Glossary_resolution_preserves_current_entries_and_assigns_a_new_revision()
        {
            using var fx = new MakerProjectFixture();
            const string path = ".maker/glossary/th.json";
            const string before = "{\"schema\":1,\"revision\":\"before\",\"entries\":[]}";
            fx.Write(path, before);
            var intended = before.Replace("before", "intended");
            var commit = new MakerCommit(fx.Store.Files, kind: "glossary", boundary: p => { if (p == "after-intent") throw new InvalidOperationException("crash"); });
            Assert.Throws<InvalidOperationException>(() => commit.Execute(fx.Operation(10), new[]
            { fx.Store.Target(path, Encoding.UTF8.GetBytes(intended)) }));
            fx.Write(path, "{\"schema\":1,\"revision\":\"external\",\"entries\":[{\"id\":\"word\",\"sourceJa\":\"ja\",\"sourceEn\":\"en\",\"target\":\"current\",\"note\":\"note\"}]}");
            var recovery = new MakerRecoveryStore(fx.Store);
            var result = recovery.Resolve(Request(fx, recovery.Preview(fx.Operation(10)).Value!));
            Assert.True(result.Ok, result.Message);
            var after = JsonReader.ParseObject(fx.Read(path));
            var entry = Assert.IsType<JsonObject>(Assert.Single(Assert.IsAssignableFrom<IList<object?>>(after["entries"])));
            Assert.Equal("current", entry["target"]);
            Assert.Equal(result.Value!.Revision, after["revision"]);
        }

        [Fact]
        public void Stale_preview_and_archive_quota_refuse_without_clearing_or_modifying_anything()
        {
            using var fx = Blocked();
            var recovery = new MakerRecoveryStore(fx.Store);
            var request = Request(fx, recovery.Preview(fx.Operation(10)).Value!);
            fx.Write("data/subtitles/th.json", "{\"one\":\"another external edit\"}");
            Assert.Equal("conflict", recovery.Resolve(request).Error);
            Assert.True(fx.Store.Files.Exists(MakerStudioPaths.Pending(fx.Operation(10))));
            var tiny = new MakerProjectStore(fx.Root, fx.Clock, fx.Store.Files.BytesUnder(".maker") + 100);
            var constrained = new MakerRecoveryStore(tiny);
            request = Request(fx, constrained.Preview(fx.Operation(10)).Value!);
            Assert.Equal("storage_limit", constrained.Resolve(request).Error);
            Assert.False(Directory.Exists(Path.Combine(fx.Root, ".maker/recovery")));
            Assert.Equal("another external edit", fx.Store.CurrentSubtitle("th", "one"));
        }

        [Fact]
        public void Unconfined_intent_is_rejected_before_archiving_or_editing()
        {
            using var fx = Blocked();
            var path = MakerStudioPaths.Pending(fx.Operation(10));
            var text = fx.Read(path).Replace("data/subtitles/th.json", "manifest.json");
            fx.Write(path, text);
            var result = new MakerRecoveryStore(fx.Store).Preview(fx.Operation(10));
            Assert.Equal("recovery_required", result.Error);
            Assert.Equal(fx.Operation(10), result.Details!.BlockedOperationId);
            Assert.False(Directory.Exists(Path.Combine(fx.Root, ".maker/recovery")));
        }

        [Fact]
        public void Edit_after_committed_resolution_does_not_block_interrupted_cleanup()
        {
            using var fx = Blocked();
            var r = new MakerRecoveryStore(fx.Store, p => { if (p.StartsWith("before-delete:", StringComparison.Ordinal)) throw new InvalidOperationException("crash"); });
            var request = Request(fx, r.Preview(fx.Operation(10)).Value!);
            Assert.Throws<InvalidOperationException>(() => r.Resolve(request));
            Assert.False(fx.Store.Files.Exists(MakerStudioPaths.Pending(fx.Operation(10))));
            fx.Write("data/subtitles/th.json", "{\"one\":\"later edit\"}");
            Assert.True(fx.Store.RecoverPending().Ok);
            Assert.Equal("later edit", fx.Store.CurrentSubtitle("th", "one"));
            Assert.True(new MakerRecoveryStore(fx.Store).Resolve(request).Ok);
        }

        [Fact]
        public void Metadata_edit_after_stages_are_cleaned_does_not_resurrect_completed_resolution()
        {
            using var fx = Blocked();
            var r = new MakerRecoveryStore(fx.Store, p =>
            {
                if (p == "before-delete:" + MakerStudioPaths.Pending(fx.Operation(20)))
                    throw new InvalidOperationException("crash");
            });
            var request = Request(fx, r.Preview(fx.Operation(10)).Value!);
            Assert.Throws<InvalidOperationException>(() => r.Resolve(request));
            const string path = ".maker/lines/th/one.json";
            var line = JsonReader.ParseObject(fx.Read(path));
            line["revision"] = "later manual metadata";
            fx.Write(path, ApiJson.Write(line));
            var result = fx.Store.RecoverPending();
            Assert.True(result.Ok, result.Message);
            Assert.Contains("later manual metadata", fx.Read(path));
            Assert.True(new MakerRecoveryStore(fx.Store).Resolve(request).Ok);
        }

        [Fact]
        public void Resolution_recovers_at_every_journal_write_and_cleanup_boundary()
        {
            var points = new List<string>();
            using (var probe = Blocked())
            {
                var r = new MakerRecoveryStore(probe.Store, points.Add);
                Assert.True(r.Resolve(Request(probe, r.Preview(probe.Operation(10)).Value!)).Ok);
            }
            Assert.Contains("after-intent", points);
            foreach (var point in points.Distinct())
            {
                using var fx = Blocked();
                var versions = Versions(fx);
                var r = new MakerRecoveryStore(fx.Store, p => { if (p == point) throw new InvalidOperationException("crash"); });
                var request = Request(fx, r.Preview(fx.Operation(10)).Value!);
                Assert.Throws<InvalidOperationException>(() => r.Resolve(request));
                var recovered = fx.Store.RecoverPending();
                if (!recovered.Ok)
                {
                    // Rolled back: the blocked operation stays blocked and keeps both staged versions.
                    Assert.Equal("recovery_required", recovered.Error);
                    Assert.Equal(fx.Operation(10), recovered.Details?.BlockedOperationId);
                    Assert.False(fx.Store.Files.Exists(MakerStudioPaths.Pending(fx.Operation(20))));
                    Assert.True(fx.Store.Files.Exists(MakerStudioPaths.Stage(fx.Operation(10), 0)), point);
                    Assert.True(fx.Store.Files.Exists(MakerStudioPaths.Stage(fx.Operation(10), 1)), point);
                }
                else
                {
                    // Rolled forward: the blocked operation is retired and its archive holds what it intended.
                    Assert.False(fx.Store.Files.Exists(MakerStudioPaths.Pending(fx.Operation(10))), point);
                    Assert.True(fx.Store.Files.Exists(Archive(fx, fx.Operation(20)) + "/0.intended.bin"), point);
                }
                AssertNoVersionLost(fx, point + " after recovery", versions);
                var replay = new MakerRecoveryStore(fx.Store).Resolve(request);
                Assert.True(replay.Ok, point + ": " + replay.Message);
                Assert.True(fx.Store.RecoverPending().Ok, point + " after replay");
                Assert.Equal("external", fx.Store.CurrentSubtitle("th", "one"));
                Assert.True(fx.Store.Files.Exists(replay.Value!.ArchivePath + "/0.intended.bin"));
                AssertNoVersionLost(fx, point + " after replay", versions);
            }
        }

        [Fact]
        public void Router_editor_resolution_preserves_access_checks_and_the_wire_shapes()
        {
            using var fx = Blocked();
            var live = 0;
            var editor = new MakerStudioEditor((pack, editing) => pack == "reference" && editing
                ? MakerResult<MakerProjectStore>.Fail("read_only_pack", "Reference pack")
                : MakerResult<MakerProjectStore>.Success(fx.Store),
                (_, _, _, _, _) => { live++; return new MakerSubtitleLiveResult(); });
            var rig = new MakerRig(studio: () => new MakerStudioBackends(projects: editor, recovery: editor));
            var path = "/v1/maker/studio/packs/sample.pack/operations/" + fx.Operation(10);
            var response = rig.Send(MakerHttp.Get(path + "/recovery", rig.Token));
            Assert.Equal(200, response.Status);
            var preview = ApiTestKit.Json(response);
            Assert.Equal(new[] { "recoveryRevision", "blockedOperationId", "resources" }, preview.Keys);
            Assert.DoesNotContain("external", response.Body); // Hashes/identifiers only, never snapshot bytes.
            var body = "{\"operationId\":\"" + fx.Operation(20) + "\",\"expectedRecoveryRevision\":\""
                + preview["recoveryRevision"] + "\",\"strategy\":\"keep-current\"}";
            Assert.Equal(401, rig.Send(MakerHttp.Get(path + "/recovery", null)).Status);
            Assert.Equal(403, rig.Send(MakerHttp.Json("POST", path.Replace("sample.pack", "reference") + "/resolve", rig.Token, body)).Status);
            Assert.Equal(400, rig.Send(MakerHttp.Json("POST", path + "/resolve", rig.Token, body.Replace("keep-current", "use-intended"))).Status);
            var resolved = rig.Send(MakerHttp.Json("POST", path + "/resolve", rig.Token, body));
            Assert.Equal(200, resolved.Status);
            Assert.Equal(false, ApiTestKit.Json(resolved)["live"]);
            Assert.Equal(0, live);
            Assert.Equal(resolved.Body, rig.Send(MakerHttp.Json("POST", path + "/resolve", rig.Token, body)).Body);
        }

        // Resolution targets in the Blocked() fixture: 0 intent, 1-4 current/intended copies, 5 archive manifest,
        // 6 subtitle table (check only), 7 the line sidecar (metadata), 8 receipt, 9-13 retirement of the blocked operation.
        [Theory]
        [InlineData("before-write:5", "the archive manifest write (before any metadata)")]
        [InlineData("before-write:7", "the line sidecar write (the first metadata write)")]
        [InlineData("after-write:7", "the receipt write, after the line sidecar was written")]
        public void Failed_resolution_followed_by_preview_and_second_resolution_recovers_and_serves_studio_get(string point, string where)
        {
            using var fx = Blocked();
            var versions = Versions(fx);
            var preview1 = new MakerRecoveryStore(fx.Store).Preview(fx.Operation(10));
            Assert.True(preview1.Ok, preview1.Message);
            var r1 = new MakerRecoveryStore(fx.Store, p =>
            {
                if (p == point) throw new IOException("injected failure before " + where);
            }).Resolve(Request(fx, preview1.Value!));
            Assert.False(r1.Ok);
            Assert.Equal("recovery_required", r1.Error);

            var preview2 = new MakerRecoveryStore(fx.Store).Preview(fx.Operation(10));
            Assert.True(preview2.Ok, preview2.Message);
            var r2Request = new MakerRecoveryRequest
            {
                PackId = "sample.pack",
                BlockedOperationId = fx.Operation(10),
                OperationId = fx.Operation(21),
                ExpectedRecoveryRevision = preview2.Value!.RecoveryRevision,
                Strategy = "keep-current",
            };
            var r2 = new MakerRecoveryStore(fx.Store).Resolve(r2Request);
            Assert.True(r2.Ok, r2.Message);

            var recovered = fx.Store.RecoverPending();
            Assert.True(recovered.Ok, recovered.Message);
            AssertNoVersionLost(fx, point, versions);
            var editor = new MakerStudioEditor((_, _) => MakerResult<MakerProjectStore>.Success(fx.Store),
                (_, _, _, _, _) => new MakerSubtitleLiveResult());
            var rig = new MakerRig(studio: () => new MakerStudioBackends(subtitles: editor, projects: editor, recovery: editor));
            var get = rig.Send(MakerHttp.Get("/v1/maker/studio/packs/sample.pack/languages/th/lines/one/history", rig.Token));
            Assert.Equal(200, get.Status);
        }

        [Fact]
        public void V1_save_after_interrupted_resolution_at_every_boundary_completes_or_leaves_blocked_operation_resolvable()
        {
            var boundaries = new List<string>();
            using (var probe = Blocked())
            {
                var p = new MakerRecoveryStore(probe.Store).Preview(probe.Operation(10)).Value!;
                Assert.True(new MakerRecoveryStore(probe.Store, boundaries.Add).Resolve(Request(probe, p)).Ok);
            }

            Assert.NotEmpty(boundaries);
            foreach (var point in boundaries)
            {
                using var fx = Blocked();
                // The v1 save below rewrites the subtitle table on purpose, so its earlier bytes are superseded, not lost.
                var versions = Versions(fx).Where(v => v.Key != "current subtitle table").ToList();
                var preview = new MakerRecoveryStore(fx.Store).Preview(fx.Operation(10)).Value!;
                var request = Request(fx, preview);
                Assert.Throws<InvalidOperationException>(() => new MakerRecoveryStore(fx.Store, p =>
                {
                    if (p == point) throw new InvalidOperationException(point);
                }).Resolve(request));

                var w = CwyLs.ModLoader.Core.Api.Maker.Editor.MakerSubtitleStore.Save(fx.Store.Files, "th", "other", "v1-edit");
                new MakerHistoryStore(fx.Store).CaptureV1("th", "other", w.Previous, w.Current);
                var recovered = fx.Store.RecoverPending();
                if (recovered.Ok)
                {
                    // Rolled forward: the blocked operation is retired and its archive holds what it intended.
                    Assert.False(fx.Store.Files.Exists(MakerStudioPaths.Pending(fx.Operation(10))), point);
                    Assert.True(fx.Store.Files.Exists(Archive(fx, fx.Operation(20)) + "/0.intended.bin"), point);
                }
                else
                {
                    Assert.Equal("recovery_required", recovered.Error);
                    Assert.Equal(fx.Operation(10), recovered.Details?.BlockedOperationId);
                    // Rolled back: the blocked operation keeps both staged versions.
                    Assert.True(fx.Store.Files.Exists(MakerStudioPaths.Stage(fx.Operation(10), 0)), point);
                    Assert.True(fx.Store.Files.Exists(MakerStudioPaths.Stage(fx.Operation(10), 1)), point);
                    AssertNoVersionLost(fx, point + " after rollback", versions);
                    var nextPreview = new MakerRecoveryStore(fx.Store).Preview(fx.Operation(10));
                    Assert.True(nextPreview.Ok, point + " preview: " + nextPreview.Message);
                    var nextResolve = new MakerRecoveryStore(fx.Store).Resolve(new MakerRecoveryRequest
                    {
                        PackId = "sample.pack",
                        BlockedOperationId = fx.Operation(10),
                        OperationId = fx.Operation(22),
                        ExpectedRecoveryRevision = nextPreview.Value!.RecoveryRevision,
                        Strategy = "keep-current",
                    });
                    Assert.True(nextResolve.Ok, point + " resolve: " + nextResolve.Message);
                    recovered = fx.Store.RecoverPending();
                }

                Assert.True(recovered.Ok, point + " recover: " + recovered.Message);
                Assert.Equal("v1-edit", fx.Store.CurrentSubtitle("th", "other"));
                AssertNoVersionLost(fx, point, versions);
            }
        }

        [Fact]
        public void Prune_never_deletes_a_receipt_whose_operation_still_has_a_pending_journal()
        {
            using var fx = Blocked();
            var versions = Versions(fx);
            var resolution = fx.Operation(20);
            // The resolution's own journal cannot be deleted (a persistent IOException at its delete, as for a locked
            // file). Every cleanup pass completes the resolution but keeps the journal, so recovery reports success
            // while the journal is still there.
            Action<string> locked = p =>
            {
                if (p == "before-delete:" + MakerStudioPaths.Pending(resolution)) throw new IOException("injected: journal locked");
            };
            var storeBoundary = typeof(MakerProjectStore).GetField("_commitBoundary",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            storeBoundary.SetValue(fx.Store, locked);
            var r = new MakerRecoveryStore(fx.Store, locked);
            var resolved = r.Resolve(Request(fx, r.Preview(fx.Operation(10)).Value!));
            Assert.True(resolved.Ok, resolved.Message);
            Assert.True(fx.Store.Files.Exists(MakerStudioPaths.Pending(resolution)));

            fx.Clock.Advance(25 * 60 * 60); // past the 24 h receipt retention
            new MakerOperationLedger(fx.Store).Prune();
            Assert.True(fx.Store.Files.Exists(MakerStudioPaths.Pending(resolution)));
            Assert.True(fx.Store.Files.Exists(MakerStudioPaths.Receipt(resolution)), "pruned a receipt whose journal is still pending");

            storeBoundary.SetValue(fx.Store, null); // the lock clears
            var recovered = fx.Store.RecoverPending();
            Assert.True(recovered.Ok, recovered.Message);
            Assert.False(fx.Store.Files.Exists(MakerStudioPaths.Pending(resolution)));
            AssertNoVersionLost(fx, "after the journal could be retired", versions);
        }

        [Fact]
        public void Resolve_against_unparseable_subtitle_table_returns_503_metadata_unavailable_not_500()
        {
            using var fx = Blocked();
            fx.Write("data/subtitles/th.json", "{broken-json");
            var editor = new MakerStudioEditor((_, _) => MakerResult<MakerProjectStore>.Success(fx.Store),
                (_, _, _, _, _) => new MakerSubtitleLiveResult());
            var rig = new MakerRig(studio: () => new MakerStudioBackends(projects: editor, recovery: editor));
            var path = "/v1/maker/studio/packs/sample.pack/operations/" + fx.Operation(10);
            var previewResp = rig.Send(MakerHttp.Get(path + "/recovery", rig.Token));
            Assert.Equal(200, previewResp.Status);
            var preview = ApiTestKit.Json(previewResp);
            var body = "{\"operationId\":\"" + fx.Operation(20) + "\",\"expectedRecoveryRevision\":\""
                + preview["recoveryRevision"] + "\",\"strategy\":\"keep-current\"}";
            var resolved = rig.Send(MakerHttp.Json("POST", path + "/resolve", rig.Token, body));
            Assert.Equal(503, resolved.Status);
            Assert.Equal("metadata_unavailable", ApiTestKit.ErrorCode(resolved));
        }

        [Fact]
        public void Take_commit_crash_at_every_boundary_recovers_cleanly_including_render_deletion()
        {
            var boundaries = new List<string>();
            using (var probe = new MakerProjectFixture())
            {
                var takes = new MakerTakeStore(probe.Store);
                var rev = probe.Store.ReadLine("th", "one").Value!.Revision;
                var created = takes.Create(new MakerStudioCreateTakeRequest
                {
                    PackId = "sample.pack", Lang = "th", Id = "one", ExpectedRevision = rev, OperationId = probe.Operation(1),
                    Origin = "own", Consent = new MakerStudioConsent { Confirmed = true, StatementVersion = "consent-1" },
                }).Value!;
                var takeId = created.Take!.Id;
                var s1 = takes.SaveAudio(new MakerStudioTakeAudioRequest
                {
                    PackId = "sample.pack", Lang = "th", Id = "one", TakeId = takeId, Kind = "source",
                    ExpectedRevision = created.Revision, OperationId = probe.Operation(2),
                }, TestWav.Tone(0.5, 24000, 1)).Value!;
                var r1 = takes.SaveAudio(new MakerStudioTakeAudioRequest
                {
                    PackId = "sample.pack", Lang = "th", Id = "one", TakeId = takeId, Kind = "render",
                    ExpectedRevision = s1.Revision, OperationId = probe.Operation(3),
                }, TestWav.Tone(0.5, 48000, 1)).Value!;

                var lineJson = JsonReader.ParseObject( probe.Read(MakerStudioPaths.Line("th", "one")) );
                var takeJson = Assert.IsType<JsonObject>(Assert.Single(Assert.IsAssignableFrom<IList<object?>>(lineJson["takes"])));
                takeJson["renderReady"] = false;
                takeJson["renderDurationSec"] = null;
                takeJson["renderFingerprint"] = null;
                takeJson["evenOutVolume"] = true;
                lineJson["revision"] = "after-even-out";
                var targets = new[]
                {
                    probe.Store.DeleteTarget(MakerStudioPaths.TakeAudio("th", "one", takeId, "render")),
                    probe.Store.Target(MakerStudioPaths.Line("th", "one"), Encoding.UTF8.GetBytes(ApiJson.Write(lineJson))),
                };
                Assert.True(new MakerCommit(probe.Store.Files, "take", boundary: boundaries.Add).Execute(probe.Operation(10), targets).Ok);
            }

            Assert.NotEmpty(boundaries);
            foreach (var point in boundaries)
            {
                using var fx = new MakerProjectFixture();
                var takes = new MakerTakeStore(fx.Store);
                var rev = fx.Store.ReadLine("th", "one").Value!.Revision;
                var created = takes.Create(new MakerStudioCreateTakeRequest
                {
                    PackId = "sample.pack", Lang = "th", Id = "one", ExpectedRevision = rev, OperationId = fx.Operation(1),
                    Origin = "own", Consent = new MakerStudioConsent { Confirmed = true, StatementVersion = "consent-1" },
                }).Value!;
                var takeId = created.Take!.Id;
                var s1 = takes.SaveAudio(new MakerStudioTakeAudioRequest
                {
                    PackId = "sample.pack", Lang = "th", Id = "one", TakeId = takeId, Kind = "source",
                    ExpectedRevision = created.Revision, OperationId = fx.Operation(2),
                }, TestWav.Tone(0.5, 24000, 1)).Value!;
                var r1 = takes.SaveAudio(new MakerStudioTakeAudioRequest
                {
                    PackId = "sample.pack", Lang = "th", Id = "one", TakeId = takeId, Kind = "render",
                    ExpectedRevision = s1.Revision, OperationId = fx.Operation(3),
                }, TestWav.Tone(0.5, 48000, 1)).Value!;

                var lineJson = JsonReader.ParseObject(fx.Read(MakerStudioPaths.Line("th", "one")));
                var takeJson = Assert.IsType<JsonObject>(Assert.Single(Assert.IsAssignableFrom<IList<object?>>(lineJson["takes"])));
                takeJson["renderReady"] = false;
                takeJson["renderDurationSec"] = null;
                takeJson["renderFingerprint"] = null;
                takeJson["evenOutVolume"] = true;
                lineJson["revision"] = "after-even-out";
                var renderRel = MakerStudioPaths.TakeAudio("th", "one", takeId, "render");
                var targets = new[]
                {
                    fx.Store.DeleteTarget(renderRel),
                    fx.Store.Target(MakerStudioPaths.Line("th", "one"), Encoding.UTF8.GetBytes(ApiJson.Write(lineJson))),
                };
                Assert.Throws<InvalidOperationException>(() => new MakerCommit(fx.Store.Files, "take", boundary: p =>
                {
                    if (p == point) throw new InvalidOperationException(point);
                }).Execute(fx.Operation(10), targets));

                var recovered = fx.Store.RecoverPending();
                Assert.True(recovered.Ok, point + ": " + recovered.Message);
                var page = takes.ReadTakes("th", "one").Value!;
                if (point == "before-intent")
                {
                    Assert.True(page.Items[0].RenderReady);
                    Assert.True(fx.Store.Files.Exists(renderRel));
                }
                else
                {
                    Assert.False(page.Items[0].RenderReady);
                    Assert.True(page.Items[0].EvenOutVolume);
                    Assert.False(fx.Store.Files.Exists(renderRel));
                }
            }
        }

        [Fact]
        public void Blocked_take_commit_previews_and_resolves_with_keep_current_reconciling_take_wavs()
        {
            using var fx = new MakerProjectFixture();
            var takes = new MakerTakeStore(fx.Store);
            var rev = fx.Store.ReadLine("th", "one").Value!.Revision;
            var created = takes.Create(new MakerStudioCreateTakeRequest
            {
                PackId = "sample.pack", Lang = "th", Id = "one", ExpectedRevision = rev, OperationId = fx.Operation(1),
                Origin = "own", Consent = new MakerStudioConsent { Confirmed = true, StatementVersion = "consent-1" },
            }).Value!;
            var takeId = created.Take!.Id;
            var s1 = takes.SaveAudio(new MakerStudioTakeAudioRequest
            {
                PackId = "sample.pack", Lang = "th", Id = "one", TakeId = takeId, Kind = "source",
                ExpectedRevision = created.Revision, OperationId = fx.Operation(2),
            }, TestWav.Tone(0.5, 24000, 1)).Value!;
            var r1 = takes.SaveAudio(new MakerStudioTakeAudioRequest
            {
                PackId = "sample.pack", Lang = "th", Id = "one", TakeId = takeId, Kind = "render",
                ExpectedRevision = s1.Revision, OperationId = fx.Operation(3),
            }, TestWav.Tone(0.5, 48000, 1)).Value!;

            var sourceRel = MakerStudioPaths.TakeAudio("th", "one", takeId, "source");
            var renderRel = MakerStudioPaths.TakeAudio("th", "one", takeId, "render");
            var lineRel = MakerStudioPaths.Line("th", "one");
            var newSource = TestWav.Tone(0.75, 24000, 1);
            var renderBytes = fx.Store.Files.ReadBytes(renderRel)!;
            var lineJson = JsonReader.ParseObject(fx.Read(lineRel));
            lineJson["revision"] = "intended-take-rev";
            var targets = new[]
            {
                fx.Store.Target(sourceRel, newSource),
                fx.Store.DeleteTarget(renderRel),
                fx.Store.Target(lineRel, Encoding.UTF8.GetBytes(ApiJson.Write(lineJson))),
            };
            var commit = new MakerCommit(fx.Store.Files, "take", boundary: p =>
            {
                if (p == "after-intent") throw new InvalidOperationException("crash");
            });
            Assert.Throws<InvalidOperationException>(() => commit.Execute(fx.Operation(10), targets));

            // External edit to source.wav while commit is pending triggers recovery_required.
            var externalSource = TestWav.Tone(0.9, 24000, 1);
            File.WriteAllBytes(Path.Combine(fx.Root, sourceRel), externalSource);

            var blockedRead = takes.ReadTakes("th", "one");
            Assert.False(blockedRead.Ok);
            Assert.Equal("recovery_required", blockedRead.Error);
            Assert.Equal(fx.Operation(10), blockedRead.Details?.BlockedOperationId);

            var recovery = new MakerRecoveryStore(fx.Store);
            var preview = recovery.Preview(fx.Operation(10));
            Assert.True(preview.Ok, preview.Message);
            Assert.Contains(preview.Value!.Resources, r => r.Resource == sourceRel && r.CurrentExists && r.IntendedExists);
            Assert.Contains(preview.Value.Resources, r => r.Resource == renderRel && r.CurrentExists && !r.IntendedExists);

            var resolved = recovery.Resolve(Request(fx, preview.Value));
            Assert.True(resolved.Ok, resolved.Message);
            // The original source was overwritten by the external edit itself; everything else is still retrievable.
            AssertNoVersionLost(fx, "take keep-current", new Dictionary<string, byte[]>
            {
                ["external source"] = externalSource, ["intended source"] = newSource, ["kept render"] = renderBytes,
            });

            var afterResolve = takes.ReadTakes("th", "one");
            Assert.True(afterResolve.Ok, afterResolve.Message);
            Assert.Equal(externalSource, takes.ReadAudio("th", "one", takeId, "source").Value!.Bytes);
            // The kept render was made from the old source, so it is not ready for the externally edited one.
            Assert.False(afterResolve.Value!.Items[0].RenderReady);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Rollback_restores_a_sidecar_whose_target_vanished_after_the_resolution_replaced_it(bool interruptedReplace)
        {
            using var fx = Blocked();
            var versions = Versions(fx);
            var resolution = fx.Operation(20);
            var r = new MakerRecoveryStore(fx.Store, p =>
            {
                if (p == "after-write:7") throw new InvalidOperationException("crash after the line sidecar write");
            });
            Assert.Throws<InvalidOperationException>(() => r.Resolve(Request(fx, r.Preview(fx.Operation(10)).Value!)));
            Assert.True(fx.Store.Files.Exists(Swap(resolution, 7)), "the displaced sidecar is held in the resolution's swap");
            var full = Path.Combine(fx.Root, Sidecar.Replace('/', Path.DirectorySeparatorChar));
            if (interruptedReplace)
                File.Move(full, full + "." + new string('c', 32) + ".maker-tmp"); // a replace cut between its two renames
            else
                File.Delete(full); // deleted by hand while the resolution was pending

            var recovered = fx.Store.RecoverPending();

            Assert.False(fx.Store.Files.Exists(MakerStudioPaths.Pending(resolution)), "rollback did not finish: " + recovered.Message);
            Assert.Equal(versions["current sidecar"], fx.Store.Files.ReadBytes(Sidecar));
            Assert.Equal("recovery_required", recovered.Error); // the blocked operation is blocked again, and resolvable
            Assert.Equal(fx.Operation(10), recovered.Details?.BlockedOperationId);
            AssertNoVersionLost(fx, "after rollback", versions);
            var preview = new MakerRecoveryStore(fx.Store).Preview(fx.Operation(10));
            Assert.True(preview.Ok, preview.Message);
            var resolved = new MakerRecoveryStore(fx.Store).Resolve(Request(fx, preview.Value!));
            Assert.True(resolved.Ok, resolved.Message);
            AssertNoVersionLost(fx, "after the second resolution", versions);
        }

        [Fact]
        public void Receipt_lost_after_the_resolution_began_retiring_rolls_forward_and_keeps_the_intent_and_archive()
        {
            using var fx = Blocked();
            var versions = Versions(fx);
            var resolution = fx.Operation(20);
            var r = new MakerRecoveryStore(fx.Store, p =>
            {
                if (p == "after-write:13") throw new InvalidOperationException("crash after the blocked intent was retired");
            });
            var request = Request(fx, r.Preview(fx.Operation(10)).Value!);
            Assert.Throws<InvalidOperationException>(() => r.Resolve(request));
            Assert.False(fx.Store.Files.Exists(MakerStudioPaths.Pending(fx.Operation(10))), "the blocked intent was moved aside");
            fx.Store.Files.Delete(MakerStudioPaths.Receipt(resolution)); // deleted by hand, or pruned by an older build

            var recovered = fx.Store.RecoverPending();

            Assert.True(recovered.Ok, recovered.Message);
            Assert.False(fx.Store.Files.Exists(MakerStudioPaths.Pending(resolution)));
            Assert.True(fx.Store.Files.Exists(Archive(fx, resolution) + "/intent.json"));
            Assert.True(fx.Store.Files.Exists(Archive(fx, resolution) + "/0.intended.bin"));
            Assert.True(fx.Store.Files.Exists(MakerStudioPaths.Receipt(resolution)), "the receipt is reinstalled from its stage");
            AssertNoVersionLost(fx, "after roll-forward", versions);
            var replay = new MakerRecoveryStore(fx.Store).Resolve(request);
            Assert.True(replay.Ok, replay.Message);
        }

        [Fact]
        public void Sidecar_edited_after_an_unfinished_resolution_wrote_it_rolls_forward_and_keeps_every_version()
        {
            using var fx = Blocked();
            var versions = Versions(fx);
            var resolution = fx.Operation(20);
            var r = new MakerRecoveryStore(fx.Store, p =>
            {
                if (p == "after-write:7") throw new InvalidOperationException("crash after the line sidecar write");
            });
            Assert.Throws<InvalidOperationException>(() => r.Resolve(Request(fx, r.Preview(fx.Operation(10)).Value!)));
            var line = JsonReader.ParseObject(fx.Read(Sidecar));
            line["revision"] = "edited after the resolution wrote it";
            fx.Write(Sidecar, ApiJson.Write(line));
            var edited = fx.Store.Files.ReadBytes(Sidecar)!;

            var recovered = fx.Store.RecoverPending();

            // Rolling back cannot restore the sidecar without destroying the newer edit, so the resolution completes:
            // the edit stays current and the pre-resolution sidecar stays in the archive.
            Assert.True(recovered.Ok, recovered.Message);
            Assert.Equal(edited, fx.Store.Files.ReadBytes(Sidecar));
            Assert.False(fx.Store.Files.Exists(MakerStudioPaths.Pending(resolution)));
            Assert.False(fx.Store.Files.Exists(MakerStudioPaths.Pending(fx.Operation(10))));
            Assert.Equal(versions["current sidecar"], fx.Store.Files.ReadBytes(Archive(fx, resolution) + "/1.current.bin"));
            AssertNoVersionLost(fx, "after roll-forward", versions);
        }

        [Fact]
        public void Rollback_puts_back_an_edit_displaced_into_its_swap_instead_of_deleting_it()
        {
            using var fx = Blocked();
            var resolution = fx.Operation(20);
            var r = new MakerRecoveryStore(fx.Store, p =>
            {
                if (p == "after-write:7") throw new InvalidOperationException("crash after the line sidecar write");
            });
            Assert.Throws<InvalidOperationException>(() => r.Resolve(Request(fx, r.Preview(fx.Operation(10)).Value!)));
            // A replacement that crashed before it put back what it displaced: the swap holds an edit made after the
            // preview (not the bytes the resolution expected), and the target holds the resolution's own sidecar.
            var line = JsonReader.ParseObject(fx.Read(Sidecar));
            line["revision"] = "edited after the preview";
            var displaced = Encoding.UTF8.GetBytes(ApiJson.Write(line));
            File.WriteAllBytes(Path.Combine(fx.Root, Swap(resolution, 7).Replace('/', Path.DirectorySeparatorChar)), displaced);
            // That edit superseded the sidecar the preview saw, so only the edit and the blocked intent must survive.
            var versions = Versions(fx).Where(v => v.Key != "current sidecar").ToList();

            var recovered = fx.Store.RecoverPending();

            Assert.Equal(displaced, fx.Store.Files.ReadBytes(Sidecar));
            Assert.False(fx.Store.Files.Exists(MakerStudioPaths.Pending(resolution)), recovered.Message);
            Assert.Equal("recovery_required", recovered.Error);
            Assert.Equal(fx.Operation(10), recovered.Details?.BlockedOperationId);
            AssertNoVersionLost(fx, "after rollback", versions);
        }

        [Fact]
        public void Rollback_deletes_a_sidecar_that_the_unfinished_resolution_created()
        {
            using var fx = Blocked();
            fx.Store.Files.Delete(Sidecar); // the resolution will create the sidecar rather than replace it
            var versions = Versions(fx);
            var resolution = fx.Operation(20);
            // With no current sidecar there is no 1.current.bin copy, so the sidecar is target 6 here, not 7.
            var r = new MakerRecoveryStore(fx.Store, p =>
            {
                if (p == "after-write:6") throw new InvalidOperationException("crash after the line sidecar write");
            });
            Assert.Throws<InvalidOperationException>(() => r.Resolve(Request(fx, r.Preview(fx.Operation(10)).Value!)));
            Assert.True(fx.Store.Files.Exists(Sidecar));
            Assert.False(fx.Store.Files.Exists(MakerStudioPaths.Receipt(resolution)), "the crash came before the receipt");
            Assert.False(fx.Store.Files.Exists(Swap(resolution, 6)), "nothing was displaced");

            var recovered = fx.Store.RecoverPending();

            Assert.False(fx.Store.Files.Exists(Sidecar));
            Assert.False(fx.Store.Files.Exists(MakerStudioPaths.Pending(resolution)), recovered.Message);
            Assert.False(Directory.Exists(Path.Combine(fx.Root, Archive(fx, resolution).Replace('/', Path.DirectorySeparatorChar))));
            Assert.True(fx.Store.Files.Exists(MakerStudioPaths.Stage(fx.Operation(10), 0)));
            Assert.True(fx.Store.Files.Exists(MakerStudioPaths.Stage(fx.Operation(10), 1)));
            Assert.Equal("recovery_required", recovered.Error);
            AssertNoVersionLost(fx, "after rollback", versions);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Request_that_finishes_an_interrupted_resolution_answers_409_conflict_and_then_404(bool preview)
        {
            using var fx = Blocked();
            var versions = Versions(fx);
            var r = new MakerRecoveryStore(fx.Store, p =>
            {
                if (p == "after-write:8") throw new InvalidOperationException("crash after the receipt write");
            });
            var first = Request(fx, r.Preview(fx.Operation(10)).Value!);
            Assert.Throws<InvalidOperationException>(() => r.Resolve(first));
            Assert.True(fx.Store.Files.Exists(MakerStudioPaths.Pending(fx.Operation(10))));
            var recovery = new MakerRecoveryStore(fx.Store);

            var error = preview
                ? recovery.Preview(fx.Operation(10)).Error
                : recovery.Resolve(new MakerRecoveryRequest
                {
                    PackId = "sample.pack", BlockedOperationId = fx.Operation(10), OperationId = fx.Operation(21),
                    ExpectedRecoveryRevision = first.ExpectedRecoveryRevision, Strategy = "keep-current",
                }).Error;

            Assert.Equal("conflict", error); // the interrupted resolution finished while answering: refetch
            Assert.False(fx.Store.Files.Exists(MakerStudioPaths.Pending(fx.Operation(10))));
            Assert.Equal("not_found", recovery.Preview(fx.Operation(10)).Error);
            Assert.True(recovery.Resolve(first).Ok); // the interrupted request itself replays its receipt
            AssertNoVersionLost(fx, "after the interrupted resolution finished", versions);
        }

        private const string Sidecar = ".maker/lines/th/one.json";

        private static string Swap(string op, int i) => MakerStudioPaths.Transactions + "/" + op + "." + i + ".swap";

        private static string Archive(MakerProjectFixture fx, string resolution) => ".maker/recovery/" + fx.Operation(10) + "/" + resolution;

        /// <summary>
        /// The versions a <see cref="Blocked"/> pack holds before any resolution: the current subtitle table and line
        /// sidecar, and the blocked operation's intended table and sidecar (its two stages). Absent files are left out.
        /// </summary>
        private static Dictionary<string, byte[]> Versions(MakerProjectFixture fx)
        {
            var versions = new Dictionary<string, byte[]>();
            void Add(string name, string path) { var bytes = fx.Store.Files.ReadBytes(path); if (bytes != null) versions[name] = bytes; }
            Add("current subtitle table", "data/subtitles/th.json");
            Add("current sidecar", Sidecar);
            Add("intended subtitle table", MakerStudioPaths.Stage(fx.Operation(10), 0));
            Add("intended sidecar", MakerStudioPaths.Stage(fx.Operation(10), 1));
            return versions;
        }

        /// <summary>
        /// No version lost: each version is still the exact content of some file in the pack, in place, in a stage or
        /// swap, or in a recovery archive.
        /// </summary>
        private static void AssertNoVersionLost(MakerProjectFixture fx, string context, IEnumerable<KeyValuePair<string, byte[]>> versions)
        {
            var present = new HashSet<string>(
                Directory.EnumerateFiles(fx.Root, "*", SearchOption.AllDirectories)
                    .Select(path => CwyLs.ModLoader.Core.Api.Maker.Editor.MakerPackFiles.Fingerprint(File.ReadAllBytes(path))),
                StringComparer.Ordinal);
            foreach (var version in versions)
            {
                Assert.True(present.Contains(CwyLs.ModLoader.Core.Api.Maker.Editor.MakerPackFiles.Fingerprint(version.Value)),
                    context + ": the " + version.Key + " is no longer retrievable");
            }
        }

        internal static MakerProjectFixture Blocked()
        {
            var fx = new MakerProjectFixture();
            var history = new MakerHistoryStore(fx.Store);
            history.Save(new MakerStudioSubtitleRequest { PackId = "sample.pack", Lang = "th", Id = "one", Text = "before",
                ExpectedRevision = fx.Store.ReadLine("th", "one").Value!.Revision, OperationId = fx.Operation(1) });
            var line = JsonReader.ParseObject(fx.Read(".maker/lines/th/one.json"));
            line["revision"] = "intended"; line["subtitleFingerprint"] = fx.Store.SubtitleFingerprint("intended");
            var targets = new[]
            {
                fx.Store.Target("data/subtitles/th.json", Encoding.UTF8.GetBytes("{\"one\":\"intended\"}")),
                fx.Store.Target(".maker/lines/th/one.json", Encoding.UTF8.GetBytes(ApiJson.Write(line))),
            };
            var commit = new MakerCommit(fx.Store.Files, "subtitle", boundary: p => { if (p == "after-intent") throw new InvalidOperationException("crash"); });
            Assert.Throws<InvalidOperationException>(() => commit.Execute(fx.Operation(10), targets));
            fx.Write("data/subtitles/th.json", "{\"one\":\"external\"}");
            return fx;
        }

        private static MakerRecoveryRequest Request(MakerProjectFixture fx, MakerRecoveryPreview preview) =>
            new MakerRecoveryRequest { PackId = "sample.pack", BlockedOperationId = fx.Operation(10),
                OperationId = fx.Operation(20), ExpectedRecoveryRevision = preview.RecoveryRevision, Strategy = "keep-current" };
    }
}
