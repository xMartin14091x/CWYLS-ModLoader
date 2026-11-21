using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CwyLs.ModLoader.Core.Api.Maker;
using CwyLs.ModLoader.Core.Api.Maker.Catalog;
using CwyLs.ModLoader.Core.Api.Maker.Studio;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// W2b-05: the Studio catalog's answers over real temporary pack folders: owned versus effective state, the eight
    /// pack-scoped filters, search, group and paging, additive progress, revisions that agree with the mutation path,
    /// the catalog revision, pack states, and the refusals. Every row, text and clip is invented.
    /// </summary>
    public sealed class MakerStudioViewTests : IDisposable
    {
        private const string Mine = "lab.mine";
        private const string Other = "lab.other";

        private readonly string _root = Path.Combine(Path.GetTempPath(), "cwy-studio-view-" + Guid.NewGuid().ToString("N"));
        private readonly List<MakerPackSource> _extraSources = new List<MakerPackSource>();
        private MakerStudioCatalog _catalog = null!;

        public MakerStudioViewTests()
        {
            Directory.CreateDirectory(ModsRoot);
            Directory.CreateDirectory(Folder(Mine));
            Directory.CreateDirectory(Folder(Other));
            _catalog = new MakerStudioCatalog(new Source(Snapshot));
        }

        private string ModsRoot => Path.Combine(_root, "CWYLS Mods");

        public void Dispose()
        {
            try { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
            catch (IOException) { /* a leftover temp folder is harmless */ }
        }

        private string Folder(string pack) => Path.Combine(ModsRoot, pack);

        private void Write(string pack, string relative, string text) => WriteBytes(pack, relative, new UTF8Encoding(false).GetBytes(text));

        private void WriteBytes(string pack, string relative, byte[] bytes)
        {
            var path = Path.Combine(Folder(pack), relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
        }

        private static List<MakerGameRow> Rows() => new List<MakerGameRow>
        {
            new MakerGameRow("line_a", "Voice_A", "morning", "A ja", "A en"),
            new MakerGameRow("line_b", "Voice_B", "morning", "B ja", "B en"),
            new MakerGameRow("line_c", null, "focus", "C ja", "C en"),
            new MakerGameRow("line_d", "Voice_D", null, "D ja", "D en"),
            new MakerGameRow("line_e_omit", "Voice_E", "morning", "E ja", "E en"),
            new MakerGameRow("line_alias", "Voice_A", "focus", "Alias ja", "Alias en"),
        };

        private static Dictionary<string, double> Clips() => new Dictionary<string, double>
        {
            ["Voice_A"] = 2.0, ["Voice_B"] = 3.0, ["Voice_D"] = 1.5,
        };

        private static List<MakerGameNovelRow> Novel() => new List<MakerGameNovelRow>
        {
            new MakerGameNovelRow("n1", "morning", "line_a", "Heroine", "1", "2"),
            new MakerGameNovelRow("n2", "morning", "line_b", "Heroine", null, null),
            new MakerGameNovelRow("n3", "focus", "line_c", "Heroine", null, null),
            new MakerGameNovelRow("n4", "focus", "line_alias", "Heroine", null, null),
            new MakerGameNovelRow("n5", null, "line_d", "Heroine", null, null),
            new MakerGameNovelRow("n6", "morning", "line_e_omit", "Heroine", null, null),
        };

        private static List<MakerGameGroup> Groups() => new List<MakerGameGroup>
        {
            new MakerGameGroup("focus", "Focus time"),
            new MakerGameGroup("morning", "Good morning"),
        };

        private MakerStudioCatalogSnapshot Snapshot()
        {
            var sources = new List<MakerPackSource>
            {
                new MakerPackSource(Mine, "Mine", "1.0.0", Folder(Mine), false, new[] { "th" }),
                new MakerPackSource(Other, "Other", "1.0.0", Folder(Other), false, new[] { "th" }),
            };
            sources.AddRange(_extraSources);
            var view = new MakerCatalogView(new MakerGameData(Rows(), Clips(), null, Novel(), Groups()),
                MakerPackScanner.Scan(sources), null, new MakerDubLengths().LengthOf);
            return new MakerStudioCatalogSnapshot(view, view.Packs(sources, ModsRoot, MakerPackRules.IsWritableFolder), sources, ModsRoot);
        }

        private sealed class Source : IMakerStudioCatalogSource
        {
            private readonly Func<MakerStudioCatalogSnapshot> _make;
            internal Source(Func<MakerStudioCatalogSnapshot> make) => _make = make;

            public Task<MakerResult<MakerStudioCatalogSnapshot>> SnapshotAsync(bool editorVerdicts, CancellationToken cancel) =>
                Task.FromResult(MakerResult<MakerStudioCatalogSnapshot>.Success(_make()));
        }

        private static T Ok<T>(MakerResult<T> result)
        {
            Assert.True(result.Ok, result.Error + ": " + result.Message);
            return result.Value!;
        }

        private static void Refused<T>(MakerResult<T> result, string code, string? contains = null)
        {
            Assert.False(result.Ok);
            Assert.Equal(code, result.Error);
            if (contains != null) Assert.Contains(contains, result.Message, StringComparison.OrdinalIgnoreCase);
        }

        private MakerStudioLineQuery Query(string pack = Mine, string filter = "all", string? q = null, string? group = null,
            int offset = 0, int limit = 100, string? revision = null) =>
            new MakerStudioLineQuery { PackId = pack, Lang = "th", Filter = filter, Search = q, Group = group, Offset = offset, Limit = limit, CatalogRevision = revision };

        private MakerStudioLinePage Page(MakerStudioLineQuery query) => Ok(_catalog.LinesNow(query));

        private string[] Ids(MakerStudioLineQuery query) => Page(query).Items.Select(l => l.Id).ToArray();

        private MakerStudioLine Line(string id, string pack = Mine, string? occurrence = null) =>
            Ok(_catalog.LineNow(pack, "th", id, occurrence));

        private void SetWorkflow(string id, string? status, bool? starred = null, string pack = Mine)
        {
            var store = new MakerProjectStore(Folder(pack));
            var revision = store.ReadLine("th", id).Value!.Revision;
            var result = new MakerWorkflowStore(store).Update(new MakerStudioWorkflowRequest
            {
                PackId = pack, Lang = "th", Id = id, ExpectedRevision = revision, OperationId = Guid.NewGuid().ToString("N"),
                Status = status, Starred = starred,
            });
            Assert.True(result.Ok, result.Message);
        }

        private void Seed()
        {
            Write(Mine, "data/subtitles/th.json", "{ \"line_a\": \"mine A\", \"line_b\": \"mine B\" }");
            Write(Other, "data/subtitles/th.json", "{ \"line_a\": \"other A\" }");
            WriteBytes(Mine, "audio/voice/th/Voice_B.wav", MakerAudioFixtures.Wav(8000, 1, 8000));
            WriteBytes(Other, "audio/voice/th/Voice_A.wav", MakerAudioFixtures.Wav(8000, 1, 16000));
        }

        // ----- owned versus effective --------------------------------------------------------------------------

        [Fact]
        public void Owned_state_is_the_selected_packs_while_the_inherited_fields_follow_the_winning_pack()
        {
            Seed();

            var a = Line("line_a");
            Assert.Equal("mine A", a.OwnedSubtitle);
            Assert.Equal("other A", a.Subtitle);
            Assert.Equal(Other, a.SubtitlePack);
            Assert.False(a.OwnedDub);
            Assert.Equal(Other, a.DubPack);
            Assert.Equal(2.0, a.DubDurationSec);

            var b = Line("line_b");
            Assert.Equal("mine B", b.Subtitle);
            Assert.Equal(Mine, b.SubtitlePack);
            Assert.True(b.OwnedDub);
            Assert.Equal(Mine, b.DubPack);

            var alias = Line("line_alias");
            Assert.Null(alias.OwnedSubtitle);
            Assert.Null(alias.Subtitle);
            Assert.False(alias.OwnedDub);
            Assert.Equal(Other, alias.DubPack);

            var seenFromOther = Line("line_a", Other);
            Assert.Equal("other A", seenFromOther.OwnedSubtitle);
            Assert.True(seenFromOther.OwnedDub);
            Assert.Equal("A en", seenFromOther.En);
            Assert.Equal("morning", seenFromOther.Group);
            Assert.Equal(2.0, seenFromOther.DurationSec);
        }

        [Fact]
        public void A_dub_made_outside_Studio_in_another_format_still_counts_as_owned()
        {
            WriteBytes(Mine, "audio/voice/th/Voice_D.ogg", new byte[] { (byte)'O', (byte)'g', (byte)'g', (byte)'S', 0, 0, 0, 0 });

            var line = Line("line_d");

            Assert.True(line.OwnedDub);
            Assert.Equal(0, line.TakeCount);
            Assert.Equal(Mine, line.DubPack);
        }

        [Fact]
        public void An_empty_subtitle_entry_is_no_subtitle()
        {
            Write(Mine, "data/subtitles/th.json", "{ \"line_a\": \"\" }");

            Assert.Null(Line("line_a").OwnedSubtitle);
            Assert.Contains("line_a", Ids(Query(filter: "missing-subtitle")));
        }

        // ----- filters, search, group, paging -------------------------------------------------------------------

        [Fact]
        public void Every_filter_selects_by_the_selected_packs_own_state_and_omitted_lines_need_nothing()
        {
            Seed();
            SetWorkflow("line_a", "draft", true);
            SetWorkflow("line_b", "done");
            SetWorkflow("line_d", "needs-retake");

            Assert.Equal(new[] { "line_a", "line_b", "line_c", "line_d", "line_alias", "line_e_omit" }, Ids(Query()));
            Assert.Equal(new[] { "line_c", "line_alias", "line_e_omit" }, Ids(Query(filter: "todo")));
            Assert.Equal(new[] { "line_a" }, Ids(Query(filter: "draft")));
            Assert.Equal(new[] { "line_b" }, Ids(Query(filter: "done")));
            Assert.Equal(new[] { "line_d" }, Ids(Query(filter: "needs-retake")));
            Assert.Equal(new[] { "line_a" }, Ids(Query(filter: "starred")));
            Assert.Equal(new[] { "line_c", "line_d", "line_alias" }, Ids(Query(filter: "missing-subtitle")));
            Assert.Equal(new[] { "line_a", "line_d", "line_alias" }, Ids(Query(filter: "missing-dub")));
        }

        [Fact]
        public void The_same_lines_filter_differently_for_another_selected_pack()
        {
            Seed();
            SetWorkflow("line_a", "draft", pack: Mine);

            Assert.Equal(new[] { "line_a" }, Ids(Query(Mine, "draft")));
            Assert.Empty(Ids(Query(Other, "draft")));
            Assert.Equal(new[] { "line_b", "line_c", "line_d", "line_alias" }, Ids(Query(Other, "missing-subtitle")));
            Assert.Equal(new[] { "line_b", "line_d" }, Ids(Query(Other, "missing-dub")));
        }

        [Fact]
        public void Search_group_and_paging_narrow_the_page_and_keep_the_total()
        {
            Seed();

            Assert.Equal(new[] { "line_b" }, Ids(Query(q: "LINE_B")));
            Assert.Equal(new[] { "line_a" }, Ids(Query(q: "mine a")));
            Assert.Equal(new[] { "line_alias" }, Ids(Query(q: "voice_a", group: "focus")));
            Assert.Equal(new[] { "line_c", "line_alias" }, Ids(Query(group: "focus")));
            Assert.Equal(new[] { "line_d" }, Ids(Query(group: "ungrouped")));
            Assert.Equal(new[] { "line_e_omit" }, Ids(Query(group: "(omitted)")));
            Assert.Empty(Ids(Query(group: "no-such-group")));

            var middle = Page(Query(offset: 2, limit: 2));
            Assert.Equal(6, middle.Total);
            Assert.Equal(2, middle.Offset);
            Assert.Equal(new[] { "line_c", "line_d" }, middle.Items.Select(l => l.Id).ToArray());
            var past = Page(Query(offset: 40));
            Assert.Equal(6, past.Total);
            Assert.Empty(past.Items);
        }

        // ----- progress ------------------------------------------------------------------------------------------

        [Fact]
        public void Progress_is_additive_text_only_lines_are_not_dubbable_and_each_line_has_one_primary_group()
        {
            Seed();
            SetWorkflow("line_a", "draft", true);
            SetWorkflow("line_b", "done");
            SetWorkflow("line_d", "needs-retake", true);

            var groups = Ok(_catalog.GroupsNow(Mine, "th"));

            Assert.Equal(new[] { "focus", "morning", "ungrouped", "(omitted)" }, groups.Groups.Select(g => g.Id).ToArray());
            Assert.Equal(new[] { 0, 1, 2, 3 }, groups.Groups.Select(g => g.Order).ToArray());
            Assert.Equal(new string?[] { "group.focus", "group.morning", "group.ungrouped", null }, groups.Groups.Select(g => g.LabelKey).ToArray());
            Assert.Equal(new string?[] { "Focus time", "Good morning", null, null }, groups.Groups.Select(g => g.Title).ToArray());

            var overall = groups.Overall;
            Assert.Equal(6, overall.TotalLines);
            Assert.Equal(4, overall.DubbableLines);        // a, b, d, alias: not the text-only line_c, not the omitted line_e
            Assert.Equal(2, overall.SubtitlePresent);
            Assert.Equal(1, overall.DubPresent);
            Assert.Equal(3, overall.Todo);
            Assert.Equal(1, overall.Draft);
            Assert.Equal(1, overall.Done);
            Assert.Equal(1, overall.NeedsRetake);
            Assert.Equal(2, overall.Starred);
            foreach (var field in new Func<MakerStudioProgress, int>[]
                { p => p.TotalLines, p => p.DubbableLines, p => p.SubtitlePresent, p => p.DubPresent, p => p.Todo, p => p.Draft, p => p.Done, p => p.NeedsRetake, p => p.Starred })
            {
                Assert.Equal(field(overall), groups.Groups.Sum(g => field(g.Progress)));
            }

            var focus = groups.Groups[0].Progress;
            Assert.Equal(2, focus.TotalLines);
            Assert.Equal(1, focus.DubbableLines);
            var omitted = groups.Groups[3].Progress;
            Assert.Equal(1, omitted.TotalLines);
            Assert.Equal(0, omitted.DubbableLines);
        }

        [Fact]
        public void A_line_that_two_scenes_speak_is_counted_once()
        {
            var novel = Novel();
            novel.Add(new MakerGameNovelRow("n9", "focus", "line_a", "Heroine", null, null));
            var view = new MakerCatalogView(new MakerGameData(Rows(), Clips(), null, novel, Groups()), MakerPackContent.Empty);
            var sources = new List<MakerPackSource> { new MakerPackSource(Mine, "Mine", "1.0.0", Folder(Mine), false, new[] { "th" }) };
            var catalog = new MakerStudioCatalog(new Source(() =>
                new MakerStudioCatalogSnapshot(view, view.Packs(sources, ModsRoot, MakerPackRules.IsWritableFolder), sources, ModsRoot)));

            var groups = Ok(catalog.GroupsNow(Mine, "th"));
            var line = Ok(catalog.LineNow(Mine, "th", "line_a", null));

            Assert.Equal(6, groups.Overall.TotalLines);
            Assert.Equal(6, groups.Groups.Sum(g => g.Progress.TotalLines));
            Assert.Equal(2, line.Context.Occurrences.Count);
            Assert.Equal("morning", line.Group);
        }

        [Fact]
        public void A_line_whose_id_cannot_name_a_Studio_file_is_listed_read_only_with_a_reason_code_and_others_are_not()
        {
            // TD-3 / T-059: a Windows device name or a trailing dot cannot name .maker/lines/<lang>/<id>.json.
            var rows = Rows();
            rows.Add(new MakerGameRow("nul", "Voice_N", "focus", "N ja", "N en"));
            rows.Add(new MakerGameRow("line_f.", "Voice_F", "focus", "F ja", "F en"));
            var novel = Novel();
            novel.Add(new MakerGameNovelRow("n7", "focus", "nul", "Heroine", null, null));
            novel.Add(new MakerGameNovelRow("n8", "focus", "line_f.", "Heroine", null, null));
            var view = new MakerCatalogView(new MakerGameData(rows, Clips(), null, novel, Groups()), MakerPackContent.Empty);
            var sources = new List<MakerPackSource> { new MakerPackSource(Mine, "Mine", "1.0.0", Folder(Mine), false, new[] { "th" }) };
            var catalog = new MakerStudioCatalog(new Source(() =>
                new MakerStudioCatalogSnapshot(view, view.Packs(sources, ModsRoot, MakerPackRules.IsWritableFolder), sources, ModsRoot)));

            Assert.Equal(MakerStudioLineReadOnly.IdNotAFileName, Ok(catalog.LineNow(Mine, "th", "nul", null)).ReadOnly);
            Assert.Equal(MakerStudioLineReadOnly.IdNotAFileName, Ok(catalog.LineNow(Mine, "th", "line_f.", null)).ReadOnly);
            Assert.Null(Ok(catalog.LineNow(Mine, "th", "line_a", null)).ReadOnly);
            Assert.Equal(MakerStudioLineReadOnly.IdNotAFileName, MakerStudioCatalogJson.Line(Ok(catalog.LineNow(Mine, "th", "nul", null)))["readOnly"]);
            Assert.False(MakerStudioPaths.CanNameFile("con.001"));
            Assert.True(MakerStudioPaths.CanNameFile("Voice_MainScenario_12(5)_001"));
        }

        // ----- revisions ------------------------------------------------------------------------------------------

        [Fact]
        public void A_page_revision_is_the_one_the_mutation_path_compares_for_no_file_in_sync_and_drifted_lines()
        {
            Seed();
            var store = new MakerProjectStore(Folder(Mine));
            string Mutation(string id) => store.ReadLine("th", id).Value!.Revision;
            string Listed(string id) => Page(Query()).Items.Single(l => l.Id == id).Revision;

            // No line file yet: the initial revision.
            Assert.Equal(Mutation("line_c"), Listed("line_c"));
            Assert.Equal(Mutation("line_a"), Listed("line_a"));

            // A workflow save creates the file; it is in sync with the subtitle table.
            SetWorkflow("line_b", "draft");
            Assert.Equal(Mutation("line_b"), Listed("line_b"));

            // The subtitle changes outside Studio: the file drifts and gets the external revision.
            var inSync = Mutation("line_b");
            Write(Mine, "data/subtitles/th.json", "{ \"line_a\": \"mine A\", \"line_b\": \"edited elsewhere\" }");
            Assert.NotEqual(inSync, Mutation("line_b"));
            Assert.Equal(Mutation("line_b"), Listed("line_b"));
            Assert.Equal(Mutation("line_b"), Line("line_b").Revision);
        }

        [Fact]
        public void A_line_with_takes_reports_its_take_count_and_the_authoritative_revision()
        {
            Seed();
            var store = new MakerProjectStore(Folder(Mine));
            var takes = new MakerTakeStore(store);
            var created = takes.Create(new MakerStudioCreateTakeRequest
            {
                PackId = Mine, Lang = "th", Id = "line_a", OperationId = Guid.NewGuid().ToString("N"),
                ExpectedRevision = takes.ReadTakes("th", "line_a").Value!.Revision, Origin = "own",
                Consent = new MakerStudioConsent { Confirmed = true, StatementVersion = MakerStudioContract.ConsentVersion },
            });
            Assert.True(created.Ok, created.Message);

            var listed = Page(Query()).Items.Single(l => l.Id == "line_a");
            var single = Line("line_a");

            Assert.Equal(1, listed.TakeCount);
            Assert.Equal(1, single.TakeCount);
            Assert.Equal(store.ReadLine("th", "line_a").Value!.Revision, listed.Revision);
            Assert.Equal(listed.Revision, single.Revision);
            Assert.Null(single.PublishedTakeId);
        }

        [Fact]
        public void The_catalog_revision_keeps_a_paging_session_consistent_and_changes_when_creator_state_changes()
        {
            Seed();
            var first = Page(Query(limit: 2));
            Assert.Equal(first.CatalogRevision, Page(Query(limit: 2, offset: 2, revision: first.CatalogRevision)).CatalogRevision);
            Assert.Equal(first.CatalogRevision, Ok(_catalog.GroupsNow(Mine, "th")).CatalogRevision);

            SetWorkflow("line_a", "draft");

            Refused(_catalog.LinesNow(Query(offset: 2, revision: first.CatalogRevision)), MakerStudioErrorCodes.Conflict, "paging");
            var second = Page(Query(limit: 2));
            Assert.NotEqual(first.CatalogRevision, second.CatalogRevision);
            Assert.Equal(second.CatalogRevision, Ok(_catalog.GroupsNow(Mine, "th")).CatalogRevision);
            Assert.NotEqual(second.CatalogRevision, Page(Query(Other, limit: 2)).CatalogRevision);
        }

        [Fact]
        public void The_catalog_revision_moves_when_another_packs_subtitle_changes_the_winning_text()
        {
            Seed();
            var before = Page(Query()).CatalogRevision;

            Write(Other, "data/subtitles/th.json", "{ \"line_a\": \"other A\", \"line_b\": \"other B\" }");

            Assert.NotEqual(before, Page(Query()).CatalogRevision);
            Assert.Equal("other B", Line("line_b").Subtitle);
        }

        [Fact]
        public void The_clip_revision_is_shared_by_aliases_and_moves_when_the_selected_pack_gets_a_clip()
        {
            var before = Line("line_a").ClipRevision;
            var alias = Line("line_alias").ClipRevision;
            var other = Line("line_d").ClipRevision;
            Assert.Equal(before, alias);
            Assert.NotEqual(before, other);
            Assert.Equal(before, Page(Query()).Items.Single(l => l.Id == "line_a").ClipRevision);

            WriteBytes(Mine, "audio/voice/th/Voice_A.wav", MakerAudioFixtures.Wav(8000, 1, 4000));

            var after = Line("line_a").ClipRevision;
            Assert.NotEqual(before, after);
            Assert.Equal(after, Line("line_alias").ClipRevision);
            Assert.Equal(other, Line("line_d").ClipRevision);
            Assert.False(string.IsNullOrEmpty(Line("line_c").ClipRevision));
        }

        // Merge (W2b M5d + W2c M6a): the catalogue reports the clip revision and publication that publish checks.
        [Fact]
        public async Task The_catalogue_reports_the_same_clip_revision_and_publication_as_publish_for_every_alias()
        {
            var store = new MakerProjectStore(Folder(Mine));
            var editor = new MakerStudioEditor((_, _) => MakerResult<MakerProjectStore>.Success(store), (_, _, _, _, _) => new MakerSubtitleLiveResult(),
                lineVoice: (_, id, _) => Task.FromResult(MakerResult<string?>.Success(id == "line_d" ? "Voice_D" : "Voice_A")));
            var takes = new MakerTakeStore(store);
            var created = takes.Create(new MakerStudioCreateTakeRequest
            {
                PackId = Mine, Lang = "th", Id = "line_a", ExpectedRevision = store.ReadLine("th", "line_a").Value!.Revision, OperationId = new string('1', 32),
                Origin = "own", Consent = new MakerStudioConsent { Confirmed = true, StatementVersion = MakerStudioContract.ConsentVersion },
            }).Value!;
            var takeId = created.Take!.Id;
            var revision = created.Revision;
            foreach (var (kind, rate, op) in new[] { ("source", 24000, '2'), ("render", 48000, '3') })
            {
                var saved = takes.SaveAudio(new MakerStudioTakeAudioRequest
                {
                    PackId = Mine, Lang = "th", Id = "line_a", TakeId = takeId, Kind = kind, ExpectedRevision = revision, OperationId = new string(op, 32),
                }, TestWav.Tone(0.5, rate, 1));
                Assert.True(saved.Ok, saved.Message);
                revision = saved.Value!.Revision;
            }

            var page = (await editor.TakesAsync(Mine, "th", "line_a", default)).Value!;
            Assert.Equal(page.ClipRevision, Line("line_a").ClipRevision);
            Assert.Equal(page.ClipRevision, Line("line_alias").ClipRevision);

            var published = await editor.PublishAsync(new MakerStudioPublishRequest
            {
                PackId = Mine, Lang = "th", Id = "line_a", TakeId = takeId, ExpectedRevision = page.Revision,
                ExpectedClipRevision = Line("line_alias").ClipRevision, OperationId = new string('7', 32),
            }, default);

            Assert.True(published.Ok, published.Message);
            var after = (await editor.TakesAsync(Mine, "th", "line_a", default)).Value!;
            foreach (var id in new[] { "line_a", "line_alias" })
            {
                var line = Line(id);
                Assert.Equal(after.ClipRevision, line.ClipRevision);
                Assert.Equal(takeId, line.PublishedTakeId);
                Assert.True(line.OwnedDub);
            }

            Assert.Null(Line("line_d").PublishedTakeId);
            Assert.Equal(1, Page(Query()).Items.Single(l => l.Id == "line_a").TakeCount);
        }

        // ----- damaged and absent creator state -----------------------------------------------------------------

        [Fact]
        public void A_damaged_line_file_degrades_the_list_but_the_line_itself_answers_metadata_unavailable()
        {
            Seed();
            Write(Mine, ".maker/lines/th/line_a.json", "{ not json");

            var listed = Page(Query()).Items.Single(l => l.Id == "line_a");
            Assert.Equal("todo", listed.Workflow.Status);
            Assert.Equal("mine A", listed.OwnedSubtitle);
            Refused(_catalog.LineNow(Mine, "th", "line_a", null), MakerStudioErrorCodes.MetadataUnavailable);
        }

        [Fact]
        public void A_damaged_subtitle_table_degrades_the_list_and_the_line_answers_metadata_unavailable()
        {
            Seed();
            Write(Mine, "data/subtitles/th.json", "{ not json");

            var listed = Page(Query()).Items.Single(l => l.Id == "line_a");
            Assert.Null(listed.OwnedSubtitle);
            Assert.Equal("other A", listed.Subtitle);
            Refused(_catalog.LineNow(Mine, "th", "line_a", null), MakerStudioErrorCodes.MetadataUnavailable);
        }

        // ----- packs ----------------------------------------------------------------------------------------------

        [Fact]
        public void The_pack_list_has_loaded_refused_and_pending_packs_with_their_real_states()
        {
            Write("lab.broken", ".maker/project.json", "{\"schema\":99}");
            Directory.CreateDirectory(Path.Combine(Folder("lab.broken"), "data"));
            Write("lab.recover", ".maker/transactions/" + new string('a', 32) + ".pending.json", "{}");
            _extraSources.Add(new MakerPackSource("lab.broken", "Broken", "1.0.0", Folder("lab.broken"), false));
            _extraSources.Add(new MakerPackSource("lab.recover", "Recover", "1.0.0", Folder("lab.recover"), false));
            _extraSources.Add(new MakerPackSource("lab.refused", null, null, null, false, null, "manifest.json is not valid"));
            _extraSources.Add(new MakerPackSource("cwy.example", "Example", "1.0.0", Folder(Mine), false));
            var creator = new MakerPackCreator(() => ModsRoot, id => _extraSources.Concat(Snapshot().Sources).Any(s => s.Id == id), _ => "cwy.thai");
            Ok(creator.CreateNow(new MakerStudioCreatePackRequest
            {
                OperationId = new string('1', 32), Id = "lab.pending", Name = "Pending", Language = new MakerStudioLanguage { Code = "th" },
            }));

            var packs = Ok(_catalog.PacksNow()).ToDictionary(p => p.Id);

            Assert.True(packs[Mine].Loaded && packs[Mine].Editable && packs[Mine].StudioEditable);
            Assert.Equal("ready", packs[Mine].MetadataState);
            Assert.Null(packs[Mine].StudioReadOnlyReason);
            Assert.False(packs[Mine].RestartRequired);

            Assert.True(packs["lab.broken"].Editable);
            Assert.False(packs["lab.broken"].StudioEditable);
            Assert.Equal("unavailable", packs["lab.broken"].MetadataState);
            Assert.Contains("creator metadata", packs["lab.broken"].StudioReadOnlyReason, StringComparison.OrdinalIgnoreCase);

            Assert.Equal("recovery-required", packs["lab.recover"].MetadataState);
            Assert.True(packs["lab.recover"].StudioEditable);

            Assert.False(packs["lab.refused"].Loaded);
            Assert.False(packs["lab.refused"].StudioEditable);
            Assert.Contains("did not load", packs["lab.refused"].ReadOnlyReason, StringComparison.OrdinalIgnoreCase);

            Assert.False(packs["cwy.example"].Editable);
            Assert.False(packs["cwy.example"].StudioEditable);
            Assert.NotNull(packs["cwy.example"].StudioReadOnlyReason);

            var pending = packs["lab.pending"];
            Assert.False(pending.Loaded);
            Assert.True(pending.RestartRequired);
            Assert.False(pending.StudioEditable);
            Assert.Equal("pending", pending.MetadataState);
            Assert.Equal("lab.pending", ((IEnumerable<MakerStudioPack>)Ok(_catalog.PacksNow())).Last().Id);
        }

        [Fact]
        public void A_pending_pack_a_failed_pack_and_an_unknown_pack_each_refuse_catalog_reads_with_their_own_code()
        {
            var creator = new MakerPackCreator(() => ModsRoot, id => Snapshot().Sources.Any(s => s.Id == id), _ => "cwy.thai");
            Ok(creator.CreateNow(new MakerStudioCreatePackRequest
            {
                OperationId = new string('2', 32), Id = "lab.pending", Name = "Pending", Language = new MakerStudioLanguage { Code = "th" },
            }));
            _extraSources.Add(new MakerPackSource("lab.refused", null, null, null, false, null, "manifest.json is not valid"));

            Refused(_catalog.LinesNow(Query("lab.pending")), MakerStudioErrorCodes.RestartRequired, "restart");
            Refused(_catalog.LineNow("lab.pending", "th", "line_a", null), MakerStudioErrorCodes.RestartRequired);
            Refused(_catalog.GroupsNow("lab.pending", "th"), MakerStudioErrorCodes.RestartRequired);
            Refused(_catalog.LinesNow(Query("lab.refused")), MakerErrorCodes.ReadOnlyPack, "did not load");
            Refused(_catalog.LinesNow(Query("lab.nothing")), MakerErrorCodes.NotFound);
            Refused(_catalog.LineNow(Mine, "th", "line_nope", null), MakerErrorCodes.NotFound);
        }

        [Fact]
        public void A_read_only_reference_packs_lines_can_still_be_listed()
        {
            _extraSources.Add(new MakerPackSource("cwy.example", "Example", "1.0.0", Folder("cwy.example"), false));
            Directory.CreateDirectory(Folder("cwy.example"));

            var page = Page(Query("cwy.example"));

            Assert.Equal(6, page.Total);
            Assert.All(page.Items, l => Assert.Equal("todo", l.Workflow.Status));
        }

        // ----- input -------------------------------------------------------------------------------------------------

        [Theory]
        [InlineData("warnings", 0, 100, null)]
        [InlineData("retake", 0, 100, null)]
        [InlineData("all", -1, 100, null)]
        [InlineData("all", 0, 0, null)]
        [InlineData("all", 0, 501, null)]
        public void An_unknown_filter_or_a_page_out_of_range_is_a_400(string filter, int offset, int limit, string? q)
        {
            var result = _catalog.LinesNow(Query(filter: filter, offset: offset, limit: limit, q: q));

            Refused(result, MakerErrorCodes.BadRequest);
        }

        [Fact]
        public void The_limit_message_names_the_range_and_a_long_search_or_bad_language_is_refused()
        {
            Refused(_catalog.LinesNow(Query(limit: 501)), MakerErrorCodes.BadRequest, "limit must be 1-500");
            Refused(_catalog.LinesNow(Query(q: new string('x', 201))), MakerErrorCodes.BadRequest, "q may be");
            var badLang = Query();
            badLang.Lang = "T H";
            Refused(_catalog.LinesNow(badLang), MakerErrorCodes.BadRequest);
            Refused(_catalog.GroupsNow(Mine, "not a language"), MakerErrorCodes.BadRequest);
            Refused(_catalog.LineNow(Mine, "th", "bad id!", null), MakerErrorCodes.BadRequest);
        }

        [Fact]
        public void A_language_alias_reads_the_same_creator_state_as_its_canonical_code()
        {
            Seed();
            SetWorkflow("line_a", "draft");

            var viaAlias = Ok(_catalog.LinesNow(new MakerStudioLineQuery { PackId = Mine, Lang = "tha", Filter = "draft" }));

            Assert.Equal(new[] { "line_a" }, viaAlias.Items.Select(l => l.Id).ToArray());
        }

        [Fact]
        public void The_pack_list_asks_for_the_editors_verdicts_and_line_reads_do_not()
        {
            var asked = new List<bool>();
            var catalog = new MakerStudioCatalog(new Recording(Snapshot, asked));

            catalog.PacksNow();
            catalog.LinesNow(Query());
            catalog.LineNow(Mine, "th", "line_a", null);
            catalog.GroupsNow(Mine, "th");

            Assert.Equal(new[] { true, false, false, false }, asked.ToArray());
        }

        [Fact]
        public async Task The_pack_list_does_not_wait_for_a_pack_that_is_being_written()
        {
            Seed();
            var store = new MakerProjectStore(Folder(Mine));
            using var held = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var writer = Task.Run(() =>
            {
                lock (store.SyncRoot)
                {
                    held.Set();
                    release.Wait(TimeSpan.FromSeconds(10));
                }
            });
            Assert.True(held.Wait(TimeSpan.FromSeconds(5)));
            try
            {
                var listing = Task.Run(() => _catalog.PacksNow());

                var finished = await Task.WhenAny(listing, Task.Delay(TimeSpan.FromSeconds(3)));

                Assert.Same(listing, finished);
                Assert.Equal("ready", (await listing).Value!.Single(p => p.Id == Mine).MetadataState);
            }
            finally
            {
                release.Set();
                await writer;
            }
        }

        [Fact]
        public void A_source_that_is_not_ready_passes_its_error_through()
        {
            var catalog = new MakerStudioCatalog(new NotReady());

            Refused(catalog.PacksNow(), MakerErrorCodes.NotReady, "lines are not loaded");
            Refused(catalog.LinesNow(Query()), MakerErrorCodes.NotReady);
            Refused(catalog.GroupsNow(Mine, "th"), MakerErrorCodes.NotReady);
            Refused(catalog.LineNow(Mine, "th", "line_a", null), MakerErrorCodes.NotReady);
        }

        private sealed class Recording : IMakerStudioCatalogSource
        {
            private readonly Func<MakerStudioCatalogSnapshot> _make;
            private readonly List<bool> _asked;
            internal Recording(Func<MakerStudioCatalogSnapshot> make, List<bool> asked) { _make = make; _asked = asked; }

            public Task<MakerResult<MakerStudioCatalogSnapshot>> SnapshotAsync(bool editorVerdicts, CancellationToken cancel)
            {
                lock (_asked) { _asked.Add(editorVerdicts); }
                return Task.FromResult(MakerResult<MakerStudioCatalogSnapshot>.Success(_make()));
            }
        }

        private sealed class NotReady : IMakerStudioCatalogSource
        {
            public Task<MakerResult<MakerStudioCatalogSnapshot>> SnapshotAsync(bool editorVerdicts, CancellationToken cancel) =>
                Task.FromResult(MakerResult<MakerStudioCatalogSnapshot>.Fail(MakerErrorCodes.NotReady, "The game's lines are not loaded yet."));
        }
    }
}
