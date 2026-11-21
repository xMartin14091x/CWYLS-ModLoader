using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CwyLs.ModLoader.Core.Api;
using CwyLs.ModLoader.Core.Api.Maker;
using CwyLs.ModLoader.Core.Api.Maker.Studio;
using CwyLs.ModLoader.Core.Json;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// Contract v2.3 (Pack Maker Phase C): pack info, icon, export checks, version bump, release notes and the zip. Every
    /// folder is a temporary <c>CWYLS Mods</c>; the pack, its lines and its audio are invented (silent WAVs).
    /// </summary>
    public sealed class MakerPackExportTests : IDisposable
    {
        private const string PackId = "lab.export";
        private const string Manifest =
            "{\n  \"id\": \"lab.export\",\n  \"name\": \"Export lab\",\n  \"version\": \"0.1.0\",\n  \"author\": \"Lab\",\n" +
            "  \"description\": \"Invented.\",\n  \"loaderVersion\": \"0.9.2\"\n}\n";

        private readonly string _root = Path.Combine(Path.GetTempPath(), "cwy-pack-export-" + Guid.NewGuid().ToString("N"));
        private readonly ManualApiClock _clock = new ManualApiClock();
        private readonly List<string> _events = new List<string>();

        public MakerPackExportTests()
        {
            Directory.CreateDirectory(Pack);
            Write("manifest.json", Manifest);
            Write("data/subtitles/th.json", "{\n  \"line_001\": \"สวัสดี\"\n}\n");
            var wav = MakerAudioFixtures.Wav(48000, 1, 4800);
            WriteBytes("audio/voice/th/clip_001.wav", wav);
            Write("voice-provenance.json", Encoding.UTF8.GetString(MakerVoiceProvenance.Write(new[]
            {
                new MakerProvenanceEntry { Lang = "th", Clip = "clip_001", Origin = "own", StatementVersion = "consent-1",
                    ConfirmedAt = "2026-11-09T10:00:00+07:00", Sha256 = MakerVoiceProvenance.Sha256(wav), PublishedAt = "2026-11-09T10:00:00+07:00" },
            })));
            WriteBytes(".maker/takes/th/line_001/" + Op("a") + "/source.wav", wav);
            Write(".maker/project.json", "{\"schema\":1,\"projectId\":\"ffffffffffffffffffffffffffffffff\"}");
            Write("_maker_backup/data/subtitles/th.json", "{}");
            Write(".DS_Store", "x");
        }

        private string Mods => Path.Combine(_root, "CWYLS Mods");
        private string Pack => Path.Combine(Mods, PackId);
        private string SharedExports => Path.Combine(Mods, MakerPackExporter.ExportsFolderName);

        private string Exports => Path.Combine(SharedExports, "lab.export");

        public void Dispose()
        {
            try { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
            catch (IOException) { /* a leftover temp folder is harmless */ }
        }

        private void Write(string relative, string text) => WriteBytes(relative, Encoding.UTF8.GetBytes(text));

        private void WriteBytes(string relative, byte[] bytes)
        {
            var full = Path.Combine(Pack, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, bytes);
        }

        private static string Op(string n) => n.PadLeft(32, '0');

        private MakerPackExporter Exporter(Func<string, string?>? pending = null) =>
            new MakerPackExporter(
                (id, editing) => id == PackId ? MakerResult<MakerProjectStore>.Success(new MakerProjectStore(Pack, _clock))
                    : MakerResult<MakerProjectStore>.Fail(MakerErrorCodes.NotFound, "no such pack"),
                pending ?? (_ => null), () => SharedExports, publish: (type, data) => _events.Add(type + ":" + data["reason"]), clock: _clock);

        private MakerExportRequest Request(string op = "1", string expected = "0.1.0", string bump = "patch", string? notes = "First release.") =>
            new MakerExportRequest { PackId = PackId, OperationId = Op(op), ExpectedVersion = expected, Bump = bump, Notes = notes };

        private static byte[] Png(int width, int height)
        {
            var png = new byte[33];
            new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R' }.CopyTo(png, 0);
            png[16] = (byte)(width >> 24); png[17] = (byte)(width >> 16); png[18] = (byte)(width >> 8); png[19] = (byte)width;
            png[20] = (byte)(height >> 24); png[21] = (byte)(height >> 16); png[22] = (byte)(height >> 8); png[23] = (byte)height;
            return png;
        }

        [Fact]
        public async Task The_check_passes_a_clean_pack_and_offers_the_next_versions()
        {
            var preview = await Exporter().CheckAsync(PackId, default);

            Assert.True(preview.Ok, preview.Message);
            var p = preview.Value!;
            Assert.True(p.Ok, string.Join("; ", p.Checks.Select(c => c.Id + "=" + c.Severity + " " + c.Message)));
            Assert.Equal("0.1.0", p.Version);
            Assert.Equal(("0.1.1", "0.2.0", "1.0.0"), (p.NextVersions.Patch, p.NextVersions.Minor, p.NextVersions.Major));
            Assert.Equal(new[] { "manifest", "subtitles", "audio", "provenance", "size", "progress", "links" }, p.Checks.Select(c => c.Id).ToArray());
            Assert.Equal(4, p.Files.Included);
            Assert.Equal(4, p.Files.Excluded);
        }

        [Fact]
        public async Task An_export_bumps_the_version_writes_the_changelog_and_zips_only_player_files()
        {
            var result = await Exporter().ExportAsync(Request(), default);

            Assert.True(result.Ok, result.Message);
            var r = result.Value!;
            Assert.Equal(("0.1.1", "0.1.0", "lab.export-v0.1.1.zip"), (r.Version, r.PreviousVersion, r.FileName));
            Assert.True(r.ManifestUpdated && r.ChangelogUpdated && r.Downloadable);
            Assert.Equal(Manifest.Replace("\"0.1.0\"", "\"0.1.1\""), File.ReadAllText(Path.Combine(Pack, "manifest.json")));
            Assert.StartsWith("# Changelog\n\n## 0.1.1 (2026-10-02)\n\nFirst release.\n", File.ReadAllText(Path.Combine(Pack, "CHANGELOG.md")));

            using var zip = ZipFile.OpenRead(Path.Combine(Exports, r.FileName));
            var names = zip.Entries.Select(e => e.FullName).ToArray();
            Assert.Equal(new[]
            {
                "lab.export/CHANGELOG.md", "lab.export/audio/voice/th/clip_001.wav", "lab.export/data/subtitles/th.json",
                "lab.export/manifest.json", "lab.export/voice-provenance.json",
            }, names);
            using var manifest = new StreamReader(zip.GetEntry("lab.export/manifest.json")!.Open());
            Assert.Contains("\"version\": \"0.1.1\"", manifest.ReadToEnd());
            Assert.Equal(File.ReadAllBytes(Path.Combine(Pack, "audio/voice/th/clip_001.wav")).Length, zip.GetEntry("lab.export/audio/voice/th/clip_001.wav")!.Length);
            Assert.Contains("maker.pack.changed:export", _events);
            Assert.Empty(Directory.GetFiles(Exports, ".*"));
        }

        [Theory]
        [InlineData("lab.export", "0.1.1", "Lab", "lab.export-v0.1.1.zip")]
        [InlineData("sample.studio", "0.1.1", "Lab", "sample.studio-v0.1.1-by-lab.zip")]
        [InlineData("sample.studio", "1.0.0-beta.1", "Ploy Sriwan", "sample.studio-v1.0.0-beta.1-by-ploysriwan.zip")]
        [InlineData("sample.studio", "0.1.1", "  Lab-2 ", "sample.studio-v0.1.1-by-lab2.zip")]
        [InlineData("sample.studio", "0.1.1", null, "sample.studio-v0.1.1.zip")]
        [InlineData("sample.studio", "0.1.1", "", "sample.studio-v0.1.1.zip")]
        [InlineData("sample.studio", "0.1.1", "ภาษาไทย", "sample.studio-v0.1.1.zip")]
        public void The_export_file_name_carries_the_author_slug_only_when_the_pack_id_lacks_it(string packId, string version, string? author, string expected) =>
            Assert.Equal(expected, MakerPackExporter.ExportFileName(packId, version, author));

        [Fact]
        public void A_long_author_slug_is_cut_so_the_version_segment_stays_within_the_download_rule()
        {
            var name = MakerPackExporter.ExportFileName("sample.studio", "0.1.1", new string('a', 80));

            Assert.Equal("sample.studio-v0.1.1-by-" + new string('a', 54) + ".zip", name);
            Assert.True(name.Substring("sample.studio-".Length, name.Length - "sample.studio-".Length - ".zip".Length).Length <= 64);
        }

        [Fact]
        public async Task An_export_by_another_author_names_the_zip_after_them_and_downloads_by_that_name()
        {
            Write("manifest.json", Manifest.Replace("\"Lab\"", "\"Some Body\""));
            var exporter = Exporter();

            var result = await exporter.ExportAsync(Request(), default);
            var download = await exporter.ReadExportAsync(PackId, result.Value!.FileName, default);

            Assert.True(result.Ok, result.Message);
            Assert.Equal("lab.export-v0.1.1-by-somebody.zip", result.Value.FileName);
            Assert.True(download.Ok, download.Message);
            Assert.Equal(result.Value.Sha256, MakerVoiceProvenance.Sha256(download.Value!.Bytes!));
        }

        [Fact]
        public async Task The_same_request_replays_its_receipt_and_another_request_or_a_stale_version_is_409()
        {
            var exporter = Exporter();
            var first = (await exporter.ExportAsync(Request(), default)).Value!;

            var replay = await exporter.ExportAsync(Request(), default);
            var reused = await exporter.ExportAsync(Request(notes: "Other."), default);
            var stale = await exporter.ExportAsync(Request("2", expected: "0.1.0"), default);

            Assert.True(replay.Ok);
            Assert.Equal((first.Version, first.Sha256), (replay.Value!.Version, replay.Value.Sha256));
            Assert.Equal(MakerStudioErrorCodes.Conflict, reused.Error);
            Assert.Equal(MakerStudioErrorCodes.Conflict, stale.Error);
            Assert.Contains("\"0.1.1\"", File.ReadAllText(Path.Combine(Pack, "manifest.json")));
            Assert.Equal("lab.export-v0.1.1.zip", (await exporter.CheckAsync(PackId, default)).Value!.LastExport!.FileName);
        }

        [Fact]
        public async Task Every_check_carries_a_code_and_params_so_the_page_can_word_it_in_any_language()
        {
            // TD-13 / T-069: the Thai UI showed the backend's English prose; the page now words each check from code + params.
            var progress = new MakerStudioGroups { Overall = new MakerStudioProgress { TotalLines = 10, DubbableLines = 8, SubtitlePresent = 7, DubPresent = 5 } };
            var exporter = new MakerPackExporter(
                (id, editing) => MakerResult<MakerProjectStore>.Success(new MakerProjectStore(Pack, _clock)), _ => null, () => SharedExports,
                progress: (_, __, ___) => Task.FromResult(MakerResult<MakerStudioGroups>.Success(progress)), clock: _clock);

            var preview = (await exporter.CheckAsync(PackId, default)).Value!;

            Assert.All(preview.Checks, c => Assert.False(string.IsNullOrEmpty(c.Code), c.Id + " has no code"));
            Assert.Equal(("manifestOk", "Export lab", "0.1.0"), Coded(preview, "manifest", "name", "version"));
            Assert.Equal(("subCount", (object?)1L, (object?)1L), Coded(preview, "subtitles", "n", "m"));
            Assert.Equal(("audioCount", (object?)1L), (preview.Checks.Single(c => c.Id == "audio").Code, preview.Checks.Single(c => c.Id == "audio").Params["n"]));
            var langs = (IReadOnlyList<object?>)preview.Checks.Single(c => c.Id == "progress").Params["langs"]!;
            var th = (IReadOnlyDictionary<string, object?>)langs.Single()!;
            Assert.Equal(("progress", "th", 3L, 8L, 3L, 10L),
                (preview.Checks.Single(c => c.Id == "progress").Code, th["lang"], th["dub"], th["voiced"], th["sub"], th["total"]));

            var json = (JsonObject)((IList<object?>)MakerStudioExportJson.Preview(preview)["checks"]!)[1]!;
            Assert.Equal(new[] { "id", "severity", "message", "count", "paths", "code", "params" }, json.Keys.ToArray());
            Assert.Equal(1L, ((JsonObject)json["params"]!)["n"]);
        }

        private static (string, object?, object?) Coded(MakerExportPreview preview, string id, string a, string b)
        {
            var check = preview.Checks.Single(c => c.Id == id);
            return (check.Code, check.Params[a], check.Params[b]);
        }

        [Fact]
        public async Task The_result_and_the_last_export_name_the_folder_the_zip_is_saved_in_and_the_receipt_does_not_store_it()
        {
            var exporter = Exporter();
            var result = (await exporter.ExportAsync(Request(), default)).Value!;
            var replay = (await exporter.ExportAsync(Request(), default)).Value!;
            var last = (await exporter.CheckAsync(PackId, default)).Value!.LastExport!;

            Assert.Equal(Exports, result.Folder);
            Assert.True(File.Exists(Path.Combine(result.Folder!, result.FileName)));
            Assert.Equal(Exports, replay.Folder);
            Assert.Equal(Exports, last.Folder);
            Assert.Equal(Exports, MakerStudioExportJson.Result(result)["folder"]);
            var receipt = File.ReadAllText(Path.Combine(Pack, ".maker", "exports", Op("1") + ".json"));
            Assert.DoesNotContain("folder", receipt);
            Assert.DoesNotContain("folder", File.ReadAllText(Path.Combine(Pack, ".maker", "exports", "last.json")));
        }

        [Fact]
        public async Task Receipts_older_than_the_retention_age_are_pruned_and_newer_ones_kept()
        {
            _clock.Now = DateTimeOffset.UtcNow;
            var folder = Path.Combine(Pack, ".maker", "exports");
            Directory.CreateDirectory(folder);
            var old = Path.Combine(folder, Op("e1") + ".json");
            var recent = Path.Combine(folder, Op("e2") + ".json");
            File.WriteAllText(old, "{}");
            File.WriteAllText(recent, "{}");
            File.SetLastWriteTimeUtc(old, _clock.Now.UtcDateTime - MakerPackExporter.ReceiptMaxAge - TimeSpan.FromDays(1));
            File.SetLastWriteTimeUtc(recent, _clock.Now.UtcDateTime - MakerPackExporter.ReceiptMaxAge + TimeSpan.FromDays(1));

            var result = await Exporter().ExportAsync(Request(), default);

            Assert.True(result.Ok, result.Message);
            Assert.False(File.Exists(old), "a receipt past the retention age is pruned");
            Assert.True(File.Exists(recent));
            Assert.True(File.Exists(Path.Combine(folder, Op("1") + ".json")), "this export's receipt stays");
        }

        [Fact]
        public async Task A_dub_changed_after_publishing_or_a_broken_wav_blocks_the_export_and_changes_nothing()
        {
            WriteBytes("audio/voice/th/clip_001.wav", MakerAudioFixtures.Wav(48000, 1, 9600));
            WriteBytes("audio/voice/th/clip_002.wav", Encoding.ASCII.GetBytes("RIFF0000WAVEjunk"));

            var preview = (await Exporter().CheckAsync(PackId, default)).Value!;
            var result = await Exporter().ExportAsync(Request(), default);

            Assert.False(preview.Ok);
            Assert.Equal(MakerExportSeverity.Error, preview.Checks.Single(c => c.Id == "audio").Severity);
            Assert.Equal(MakerExportSeverity.Error, preview.Checks.Single(c => c.Id == "provenance").Severity);
            Assert.Equal(MakerStudioErrorCodes.ExportBlocked, result.Error);
            Assert.Equal(Manifest, File.ReadAllText(Path.Combine(Pack, "manifest.json")));
            Assert.False(File.Exists(Path.Combine(Pack, "CHANGELOG.md")));
            Assert.False(Directory.Exists(Exports) && Directory.GetFiles(Exports).Length > 0);
        }

        [Fact]
        public void Version_and_changelog_edits_keep_everything_else()
        {
            var json = "{ \"id\":\"a.b.c\", \"dependsOn\":[{\"id\":\"x.y.z\",\"version\":\"1.0.0\"}],\n\t\"version\" :  \"1.2.3-beta.1\" }";
            Assert.True(MakerPackChecks.TrySetVersion(json, "1.2.3", out var updated));
            Assert.Equal(json.Replace("\"1.2.3-beta.1\"", "\"1.2.3\""), updated);
            Assert.True(MakerPackChecks.TryBump("1.2.3-beta.1", "patch", out var patch));
            Assert.Equal("1.2.3", patch);

            var log = "# Changelog\n\n## 1.0.0 (2026-11-01)\n\nOld.\n\n## 0.9.0 (2026-10-01)\n\nOlder.\n";
            var replaced = MakerPackChecks.WithEntry(log, "1.0.0", "2026-11-09", "New.");
            Assert.Equal("# Changelog\n\n## 1.0.0 (2026-11-09)\n\nNew.\n\n## 0.9.0 (2026-10-01)\n\nOlder.\n", replaced);
            var added = MakerPackChecks.WithEntry(log, "1.1.0", "2026-11-09", "Next.");
            Assert.StartsWith("# Changelog\n\n## 1.1.0 (2026-11-09)\n\nNext.\n\n## 1.0.0 (2026-11-01)", added);
            Assert.Equal(("1.1.0", "Next."), (MakerPackChecks.TopEntry(added)!.Version, MakerPackChecks.TopEntry(added)!.Notes));
        }

        [Fact]
        public async Task Info_and_icon_work_on_a_pending_pack_and_refuse_a_bad_png()
        {
            var exporter = Exporter(id => id == "lab.pending" ? Pack : null);

            var bad = await exporter.SaveIconAsync("lab.pending", Png(64, 32), default);
            var saved = await exporter.SaveIconAsync("lab.pending", Png(128, 128), default);
            var info = (await exporter.InfoAsync("lab.pending", default)).Value!;
            var icon = (await exporter.ReadIconAsync("lab.pending", default)).Value!;

            Assert.Equal(MakerErrorCodes.UnsupportedMedia, bad.Error);
            Assert.True(saved.Ok, saved.Message);
            Assert.True(info.HasIcon && info.Pending);
            Assert.Equal(("Export lab", "Lab", "Invented."), (info.Name, info.Author, info.Description));
            Assert.Equal(new[] { "th" }, info.Languages);
            Assert.Equal(Png(128, 128), icon.Bytes);
        }

        [Fact]
        public void The_router_serves_the_v23_routes_with_their_shapes()
        {
            var rig = new MakerRig(studio: () => new MakerStudioBackends(export: Exporter()));
            var features = ((IEnumerable<object?>)ApiTestKit.Json(rig.Send(MakerHttp.Get("/v1/maker/studio/capabilities", rig.Token)))["features"]!).Cast<string>().ToArray();
            Assert.Equal(new[] { "packInfo", "export" }, features);

            var check = rig.Send(MakerHttp.Get("/v1/maker/studio/packs/lab.export/export", rig.Token));
            Assert.Equal(200, check.Status);
            Assert.Equal(new[] { "packId", "version", "nextVersions", "ok", "errorCount", "warningCount", "checks", "files", "changelog", "lastExport" },
                ApiTestKit.Json(check).Keys.ToArray());

            var body = "{\"operationId\":\"" + Op("9") + "\",\"expectedVersion\":\"0.1.0\",\"bump\":\"minor\",\"notes\":null}";
            var export = rig.Send(MakerHttp.Json("POST", "/v1/maker/studio/packs/lab.export/export", rig.Token, body));
            Assert.Equal(201, export.Status);
            Assert.Equal("lab.export-v0.2.0.zip", ApiTestKit.Json(export)["fileName"]);
            Assert.False((bool)ApiTestKit.Json(export)["changelogUpdated"]!);

            var download = rig.Send(MakerHttp.Get("/v1/maker/studio/packs/lab.export/exports/lab.export-v0.2.0.zip", rig.Token));
            Assert.Equal(200, download.Status);
            Assert.Equal("application/zip", download.MediaType);
            Assert.Contains(download.Headers, h => h.Key == "Content-Disposition" && h.Value.Contains("lab.export-v0.2.0.zip"));
            Assert.Equal(404, rig.Send(MakerHttp.Get("/v1/maker/studio/packs/lab.export/exports/other.pack-v0.2.0.zip", rig.Token)).Status);

            Assert.Equal(400, rig.Send(MakerHttp.Json("POST", "/v1/maker/studio/packs/lab.export/export", rig.Token, body.Replace("minor", "huge"))).Status);
            Assert.Equal(415, rig.Send(MakerHttp.Build("PUT", "/v1/maker/studio/packs/lab.export/icon", rig.Token, Png(64, 64), "image/jpeg")).Status);
            Assert.Equal(200, rig.Send(MakerHttp.Build("PUT", "/v1/maker/studio/packs/lab.export/icon", rig.Token, Png(64, 64), "image/png")).Status);
            var icon = rig.Send(MakerHttp.Get("/v1/maker/studio/packs/lab.export/icon", rig.Token));
            Assert.Equal(("image/png", 33), (icon.MediaType, icon.Bytes!.Length));
            var info = ApiTestKit.Json(rig.Send(MakerHttp.Get("/v1/maker/studio/packs/lab.export/info", rig.Token)));
            Assert.Equal(new[] { "id", "name", "version", "author", "description", "languages", "hasIcon", "sizeBytes", "pending", "changelog" }, info.Keys.ToArray());
        }

        // HN-045 / milestone-1 P3 B3: a Blocked export intent (recovery_required) was re-run on every editing call and also
        // blocked an unrelated icon save until .maker/exports/pending.json was removed by hand.
        [Fact]
        public async Task A_stuck_export_recovery_does_not_block_an_unrelated_icon_save()
        {
            var exporter = Exporter();
            var boundary = typeof(MakerPackExporter).GetField("_exportBoundary", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            boundary.SetValue(exporter, (Action<string>)(point => { if (point == "after-intent") throw new IOException("crash after the intent is durable"); }));

            var interrupted = await exporter.ExportAsync(Request(), default);
            Assert.False(interrupted.Ok);
            // An outside edit/move of the staged archive (the rare case the review flags): the intent can never complete now.
            foreach (var stray in Directory.GetFiles(Exports, ".*")) File.Delete(stray);

            var stuckCheck = await Exporter().CheckAsync(PackId, default);
            Assert.Equal(MakerStudioErrorCodes.RecoveryRequired, stuckCheck.Error);

            var icon = await Exporter().SaveIconAsync(PackId, Png(128, 128), default);
            Assert.True(icon.Ok, icon.Message); // failed before the fix: the stuck export intent blocked an unrelated icon save

            File.Delete(Path.Combine(Pack, ".maker", "exports", "pending.json"));
            var recovered = await Exporter().SaveIconAsync(PackId, Png(128, 128), default);
            Assert.True(recovered.Ok, recovered.Message);
        }

        // Milestone-1 P3 B3: a stuck intent holds back export and the export check only; the message names the one
        // manual step that gives it up.
        [Fact]
        public async Task A_stuck_export_still_lets_an_earlier_export_download_and_names_the_file_that_gives_it_up()
        {
            var first = await Exporter().ExportAsync(Request("1"), default);
            Assert.True(first.Ok, first.Message);
            var exporter = Exporter();
            Interrupt(exporter, "after-intent");
            Assert.False((await exporter.ExportAsync(Request("2", expected: "0.1.1"), default)).Ok);
            foreach (var stray in Directory.GetFiles(Exports, ".*")) File.Delete(stray);
            var pending = Path.Combine(Pack, ".maker", "exports", "pending.json");
            var intent = File.ReadAllBytes(pending);

            var stuck = await Exporter().ExportAsync(Request("3", expected: "0.1.1"), default);
            Assert.Equal(MakerStudioErrorCodes.RecoveryRequired, stuck.Error);
            Assert.Contains("pending.json' out of the pack folder", stuck.Message);
            Assert.Equal(intent, File.ReadAllBytes(pending)); // the refused export left the stuck intent as it was

            var download = await Exporter().ReadExportAsync(PackId, first.Value!.FileName, default);
            Assert.True(download.Ok, download.Message); // was recovery_required before the fix
            Assert.Equal(first.Value.SizeBytes, download.Value!.Bytes!.LongLength);

            File.Move(pending, Path.Combine(_root, "pending.json")); // the step the message names
            var again = await Exporter().ExportAsync(Request("3", expected: "0.1.1"), default);
            Assert.True(again.Ok, again.Message);
            Assert.Equal("0.1.2", again.Value!.Version);
        }

        [Fact]
        public async Task A_finished_export_whose_bookkeeping_failed_is_retired_after_its_zip_was_moved_away()
        {
            var exporter = Exporter();
            Interrupt(exporter, "after-receipt"); // housekeeping fails after the receipt is durable
            var done = await exporter.ExportAsync(Request(), default);
            Assert.True(done.Ok, done.Message);
            var pending = Path.Combine(Pack, ".maker", "exports", "pending.json");
            Assert.True(File.Exists(pending));
            // The creator moves finished exports out of the folder, as the storage message tells them to.
            File.Move(Path.Combine(Exports, done.Value!.FileName), Path.Combine(_root, done.Value.FileName));

            var check = await Exporter().CheckAsync(PackId, default);
            Assert.True(check.Ok, check.Message); // was recovery_required ("the original archive is missing or changed")
            Assert.Equal("0.1.1", check.Value!.Version);
            Assert.Equal(done.Value.FileName, check.Value.LastExport!.FileName);
            Assert.False(File.Exists(pending));
        }

        private static void Interrupt(MakerPackExporter exporter, string point) =>
            typeof(MakerPackExporter).GetField("_exportBoundary", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .SetValue(exporter, (Action<string>)(p => { if (p == point) throw new IOException("injected interruption at " + point); }));

        [Fact]
        public void A_new_pack_takes_an_optional_description()
        {
            var creator = new MakerPackCreator(() => Mods, _ => false, code => code == "th" ? "cwy.thai" : null, () => "1.18.0", () => "0.9.2", (t, d) => { }, _clock);
            var request = new MakerStudioCreatePackRequest
            {
                OperationId = Op("c"), Id = "lab.described", Name = "Described", Description = "A Thai dub of the greetings.",
                Language = new MakerStudioLanguage { Code = "th" },
            };

            var created = creator.CreateNow(request, default);
            var tooLong = creator.CreateNow(new MakerStudioCreatePackRequest
            {
                OperationId = Op("d"), Id = "lab.long", Name = "Long", Description = new string('x', 501), Language = new MakerStudioLanguage { Code = "th" },
            }, default);

            Assert.True(created.Ok, created.Message);
            Assert.Contains("\"description\": \"A Thai dub of the greetings.\"", File.ReadAllText(Path.Combine(Mods, "lab.described", "manifest.json")));
            Assert.Equal(MakerErrorCodes.BadRequest, tooLong.Error);
        }
    }
}
