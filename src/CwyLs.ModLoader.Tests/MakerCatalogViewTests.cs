using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using CwyLs.ModLoader.Core.Api.Maker;
using CwyLs.ModLoader.Core.Api.Maker.Catalog;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// DS-02: the catalog's answers - every Line field from its source, search, the four filters,
    /// paging bounds, omitted lines, missing data, and the pack list. All rows, texts, packs and
    /// clips are invented; nothing comes from the game.
    /// </summary>
    public sealed class MakerCatalogViewTests : IDisposable
    {
        private const string IntroOne = "sample_intro_001";
        private const string IntroTwo = "sample_intro_002";
        private const string IntroOmit = "sample_intro_003_omit";
        private const string Settings = "sample_ui_settings";
        private const string Stretch = "sample_motion_stretch";
        private const string Ghost = "sample_ghost_001";
        private const string PackLine = "sample_pack_001";
        private const string Click = "sample_click_001";

        private readonly string _root = Path.Combine(Path.GetTempPath(), "cwy-maker-view-tests-" + Guid.NewGuid().ToString("N"));
        private readonly MakerPackSource _thai;
        private readonly MakerPackSource _fix;

        public MakerCatalogViewTests()
        {
            _thai = Pack("lab.thai", declared: new[] { "th" });
            _fix = Pack("lab.fix");

            Write(_thai, "data/subtitles/th.json",
                "{ \"" + IntroOne + "\": \"สวัสดีตอนเช้า\", \"" + Settings + "\": \"ตั้งค่า\", \"" + PackLine + "\": \"\" }");
            WriteBytes(_thai, "audio/voice/th/Voice_Sample_Intro_001.wav", MakerAudioFixtures.Wav(8000, 1, 12000));
            WriteBytes(_thai, "audio/voice/th/Voice_Sample_PackOnly.wav", MakerAudioFixtures.Wav(8000, 1, 4000));

            Write(_fix, "data/subtitles/th.json",
                "{ \"" + IntroOne + "\": \"อรุณสวัสดิ์\", \"" + IntroOmit + "\": \"ข้าม\" }");
            WriteBytes(_fix, "audio/voice/th/Voice_Sample_Stretch.wav", MakerAudioFixtures.Wav(8000, 1, 2000));
        }

        private string ModsRoot => Path.Combine(_root, "CWYLS Mods");

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_root))
                {
                    Directory.Delete(_root, recursive: true);
                }
            }
            catch (IOException)
            {
                // A leftover temp folder is harmless.
            }
        }

        [Fact]
        public void All_lists_every_line_in_the_games_order_with_omitted_lines_last()
        {
            var page = Ok(View().Lines(Query("th")));

            Assert.Equal(8, page.Total);
            Assert.Equal(0, page.Offset);
            Assert.Equal(
                new[] { IntroOne, IntroTwo, Settings, Stretch, PackLine, Click, IntroOmit, Ghost },
                page.Items.Select(l => l.Id).ToArray());
        }

        [Fact]
        public void A_finished_line_carries_every_field_from_its_source()
        {
            var line = Ok(View().Line(IntroOne, "th"));

            Assert.Equal(IntroOne, line.Id);
            Assert.Equal("Voice_Sample_Intro_001", line.Voice);
            Assert.Equal("sample_intro", line.Group);
            Assert.Equal(2.5, line.DurationSec);
            Assert.Equal("おはようございます", line.Ja);
            Assert.Equal("Good morning", line.En);

            // Both packs have a subtitle; the later pack wins and is named.
            Assert.Equal("อรุณสวัสดิ์", line.Subtitle);
            Assert.Equal("lab.fix", line.SubtitlePack);

            Assert.Equal("lab.thai", line.DubPack);
            Assert.Equal(1.5, line.DubDurationSec);
        }

        [Fact]
        public void Missing_data_is_null_not_invented()
        {
            var view = View();

            var settings = Ok(view.Line(Settings, "th"));
            Assert.Null(settings.Voice);
            Assert.Null(settings.Group);
            Assert.Null(settings.DurationSec);
            Assert.Equal("ตั้งค่า", settings.Subtitle);
            Assert.Null(settings.DubPack);
            Assert.Null(settings.DubDurationSec);

            // An empty table entry is no subtitle; the pack's clip is still its dub.
            var packLine = Ok(view.Line(PackLine, "th"));
            Assert.Null(packLine.Subtitle);
            Assert.Null(packLine.SubtitlePack);
            Assert.Equal(string.Empty, packLine.Ja);
            Assert.Null(packLine.DurationSec);
            Assert.Equal("lab.thai", packLine.DubPack);
            Assert.Equal(0.5, packLine.DubDurationSec);

            // A language nobody has made anything for.
            var korean = Ok(view.Line(IntroOne, "ko"));
            Assert.Null(korean.Subtitle);
            Assert.Null(korean.SubtitlePack);
            Assert.Null(korean.DubPack);
            Assert.Null(korean.DubDurationSec);
            Assert.Equal(2.5, korean.DurationSec);
        }

        [Fact]
        public void Group_and_original_length_fall_back_to_what_the_game_was_heard_speaking()
        {
            var spoken = new MakerSpokenCache();
            spoken.Note("Voice_Sample_Click_001", Click, "clickReaction", 1.75, gameClip: true);

            var line = Ok(View(spoken: spoken).Line(Click, "th"));
            Assert.Equal("clickReaction", line.Group);
            Assert.Equal(1.75, line.DurationSec);

            var unheard = Ok(View().Line(Click, "th"));
            Assert.Null(unheard.Group);
            Assert.Null(unheard.DurationSec);

            // The novel group wins over a spoken one.
            spoken.Note("Voice_Sample_Intro_001", IntroOne, "elsewhere", 9d, gameClip: true);
            var intro = Ok(View(spoken: spoken).Line(IntroOne, "th"));
            Assert.Equal("sample_intro", intro.Group);
            Assert.Equal(2.5, intro.DurationSec);
        }

        [Fact]
        public void Omitted_lines_are_marked_and_need_nothing()
        {
            var view = View();
            Assert.Equal(2, view.OmittedCount);

            var omit = Ok(view.Line(IntroOmit, "th"));
            Assert.Equal(MakerLineRules.OmittedGroup, omit.Group);
            Assert.Null(omit.DurationSec);
            Assert.Equal("Voice_Sample_Intro_003", omit.Voice);
            Assert.Equal("ข้าม", omit.Subtitle);

            var ghost = Ok(view.Line(Ghost, "th"));
            Assert.Equal(MakerLineRules.OmittedGroup, ghost.Group);

            foreach (var filter in new[] { "missing-subtitle", "missing-dub", "done" })
            {
                var ids = Ok(view.Lines(Query("th", filter: filter, limit: 500))).Items.Select(l => l.Id).ToArray();
                Assert.DoesNotContain(IntroOmit, ids);
                Assert.DoesNotContain(Ghost, ids);
            }

            Assert.True(view.StateOf(Ghost, "th")!.Value.Omitted);
            Assert.Null(view.StateOf("sample_none", "th"));
        }

        [Fact]
        public void A_voice_is_only_missing_everywhere_when_the_game_clip_list_was_read()
        {
            // Voice cache not readable yet: only the id rule applies.
            var unknown = new MakerCatalogView(new MakerGameData(Rows(), null, null), Content());
            Assert.Equal(1, unknown.OmittedCount);
            Assert.Equal("sample_motion", Ok(unknown.Line(Stretch, "th")).Group);
            Assert.Null(Ok(unknown.Line(Ghost, "th")).Group);

            // A mod registered the voice at runtime.
            var registered = new MakerCatalogView(new MakerGameData(Rows(), Clips(), new[] { "Voice_Sample_Ghost" }), Content());
            Assert.Equal(1, registered.OmittedCount);

            // A pack ships the voice globally.
            WriteBytes(_fix, "audio/voice/Voice_Sample_Ghost.ogg", MakerAudioFixtures.OggVorbis(48000, 48000));
            var global = View();
            Assert.Equal(1, global.OmittedCount);
            Assert.Null(Ok(global.Line(Ghost, "th")).Group);
            Assert.Null(Ok(global.Line(Ghost, "th")).DubPack);
        }

        [Fact]
        public void Filters_select_what_each_line_still_needs()
        {
            var view = View();

            Assert.Equal(new[] { IntroTwo, PackLine, Click }, Ids(view.Lines(Query("th", filter: "missing-subtitle"))));
            Assert.Equal(new[] { IntroTwo, Click }, Ids(view.Lines(Query("th", filter: "missing-dub"))));
            Assert.Equal(new[] { IntroOne, Settings, Stretch }, Ids(view.Lines(Query("th", filter: "done"))));

            Assert.Equal(new[] { IntroOne, IntroTwo, Settings, PackLine, Click }, Ids(view.Lines(Query("ko", filter: "missing-subtitle"))));
            Assert.Equal(new[] { IntroOne, IntroTwo, Stretch, PackLine, Click }, Ids(view.Lines(Query("ko", filter: "missing-dub"))));
            Assert.Empty(Ids(view.Lines(Query("ko", filter: "done"))));
        }

        [Fact]
        public void Search_covers_id_voice_both_texts_and_the_subtitle_of_the_query_language()
        {
            var view = View();

            Assert.Equal(new[] { IntroOne }, Ids(view.Lines(Query("th", search: "GOOD"))));
            Assert.Equal(new[] { IntroOne }, Ids(view.Lines(Query("th", search: "อรุณ"))));
            Assert.Empty(Ids(view.Lines(Query("ko", search: "อรุณ"))));
            Assert.Equal(new[] { IntroTwo }, Ids(view.Lines(Query("th", search: "がんばろう"))));
            Assert.Equal(new[] { IntroOne, IntroTwo, IntroOmit }, Ids(view.Lines(Query("th", search: "voice_sample_intro"))));
            Assert.Equal(new[] { Settings }, Ids(view.Lines(Query("th", search: "  ui_set  "))));
            Assert.Empty(Ids(view.Lines(Query("th", search: "nothing like this"))));

            // Search and filter together; the total counts the matches, not the page.
            var page = Ok(view.Lines(Query("th", search: "sample_intro", filter: "missing-dub", limit: 1)));
            Assert.Equal(1, page.Total);
            Assert.Equal(new[] { IntroTwo }, page.Items.Select(l => l.Id).ToArray());
        }

        [Fact]
        public void Paging_slices_the_matches_and_reports_the_total()
        {
            var view = View();

            var first = Ok(view.Lines(Query("th", offset: 0, limit: 2)));
            Assert.Equal(8, first.Total);
            Assert.Equal(new[] { IntroOne, IntroTwo }, first.Items.Select(l => l.Id).ToArray());

            var last = Ok(view.Lines(Query("th", offset: 6, limit: 5)));
            Assert.Equal(8, last.Total);
            Assert.Equal(6, last.Offset);
            Assert.Equal(new[] { IntroOmit, Ghost }, last.Items.Select(l => l.Id).ToArray());

            foreach (var offset in new[] { 8, 100, int.MaxValue })
            {
                var beyond = Ok(view.Lines(Query("th", offset: offset, limit: 10)));
                Assert.Equal(8, beyond.Total);
                Assert.Equal(offset, beyond.Offset);
                Assert.Empty(beyond.Items);
            }

            Assert.Equal(8, Ok(view.Lines(Query("th", limit: MakerLineRules.MaxLimit))).Items.Count);
            Assert.Single(Ok(view.Lines(Query("th", limit: MakerLineRules.MinLimit))).Items);
        }

        [Theory]
        [InlineData("th", "all", 0, 0)]
        [InlineData("th", "all", 0, 501)]
        [InlineData("th", "all", -1, 10)]
        [InlineData("", "all", 0, 10)]
        [InlineData("thai", "all", 0, 10)]
        [InlineData("th", "everything", 0, 10)]
        public void A_bad_query_is_a_bad_request(string lang, string filter, int offset, int limit)
        {
            var result = View().Lines(Query(lang, filter: filter, offset: offset, limit: limit));

            Assert.False(result.Ok);
            Assert.Equal(MakerErrorCodes.BadRequest, result.Error);
            Assert.False(string.IsNullOrWhiteSpace(result.Message));
        }

        [Fact]
        public void A_null_query_and_bad_line_requests_are_refused_and_an_unknown_line_is_not_found()
        {
            var view = View();

            Assert.Equal(MakerErrorCodes.BadRequest, view.Lines(null).Error);
            Assert.Equal(MakerErrorCodes.BadRequest, view.Line("", "th").Error);
            Assert.Equal(MakerErrorCodes.BadRequest, view.Line(IntroOne, null).Error);
            Assert.Equal(MakerErrorCodes.BadRequest, view.Line(IntroOne, "th_TH").Error);
            Assert.Equal(MakerErrorCodes.NotFound, view.Line("sample_none", "th").Error);
            Assert.Equal(MakerErrorCodes.NotFound, view.Line("SAMPLE_INTRO_001", "th").Error);
        }

        [Fact]
        public void An_empty_game_and_no_packs_answer_with_an_empty_page()
        {
            var view = new MakerCatalogView(MakerGameData.Empty, MakerPackContent.Empty);

            var page = Ok(view.Lines(Query("th")));
            Assert.Equal(0, page.Total);
            Assert.Empty(page.Items);
            Assert.Equal(MakerErrorCodes.NotFound, view.Line(IntroOne, "th").Error);
            Assert.Empty(view.Packs(null, ModsRoot, _ => true));
        }

        [Fact]
        public void Dub_lengths_are_left_unknown_without_a_reader()
        {
            var view = new MakerCatalogView(new MakerGameData(Rows(), Clips(), null), Content());

            var line = Ok(view.Line(IntroOne, "th"));
            Assert.Equal("lab.thai", line.DubPack);
            Assert.Null(line.DubDurationSec);
        }

        [Fact]
        public void The_pack_list_puts_loaded_packs_first_with_languages_and_reasons()
        {
            var code = Pack("lab.code");
            var reference = Pack("cwy.example");
            var sources = new[]
            {
                new MakerPackSource("lab.broken", null, null, null, false, null, "BadManifest - version is missing"),
                _thai,
                _fix,
                new MakerPackSource(code.Id, "Lab Code", "2.0.0", code.RootPath, true),
                reference,
            };

            var packs = View().Packs(sources, ModsRoot, _ => true);

            Assert.Equal(new[] { "lab.thai", "lab.fix", "lab.code", "cwy.example", "lab.broken" }, packs.Select(p => p.Id).ToArray());

            var thai = packs[0];
            Assert.Equal("lab.thai", thai.Name);
            Assert.Equal("1.0.0", thai.Version);
            Assert.Equal(new[] { "th" }, thai.Languages);
            Assert.True(thai.Editable);
            Assert.Null(thai.ReadOnlyReason);

            Assert.True(packs[1].Editable);
            Assert.Equal(new[] { "th" }, packs[1].Languages);

            Assert.False(packs[2].Editable);
            Assert.Equal(MakerPackRules.CodeModReason, packs[2].ReadOnlyReason);
            Assert.Equal("Lab Code", packs[2].Name);
            Assert.Empty(packs[2].Languages);

            Assert.False(packs[3].Editable);
            Assert.Equal(MakerPackRules.ReferencePackReason, packs[3].ReadOnlyReason);

            var broken = packs[4];
            Assert.False(broken.Editable);
            Assert.StartsWith(MakerPackRules.NotLoadedReason, broken.ReadOnlyReason);
            Assert.Equal("lab.broken", broken.Name);
            Assert.Equal(string.Empty, broken.Version);
            Assert.Empty(broken.Languages);
        }

        private static MakerLineQuery Query(string lang, string? search = null, string filter = "all", int offset = 0, int limit = 100) =>
            new MakerLineQuery { Lang = lang, Search = search, Filter = filter, Offset = offset, Limit = limit };

        private static T Ok<T>(MakerResult<T> result)
        {
            Assert.True(result.Ok, result.Error + ": " + result.Message);
            return result.Value!;
        }

        private static string[] Ids(MakerResult<MakerLinePage> result) => Ok(result).Items.Select(l => l.Id).ToArray();

        private static List<MakerGameRow> Rows() => new List<MakerGameRow>
        {
            new MakerGameRow(IntroOne, "Voice_Sample_Intro_001", "sample_intro", "おはようございます", "Good morning"),
            new MakerGameRow(IntroTwo, "Voice_Sample_Intro_002", "sample_intro", "今日もがんばろう", "Let's do our best today"),
            new MakerGameRow(IntroOmit, "Voice_Sample_Intro_003", "sample_intro", "省略", "Left out"),
            new MakerGameRow(Settings, null, null, "設定", "Settings"),
            new MakerGameRow(Stretch, "Voice_Sample_Stretch", "sample_motion", "", ""),
            new MakerGameRow(Ghost, "Voice_Sample_Ghost", null, "幽霊", "Ghost"),
            new MakerGameRow(PackLine, "Voice_Sample_PackOnly", "lab_scene", "", "A line from a pack"),
            new MakerGameRow(Click, "Voice_Sample_Click_001", null, "クリック", "Click"),
        };

        private static Dictionary<string, double> Clips() => new Dictionary<string, double>
        {
            ["Voice_Sample_Intro_001"] = 2.5,
            ["Voice_Sample_Intro_002"] = 3.25,
            ["Voice_Sample_Stretch"] = 0.8,
            ["Voice_Sample_Click_001"] = 0d,
        };

        private MakerPackContent Content() => MakerPackScanner.Scan(new[] { _thai, _fix });

        private MakerCatalogView View(MakerSpokenCache? spoken = null) =>
            new MakerCatalogView(new MakerGameData(Rows(), Clips(), null), Content(), spoken, new MakerDubLengths().LengthOf);

        private MakerPackSource Pack(string id, string[]? declared = null)
        {
            var folder = Path.Combine(ModsRoot, id);
            Directory.CreateDirectory(folder);
            return new MakerPackSource(id, id, "1.0.0", folder, false, declared);
        }

        private static void Write(MakerPackSource pack, string relative, string text) =>
            WriteBytes(pack, relative, new UTF8Encoding(false).GetBytes(text));

        private static void WriteBytes(MakerPackSource pack, string relative, byte[] bytes)
        {
            var path = Path.Combine(pack.RootPath!, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
        }
    }
}
