using System;
using System.IO;
using CwyLs.ModLoader.Core.Api.Maker.Editor;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// The editor's gates: segments, languages, the paths it may write, the editable-pack rules
    /// (CONTRACT.md "Editable packs") and path safety inside a real temporary pack folder.
    /// </summary>
    public sealed class MakerEditorPathsTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "cwy-maker-paths-" + Guid.NewGuid().ToString("N"));

        public MakerEditorPathsTests()
        {
            Directory.CreateDirectory(Path.Combine(_root, "CWYLS Mods", "my.thai"));
        }

        private string PacksRoot => Path.Combine(_root, "CWYLS Mods");

        private string Pack => Path.Combine(PacksRoot, "my.thai");

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
                // A temp folder the OS still holds is not a test failure.
            }
        }

        [Theory]
        [InlineData("my.thai", true)]
        [InlineData("a", true)]
        [InlineData("My.Thai", false)]
        [InlineData("../x", false)]
        [InlineData("-x", false)]
        [InlineData("x\n", false)]
        [InlineData("", false)]
        public void Pack_ids_follow_the_contract(string id, bool ok) => Assert.Equal(ok, MakerEditRules.IsPackId(id));

        [Theory]
        [InlineData("Voice_Tutorial_1_001", true)]
        [InlineData("Voice-X", true)]
        [InlineData("a.b", false)]
        [InlineData("..", false)]
        [InlineData("a/b", false)]
        [InlineData("Voice(1)", false)]
        [InlineData("", false)]
        public void Clips_must_be_contract_segments_and_voice_names(string clip, bool ok) =>
            Assert.Equal(ok, MakerEditRules.IsClip(clip));

        [Fact]
        public void Line_ids_allow_dots_but_nothing_path_like()
        {
            Assert.True(MakerEditRules.IsLineId("cwy_demo_01.001"));
            Assert.False(MakerEditRules.IsLineId("a/b"));
            Assert.False(MakerEditRules.IsLineId(new string('a', 129)));
        }

        [Fact]
        public void Languages_are_checked_then_normalised()
        {
            Assert.True(MakerEditRules.TryLanguage("th", out var th, out _));
            Assert.Equal("th", th);
            Assert.True(MakerEditRules.TryLanguage("zh", out var zh, out _));
            Assert.Equal("zh-hans", zh);
            Assert.False(MakerEditRules.TryLanguage("TH", out _, out var upper));
            Assert.NotNull(upper);
            Assert.False(MakerEditRules.TryLanguage("th/../x", out _, out _));
            Assert.False(MakerEditRules.TryLanguage(null, out _, out _));
        }

        [Fact]
        public void Subtitle_tables_are_only_for_languages_the_game_lacks()
        {
            Assert.True(MakerEditRules.AcceptsSubtitles("th", out _));
            Assert.False(MakerEditRules.AcceptsSubtitles("en", out var problem));
            Assert.Contains("game's own", problem);
        }

        [Fact]
        public void Subtitle_text_is_one_to_two_thousand_characters()
        {
            Assert.True(MakerEditRules.IsSubtitleText("x", out _));
            Assert.True(MakerEditRules.IsSubtitleText(new string('x', 2000), out _));
            Assert.False(MakerEditRules.IsSubtitleText(new string('x', 2001), out _));
            Assert.False(MakerEditRules.IsSubtitleText("   ", out _));
        }

        [Fact]
        public void The_paths_written_are_the_contract_paths()
        {
            Assert.Equal("data/subtitles/th.json", MakerEditRules.SubtitlePath("th"));
            Assert.Equal("audio/voice/th/Voice_X.wav", MakerEditRules.VoicePath("th", "Voice_X"));
            Assert.Equal("_maker_backup/audio/voice/th/Voice_X.ogg", MakerEditRules.BackupPathOf("audio/voice/th/Voice_X.ogg"));
        }

        [Theory]
        [InlineData("cwy.example", true)]
        [InlineData("cwy.demo.audio", true)]
        [InlineData("cwy.sample.hello", true)]
        [InlineData("cwy.thai", false)]
        [InlineData("my.thai", false)]
        public void Reference_packs_are_recognised(string id, bool reference) =>
            Assert.Equal(reference, MakerEditRules.IsReferencePack(id));

        [Fact]
        public void An_installed_content_pack_in_cwyls_mods_is_editable()
        {
            var verdict = MakerPackAccess.Judge(Facts());

            Assert.True(verdict.Known);
            Assert.True(verdict.Editable);
            Assert.Null(verdict.Reason);
        }

        [Fact]
        public void Each_rule_gives_its_own_reason()
        {
            var unknown = Facts();
            unknown.Loaded = false;
            var skipped = Facts();
            skipped.Loaded = false;
            skipped.SkipReason = "MissingDependency";
            var code = Facts();
            code.IsCodeMod = true;
            var reference = Facts();
            reference.Id = "cwy.example";
            var elsewhere = Facts();
            elsewhere.RootPath = Path.Combine(_root, "elsewhere", "my.thai");
            var locked = Facts();
            locked.WriteProblem = "Access denied";

            Assert.False(MakerPackAccess.Judge(unknown).Known);
            Assert.Contains("MissingDependency", MakerPackAccess.Judge(skipped).Reason);
            Assert.Contains("code mod", MakerPackAccess.Judge(code).Reason);
            Assert.Contains("reference pack", MakerPackAccess.Judge(reference).Reason);
            Assert.Contains("not in CWYLS Mods", MakerPackAccess.Judge(elsewhere).Reason);
            Assert.Contains("Access denied", MakerPackAccess.Judge(locked).Reason);
            Assert.False(MakerPackAccess.Judge(locked).Editable);
        }

        [Fact]
        public void A_pack_nested_deeper_than_cwyls_mods_is_not_a_direct_child()
        {
            Assert.True(MakerPackAccess.IsDirectChild(Pack, PacksRoot));
            Assert.True(MakerPackAccess.IsDirectChild(Pack + Path.DirectorySeparatorChar, PacksRoot));
            Assert.False(MakerPackAccess.IsDirectChild(Path.Combine(Pack, "inner"), PacksRoot));
            Assert.False(MakerPackAccess.IsDirectChild(PacksRoot, PacksRoot));
        }

        [Fact]
        public void Safe_paths_stay_inside_the_pack()
        {
            var files = new MakerPackFiles(Pack);

            Assert.True(files.TryResolve("data/subtitles/th.json", out var full, out _));
            Assert.Equal(Path.Combine(Pack, "data", "subtitles", "th.json"), full);
            Assert.False(files.TryResolve("../other.pack/data/x.json", out _, out _));
            Assert.False(files.TryResolve("data/../../x", out _, out _));
            // A leading slash is stripped, never honoured: the path stays inside the pack.
            Assert.True(files.TryResolve("/etc/passwd", out var rooted, out _));
            Assert.StartsWith(Pack + Path.DirectorySeparatorChar, rooted);
            Assert.False(files.TryResolve("C:/x", out _, out _));
            Assert.False(files.TryResolve("", out _, out _));
            Assert.Throws<UnauthorizedAccessException>(() => files.Resolve("../x"));
        }

        [Fact]
        public void A_link_inside_the_pack_is_refused()
        {
            var outside = Path.Combine(_root, "outside");
            Directory.CreateDirectory(outside);
            Directory.CreateDirectory(Path.Combine(Pack, "audio"));
            try
            {
                Directory.CreateSymbolicLink(Path.Combine(Pack, "audio", "voice"), outside);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is PlatformNotSupportedException)
            {
                // No symlink rights (Windows without developer mode): nothing to prove here.
                return;
            }

            var files = new MakerPackFiles(Pack);

            Assert.False(files.TryResolve("audio/voice/th/Voice_X.wav", out _, out var problem));
            Assert.Contains("link", problem);
        }

        [Fact]
        public void A_writable_pack_folder_has_no_write_problem_and_leaves_no_probe_behind()
        {
            var files = new MakerPackFiles(Pack);

            Assert.Null(files.WriteProblem());
            Assert.Empty(Directory.GetFileSystemEntries(Pack));
            Assert.NotNull(new MakerPackFiles(Path.Combine(_root, "missing")).WriteProblem());
        }

        private MakerPackFacts Facts() => new MakerPackFacts
        {
            Id = "my.thai",
            Loaded = true,
            RootPath = Pack,
            PacksRoot = PacksRoot,
        };
    }
}
