using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CwyLs.ModLoader.Core.Api.Maker;
using CwyLs.ModLoader.Core.Api.Maker.Catalog;
using CwyLs.ModLoader.Core.Api.Maker.Studio;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// Pack Maker's DLC gate with a fake DLC marker (<c>fake_dlc_*</c> groups, app 999): a DLC line the player is not known to
    /// own is not listed, not editable, not exported and not counted; an owned one is normal. Invented data only.
    /// </summary>
    public sealed class MakerDlcGateTests : IDisposable, IMakerStudioCatalogSource
    {
        private const string Pack = "lab.dlc";
        private const uint FakeApp = 999;
        private static readonly MakerDlcMarker FakeGroups = new MakerDlcMarker(MakerDlcMarkerKind.Group, "fake_dlc_*", FakeApp);

        private readonly string _root = Path.Combine(Path.GetTempPath(), "cwy-dlc-" + Guid.NewGuid().ToString("N"));
        private readonly MakerStudioCatalog _catalog;
        private MakerDlcOwnership _owned = MakerDlcOwnership.Unknown;

        public MakerDlcGateTests()
        {
            Directory.CreateDirectory(Path.Combine(_root, "data", "subtitles"));
            File.WriteAllText(Path.Combine(_root, "manifest.json"), "{\"id\":\"lab.dlc\",\"name\":\"DLC lab\",\"version\":\"0.1.0\",\"loaderVersion\":\"0.9.2\"}");
            File.WriteAllText(Path.Combine(_root, "data", "subtitles", "th.json"), "{\"free_hello\":\"Free translation\"}");
            _catalog = new MakerStudioCatalog(this);
        }

        private MakerGameData Game(MakerDlcGate? gate, bool withGroups = true)
        {
            var rows = new[]
            {
                new MakerGameRow("free_hello", "Voice_Free_01", "free_story", "Invented ja", "Free hello"),
                new MakerGameRow("free_quiet", null, "free_story", "Invented ja", "Free quiet"),
                new MakerGameRow("dlc_bed_01", "Voice_Dlc_01", "fake_dlc_bed", "Invented ja", "Paid bed sentinel"),
                new MakerGameRow("dlc_bed_02", null, "fake_dlc_bed", "Invented ja", "Paid quiet sentinel"),
                new MakerGameRow("shared_yes", "Voice_Shared", "free_story", "Invented ja", "Shared yes"),
            };
            var novel = new[]
            {
                new MakerGameNovelRow("n1", "free_story", "free_hello", "Heroine", null, null),
                new MakerGameNovelRow("n2", "free_story", "free_quiet", "Heroine", null, null),
                new MakerGameNovelRow("n3", "free_story", "shared_yes", "Heroine", null, null),
                new MakerGameNovelRow("n4", "fake_dlc_bed", "dlc_bed_01", "Heroine", null, null),
                new MakerGameNovelRow("n5", "fake_dlc_bed", "dlc_bed_02", "Heroine", null, null),
                new MakerGameNovelRow("n6", "fake_dlc_bed", "shared_yes", "Heroine", null, null),
            };
            var groups = new[]
            {
                // The game 2.0.0 adapter reads the game's own DLC flag; these invented groups carry it unset.
                new MakerGameGroup("free_story", "Free story", 1, false),
                new MakerGameGroup("fake_dlc_bed", "Paid title sentinel", 1, false),
            };
            return new MakerGameData(rows, null, null, novel, withGroups ? groups : null, null, gate);
        }

        private MakerDlcGate FakeGate() => new MakerDlcGate(new[] { FakeGroups }, app => app == FakeApp ? _owned : MakerDlcOwnership.Unknown);

        public Task<MakerResult<MakerStudioCatalogSnapshot>> SnapshotAsync(bool editorVerdicts, CancellationToken cancel)
        {
            var source = new MakerPackSource(Pack, "DLC lab", "0.1.0", _root, false, new[] { "th" });
            var sources = new[] { source };
            var view = new MakerCatalogView(Game(FakeGate()), MakerPackScanner.Scan(sources));
            return Task.FromResult(MakerResult<MakerStudioCatalogSnapshot>.Success(new MakerStudioCatalogSnapshot(
                view, new[] { new MakerPack { Id = Pack, Editable = true, Languages = new[] { "th" } } }, sources, null)));
        }

        [Fact]
        public void Shipped_markers_today_are_the_games_DLC_story_type_only_and_the_DLC_day_list_is_empty()
        {
            Assert.Empty(MakerDlcMarkers.DlcDay);
            // The DLC story type, the game's own DLC flag (2.0.0), the 16 My Pace scenario types and the 4 movement-line prefixes,
            // all for the room expansion.
            Assert.Equal(22, MakerDlcMarkers.BuiltIn.Count);
            Assert.Equal(4, MakerDlcMarkers.BuiltIn.Count(m => m.Kind == MakerDlcMarkerKind.Line));
            var builtIn = MakerDlcMarkers.BuiltIn[0];
            Assert.Equal(MakerDlcMarkerKind.ScenarioType, builtIn.Kind);
            Assert.Equal(3, builtIn.ScenarioType);
            Assert.All(MakerDlcMarkers.BuiltIn, m => Assert.Equal(3718890u, m.AppId));
            Assert.Single(MakerDlcMarkers.BuiltIn, m => m.Kind == MakerDlcMarkerKind.GroupFlag);
            Assert.Equal(MakerDlcMarkers.BuiltIn.Concat(MakerDlcMarkers.DlcDay), MakerDlcMarkers.All);
            Assert.Equal(MakerDlcMarkers.All, MakerDlcGate.Shipped.Markers);
            // No ownership read is shipped: every app is unknown, so DLC lines are hidden for everyone.
            Assert.Equal(MakerDlcOwnership.Unknown, MakerDlcGate.Shipped.OwnershipOf(MakerDlcMarkers.RoomExpansionAppId));
        }

        [Fact]
        public void Shipped_gate_hides_nothing_from_todays_kind_of_data_but_hides_every_DLCScenario_group()
        {
            var free = Game(MakerDlcGate.Shipped);
            Assert.True(free.IsLoaded);
            Assert.Equal(5, free.Rows.Count);
            Assert.Equal(0, free.Hidden.LineCount);

            var typed = new MakerGameData(
                new[] { new MakerGameRow("free_a", null, "g_free", "", "Free"), new MakerGameRow("paid_a", "Voice_Paid", "g_dlc", "", "Paid sentinel") },
                null, null,
                new[] { new MakerGameNovelRow("1", "g_free", "free_a", null, null, null), new MakerGameNovelRow("2", "g_dlc", "paid_a", null, null, null) },
                new[] { new MakerGameGroup("g_free", null, 1, false), new MakerGameGroup("g_dlc", "Paid title sentinel", MakerDlcMarkers.DlcScenarioType, false) },
                null, MakerDlcGate.Shipped);
            Assert.Equal(new[] { "free_a" }, typed.Rows.Select(r => r.Id));
            Assert.Equal(new[] { "g_free" }, typed.Groups.Select(g => g.Id));
            Assert.Equal(new[] { "free_a" }, typed.NovelRows.Select(r => r.LineId));
            Assert.True(typed.Hidden.IsVoice("Voice_Paid"));
            Assert.Contains("ownership unknown", typed.Hidden.Summary);
        }

        [Theory]
        [InlineData(MakerDlcOwnership.Unknown)]
        [InlineData(MakerDlcOwnership.NotOwned)]
        public void Unowned_DLC_lines_their_novel_rows_groups_and_shared_lines_leave_the_snapshot(MakerDlcOwnership ownership)
        {
            _owned = ownership;
            var game = Game(FakeGate());
            Assert.True(game.IsLoaded);
            Assert.Equal(new[] { "free_hello", "free_quiet" }, game.Rows.Select(r => r.Id));
            Assert.False(game.TryFind("dlc_bed_01", out _));
            Assert.False(game.IsStoryLine("dlc_bed_01"));
            Assert.False(game.IsStoryLine("shared_yes")); // spoken by a DLC group too: fail closed
            Assert.DoesNotContain(game.Groups, g => g.Id == "fake_dlc_bed");
            Assert.DoesNotContain(game.NovelRows, r => r.GroupId == "fake_dlc_bed" || r.LineId == "shared_yes");
            Assert.Equal(3, game.Hidden.LineCount);
            Assert.True(game.Hidden.IsVoice("Voice_Dlc_01"));
            Assert.True(game.Hidden.IsVoice("Voice_Shared"));
            Assert.False(game.Hidden.IsVoice("Voice_Free_01"));
            Assert.Equal(MakerErrorCodes.NotFound, game.Hidden.Refusal(null, "Voice_Dlc_01")!.Error);
            Assert.Equal(MakerErrorCodes.NotFound, game.Hidden.Refusal("dlc_bed_02", null)!.Error);
            Assert.Null(game.Hidden.Refusal("free_hello", "Voice_Free_01"));
            Assert.DoesNotContain("sentinel", game.Hidden.Summary, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void An_ownership_read_that_throws_hides_like_unknown()
        {
            var gate = new MakerDlcGate(new[] { FakeGroups }, _ => throw new InvalidOperationException("Steam not initialised"));
            Assert.Equal(MakerDlcOwnership.Unknown, gate.OwnershipOf(FakeApp));
            Assert.Equal(new[] { "free_hello", "free_quiet" }, Game(gate).Rows.Select(r => r.Id));
        }

        [Fact]
        public void Owned_DLC_lines_are_normal()
        {
            _owned = MakerDlcOwnership.Owned;
            var game = Game(FakeGate());
            Assert.Equal(5, game.Rows.Count);
            Assert.True(game.TryFind("dlc_bed_01", out _));
            Assert.Contains(game.Groups, g => g.Id == "fake_dlc_bed");
            Assert.Equal(0, game.Hidden.LineCount);
            Assert.Null(game.Hidden.Refusal("dlc_bed_01", "Voice_Dlc_01"));
            Assert.NotEqual(Game(new MakerDlcGate(new[] { FakeGroups }, null)).Stamp, game.Stamp);
        }

        [Fact]
        public void A_type_marker_without_the_games_group_list_fails_closed()
        {
            var gate = new MakerDlcGate(new[] { new MakerDlcMarker(MakerDlcMarkerKind.ScenarioType, "3", FakeApp) }, null);
            var game = Game(gate, withGroups: false);
            Assert.False(game.IsLoaded);
            Assert.Empty(game.Rows);
            Assert.Contains("paid DLC", game.Problem);
            Assert.False(MakerDlcHidden.Unknown("x").IsKnown);
            Assert.True(MakerDlcHidden.Unknown("x").IsLine("free_hello"));
            Assert.Equal(MakerErrorCodes.NotReady, MakerDlcHidden.Unknown("x").Refusal("free_hello", null)!.Error);
        }

        /// <summary>The shipped type marker on the build the markers were made for (unless <paramref name="version"/> says otherwise).</summary>
        private static MakerDlcGate TypeGate(string? version = MakerDlcMarkers.MadeForGameVersion) =>
            new MakerDlcGate(MakerDlcMarkers.All, null, () => version, null, null, TypedPreDlc);

        /// <summary>The invented groups of <see cref="Typed"/> that stand for free groups known before the DLC (the DLC flag fallback's set).</summary>
        private static readonly IReadOnlyCollection<string> TypedPreDlc = new HashSet<string>(StringComparer.Ordinal) { "g_free", "g_chapter_new" };

        /// <summary>
        /// Invented snapshot: a free story (type 1), a DLC-type story, a room line no group speaks, and whatever
        /// <paramref name="extraNovel"/> / <paramref name="extraGroups"/> add.
        /// </summary>
        private static MakerGameData Typed(MakerDlcGate gate, MakerGameNovelRow[]? extraNovel = null, MakerGameGroup[]? extraGroups = null,
            MakerGameRow[]? extraRows = null)
        {
            var rows = new List<MakerGameRow>
            {
                new MakerGameRow("free_a", "Voice_Free_A", "g_free", "", "Free"),
                new MakerGameRow("paid_a", "Voice_Paid_A", "g_dlc", "", "Paid sentinel"),
                new MakerGameRow("room_a", "Voice_Room_A", null, "", "Room line"),
            };
            rows.AddRange(extraRows ?? new MakerGameRow[0]);
            var novel = new List<MakerGameNovelRow>
            {
                new MakerGameNovelRow("1", "g_free", "free_a", null, null, null),
                new MakerGameNovelRow("2", "g_dlc", "paid_a", null, null, null),
            };
            novel.AddRange(extraNovel ?? new MakerGameNovelRow[0]);
            var groups = new List<MakerGameGroup> { new MakerGameGroup("g_free", null, 1), new MakerGameGroup("g_dlc", null, MakerDlcMarkers.DlcScenarioType) };
            groups.AddRange(extraGroups ?? new MakerGameGroup[0]);
            return new MakerGameData(rows, null, null, novel, groups, null, gate);
        }

        [Theory]
        [InlineData("both")]   // the adapter's shape: the novel row names the group and the row carries it
        [InlineData("novel")]  // only a novel row names the missing group
        [InlineData("row")]    // only the row carries it
        public void IG02_a_line_of_a_group_missing_from_the_group_list_is_hidden_while_a_type_marker_is_active(string where)
        {
            var game = Typed(TypeGate(),
                extraNovel: where == "row" ? null : new[] { new MakerGameNovelRow("3", "g_missing", "novel_x", null, null, null) },
                extraRows: new[] { new MakerGameRow("novel_x", "Voice_Novel_X", where == "novel" ? null : "g_missing", "", "Unclassified sentinel") });

            Assert.True(game.IsLoaded);
            Assert.Equal(new[] { "free_a", "room_a" }, game.Rows.Select(r => r.Id));
            Assert.True(game.Hidden.IsLine("novel_x"));
            Assert.True(game.Hidden.IsGroup("g_missing"));
            Assert.True(game.Hidden.IsVoice("Voice_Novel_X"));
            Assert.Equal(1, game.Hidden.UnresolvedGroups);
            Assert.DoesNotContain(game.NovelRows, r => r.GroupId == "g_missing");
            Assert.Equal(MakerErrorCodes.NotFound, game.Hidden.Refusal("novel_x", null)!.Error);
            Assert.DoesNotContain("sentinel", game.Hidden.Summary, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void IG02_a_group_of_unknown_type_is_hidden_while_a_type_marker_is_active()
        {
            var game = Typed(TypeGate(),
                extraNovel: new[] { new MakerGameNovelRow("3", "g_untyped", "untyped_a", null, null, null) },
                extraGroups: new[] { new MakerGameGroup("g_untyped", "Untyped title sentinel", null) },
                extraRows: new[] { new MakerGameRow("untyped_a", "Voice_Untyped", "g_untyped", "", "Untyped sentinel") });

            Assert.Equal(new[] { "free_a", "room_a" }, game.Rows.Select(r => r.Id));
            Assert.DoesNotContain(game.Groups, g => g.Id == "g_untyped");
            Assert.True(game.Hidden.IsVoice("Voice_Untyped"));
            Assert.True(TypeGate().HidesGroup("g_untyped", null));
            Assert.False(TypeGate().HidesGroup("g_free", 1));

            // Only a type marker needs the type: an id marker alone does not hide an untyped group it does not name.
            var idOnly = Typed(new MakerDlcGate(new[] { FakeGroups }, null, () => MakerDlcMarkers.MadeForGameVersion, null),
                extraNovel: new[] { new MakerGameNovelRow("3", "g_untyped", "untyped_a", null, null, null) },
                extraGroups: new[] { new MakerGameGroup("g_untyped", null, null) },
                extraRows: new[] { new MakerGameRow("untyped_a", null, "g_untyped", "", "Untyped") });
            Assert.Contains(idOnly.Rows, r => r.Id == "untyped_a");
        }

        [Theory]
        [InlineData("2.0.1")]
        [InlineData("2.1.0")]
        [InlineData("3.0.0")]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("not a version")]
        public void F1_on_a_newer_or_unknown_game_build_lines_no_classified_group_speaks_are_hidden(string? version)
        {
            var gate = TypeGate(version);
            Assert.False(gate.GameVersionConfirmed(out _));

            var game = Typed(gate);

            Assert.True(game.IsLoaded);
            Assert.True(game.Hidden.IsKnown);
            Assert.Equal(new[] { "free_a" }, game.Rows.Select(r => r.Id));
            Assert.True(game.Hidden.IsLine("room_a"));
            Assert.True(game.Hidden.IsVoice("Voice_Room_A"));
            Assert.True(game.Hidden.IsLine("paid_a"));
            Assert.Contains("loader update", game.Hidden.Summary);
            Assert.Contains(MakerDlcMarkers.MadeForGameVersion, game.Hidden.Summary);
        }

        [Fact]
        public void F1_a_version_read_that_throws_is_an_unknown_build()
        {
            var gate = new MakerDlcGate(MakerDlcMarkers.All, null, () => throw new InvalidOperationException("no version"), null, null, TypedPreDlc);
            Assert.False(gate.GameVersionConfirmed(out var running));
            Assert.Equal("unknown", running);
            Assert.Equal(new[] { "free_a" }, Typed(gate).Rows.Select(r => r.Id));
        }

        [Theory]
        [InlineData(MakerDlcMarkers.MadeForGameVersion)]
        [InlineData(MakerDlcMarkers.MadeForGameVersion + "+steam.1")]
        [InlineData("1.18.1")]
        [InlineData("1.17.3")]
        public void F1_on_the_build_the_markers_were_made_for_or_an_older_one_room_lines_stay_listed(string version)
        {
            var gate = TypeGate(version);
            Assert.True(gate.GameVersionConfirmed(out _));
            var game = Typed(gate);
            Assert.Equal(new[] { "free_a", "room_a" }, game.Rows.Select(r => r.Id));
            Assert.DoesNotContain("loader update", game.Hidden.Summary);
        }

        [Fact]
        public void F1_a_new_free_story_chapter_on_a_newer_build_stays_listed_when_its_groups_classify_as_free()
        {
            // The update adds a chapter whose groups are in the group list with a free type: not blanket-hidden.
            var game = Typed(TypeGate("2.1.0"),
                extraNovel: new[]
                {
                    new MakerGameNovelRow("10", "g_chapter_new", "chapter_a", null, null, null),
                    new MakerGameNovelRow("11", "g_chapter_new", "chapter_b", null, null, null),
                },
                extraGroups: new[] { new MakerGameGroup("g_chapter_new", "New chapter", 1) },
                extraRows: new[]
                {
                    new MakerGameRow("chapter_a", "Voice_Chapter_A", "g_chapter_new", "", "New free line"),
                    new MakerGameRow("chapter_b", null, null, "", "New free line"), // classified through its novel row only
                });

            Assert.Equal(new[] { "free_a", "chapter_a", "chapter_b" }, game.Rows.Select(r => r.Id));
            Assert.Contains(game.Groups, g => g.Id == "g_chapter_new");
            Assert.False(game.Hidden.IsVoice("Voice_Chapter_A"));
            Assert.Null(game.Hidden.Refusal("chapter_a", "Voice_Chapter_A"));
            Assert.True(game.Hidden.IsLine("paid_a")); // the DLC type is still hidden
        }

        [Fact]
        public void HidesGroup_answers_scene_play_by_type_or_group_id_unless_owned()
        {
            Assert.True(MakerDlcGate.Shipped.HidesGroup("any_dlc_story", MakerDlcMarkers.DlcScenarioType));
            Assert.False(MakerDlcGate.Shipped.HidesGroup("main_05", 1));
            Assert.True(MakerDlcGate.Shipped.HidesGroup("any_dlc_story", null)); // IG-02: unknown type while a type marker is active
            Assert.True(FakeGate().HidesGroup("fake_dlc_bed", null));
            Assert.False(FakeGate().HidesGroup(null, MakerDlcMarkers.DlcScenarioType));
            _owned = MakerDlcOwnership.Owned;
            Assert.False(FakeGate().HidesGroup("fake_dlc_bed", null));
        }

        [Fact]
        public void Line_markers_match_exact_ids_and_prefixes_and_bad_markers_are_refused()
        {
            var gate = new MakerDlcGate(new[] { new MakerDlcMarker(MakerDlcMarkerKind.Line, "dlc_bed_*", FakeApp), new MakerDlcMarker(MakerDlcMarkerKind.Line, "shared_yes", FakeApp) }, null);
            var game = Game(gate);
            Assert.Equal(new[] { "free_hello", "free_quiet" }, game.Rows.Select(r => r.Id));
            Assert.Contains(game.Groups, g => g.Id == "fake_dlc_bed"); // a line marker hides lines, not the group
            Assert.DoesNotContain(game.NovelRows, r => r.LineId != null && r.LineId.StartsWith("dlc_bed_", StringComparison.Ordinal));
            Assert.Throws<ArgumentException>(() => new MakerDlcMarker(MakerDlcMarkerKind.Line, "*", FakeApp));
            Assert.Throws<ArgumentException>(() => new MakerDlcMarker(MakerDlcMarkerKind.Group, " ", FakeApp));
            Assert.Throws<ArgumentException>(() => new MakerDlcMarker(MakerDlcMarkerKind.ScenarioType, "DLCScenario", FakeApp));
            Assert.Equal(0, Game(new MakerDlcGate(null, null)).Hidden.LineCount);
        }

        [Fact]
        public async Task Unowned_DLC_lines_are_not_listed_searched_found_grouped_or_counted()
        {
            var v1 = new MakerCatalogView(Game(FakeGate()), MakerPackScanner.Scan(new MakerPackSource[0]));
            var v1Page = v1.Lines(new MakerLineQuery { Lang = "th", Limit = 100 });
            Assert.True(v1Page.Ok, v1Page.Message);
            Assert.Equal(new[] { "free_hello", "free_quiet" }, v1Page.Value!.Items.Select(l => l.Id));
            Assert.Equal(MakerErrorCodes.NotFound, v1.Line("dlc_bed_01", "th").Error);

            var page = await _catalog.LinesAsync(new MakerStudioLineQuery { PackId = Pack, Lang = "th" }, default);
            Assert.True(page.Ok, page.Message);
            Assert.Equal(2, page.Value!.Total);
            var search = await _catalog.LinesAsync(new MakerStudioLineQuery { PackId = Pack, Lang = "th", Search = "sentinel" }, default);
            Assert.Equal(0, search.Value!.Total);
            Assert.Equal(MakerErrorCodes.NotFound, (await _catalog.LineAsync(Pack, "th", "dlc_bed_01", null, default)).Error);

            var groups = await _catalog.GroupsAsync(Pack, "th", default);
            Assert.True(groups.Ok, groups.Message);
            Assert.DoesNotContain(groups.Value!.Groups, g => g.Id == "fake_dlc_bed" || g.Title == "Paid title sentinel");
            Assert.Equal(2, groups.Value.Overall.TotalLines);
            Assert.Equal(1, groups.Value.Overall.DubbableLines);

            _owned = MakerDlcOwnership.Owned;
            Assert.Equal(5, (await _catalog.LinesAsync(new MakerStudioLineQuery { PackId = Pack, Lang = "th" }, default)).Value!.Total);
            Assert.Equal(5, (await _catalog.GroupsAsync(Pack, "th", default)).Value!.Overall.TotalLines);
            Assert.True((await _catalog.LineAsync(Pack, "th", "dlc_bed_01", null, default)).Ok);
        }

        [Fact]
        public async Task Every_editor_line_route_refuses_an_unowned_DLC_line_and_writes_nothing_and_an_owned_one_saves()
        {
            var store = new MakerProjectStore(_root);
            var table = Path.Combine(_root, "data", "subtitles", "th.json");
            var before = File.ReadAllBytes(table);
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

            var id = "dlc_bed_01";
            var op = Guid.NewGuid().ToString("N");
            var revision = store.ReadLine("th", id).Value!.Revision;
            Refused(await editor.SaveSubtitleAsync(new MakerStudioSubtitleRequest
            {
                PackId = Pack, Lang = "th", Id = id, OperationId = op, ExpectedRevision = revision, Text = "Do not edit paid content",
            }, default));
            Refused(await editor.SaveV1SubtitleAsync(Pack, "th", id, "Do not edit paid content", default));
            Refused(await editor.UpdateWorkflowAsync(new MakerStudioWorkflowRequest
            {
                PackId = Pack, Lang = "th", Id = id, OperationId = op, ExpectedRevision = revision, Status = "done",
            }, default));
            Refused(await editor.HistoryAsync(Pack, "th", id, 0, 20, default));
            Refused(await editor.TakesAsync(Pack, "th", id, default));
            Refused(await editor.PlayAsync(new MakerStudioPlayRequest { PackId = Pack, Lang = "th", Id = id, Variant = "original" }, default));
            Refused(await editor.PlayAsync(new MakerStudioPlayRequest { PackId = Pack, Lang = "th", Id = id, Variant = "sub" }, default));
            Assert.Equal(before, File.ReadAllBytes(table));
            Assert.False(Directory.Exists(Path.Combine(_root, ".maker", "lines", "th", id)));

            _owned = MakerDlcOwnership.Owned;
            var saved = await editor.SaveSubtitleAsync(new MakerStudioSubtitleRequest
            {
                PackId = Pack, Lang = "th", Id = id, OperationId = Guid.NewGuid().ToString("N"),
                ExpectedRevision = store.ReadLine("th", id).Value!.Revision, Text = "Owner's translation",
            }, default);
            Assert.True(saved.Ok, saved.Message);
            Assert.Contains("Owner's translation", File.ReadAllText(table));
        }

        [Fact]
        public async Task Export_refuses_a_pack_with_content_for_unowned_DLC_lines_and_exports_it_when_owned()
        {
            File.WriteAllText(Path.Combine(_root, "data", "subtitles", "th.json"), "{\"free_hello\":\"Free\",\"dlc_bed_02\":\"Paid translation\"}");
            Directory.CreateDirectory(Path.Combine(_root, "audio", "voice", "th"));
            var dub = MakerAudioFixtures.Wav(48000, 1, 4800);
            File.WriteAllBytes(Path.Combine(_root, "audio", "voice", "th", "Voice_Dlc_01.wav"), dub);
            // D-148: a shipped dub needs a valid voice label, or export refuses it for that reason instead.
            File.WriteAllBytes(Path.Combine(_root, MakerStudioPaths.Provenance), MakerVoiceProvenance.Write(new[]
            {
                new MakerProvenanceEntry
                {
                    Lang = "th", Clip = "Voice_Dlc_01", Origin = "own", StatementVersion = MakerStudioContract.ConsentVersion,
                    ConfirmedAt = "2026-11-17T00:00:00Z", Sha256 = MakerVoiceProvenance.Sha256(dub), PublishedAt = "2026-11-17T00:00:00Z",
                },
            }));
            var exports = Path.Combine(_root, "..", Path.GetFileName(_root) + "-exports");
            MakerPackExporter Exporter(Func<CancellationToken, Task<MakerResult<MakerDlcHidden>>>? paid) => new MakerPackExporter(
                (_, _) => MakerResult<MakerProjectStore>.Success(new MakerProjectStore(_root)), _ => null, () => exports,
                progress: _catalog.GroupsAsync, paid: paid);
            Task<MakerResult<MakerDlcHidden>> Hidden(CancellationToken _) => Task.FromResult(MakerResult<MakerDlcHidden>.Success(Game(FakeGate()).Hidden));
            MakerExportRequest Request() => new MakerExportRequest { PackId = Pack, OperationId = Guid.NewGuid().ToString("N"), ExpectedVersion = "0.1.0", Bump = "none" };

            var check = await Exporter(Hidden).CheckAsync(Pack, default);
            Assert.True(check.Ok, check.Message);
            var paid = check.Value!.Checks.Single(c => c.Id == "paid");
            Assert.Equal(MakerExportSeverity.Error, paid.Severity);
            Assert.Equal(new[] { "audio/voice/th/Voice_Dlc_01.wav", "data/subtitles/th.json" }, paid.Paths.OrderBy(p => p, StringComparer.Ordinal));
            Assert.DoesNotContain("Paid translation", paid.Message);
            Assert.False(check.Value.Ok);
            var refused = await Exporter(Hidden).ExportAsync(Request(), default);
            Assert.Equal(MakerStudioErrorCodes.ExportBlocked, refused.Error);
            Assert.True(!Directory.Exists(exports) || !Directory.EnumerateFiles(exports, "*.zip", SearchOption.AllDirectories).Any());

            // Unknown verdict (no snapshot): refused too.
            var unknown = await Exporter(_ => Task.FromResult(MakerResult<MakerDlcHidden>.Fail(MakerErrorCodes.NotReady, "not loaded"))).CheckAsync(Pack, default);
            Assert.Equal(MakerExportSeverity.Error, unknown.Value!.Checks.Single(c => c.Id == "paid").Severity);

            // Owned: no paid check, the export goes through with the owner's files.
            _owned = MakerDlcOwnership.Owned;
            var ownedCheck = await Exporter(Hidden).CheckAsync(Pack, default);
            Assert.DoesNotContain(ownedCheck.Value!.Checks, c => c.Id == "paid");
            var exported = await Exporter(Hidden).ExportAsync(Request(), default);
            Assert.True(exported.Ok, exported.Message);

            // No gate wired (offline hosts): no paid check at all.
            Assert.DoesNotContain((await Exporter(null).CheckAsync(Pack, default)).Value!.Checks, c => c.Id == "paid");
        }

        [Fact]
        public async Task A_pack_without_DLC_content_exports_with_no_paid_check_while_DLC_lines_are_hidden()
        {
            var exports = Path.Combine(_root, "..", Path.GetFileName(_root) + "-exports");
            var exporter = new MakerPackExporter((_, _) => MakerResult<MakerProjectStore>.Success(new MakerProjectStore(_root)), _ => null, () => exports,
                progress: _catalog.GroupsAsync, paid: _ => Task.FromResult(MakerResult<MakerDlcHidden>.Success(Game(FakeGate()).Hidden)));
            var check = await exporter.CheckAsync(Pack, default);
            Assert.True(check.Value!.Ok);
            Assert.DoesNotContain(check.Value.Checks, c => c.Id == "paid");
        }

        public void Dispose()
        {
            Directory.Delete(_root, true);
            var exports = Path.Combine(_root, "..", Path.GetFileName(_root) + "-exports");
            if (Directory.Exists(exports)) Directory.Delete(exports, true);
        }
    }
}
