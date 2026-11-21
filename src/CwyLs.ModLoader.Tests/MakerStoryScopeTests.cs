using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CwyLs.ModLoader.Core.Api.Maker;
using CwyLs.ModLoader.Core.Api.Maker.Catalog;
using CwyLs.ModLoader.Core.Api.Maker.Studio;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>CO2-A: invented mixed localization data, with scenario references as the sole line boundary.</summary>
    public sealed class MakerStoryScopeTests : IDisposable, IMakerStudioCatalogSource
    {
        private const string Pack = "lab.story";
        private readonly string _root = Path.Combine(Path.GetTempPath(), "cwy-story-" + Guid.NewGuid().ToString("N"));
        private readonly MakerStudioCatalog _catalog;
        private bool _novelLoaded = true;

        public MakerStoryScopeTests()
        {
            Directory.CreateDirectory(Path.Combine(_root, "data", "subtitles"));
            File.WriteAllText(Path.Combine(_root, "manifest.json"), "{\"id\":\"lab.story\",\"name\":\"Story lab\",\"version\":\"0.1.0\",\"loaderVersion\":\"0.9.2\"}");
            File.WriteAllText(Path.Combine(_root, "data", "subtitles", "th.json"),
                "{\"story_quiet\":\"Quiet translation\",\"ui_settings\":\"Keep UI translation\"}");
            _catalog = new MakerStudioCatalog(this);
        }

        public Task<MakerResult<MakerStudioCatalogSnapshot>> SnapshotAsync(bool editorVerdicts, CancellationToken cancel)
        {
            var rows = new[]
            {
                new MakerGameRow("ui_settings", null, "ui", "Invented UI ja", "Settings sentinel"),
                new MakerGameRow("story_quiet", null, null, "Invented quiet ja", "Quiet story"),
                new MakerGameRow("ui_voiced", "Voice_UI", "story", "Invented UI ja", "Voiced UI sentinel"),
                new MakerGameRow("story_voice", "Voice_Story", "story", "Invented story ja", "Spoken story"),
                new MakerGameRow("0", null, "ui", "", "Command sentinel"),
                new MakerGameRow("cwy_say_runtime", "Voice_Runtime", "story", "", "Runtime sentinel"),
            };
            var novel = new[]
            {
                new MakerGameNovelRow("n1", null, "story_quiet", "Heroine", null, null),
                new MakerGameNovelRow("n2", "story", "story_voice", "Heroine", null, null),
                new MakerGameNovelRow("n3", "other", "story_voice", "Heroine", null, null),
                new MakerGameNovelRow("command", "ui", "0", null, null, null),
                new MakerGameNovelRow("runtime", "story", "cwy_say_runtime", "Heroine", null, null),
            };
            var source = new MakerPackSource(Pack, "Story lab", "0.1.0", _root, false, new[] { "th" });
            var sources = new[] { source };
            var game = new MakerGameData(rows, null, null, _novelLoaded ? novel : null, null);
            var view = new MakerCatalogView(game, MakerPackScanner.Scan(sources));
            return Task.FromResult(MakerResult<MakerStudioCatalogSnapshot>.Success(new MakerStudioCatalogSnapshot(
                view, new[] { new MakerPack { Id = Pack, Editable = true, Languages = new[] { "th" } } }, sources, null)));
        }

        [Fact]
        public async Task Catalog_search_groups_and_progress_cover_only_unique_scenario_lines_including_silent_ungrouped_lines()
        {
            var page = await _catalog.LinesAsync(new MakerStudioLineQuery { PackId = Pack, Lang = "th" }, default);
            Assert.True(page.Ok, page.Message);
            Assert.Equal(2, page.Value!.Total);
            Assert.Equal(new[] { "story_quiet", "story_voice" }, page.Value.Items.Select(x => x.Id));
            foreach (var needle in new[] { "ui_", "sentinel", "Keep UI translation" })
            {
                var search = await _catalog.LinesAsync(new MakerStudioLineQuery { PackId = Pack, Lang = "th", Search = needle }, default);
                Assert.True(search.Ok, search.Message);
                Assert.Equal(0, search.Value!.Total);
            }

            var groups = await _catalog.GroupsAsync(Pack, "th", default);
            Assert.True(groups.Ok, groups.Message);
            Assert.Equal(2, groups.Value!.Overall.TotalLines);
            Assert.Equal(1, groups.Value.Overall.DubbableLines);
            Assert.Equal(1, groups.Value.Overall.SubtitlePresent);
            Assert.Equal(2, groups.Value.Groups.Sum(g => g.Progress.TotalLines));
            Assert.DoesNotContain(groups.Value.Groups, g => g.Id == "ui");
            Assert.Contains(groups.Value.Groups, g => g.Id == "ungrouped");
            Assert.Equal(2, (await _catalog.LineAsync(Pack, "th", "story_voice", null, default)).Value!.Context.Occurrences.Count);
        }

        [Theory]
        [InlineData("ui_settings")]
        [InlineData("ui_voiced")]
        [InlineData("0")]
        [InlineData("cwy_say_runtime")]
        public async Task Direct_line_reads_refuse_non_story_ids(string id)
        {
            var line = await _catalog.LineAsync(Pack, "th", id, null, default);
            Assert.False(line.Ok);
            Assert.Equal(MakerErrorCodes.NotFound, line.Error);
        }

        [Fact]
        public async Task Missing_scenario_data_never_falls_back_to_the_whole_localization_table()
        {
            _novelLoaded = false;
            var page = await _catalog.LinesAsync(new MakerStudioLineQuery { PackId = Pack, Lang = "th" }, default);
            Assert.True(page.Ok, page.Message);
            Assert.Equal(0, page.Value!.Total);
            Assert.Equal(0, (await _catalog.GroupsAsync(Pack, "th", default)).Value!.Overall.TotalLines);
        }

        [Fact]
        public async Task Paused_playback_still_returns_before_any_story_catalog_wait()
        {
            var paused = true;
            var scopeCalls = 0;
            var editor = new MakerStudioEditor((_, _) => MakerResult<MakerProjectStore>.Success(new MakerProjectStore(_root)),
                (_, _, _, _, _) => new MakerSubtitleLiveResult(),
                activity: () => new MakerGameActivitySnapshot { Paused = paused },
                lineScope: (_, _, _) =>
                {
                    scopeCalls++;
                    return Task.FromResult(MakerResult<bool>.Fail(MakerErrorCodes.NotFound, "not a story line"));
                });
            var request = new MakerStudioPlayRequest { Lang = "th", Id = "ui_settings", Variant = "original" };
            Assert.Equal(MakerStudioErrorCodes.GamePaused, (await editor.PlayAsync(request, default)).Error);
            Assert.Equal(0, scopeCalls);
            paused = false;
            Assert.Equal(MakerErrorCodes.NotFound, (await editor.PlayAsync(request, default)).Error);
            Assert.Equal(1, scopeCalls);
        }

        [Fact]
        public async Task Every_editor_line_route_checks_story_scope_before_reading_or_editing_creator_state()
        {
            var store = new MakerProjectStore(_root);
            var before = File.ReadAllBytes(Path.Combine(_root, "data", "subtitles", "th.json"));
            var editor = new MakerStudioEditor((_, _) => MakerResult<MakerProjectStore>.Success(store),
                (_, _, _, _, _) => new MakerSubtitleLiveResult(),
                lineScope: async (lang, id, cancel) =>
                {
                    var line = await _catalog.LineAsync(Pack, lang, id, null, cancel);
                    return line.Ok ? MakerResult<bool>.Success(true) : MakerResult<bool>.Fail(line.Error!, line.Message!);
                });
            void Refused<T>(MakerResult<T> result)
            {
                Assert.False(result.Ok);
                Assert.Equal(MakerErrorCodes.NotFound, result.Error);
            }
            var id = "ui_settings";
            var op = Guid.NewGuid().ToString("N");
            var revision = store.ReadLine("th", id).Value!.Revision;
            Refused(await editor.SaveSubtitleAsync(new MakerStudioSubtitleRequest
            {
                PackId = Pack, Lang = "th", Id = id, OperationId = op, ExpectedRevision = revision, Text = "Do not edit UI",
            }, default));
            Refused(await editor.SaveV1SubtitleAsync(Pack, "th", id, "Do not edit UI", default));
            Refused(await editor.UpdateWorkflowAsync(new MakerStudioWorkflowRequest
            {
                PackId = Pack, Lang = "th", Id = id, OperationId = op, ExpectedRevision = revision, Status = "done",
            }, default));
            Refused(await editor.HistoryAsync(Pack, "th", id, 0, 20, default));
            Refused(await editor.TakesAsync(Pack, "th", id, default));
            Refused(await editor.ReadAudioAsync(Pack, "th", id, op, "source", default));
            Refused(await editor.PlayAsync(new MakerStudioPlayRequest { PackId = Pack, Lang = "th", Id = id, Variant = "original" }, default));
            Refused(await editor.UnpublishAsync(new MakerStudioUnpublishRequest
            {
                PackId = Pack, Lang = "th", Id = id, OperationId = op, ExpectedRevision = revision, ExpectedClipRevision = revision,
            }, default));
            Refused(await editor.DeleteTakeAsync(new MakerStudioDeleteTakeRequest
            {
                PackId = Pack, Lang = "th", Id = id, OperationId = op, ExpectedRevision = revision, TakeId = op,
            }, default));
            Assert.Equal(before, File.ReadAllBytes(Path.Combine(_root, "data", "subtitles", "th.json")));
            var quietRevision = store.ReadLine("th", "story_quiet").Value!.Revision;
            var quiet = await editor.SaveSubtitleAsync(new MakerStudioSubtitleRequest
            {
                PackId = Pack, Lang = "th", Id = "story_quiet", OperationId = op, ExpectedRevision = quietRevision, Text = "Edited quiet story",
            }, default);
            Assert.True(quiet.Ok, quiet.Message);
            Assert.Contains("Keep UI translation", File.ReadAllText(Path.Combine(_root, "data", "subtitles", "th.json")));
        }

        [Fact]
        public async Task Export_progress_ignores_UI_gaps_and_export_preserves_UI_files_and_entries_byte_for_byte()
        {
            // Complete the voiced story's subtitle; unfilled UI strings must add no progress gaps.
            var table = "{\"story_quiet\":\"Quiet\",\"story_voice\":\"Spoken\",\"ui_settings\":\"Keep UI translation\"}";
            var uiFile = "{\"ui_voiced\":{\"En\":\"Keep original UI override\"}}";
            File.WriteAllText(Path.Combine(_root, "data", "subtitles", "th.json"), table);
            File.WriteAllText(Path.Combine(_root, "data", "localization.json"), uiFile);
            var exporter = new MakerPackExporter((_, _) => MakerResult<MakerProjectStore>.Success(new MakerProjectStore(_root)),
                _ => null, () => Path.Combine(_root, "..", Path.GetFileName(_root) + "-exports"), progress: _catalog.GroupsAsync);
            var check = await exporter.CheckAsync(Pack, default);
            Assert.True(check.Ok, check.Message);
            var progress = check.Value!.Checks.Single(x => x.Id == "progress");
            Assert.Equal("th: 1 of 1 voiced line(s) without a dub, 0 of 2 line(s) without a subtitle.", progress.Message);
            var exported = await exporter.ExportAsync(new MakerExportRequest
            {
                PackId = Pack, OperationId = Guid.NewGuid().ToString("N"), ExpectedVersion = "0.1.0", Bump = "patch",
            }, default);
            Assert.True(exported.Ok, exported.Message);
            var download = await exporter.ReadExportAsync(Pack, exported.Value!.FileName, default);
            Assert.True(download.Ok, download.Message);
            using var zip = new ZipArchive(new MemoryStream(download.Value!.Bytes));
            using var subtitles = new StreamReader(zip.GetEntry(Pack + "/data/subtitles/th.json")!.Open());
            using var localization = new StreamReader(zip.GetEntry(Pack + "/data/localization.json")!.Open());
            Assert.Equal(table, subtitles.ReadToEnd());
            Assert.Equal(uiFile, localization.ReadToEnd());
        }

        public void Dispose()
        {
            Directory.Delete(_root, true);
            var exports = Path.Combine(_root, "..", Path.GetFileName(_root) + "-exports");
            if (Directory.Exists(exports)) Directory.Delete(exports, true);
        }
    }
}
