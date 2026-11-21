using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CwyLs.ModLoader.Core.Api;
using CwyLs.ModLoader.Core.Api.Maker;
using CwyLs.ModLoader.Core.Api.Maker.Studio;
using CwyLs.ModLoader.Core.Language;
using CwyLs.ModLoader.Core.Packs;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// W2b-04: Studio creates a content pack (<c>POST /v1/maker/studio/packs</c>). Every folder here is a temporary
    /// <c>CWYLS Mods</c> of its own; ids, names and languages are invented.
    /// </summary>
    public sealed class MakerPackCreationTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "cwy-pack-create-" + Guid.NewGuid().ToString("N"));
        private readonly ManualApiClock _clock = new ManualApiClock();
        private readonly List<(string Type, IReadOnlyDictionary<string, object?> Data)> _events = new List<(string, IReadOnlyDictionary<string, object?>)>();

        public MakerPackCreationTests() => Directory.CreateDirectory(Mods);

        private string Mods => Path.Combine(_root, "CWYLS Mods");

        public void Dispose()
        {
            try { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
            catch (IOException) { /* a leftover temp folder is harmless */ }
        }

        private MakerPackCreator Creator(Func<string, bool>? known = null, Func<string, string?>? owner = null, Action? beforeMove = null) =>
            new MakerPackCreator(() => Mods, known ?? (_ => false), owner ?? (code => code == "th" ? "cwy.thai" : null),
                () => "1.18.0", () => "0.9.2", (type, data) => _events.Add((type, data)), _clock, beforeMove);

        private static MakerStudioCreatePackRequest Request(string operation = "1", string id = "lab.thai.dub", string name = "Thai dub (lab)",
            string? author = "Lab", string code = "th", string? caption = null, string? english = null, string? script = null, string[]? fallback = null) =>
            new MakerStudioCreatePackRequest
            {
                OperationId = Op(operation), Id = id, Name = name, Author = author,
                Language = new MakerStudioLanguage { Code = code, Caption = caption, EnglishName = english, Script = script, Fallback = fallback },
            };

        private static string Op(string n) => n.PadLeft(32, '0');

        private static MakerStudioPackCreated Ok(MakerResult<MakerStudioPackCreated> result)
        {
            Assert.True(result.Ok, result.Error + ": " + result.Message);
            return result.Value!;
        }

        private static void Refused(MakerResult<MakerStudioPackCreated> result, string code, string? contains = null)
        {
            Assert.False(result.Ok);
            Assert.Equal(code, result.Error);
            if (contains != null) Assert.Contains(contains, result.Message, StringComparison.OrdinalIgnoreCase);
        }

        private string[] Entries(string folder) =>
            Directory.GetFileSystemEntries(Path.Combine(Mods, folder), "*", SearchOption.AllDirectories)
                .Select(p => p.Substring(Path.Combine(Mods, folder).Length + 1).Replace(Path.DirectorySeparatorChar, '/')).OrderBy(p => p, StringComparer.Ordinal).ToArray();

        // ----- what a created pack is ------------------------------------------------------------------------

        [Fact]
        public void A_pack_for_a_registered_language_is_a_content_only_skeleton_the_loader_parsers_accept()
        {
            var created = Ok(Creator().CreateNow(Request()));

            Assert.Equal(new[] { ".maker", ".maker/project.json", ".maker/transactions", ".maker/transactions/" + Op("1") + ".json",
                "data", "data/subtitles", "data/subtitles/th.json", "manifest.json" }, Entries("lab.thai.dub"));
            var manifest = PackManifestParser.Parse(File.ReadAllText(Path.Combine(Mods, "lab.thai.dub", "manifest.json")));
            Assert.False(manifest.IsCodeMod);
            Assert.Null(manifest.EntryDll);
            Assert.Equal("0.1.0", manifest.Version.ToString());
            Assert.Equal("1.18.0", manifest.GameVersion);
            Assert.Equal("0.9.2", manifest.LoaderVersion!.ToString());
            Assert.Equal("Lab", manifest.Author);
            Assert.Equal("{}\n", File.ReadAllText(Path.Combine(Mods, "lab.thai.dub", "data", "subtitles", "th.json")));
            Assert.False(File.Exists(Path.Combine(Mods, "lab.thai.dub", "language.json")));

            Assert.True(created.RestartRequired);
            Assert.False(created.Live);
            Assert.Equal(MakerPackCreator.RestartNote, created.Note);
            var pack = created.Pack;
            Assert.Equal("lab.thai.dub", pack.Id);
            Assert.Equal("Thai dub (lab)", pack.Name);
            Assert.Equal("0.1.0", pack.Version);
            Assert.Equal(new[] { "th" }, pack.Languages);
            Assert.False(pack.Loaded);
            Assert.False(pack.Editable);
            Assert.False(pack.StudioEditable);
            Assert.True(pack.RestartRequired);
            Assert.Equal("pending", pack.MetadataState);
            Assert.NotNull(pack.ReadOnlyReason);
            Assert.NotNull(pack.StudioReadOnlyReason);
        }

        [Fact]
        public void A_new_custom_language_gets_one_declaration_the_loader_parser_accepts_and_no_font()
        {
            var created = Ok(Creator().CreateNow(
                Request(code: "vi", caption: "Tiếng Việt", english: "Vietnamese", script: "Latn", fallback: new[] { "en" })));

            var declaration = LanguageDeclarationParser.Parse(File.ReadAllText(Path.Combine(Mods, "lab.thai.dub", "language.json")), "lab.thai.dub").Single();
            Assert.Equal("vi", declaration.Code);
            Assert.Equal("Tiếng Việt", declaration.Caption);
            Assert.Equal("Vietnamese", declaration.EnglishName);
            Assert.Equal("latn", declaration.Script);
            Assert.Equal(new[] { "en" }, declaration.Fallback);
            Assert.Empty(declaration.FontAssets);
            Assert.True(File.Exists(Path.Combine(Mods, "lab.thai.dub", "data", "subtitles", "vi.json")));
            Assert.Equal(new[] { "vi" }, created.Pack.Languages);
        }

        [Fact]
        public void A_pending_pack_is_found_on_disk_until_the_game_knows_it()
        {
            var creator = Creator();
            Ok(creator.CreateNow(Request()));

            var pending = MakerPendingPacks.Scan(Mods, _ => false);
            Assert.Equal("lab.thai.dub", Assert.Single(pending).Id);
            Assert.True(MakerPendingPacks.TryFind(Mods, "LAB.THAI.DUB", _ => false, out var found));
            Assert.Equal(new[] { "th" }, found!.Languages);
            Assert.Empty(MakerPendingPacks.Scan(Mods, id => id == "lab.thai.dub"));
        }

        [Fact]
        public void Pending_packs_are_listed_in_id_order_until_known()
        {
            var creator = Creator();
            Ok(creator.CreateNow(Request("1", "lab.zeta")));
            Ok(creator.CreateNow(Request("2", "lab.alpha")));

            Assert.Equal(new[] { "lab.alpha", "lab.zeta" }, MakerPendingPacks.Scan(Mods, _ => false).Select(p => p.Id).ToArray());
            Assert.Equal(new[] { "lab.zeta" }, MakerPendingPacks.Scan(Mods, id => id == "lab.alpha").Select(p => p.Id).ToArray());
        }

        [Fact]
        public void Only_a_content_only_pack_named_like_its_folder_with_creator_metadata_is_pending()
        {
            var codeMod = Path.Combine(Mods, "lab.code");
            Directory.CreateDirectory(Path.Combine(codeMod, ".maker"));
            File.WriteAllText(Path.Combine(codeMod, ".maker", "project.json"), "{}");
            File.WriteAllText(Path.Combine(codeMod, "manifest.json"), "{\"id\":\"lab.code\",\"name\":\"Code\",\"version\":\"1.0.0\",\"entryDll\":\"Lab.dll\"}");

            var mismatch = Path.Combine(Mods, "lab.folder");
            Directory.CreateDirectory(Path.Combine(mismatch, ".maker"));
            File.WriteAllText(Path.Combine(mismatch, ".maker", "project.json"), "{}");
            File.WriteAllText(Path.Combine(mismatch, "manifest.json"), "{\"id\":\"lab.other\",\"name\":\"X\",\"version\":\"1.0.0\"}");

            var plain = Path.Combine(Mods, "lab.plain");
            Directory.CreateDirectory(plain);
            File.WriteAllText(Path.Combine(plain, "manifest.json"), "{\"id\":\"lab.plain\",\"name\":\"X\",\"version\":\"1.0.0\"}");

            var hidden = Path.Combine(Mods, ".lab.hidden");
            Directory.CreateDirectory(Path.Combine(hidden, ".maker"));
            File.WriteAllText(Path.Combine(hidden, ".maker", "project.json"), "{}");
            File.WriteAllText(Path.Combine(hidden, "manifest.json"), "{\"id\":\"lab.hidden\",\"name\":\"X\",\"version\":\"1.0.0\"}");

            Assert.Empty(MakerPendingPacks.Scan(Mods, _ => false));
        }

        // ----- validation ---------------------------------------------------------------------------------------

        [Theory]
        [InlineData("ab")]
        [InlineData("Lab.Upper")]
        [InlineData("lab pack")]
        [InlineData("con")]
        [InlineData("nul")]
        [InlineData("cwy.example")]
        [InlineData("cwy.demo.thing")]
        [InlineData("lab.")]
        [InlineData("-lab")]
        public void An_id_the_real_manifest_rule_or_the_reference_rule_refuses_is_a_400_and_nothing_is_created(string id)
        {
            Refused(Creator().CreateNow(Request(id: id)), MakerErrorCodes.BadRequest);

            Assert.Empty(Directory.GetFileSystemEntries(Mods));
        }

        [Fact]
        public void The_id_length_limit_is_the_manifest_limit_of_3_to_64()
        {
            var creator = Creator();
            Refused(creator.CreateNow(Request("1", new string('a', 65))), MakerErrorCodes.BadRequest);
            Ok(creator.CreateNow(Request("2", new string('a', 64))));
            Ok(creator.CreateNow(Request("3", "abc")));
        }

        [Theory]
        [InlineData("en")]
        [InlineData("ja")]
        [InlineData("zh-hans")]
        [InlineData("pt")]
        public void A_built_in_game_language_is_refused_with_400(string code)
        {
            Refused(Creator().CreateNow(Request(code: code, caption: "Native", english: "Native")), MakerErrorCodes.BadRequest, "game's own");
            Assert.Empty(Directory.GetFileSystemEntries(Mods));
        }

        [Fact]
        public void Declaration_fields_for_an_already_registered_language_are_refused_because_they_would_register_it_twice()
        {
            Refused(Creator().CreateNow(Request(caption: "ไทย", english: "Thai")), MakerErrorCodes.BadRequest, "already registered");
        }

        [Theory]
        [InlineData(null, "Vietnamese", null, "caption")]
        [InlineData("Tiếng Việt", null, null, "englishName")]
        [InlineData("Tiếng Việt", "Vietnamese", "Latin1", "script")]
        public void A_new_language_needs_a_caption_and_an_english_name_and_a_valid_script(string? caption, string? english, string? script, string field)
        {
            Refused(Creator().CreateNow(Request(code: "vi", caption: caption, english: english, script: script)),
                MakerErrorCodes.BadRequest, field);
        }

        [Fact]
        public void A_fallback_chain_the_loader_parser_refuses_is_a_400()
        {
            Refused(Creator().CreateNow(Request(code: "vi", caption: "V", english: "V", fallback: new[] { "en", "en" })),
                MakerErrorCodes.BadRequest, "listed twice");
            Refused(Creator().CreateNow(Request(code: "vi", caption: "V", english: "V", fallback: new[] { "vi" })),
                MakerErrorCodes.BadRequest, "itself");
        }

        [Theory]
        [InlineData("", "name")]
        [InlineData("   ", "name")]
        public void A_blank_name_is_a_400(string name, string field)
        {
            Refused(Creator().CreateNow(Request(name: name)), MakerErrorCodes.BadRequest, field);
        }

        [Fact]
        public void Names_and_authors_with_control_characters_or_too_long_are_refused_and_unicode_is_kept()
        {
            var creator = Creator();
            Refused(creator.CreateNow(Request("1", name: "Bad\nname")), MakerErrorCodes.BadRequest, "control");
            Refused(creator.CreateNow(Request("2", author: new string('x', 101))), MakerErrorCodes.BadRequest, "author");
            Ok(creator.CreateNow(Request("3", name: "ภาษาไทย 🙂", author: "นักพากย์")));
            var manifest = PackManifestParser.Parse(File.ReadAllText(Path.Combine(Mods, "lab.thai.dub", "manifest.json")));
            Assert.Equal("ภาษาไทย 🙂", manifest.Name);
            Assert.Equal("นักพากย์", manifest.Author);
        }

        [Fact]
        public void A_malformed_operation_id_is_a_400()
        {
            var request = Request();
            request.OperationId = "NOT-AN-ID";
            Refused(Creator().CreateNow(request), MakerErrorCodes.BadRequest, "operationId");
        }

        // ----- collisions, replay, failure ------------------------------------------------------------------------

        [Fact]
        public void A_folder_that_differs_only_by_case_is_a_409_and_is_left_exactly_as_it_was()
        {
            var existing = Path.Combine(Mods, "Lab.Thai.Dub");
            Directory.CreateDirectory(existing);
            File.WriteAllText(Path.Combine(existing, "mine.txt"), "creator work");

            Refused(Creator().CreateNow(Request()), MakerStudioErrorCodes.Conflict, "ignoring case");

            Assert.Equal(new[] { "mine.txt" }, Directory.GetFileSystemEntries(existing).Select(Path.GetFileName).ToArray());
            Assert.Equal("creator work", File.ReadAllText(Path.Combine(existing, "mine.txt")));
            Assert.Equal(new[] { "Lab.Thai.Dub" }, Directory.GetFileSystemEntries(Mods).Select(Path.GetFileName).ToArray());
        }

        [Fact]
        public void An_id_a_loaded_or_refused_pack_already_has_is_a_409()
        {
            Refused(Creator(known: id => id == "lab.thai.dub").CreateNow(Request()), MakerStudioErrorCodes.Conflict, "already installed");
            Assert.Empty(Directory.GetFileSystemEntries(Mods));
        }

        [Fact]
        public void An_exact_replay_answers_the_same_created_pack_and_creates_nothing_more()
        {
            var creator = Creator();
            var first = Ok(creator.CreateNow(Request()));
            var entries = Entries("lab.thai.dub");
            _events.Clear();

            var again = Ok(creator.CreateNow(Request()));

            Assert.Equal(MakerStudioCatalogJson.PackCreated(first).ToString(), MakerStudioCatalogJson.PackCreated(again).ToString());
            Assert.Equal(first.Pack.Id, again.Pack.Id);
            Assert.Equal(entries, Entries("lab.thai.dub"));
            Assert.Empty(_events);
        }

        [Fact]
        public void A_replay_survives_a_new_creator_because_the_receipt_is_in_the_pack()
        {
            var first = Ok(Creator().CreateNow(Request()));

            var again = Ok(Creator().CreateNow(Request()));

            Assert.Equal(first.Pack.MetadataState, again.Pack.MetadataState);
            Assert.True(again.RestartRequired);
        }

        [Fact]
        public void Reusing_an_operation_id_for_another_request_is_a_409_even_for_another_pack_id()
        {
            var creator = Creator();
            Ok(creator.CreateNow(Request()));

            Refused(creator.CreateNow(Request(name: "Another name")), MakerStudioErrorCodes.Conflict, "another request");
            Refused(creator.CreateNow(Request(id: "lab.other.pack")), MakerStudioErrorCodes.Conflict, "another request");
            Assert.False(Directory.Exists(Path.Combine(Mods, "lab.other.pack")));
        }

        [Fact]
        public void After_the_receipt_expires_the_same_request_is_a_409_because_the_pack_exists()
        {
            var creator = Creator();
            Ok(creator.CreateNow(Request()));
            _clock.Advance(25 * 3600);

            Refused(creator.CreateNow(Request()), MakerStudioErrorCodes.Conflict, "ignoring case");
        }

        [Fact]
        public void A_failure_before_the_move_leaves_no_pack_and_no_staging_folder()
        {
            var creator = Creator(beforeMove: () => throw new IOException("disk full"));

            var result = creator.CreateNow(Request());

            Refused(result, MakerErrorCodes.Unavailable, "disk full");
            Assert.Empty(Directory.GetFileSystemEntries(Mods));
            Assert.Empty(_events);
        }

        [Fact]
        public void A_folder_that_appears_during_staging_is_a_409_and_stays_untouched()
        {
            var target = Path.Combine(Mods, "lab.thai.dub");
            var creator = Creator(beforeMove: () =>
            {
                Directory.CreateDirectory(target);
                File.WriteAllText(Path.Combine(target, "arrived.txt"), "first");
            });

            Refused(creator.CreateNow(Request()), MakerStudioErrorCodes.Conflict, "appeared");

            Assert.Equal(new[] { "arrived.txt" }, Directory.GetFileSystemEntries(target).Select(Path.GetFileName).ToArray());
            Assert.Equal(new[] { "lab.thai.dub" }, Directory.GetFileSystemEntries(Mods).Select(Path.GetFileName).ToArray());
        }

        [Fact]
        public void Staging_is_hidden_so_a_pack_scan_in_between_never_sees_a_half_written_pack()
        {
            string[]? during = null;
            var creator = Creator(beforeMove: () => during = Directory.GetDirectories(Mods).Select(Path.GetFileName).ToArray()!);

            Ok(creator.CreateNow(Request()));

            Assert.NotNull(during);
            var staging = Assert.Single(during!);
            Assert.StartsWith(".", staging);
            Assert.Equal(new[] { "lab.thai.dub" }, Directory.GetDirectories(Mods).Select(Path.GetFileName).ToArray());
        }

        [Fact]
        public void Creation_publishes_the_pack_and_catalog_events_with_flat_scalar_fields_after_the_move()
        {
            Ok(Creator().CreateNow(Request()));

            Assert.Equal(new[] { "maker.pack.created", "maker.catalog.changed" }, _events.Select(e => e.Type).ToArray());
            Assert.Equal("lab.thai.dub", _events[0].Data["packId"]);
            Assert.Equal(true, _events[0].Data["restartRequired"]);
            Assert.Equal("pack.created", _events[1].Data["reason"]);
            Assert.False(string.IsNullOrEmpty((string?)_events[1].Data["catalogRevision"]));
            Assert.All(_events.SelectMany(e => e.Data.Values), v => Assert.True(v is string || v is bool));
        }

        [Fact]
        public void A_missing_or_read_only_packs_folder_is_unavailable_and_creates_nothing()
        {
            var missing = new MakerPackCreator(() => Path.Combine(_root, "absent"), _ => false, _ => "cwy.thai", clock: _clock);

            Assert.NotNull(missing.UnavailableReason());
            Refused(missing.CreateNow(Request()), MakerErrorCodes.Unavailable);
            Assert.Null(Creator().UnavailableReason());
        }

        [Fact]
        public void A_custom_language_another_pending_pack_already_declares_is_a_409()
        {
            var creator = Creator();
            Ok(creator.CreateNow(Request("1", "lab.one", code: "vi", caption: "V", english: "Vietnamese")));

            Refused(creator.CreateNow(Request("2", "lab.two", code: "vi", caption: "V", english: "Vietnamese")),
                MakerStudioErrorCodes.Conflict, "lab.one");
            Assert.False(Directory.Exists(Path.Combine(Mods, "lab.two")));
        }

        [Fact]
        public void An_empty_subtitle_table_the_pack_starts_with_parses_as_no_subtitles()
        {
            Ok(Creator().CreateNow(Request()));

            var table = CwyLs.ModLoader.Core.MasterData.SubtitleTables.Parse(
                File.ReadAllText(Path.Combine(Mods, "lab.thai.dub", "data", "subtitles", "th.json")), "data/subtitles/th.json", "th");

            Assert.Empty(table.Rows);
            Assert.Empty(table.Rejected);
        }

        [Fact]
        public void Retained_custom_language_creation_replays_after_restart_registers_its_owner()
        {
            var request = Request(code: "vi", caption: "Tiếng Việt", english: "Vietnamese");
            var first = Ok(Creator().CreateNow(request));
            _events.Clear();
            var replay = Ok(Creator(owner: code => code == "vi" ? request.Id : null).CreateNow(request));
            Assert.Equal(MakerStudioCatalogJson.PackCreated(first).ToString(), MakerStudioCatalogJson.PackCreated(replay).ToString());
            Assert.Empty(_events);
            Refused(Creator(owner: _ => request.Id).CreateNow(Request(name: "Changed", code: "vi", caption: "Tiếng Việt", english: "Vietnamese")),
                MakerStudioErrorCodes.Conflict, "another request");
        }

        [Fact]
        public void Retained_code_only_creation_replays_after_prior_language_owner_vanishes()
        {
            var request = Request();
            var first = Ok(Creator().CreateNow(request));
            var replay = Ok(Creator(owner: _ => null).CreateNow(request));
            Assert.Equal(MakerStudioCatalogJson.PackCreated(first).ToString(), MakerStudioCatalogJson.PackCreated(replay).ToString());
        }

        [Fact]
        public void Creation_allows_128_existing_packs_and_ignores_hidden_export_folders()
        {
            for (var i = 0; i < 128; i++) Directory.CreateDirectory(Path.Combine(Mods, "existing." + i));
            Directory.CreateDirectory(Path.Combine(Mods, ".maker-exports"));
            Ok(Creator().CreateNow(Request()));
            Assert.Equal(130, Directory.GetDirectories(Mods).Length);
        }

        [Fact]
        public void Hidden_export_directory_does_not_spend_a_pack_folder_slot()
        {
            Directory.CreateDirectory(Path.Combine(Mods, ".maker-exports"));
            var creator = new MakerPackCreator(() => Mods, _ => false, _ => "owner", maxPackCount: 1,
                freeBytes: _ => long.MaxValue);
            Ok(creator.CreateNow(Request()));
            Assert.Equal(2, Directory.GetDirectories(Mods).Length);
        }

        [Fact]
        public void Cumulative_pack_count_refuses_before_staging_and_preserves_existing_work()
        {
            for (var i = 0; i < 1024; i++) Directory.CreateDirectory(Path.Combine(Mods, "existing." + i));
            var file = Path.Combine(Mods, "existing.0", "keep.txt");
            File.WriteAllText(file, "creator work");
            var staged = false;
            Refused(Creator(beforeMove: () => staged = true).CreateNow(Request()), MakerStudioErrorCodes.StorageLimit, "cleanup");
            Assert.False(staged);
            Assert.Equal("creator work", File.ReadAllText(file));
            Assert.Equal(1024, Directory.GetDirectories(Mods).Length);
        }

        [Fact]
        public void Other_packs_bytes_do_not_block_creation_and_preserve_existing_work()
        {
            var existing = Path.Combine(Mods, "existing");
            Directory.CreateDirectory(existing);
            var file = Path.Combine(existing, "large.bin");
            using (var stream = File.Create(file)) stream.SetLength(8L * 1024 * 1024 * 1024);
            var staged = false;
            Ok(Creator(beforeMove: () => staged = true).CreateNow(Request()));
            Assert.True(staged);
            Assert.Equal(8L * 1024 * 1024 * 1024, new FileInfo(file).Length);
            Assert.Equal(2, Directory.GetDirectories(Mods).Length);
        }

        [Fact]
        public void Free_space_reservation_refuses_before_staging_and_replay_needs_no_new_space()
        {
            var staged = false;
            var creator = new MakerPackCreator(() => Mods, _ => false, _ => "owner", beforeMove: () => staged = true, clock: _clock,
                minimumFreeBytes: 100, freeBytes: _ => 100);
            Refused(creator.CreateNow(Request()), MakerStudioErrorCodes.StorageLimit, "cleanup");
            Assert.False(staged);
            Assert.Empty(Directory.GetFileSystemEntries(Mods));
            Ok(Creator().CreateNow(Request()));
            Ok(creator.CreateNow(Request()));
            Assert.False(staged);
        }

        [Fact]
        public void Incoming_skeleton_bytes_are_reserved_and_custom_limits_are_source_compatible()
        {
            var creator = new MakerPackCreator(() => Mods, _ => false, _ => "owner", maxPackBytes: 1,
                freeBytes: _ => long.MaxValue);
            Refused(creator.CreateNow(Request()), MakerStudioErrorCodes.StorageLimit, "byte limit");
            Assert.Empty(Directory.GetFileSystemEntries(Mods));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Unrelated_directory_and_file_symlinks_do_not_block_creation_or_touch_targets(bool directory)
        {
            var outside = Path.Combine(_root, "outside");
            Directory.CreateDirectory(outside);
            var keep = Path.Combine(outside, "keep.txt");
            File.WriteAllText(keep, "keep");
            var link = Path.Combine(Mods, "linked");
            if (directory) Directory.CreateSymbolicLink(link, outside);
            else File.CreateSymbolicLink(link, keep);
            Ok(Creator().CreateNow(Request()));
            Assert.Equal("keep", File.ReadAllText(keep));
            Assert.True((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0);
            Assert.Equal(2, Directory.GetFileSystemEntries(Mods).Length);
        }

        [Fact]
        public void Small_count_limit_is_cumulative_across_service_restarts_and_receipts_still_replay()
        {
            MakerPackCreator Limited() => new MakerPackCreator(() => Mods, _ => false, _ => "owner", maxPackCount: 1,
                freeBytes: _ => long.MaxValue);
            Ok(Limited().CreateNow(Request()));
            Ok(Limited().CreateNow(Request()));
            Refused(Limited().CreateNow(Request("2", "lab.second")), MakerStudioErrorCodes.StorageLimit, "count");
            Assert.Single(Directory.GetDirectories(Mods));
        }

        [Fact]
        public void Creation_skips_linked_pending_manifest_without_following_it()
        {
            var existing = Path.Combine(Mods, "lab.pending");
            Directory.CreateDirectory(Path.Combine(existing, ".maker"));
            File.WriteAllText(Path.Combine(existing, ".maker/project.json"), "{}");
            var manifest = Path.Combine(_root, "external-manifest.json");
            File.WriteAllText(manifest, "{\"id\":\"lab.pending\",\"name\":\"Pending\",\"version\":\"0.1.0\"}");
            File.CreateSymbolicLink(Path.Combine(existing, "manifest.json"), manifest);
            File.WriteAllText(Path.Combine(existing, "language.json"), "{\"schema\":1,\"code\":\"vi\",\"caption\":\"V\",\"englishName\":\"Vietnamese\"}");
            Ok(Creator().CreateNow(Request(code: "vi", caption: "V", english: "Vietnamese")));
            Assert.True(File.Exists(manifest));
            Assert.Equal(2, Directory.GetDirectories(Mods).Length);
        }

        // ----- through the router ---------------------------------------------------------------------------------

        private static string Body(string operation = "1", string id = "lab.thai.dub", string language = "{\"code\":\"th\"}", string extra = "") =>
            "{\"operationId\":\"" + Op(operation) + "\",\"id\":\"" + id + "\",\"name\":\"Thai dub (lab)\",\"author\":\"Lab\",\"language\":" + language + extra + "}";

        [Fact]
        public void The_router_answers_201_with_the_contract_shape_and_replays_it()
        {
            var rig = new MakerRig(studio: () => new MakerStudioBackends(newPacks: Creator()));

            var response = rig.Send(MakerHttp.Json("POST", "/v1/maker/studio/packs", rig.Token, Body()));
            var replay = rig.Send(MakerHttp.Json("POST", "/v1/maker/studio/packs", rig.Token, Body()));

            Assert.Equal(201, response.Status);
            Assert.Equal(201, replay.Status);
            Assert.Equal(response.Body, replay.Body);
            var json = ApiTestKit.Json(response);
            Assert.Equal(new[] { "pack", "restartRequired", "live", "note" }, json.Keys.ToArray());
            var pack = Assert.IsType<CwyLs.ModLoader.Core.Json.JsonObject>(json["pack"]);
            Assert.Equal(new[] { "id", "name", "version", "languages", "editable", "readOnlyReason", "loaded", "restartRequired",
                "studioEditable", "studioReadOnlyReason", "metadataState" }, pack.Keys.ToArray());
            Assert.Equal("pending", pack["metadataState"]);
        }

        [Fact]
        public void The_router_refuses_a_conflicting_replay_with_409_and_a_bad_body_with_400()
        {
            var rig = new MakerRig(studio: () => new MakerStudioBackends(newPacks: Creator()));
            Assert.Equal(201, rig.Send(MakerHttp.Json("POST", "/v1/maker/studio/packs", rig.Token, Body())).Status);

            Assert.Equal(409, rig.Send(MakerHttp.Json("POST", "/v1/maker/studio/packs", rig.Token, Body(id: "lab.else"))).Status);
            Assert.Equal(400, rig.Send(MakerHttp.Json("POST", "/v1/maker/studio/packs", rig.Token, Body("2", "lab.two", extra: ",\"surprise\":1"))).Status);
            Assert.Equal(400, rig.Send(MakerHttp.Json("POST", "/v1/maker/studio/packs", rig.Token, Body("3", "lab.three", "{\"code\":\"th\",\"font\":\"x\"}"))).Status);
            Assert.Equal(400, rig.Send(MakerHttp.Json("POST", "/v1/maker/studio/packs", rig.Token, Body("4", "lab.four", "\"th\""))).Status);
            Assert.Equal(400, rig.Send(MakerHttp.Json("POST", "/v1/maker/studio/packs", rig.Token, "{\"operationId\":\"" + Op("5") + "\"}")).Status);
            Assert.Equal(400, rig.Send(MakerHttp.Json("POST", "/v1/maker/studio/packs?x=1", rig.Token, Body("6", "lab.six"))).Status);
            Assert.Equal(415, rig.Send(MakerHttp.Build("POST", "/v1/maker/studio/packs", rig.Token,
                System.Text.Encoding.UTF8.GetBytes(Body("7", "lab.seven")), "text/plain")).Status);
            Assert.Equal(new[] { "lab.thai.dub" }, Directory.GetDirectories(Mods).Select(Path.GetFileName).ToArray());
        }

        [Fact]
        public void The_router_keeps_503_until_a_creator_is_installed_and_never_reads_the_body_first()
        {
            var rig = new MakerRig(studio: () => new MakerStudioBackends());

            var response = rig.Send(MakerHttp.Json("POST", "/v1/maker/studio/packs", rig.Token, "{}"));

            Assert.Equal(503, response.Status);
            Assert.Equal(MakerErrorCodes.Unavailable, ApiTestKit.ErrorCode(response));
            Assert.Empty(Directory.GetFileSystemEntries(Mods));
        }

        [Fact]
        public void newPacks_is_advertised_only_when_a_creator_is_installed()
        {
            var without = new MakerRig(studio: () => new MakerStudioBackends());
            var with = new MakerRig(studio: () => new MakerStudioBackends(newPacks: Creator()));

            var absent = ApiTestKit.Json(without.Send(MakerHttp.Get("/v1/maker/studio/capabilities", without.Token)));
            var present = ApiTestKit.Json(with.Send(MakerHttp.Get("/v1/maker/studio/capabilities", with.Token)));

            Assert.DoesNotContain("newPacks", ((IEnumerable<object?>)absent["features"]!).Cast<string>());
            Assert.Contains("newPacks", ((IEnumerable<object?>)present["features"]!).Cast<string>());
        }

        [Fact]
        public void A_cancelled_request_creates_nothing()
        {
            using var cancel = new System.Threading.CancellationTokenSource();
            cancel.Cancel();

            var result = Creator().CreateNow(Request(), cancel.Token);

            Refused(result, MakerErrorCodes.Unavailable);
            Assert.Empty(Directory.GetFileSystemEntries(Mods));
        }
    }
}
