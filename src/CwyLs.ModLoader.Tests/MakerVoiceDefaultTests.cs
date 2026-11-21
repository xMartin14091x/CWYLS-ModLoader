using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CwyLs.ModLoader.Core.Api;
using CwyLs.ModLoader.Core.Api.Maker;
using CwyLs.ModLoader.Core.Api.Maker.Editor;
using CwyLs.ModLoader.Core.Api.Maker.Studio;
using CwyLs.ModLoader.Core.Json;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>Contract v2.4 (Phase B1) + v2.5: the pack's voice origin and consent, set once; inheriting takes and their published disclosures follow it.</summary>
    public sealed class MakerVoiceDefaultTests
    {
        private const string Pack = "sample.pack";
        private const string Clip = "Voice_Shared_001";
        private const string Clip2 = "Voice_Shared_002";
        private const string Runtime = "audio/voice/th/" + Clip + ".wav";

        [Fact]
        public async Task Get_without_a_default_has_no_origin_and_a_stable_initial_revision()
        {
            using var rig = new Rig();
            var first = await rig.Editor.VoiceDefaultAsync(Pack, default);
            var second = await rig.Editor.VoiceDefaultAsync(Pack, default);

            Assert.True(first.Ok, first.Message);
            Assert.Null(first.Value!.Origin);
            Assert.Null(first.Value.Consent);
            Assert.Null(first.Value.UpdatedAt);
            Assert.StartsWith("initial-", first.Value.Revision);
            Assert.Equal(first.Value.Revision, second.Value!.Revision);
            Assert.False(rig.Files.Exists(MakerStudioPaths.VoiceDefault));
        }

        [Fact]
        public async Task Put_sets_the_default_and_get_returns_it_and_an_identical_put_is_a_no_op()
        {
            using var rig = new Rig();
            var initial = await rig.Revision();
            var saved = await rig.Save(initial, 1, "own");

            Assert.True(saved.Ok, saved.Message);
            Assert.Equal("own", saved.Value!.Origin);
            Assert.True(saved.Value.Consent!.Confirmed);
            Assert.Equal(MakerStudioContract.ConsentVersion, saved.Value.Consent.StatementVersion);
            Assert.Equal(rig.Iso(), saved.Value.Consent.ConfirmedAt);
            Assert.Equal(rig.Iso(), saved.Value.UpdatedAt);
            Assert.NotEqual(initial, saved.Value.Revision);

            var read = await rig.Editor.VoiceDefaultAsync(Pack, default);
            Assert.Equal((saved.Value.Revision, "own", saved.Value.Consent.ConfirmedAt), (read.Value!.Revision, read.Value.Origin, read.Value.Consent!.ConfirmedAt));
            var stored = JsonReader.ParseObject(rig.Fx.Read(MakerStudioPaths.VoiceDefault));
            Assert.Equal(new[] { "schema", "revision", "origin", "consent", "updatedAt", "inherited" }, stored.Keys.ToArray()); // v2.5 storage

            rig.Fx.Clock.Advance(60);
            var again = await rig.Save(saved.Value.Revision, 2, "own");
            Assert.True(again.Ok, again.Message);
            Assert.Equal(saved.Value.Revision, again.Value!.Revision);              // same revision
            Assert.Equal(saved.Value.Consent.ConfirmedAt, again.Value.Consent!.ConfirmedAt); // confirmedAt kept

            var changed = await rig.Save(again.Value.Revision, 3, "ai-assisted");
            Assert.Equal("ai-assisted", changed.Value!.Origin);
            Assert.Equal(rig.Iso(), changed.Value.Consent!.ConfirmedAt);           // a new origin is a new confirmation
        }

        [Fact]
        public async Task A_stale_expected_revision_is_a_conflict()
        {
            using var rig = new Rig();
            var initial = await rig.Revision();
            Assert.True((await rig.Save(initial, 1, "own")).Ok);

            var stale = await rig.Save(initial, 2, "ai-generated");

            Assert.Equal(MakerStudioErrorCodes.Conflict, stale.Error);
            Assert.Equal("own", (await rig.Editor.VoiceDefaultAsync(Pack, default)).Value!.Origin);
        }

        [Fact]
        public async Task A_replayed_operation_returns_the_same_response_and_a_reused_id_with_another_body_conflicts()
        {
            using var rig = new Rig();
            var initial = await rig.Revision();
            var first = await rig.Save(initial, 1, "own");
            rig.Fx.Clock.Advance(30);

            var replay = await rig.Save(initial, 1, "own");
            var reused = await rig.Save(initial, 1, "ai-generated");

            Assert.True(replay.Ok, replay.Message);
            Assert.Equal(ApiJson.Write(MakerStudioJson.VoiceDefault(first.Value!)), ApiJson.Write(MakerStudioJson.VoiceDefault(replay.Value!)));
            Assert.Equal(MakerStudioErrorCodes.Conflict, reused.Error);
        }

        [Fact]
        public async Task Clearing_keeps_the_file_with_null_origin_and_consent_and_a_new_revision()
        {
            using var rig = new Rig();
            var set = await rig.Save(await rig.Revision(), 1, "own");
            rig.Fx.Clock.Advance(5);

            var cleared = await rig.Save(set.Value!.Revision, 2, null);

            Assert.True(cleared.Ok, cleared.Message);
            Assert.Null(cleared.Value!.Origin);
            Assert.Null(cleared.Value.Consent);
            Assert.Equal(rig.Iso(), cleared.Value.UpdatedAt);
            Assert.NotEqual(set.Value.Revision, cleared.Value.Revision);
            var stored = JsonReader.ParseObject(rig.Fx.Read(MakerStudioPaths.VoiceDefault));
            Assert.Null(stored["origin"]);
            Assert.Null(stored["consent"]);
            Assert.Equal(cleared.Value.Revision, stored["revision"]);
            Assert.Equal(cleared.Value.Revision, (await rig.Revision()));
        }

        [Fact]
        public async Task Unconfirmed_or_half_sent_consent_is_refused()
        {
            using var rig = new Rig();
            var initial = await rig.Revision();

            var unconfirmed = await rig.Save(initial, 1, "own", confirmed: false);
            var originOnly = await rig.Editor.SaveVoiceDefaultAsync(new MakerStudioVoiceDefaultRequest
            {
                PackId = Pack, ExpectedRevision = initial, OperationId = rig.Op(2), Origin = "own",
            }, default);

            Assert.Equal(MakerErrorCodes.BadRequest, unconfirmed.Error);
            Assert.Equal(MakerErrorCodes.BadRequest, originOnly.Error);
            Assert.False(rig.Files.Exists(MakerStudioPaths.VoiceDefault));
        }

        [Fact]
        public async Task A_take_without_origin_and_consent_needs_a_pack_default()
        {
            using var rig = new Rig();

            var created = await rig.Editor.CreateTakeAsync(rig.Inherit("one", 1), default);

            Assert.Equal(MakerStudioErrorCodes.ConsentRequired, created.Error);
            Assert.Equal("set the pack's voice origin and consent first, or send origin and consent with the take", created.Message);
            Assert.Empty((await rig.Page("one")).Items);
        }

        [Fact]
        public async Task An_inheriting_take_follows_every_default_change_and_keeps_the_last_one_when_cleared()
        {
            using var rig = new Rig();
            var set = await rig.Save(await rig.Revision(), 1, "own");
            var packConfirmedAt = set.Value!.Consent!.ConfirmedAt;
            rig.Fx.Clock.Advance(120);

            var created = await rig.Editor.CreateTakeAsync(rig.Inherit("one", 2), default);

            Assert.True(created.Ok, created.Message);
            var take = created.Value!.Take!;
            Assert.Equal(("own", true, MakerStudioContract.ConsentVersion, packConfirmedAt, "pack"),
                (take.Origin, take.Consent.Confirmed, take.Consent.StatementVersion, take.Consent.ConfirmedAt, take.ConsentSource));
            Assert.Equal("pack", MakerStudioJson.Take(take)["consentSource"]);
            Assert.Contains("\"consentSource\":\"pack\"", rig.Fx.Read(MakerStudioPaths.Line("th", "one")));

            rig.Fx.Clock.Advance(60);
            rig.Events.Clear();
            var changed = await rig.Save(set.Value.Revision, 3, "ai-generated");
            Assert.True(changed.Ok, changed.Message);
            var page = await rig.Page("one");
            var followed = Assert.Single(page.Items);
            Assert.Equal(("ai-generated", true, MakerStudioContract.ConsentVersion, changed.Value!.Consent!.ConfirmedAt, "pack"),
                (followed.Origin, followed.Consent.Confirmed, followed.Consent.StatementVersion, followed.Consent.ConfirmedAt, followed.ConsentSource));
            Assert.NotEqual(packConfirmedAt, followed.Consent.ConfirmedAt);
            Assert.Equal(created.Value.Revision, page.Revision); // resolved when read: the line itself is not rewritten
            Assert.Equal(new[] { "maker.pack.changed:voice-default" }, rig.Events);

            rig.Events.Clear();
            var cleared = await rig.Save(changed.Value.Revision, 4, null);
            Assert.True(cleared.Ok, cleared.Message);
            var kept = (await rig.Page("one")).Items[0];
            Assert.Equal(("ai-generated", changed.Value.Consent.ConfirmedAt, "pack"), (kept.Origin, kept.Consent.ConfirmedAt, kept.ConsentSource));
            Assert.Equal(new[] { "maker.pack.changed:voice-default" }, rig.Events);

            rig.Fx.Clock.Advance(30);
            var again = await rig.Save(cleared.Value!.Revision, 5, "ai-assisted");
            Assert.True(again.Ok, again.Message);
            Assert.Equal(("ai-assisted", again.Value!.Consent!.ConfirmedAt), ((await rig.Page("one")).Items[0].Origin, (await rig.Page("one")).Items[0].Consent.ConfirmedAt));

            rig.Events.Clear();
            Assert.True((await rig.Save(again.Value.Revision, 6, "ai-assisted")).Ok); // identical: no-op, no event
            Assert.Empty(rig.Events);
        }

        [Fact]
        public async Task A_default_change_updates_a_published_inheriting_take_and_its_disclosure_and_leaves_an_overridden_take_and_its_disclosure()
        {
            using var rig = new Rig();
            var set = await rig.Save(await rig.Revision(), 1, "own");
            rig.Fx.Clock.Advance(10);
            var inherited = await rig.PublishNew("one", 10, own: null);
            var overridden = await rig.PublishNew("two", 20, own: "ai-assisted");
            var before = rig.Provenance();
            var ownEntry = before.Single(e => e.Clip == Clip2);
            Assert.Equal(("own", set.Value!.Consent!.ConfirmedAt), (before.Single(e => e.Clip == Clip).Origin, before.Single(e => e.Clip == Clip).ConfirmedAt));
            Assert.Equal("ai-assisted", ownEntry.Origin);
            var ownLine = rig.Fx.Read(MakerStudioPaths.Line("th", "two"));

            rig.Fx.Clock.Advance(60);
            var changed = await rig.Save(set.Value.Revision, 30, "ai-generated");

            Assert.True(changed.Ok, changed.Message);
            var after = rig.Provenance();
            Assert.Equal(2, after.Count);
            var followed = after.Single(e => e.Clip == Clip);
            var oldFollowed = before.Single(e => e.Clip == Clip);
            Assert.Equal(("th", "ai-generated", MakerStudioContract.ConsentVersion, changed.Value!.Consent!.ConfirmedAt, oldFollowed.Sha256, oldFollowed.PublishedAt),
                (followed.Lang, followed.Origin, followed.StatementVersion, followed.ConfirmedAt, followed.Sha256, followed.PublishedAt));
            var stayed = after.Single(e => e.Clip == Clip2);
            Assert.Equal((ownEntry.Origin, ownEntry.StatementVersion, ownEntry.ConfirmedAt, ownEntry.Sha256, ownEntry.PublishedAt),
                (stayed.Origin, stayed.StatementVersion, stayed.ConfirmedAt, stayed.Sha256, stayed.PublishedAt));

            var onePage = await rig.Page("one");
            Assert.Equal(inherited, onePage.PublishedTakeId);
            Assert.Equal(("ai-generated", "pack"), (onePage.Items[0].Origin, onePage.Items[0].ConsentSource));
            var twoPage = await rig.Page("two");
            Assert.Equal(overridden, twoPage.PublishedTakeId);
            Assert.Equal(("ai-assisted", "take"), (twoPage.Items[0].Origin, twoPage.Items[0].ConsentSource));
            Assert.Equal(ownLine, rig.Fx.Read(MakerStudioPaths.Line("th", "two")));

            // Clearing keeps the published inheriting take's disclosure at the last default.
            var cleared = await rig.Save(changed.Value.Revision, 31, null);
            Assert.True(cleared.Ok, cleared.Message);
            Assert.Equal(("ai-generated", changed.Value.Consent.ConfirmedAt),
                (rig.Provenance().Single(e => e.Clip == Clip).Origin, rig.Provenance().Single(e => e.Clip == Clip).ConfirmedAt));
        }

        [Fact]
        public async Task A_v2_4_default_file_keeps_inheriting_snapshots_until_the_default_next_changes()
        {
            using var rig = new Rig();
            Assert.True((await rig.Save(await rig.Revision(), 1, "own")).Ok);
            var created = await rig.Editor.CreateTakeAsync(rig.Inherit("one", 2), default);
            Assert.True(created.Ok, created.Message);
            // A v2.4 build changed the default without the v2.5 "inherited" member.
            rig.Fx.Write(MakerStudioPaths.VoiceDefault, "{\"schema\":1,\"revision\":\"legacy-r\",\"origin\":\"ai-assisted\",\"consent\":"
                + "{\"confirmed\":true,\"statementVersion\":\"consent-1\",\"confirmedAt\":\"2026-11-09T00:00:00.0000000Z\"},\"updatedAt\":\"2026-11-09T00:00:00.0000000Z\"}");

            Assert.Equal("own", (await rig.Page("one")).Items[0].Origin);

            var changed = await rig.Save("legacy-r", 3, "ai-generated");
            Assert.True(changed.Ok, changed.Message);
            Assert.Equal("ai-generated", (await rig.Page("one")).Items[0].Origin);
            Assert.Contains("\"inherited\":", rig.Fx.Read(MakerStudioPaths.VoiceDefault));
        }

        [Fact]
        public async Task An_unreadable_provenance_file_refuses_a_default_change_when_a_published_take_inherits_it_and_leaves_the_default()
        {
            using var rig = new Rig();
            var set = await rig.Save(await rig.Revision(), 1, "own");
            await rig.PublishNew("one", 10, own: null);
            rig.Fx.Write(MakerStudioPaths.Provenance, "{not json");

            var changed = await rig.Save(set.Value!.Revision, 2, "ai-generated");

            Assert.Equal(MakerStudioErrorCodes.MetadataUnavailable, changed.Error);
            var read = await rig.Editor.VoiceDefaultAsync(Pack, default);
            Assert.Equal((set.Value.Revision, "own"), (read.Value!.Revision, read.Value.Origin));
            Assert.Equal("{not json", rig.Fx.Read(MakerStudioPaths.Provenance));
        }

        [Fact]
        public async Task An_unreadable_provenance_file_does_not_refuse_a_default_change_when_no_published_take_inherits_it()
        {
            // TD-8 / T-066: only a published inheriting take's disclosure follows the default, so nothing else needs the file.
            using var rig = new Rig();
            var set = await rig.Save(await rig.Revision(), 1, "own");
            await rig.PublishNew("two", 20, own: "ai-assisted");
            Assert.True((await rig.Editor.CreateTakeAsync(rig.Inherit("one", 40), default)).Ok, "an unpublished inheriting take");
            rig.Fx.Write(MakerStudioPaths.Provenance, "{not json");

            var changed = await rig.Save(set.Value!.Revision, 2, "ai-generated");

            Assert.True(changed.Ok, changed.Message);
            Assert.Equal("ai-generated", (await rig.Editor.VoiceDefaultAsync(Pack, default)).Value!.Origin);
            Assert.Equal("{not json", rig.Fx.Read(MakerStudioPaths.Provenance));
            Assert.Equal("ai-generated", (await rig.Page("one")).Items[0].Origin);
        }

        [Fact]
        public async Task Following_the_pack_default_again_relinks_an_own_consent_take_and_its_published_disclosure()
        {
            using var rig = new Rig();
            var set = await rig.Save(await rig.Revision(), 1, "own");
            rig.Fx.Clock.Advance(10);
            var takeId = await rig.PublishNew("two", 20, own: "ai-assisted");
            rig.Fx.Clock.Advance(60);
            var changed = await rig.Save(set.Value!.Revision, 30, "ai-generated");
            Assert.True(changed.Ok, changed.Message);
            var before = rig.Provenance().Single(e => e.Clip == Clip2);
            Assert.Equal("ai-assisted", before.Origin);
            var page = await rig.Page("two");

            var relinked = await rig.Editor.UpdateTakeAsync(new MakerStudioUpdateTakeRequest
            {
                PackId = Pack, Lang = "th", Id = "two", TakeId = takeId, ExpectedRevision = page.Revision, OperationId = rig.Op(31),
                FollowPackDefault = true,
            }, default);

            Assert.True(relinked.Ok, relinked.Message);
            var take = relinked.Value!.Take!;
            Assert.Equal(("ai-generated", true, changed.Value!.Consent!.ConfirmedAt, "pack"),
                (take.Origin, take.Consent.Confirmed, take.Consent.ConfirmedAt, take.ConsentSource));
            Assert.NotEqual(page.Revision, relinked.Value.Revision);
            var after = rig.Provenance().Single(e => e.Clip == Clip2);
            Assert.Equal(("ai-generated", changed.Value.Consent.ConfirmedAt, before.Sha256, before.PublishedAt),
                (after.Origin, after.ConfirmedAt, after.Sha256, after.PublishedAt));

            // It follows later changes again, disclosure included.
            rig.Fx.Clock.Advance(60);
            var next = await rig.Save(changed.Value.Revision, 32, "own");
            Assert.True(next.Ok, next.Message);
            Assert.Equal(("own", "pack"), ((await rig.Page("two")).Items[0].Origin, (await rig.Page("two")).Items[0].ConsentSource));
            Assert.Equal(("own", next.Value!.Consent!.ConfirmedAt),
                (rig.Provenance().Single(e => e.Clip == Clip2).Origin, rig.Provenance().Single(e => e.Clip == Clip2).ConfirmedAt));
        }

        [Fact]
        public async Task Following_needs_a_pack_default_and_cannot_be_sent_with_origin_or_consent()
        {
            using var rig = new Rig();
            var created = await rig.Editor.CreateTakeAsync(new MakerStudioCreateTakeRequest
            {
                PackId = Pack, Lang = "th", Id = "one", ExpectedRevision = rig.Fx.Store.ReadLine("th", "one").Value!.Revision, OperationId = rig.Op(1),
                Origin = "own", Consent = new MakerStudioConsent { Confirmed = true, StatementVersion = MakerStudioContract.ConsentVersion },
            }, default);
            Assert.True(created.Ok, created.Message);
            MakerStudioUpdateTakeRequest Follow(int op, string revision) => new MakerStudioUpdateTakeRequest
            {
                PackId = Pack, Lang = "th", Id = "one", TakeId = created.Value!.Take!.Id, ExpectedRevision = revision, OperationId = rig.Op(op),
                FollowPackDefault = true,
            };

            var none = await rig.Editor.UpdateTakeAsync(Follow(2, created.Value!.Revision), default);
            Assert.Equal((MakerStudioErrorCodes.ConsentRequired, MakerTakeStore.FollowNoDefault), (none.Error, none.Message));

            var both = Follow(3, created.Value.Revision);
            both.Origin = "own";
            var refused = await rig.Editor.UpdateTakeAsync(both, default);
            Assert.Equal((MakerErrorCodes.BadRequest, MakerTakeStore.FollowWithOwnConsent), (refused.Error, refused.Message));
            Assert.Equal("take", (await rig.Page("one")).Items[0].ConsentSource);
        }

        [Fact]
        public void The_router_accepts_followPackDefault_true_only_and_never_with_origin()
        {
            using var fx = new Rig();
            var rig = new MakerRig(studio: () => new MakerStudioBackends(dubs: fx.Editor, voiceDefaults: fx.Editor));
            const string takes = "/v1/maker/studio/packs/sample.pack/languages/th/lines/one/takes";
            const string confirmed = "{\"confirmed\":true,\"statementVersion\":\"consent-1\"}";
            var voiceRevision = (string)ApiTestKit.Json(rig.Send(MakerHttp.Get("/v1/maker/studio/packs/sample.pack/voice", rig.Token)))["revision"]!;
            Assert.Equal(200, rig.Send(MakerHttp.Json("PUT", "/v1/maker/studio/packs/sample.pack/voice", rig.Token,
                "{\"expectedRevision\":\"" + voiceRevision + "\",\"operationId\":\"" + fx.Op(1) + "\",\"origin\":\"ai-assisted\",\"consent\":" + confirmed + "}")).Status);
            var created = ApiTestKit.Json(rig.Send(MakerHttp.Json("POST", takes, rig.Token, "{\"expectedRevision\":\""
                + fx.Fx.Store.ReadLine("th", "one").Value!.Revision + "\",\"operationId\":\"" + fx.Op(2) + "\",\"origin\":\"own\",\"consent\":" + confirmed + "}")));
            var take = takes + "/" + ((JsonObject)created["take"]!)["id"];
            string Patch(int op, string extra) => "{\"expectedRevision\":\"" + created["revision"] + "\",\"operationId\":\"" + fx.Op(op) + "\"," + extra + "}";

            Assert.Equal(400, rig.Send(MakerHttp.Json("PATCH", take, rig.Token, Patch(3, "\"followPackDefault\":false"))).Status);
            Assert.Equal(400, rig.Send(MakerHttp.Json("PATCH", take, rig.Token, Patch(4, "\"followPackDefault\":true,\"origin\":\"own\""))).Status);
            var ok = rig.Send(MakerHttp.Json("PATCH", take, rig.Token, Patch(5, "\"followPackDefault\":true")));
            Assert.Equal(200, ok.Status);
            var followed = (JsonObject)ApiTestKit.Json(ok)["take"]!;
            Assert.Equal(("ai-assisted", "pack"), (followed["origin"], followed["consentSource"]));
        }

        [Fact]
        public async Task Patching_the_origin_of_an_inherited_take_makes_its_consent_its_own()
        {
            using var rig = new Rig();
            Assert.True((await rig.Save(await rig.Revision(), 1, "own")).Ok);
            var created = await rig.Editor.CreateTakeAsync(rig.Inherit("one", 2), default);
            var page = await rig.Page("one");

            var patched = await rig.Editor.UpdateTakeAsync(new MakerStudioUpdateTakeRequest
            {
                PackId = Pack, Lang = "th", Id = "one", TakeId = created.Value!.Take!.Id, ExpectedRevision = page.Revision, OperationId = rig.Op(3),
                Origin = "ai-assisted",
            }, default);

            Assert.True(patched.Ok, patched.Message);
            Assert.Equal(("ai-assisted", "take"), (patched.Value!.Take!.Origin, patched.Value.Take.ConsentSource));
            Assert.Equal("take", (await rig.Page("one")).Items[0].ConsentSource);
            Assert.DoesNotContain("consentSource", rig.Fx.Read(MakerStudioPaths.Line("th", "one")));
        }

        [Fact]
        public async Task Publishing_an_inherited_take_discloses_the_pack_origin_and_confirmation()
        {
            using var rig = new Rig();
            var set = await rig.Save(await rig.Revision(), 1, "ai-generated");
            rig.Fx.Clock.Advance(10);
            var created = await rig.Editor.CreateTakeAsync(rig.Inherit("one", 2), default);
            Assert.True(created.Ok, created.Message);
            var takeId = created.Value!.Take!.Id;
            await rig.Upload("one", 3, takeId, "source", TestWav.Tone(0.5, 24000, 1));
            await rig.Upload("one", 4, takeId, "render", TestWav.Tone(0.5, 48000, 1));
            var page = await rig.Page("one");

            var published = await rig.Editor.PublishAsync(new MakerStudioPublishRequest
            {
                PackId = Pack, Lang = "th", Id = "one", TakeId = takeId, ExpectedRevision = page.Revision,
                ExpectedClipRevision = page.ClipRevision!, OperationId = rig.Op(5),
            }, default);

            Assert.True(published.Ok, published.Message);
            Assert.Equal(Runtime, published.Value!.Path);
            var text = File.ReadAllText(Path.Combine(rig.Fx.Root, MakerStudioPaths.Provenance));
            Assert.True(MakerVoiceProvenance.TryRead(text, out var entries, out var problem), problem);
            var entry = Assert.Single(entries);
            Assert.Equal(("th", Clip, "ai-generated", MakerStudioContract.ConsentVersion, set.Value!.Consent!.ConfirmedAt),
                (entry.Lang, entry.Clip, entry.Origin, entry.StatementVersion, entry.ConfirmedAt));
        }

        [Fact]
        public void The_router_advertises_voiceDefault_and_serves_P_voice_with_strict_bodies()
        {
            using var fx = new Rig();
            var rig = new MakerRig(studio: () => new MakerStudioBackends(dubs: fx.Editor, voiceDefaults: fx.Editor));
            const string voice = "/v1/maker/studio/packs/sample.pack/voice";
            const string takes = "/v1/maker/studio/packs/sample.pack/languages/th/lines/one/takes";

            var features = (IList<object?>)ApiTestKit.Json(rig.Send(MakerHttp.Get("/v1/maker/studio/capabilities", rig.Token)))["features"]!;
            Assert.Equal(new object?[] { "dubs", "voiceDefault" }, features.ToArray());

            var get = rig.Send(MakerHttp.Get(voice, rig.Token));
            Assert.Equal(200, get.Status);
            var initial = ApiTestKit.Json(get);
            Assert.Equal(new[] { "revision", "origin", "consent", "updatedAt" }, initial.Keys.ToArray());
            Assert.Null(initial["origin"]);
            var revision = (string)initial["revision"]!;
            var lineRevision = fx.Fx.Store.ReadLine("th", "one").Value!.Revision;

            // A take with neither origin nor consent inherits; with no default that is 403 consent_required.
            var inherit = "{\"expectedRevision\":\"" + lineRevision + "\",\"operationId\":\"" + fx.Op(1) + "\"}";
            var refused = rig.Send(MakerHttp.Json("POST", takes, rig.Token, inherit));
            Assert.Equal((403, "consent_required"), (refused.Status, ApiTestKit.ErrorCode(refused)));
            Assert.Equal(400, rig.Send(MakerHttp.Json("POST", takes, rig.Token,
                "{\"expectedRevision\":\"" + lineRevision + "\",\"operationId\":\"" + fx.Op(2) + "\",\"origin\":\"own\"}")).Status);

            string Put(int op, string origin, string consent) =>
                "{\"expectedRevision\":\"" + revision + "\",\"operationId\":\"" + fx.Op(op) + "\",\"origin\":" + origin + ",\"consent\":" + consent + "}";
            const string confirmed = "{\"confirmed\":true,\"statementVersion\":\"consent-1\"}";
            Assert.Equal(400, rig.Send(MakerHttp.Json("PUT", voice, rig.Token, Put(10, "\"own\"", "{\"confirmed\":false,\"statementVersion\":\"consent-1\"}"))).Status);
            Assert.Equal(400, rig.Send(MakerHttp.Json("PUT", voice, rig.Token, Put(11, "\"own\"", "{\"confirmed\":true,\"statementVersion\":\"consent-0\"}"))).Status);
            Assert.Equal(400, rig.Send(MakerHttp.Json("PUT", voice, rig.Token, Put(12, "\"robot\"", confirmed))).Status);
            Assert.Equal(400, rig.Send(MakerHttp.Json("PUT", voice, rig.Token, Put(13, "\"own\"", "null"))).Status);
            Assert.Equal(400, rig.Send(MakerHttp.Json("PUT", voice, rig.Token, Put(14, "\"own\"", confirmed).Replace("}}", "},\"extra\":1}"))).Status);
            Assert.Equal(400, rig.Send(MakerHttp.Json("PUT", voice + "?x=1", rig.Token, Put(15, "\"own\"", confirmed))).Status);

            var put = rig.Send(MakerHttp.Json("PUT", voice, rig.Token, Put(16, "\"own\"", confirmed)));
            Assert.Equal(200, put.Status);
            Assert.Equal("own", ApiTestKit.Json(put)["origin"]);
            var stale = rig.Send(MakerHttp.Json("PUT", voice, rig.Token, Put(17, "null", "null")));
            Assert.Equal((409, "conflict"), (stale.Status, ApiTestKit.ErrorCode(stale)));

            var created = rig.Send(MakerHttp.Json("POST", takes, rig.Token, inherit));
            Assert.Equal(201, created.Status);
            Assert.Equal("pack", ((JsonObject)ApiTestKit.Json(created)["take"]!)["consentSource"]);

            Assert.Equal(404, rig.Send(MakerHttp.Get("/v1/maker/studio/packs/other.pack/voice", rig.Token)).Status);
            Assert.Equal(404, rig.Send(MakerHttp.Json("POST", voice, rig.Token, "{}")).Status); // NoRoute
            var none = new MakerRig(studio: () => new MakerStudioBackends());
            Assert.Equal(503, none.Send(MakerHttp.Get(voice, none.Token)).Status);
            Assert.DoesNotContain("voiceDefault", (IList<object?>)ApiTestKit.Json(none.Send(MakerHttp.Get("/v1/maker/studio/capabilities", none.Token)))["features"]!);
        }

        private sealed class Rig : IDisposable
        {
            internal Rig()
            {
                Fx = new MakerProjectFixture();
                Editor = new MakerStudioEditor((pack, _) => pack == Pack
                        ? MakerResult<MakerProjectStore>.Success(Fx.Store)
                        : MakerResult<MakerProjectStore>.Fail(MakerErrorCodes.NotFound, "no such pack"),
                    (_, _, _, _, _) => new MakerSubtitleLiveResult(),
                    publish: (type, data) => Events.Add(type + (data.TryGetValue("reason", out var reason) ? ":" + reason : string.Empty)),
                    lineVoice: (_, id, _) => Task.FromResult(id == "one" ? MakerResult<string?>.Success(Clip)
                        : id == "two" ? MakerResult<string?>.Success(Clip2) : MakerResult<string?>.Fail(MakerErrorCodes.NotFound, "no such line")),
                    applyVoice: (pack, _, _, write, _, _) => Task.FromResult(new MakerVoiceLiveResult
                    {
                        Saved = new MakerSaved { Live = true, Path = write.Path, Note = "applied" }, EffectivePackId = pack,
                    }));
            }

            internal MakerProjectFixture Fx { get; }
            internal List<string> Events { get; } = new List<string>();
            internal MakerStudioEditor Editor { get; }
            internal MakerPackFiles Files => Fx.Store.Files;

            internal string Op(int n) => Fx.Operation(n);

            internal string Iso() => Fx.Clock.Now.ToString("O", CultureInfo.InvariantCulture);

            internal async Task<string> Revision()
            {
                var read = await Editor.VoiceDefaultAsync(Pack, default);
                Assert.True(read.Ok, read.Message);
                return read.Value!.Revision;
            }

            internal Task<MakerResult<MakerStudioVoiceDefault>> Save(string expected, int op, string? origin, bool confirmed = true) =>
                Editor.SaveVoiceDefaultAsync(new MakerStudioVoiceDefaultRequest
                {
                    PackId = Pack, ExpectedRevision = expected, OperationId = Op(op), Origin = origin,
                    Consent = origin is null ? null : new MakerStudioConsent { Confirmed = confirmed, StatementVersion = MakerStudioContract.ConsentVersion },
                }, default);

            internal MakerStudioCreateTakeRequest Inherit(string id, int op) => new MakerStudioCreateTakeRequest
            {
                PackId = Pack, Lang = "th", Id = id, ExpectedRevision = Fx.Store.ReadLine("th", id).Value!.Revision, OperationId = Op(op),
            };

            internal async Task<MakerStudioTakePage> Page(string id)
            {
                var page = await Editor.TakesAsync(Pack, "th", id, default);
                Assert.True(page.Ok, page.Message);
                return page.Value!;
            }

            internal async Task Upload(string id, int op, string takeId, string kind, byte[] wav)
            {
                var page = await Page(id);
                var saved = await Editor.SaveTakeAudioAsync(new MakerStudioTakeAudioRequest
                {
                    PackId = Pack, Lang = "th", Id = id, TakeId = takeId, Kind = kind, ExpectedRevision = page.Revision, OperationId = Op(op),
                }, wav, default);
                Assert.True(saved.Ok, saved.Message);
            }

            /// <summary>Creates a take on <paramref name="id"/> (inheriting, or with its own confirmed <paramref name="own"/> origin), uploads audio and publishes it.</summary>
            internal async Task<string> PublishNew(string id, int op, string? own)
            {
                var request = Inherit(id, op);
                if (own != null)
                {
                    request.Origin = own;
                    request.Consent = new MakerStudioConsent { Confirmed = true, StatementVersion = MakerStudioContract.ConsentVersion };
                }

                var created = await Editor.CreateTakeAsync(request, default);
                Assert.True(created.Ok, created.Message);
                var takeId = created.Value!.Take!.Id;
                await Upload(id, op + 1, takeId, "source", TestWav.Tone(0.5, 24000, 1));
                await Upload(id, op + 2, takeId, "render", TestWav.Tone(0.5, 48000, 1));
                var page = await Page(id);
                var published = await Editor.PublishAsync(new MakerStudioPublishRequest
                {
                    PackId = Pack, Lang = "th", Id = id, TakeId = takeId, ExpectedRevision = page.Revision,
                    ExpectedClipRevision = page.ClipRevision!, OperationId = Op(op + 3),
                }, default);
                Assert.True(published.Ok, published.Message);
                return takeId;
            }

            internal List<MakerProvenanceEntry> Provenance()
            {
                Assert.True(MakerVoiceProvenance.TryRead(Fx.Read(MakerStudioPaths.Provenance), out var entries, out var problem), problem);
                return entries;
            }

            public void Dispose() => Fx.Dispose();
        }
    }
}
