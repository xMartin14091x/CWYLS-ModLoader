using CwyLs.ModLoader.Core.Packs;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// W0-CONTRACTS: the Phases D-S content kinds, decided from paths alone, and the path-safety
    /// rules of C1 review #11 (no ':' anywhere, no Windows device names, no trailing dot or space).
    /// </summary>
    public class PacksContentKindsTests
    {
        [Theory]
        [InlineData("data/edit/music.json", PackContentKind.EditData)]
        [InlineData("data/edit/10-news.JSON", PackContentKind.EditData)]
        [InlineData("audio/ambient/Rain.ogg", PackContentKind.AmbientAudio)]
        [InlineData("audio/ambient/rain.WAV", PackContentKind.AmbientAudio)]
        [InlineData("audio/ambient_se/Thunder.ogg", PackContentKind.AmbientSeAudio)]
        [InlineData("audio/se/system/Click.wav", PackContentKind.SystemSeAudio)]
        [InlineData("audio/se/timer/Bell.ogg", PackContentKind.TimerSeAudio)]
        [InlineData("audio/direction_music/Story01.ogg", PackContentKind.DirectionMusicAudio)]
        [InlineData("audio/music/_tracks.json", PackContentKind.MusicTracks)]
        [InlineData("AUDIO/MUSIC/_TRACKS.JSON", PackContentKind.MusicTracks)]
        [InlineData("ui/_sprites.json", PackContentKind.UiSprites)]
        [InlineData("ui/Icon_Play.png", PackContentKind.UiSprite)]
        [InlineData("ui/music/panel/Icon_Play.PNG", PackContentKind.UiSprite)]
        [InlineData("data/triggers.json", PackContentKind.Triggers)]
        public void Classifies_the_new_conventions(string path, PackContentKind expected) =>
            Assert.Equal(expected, PackContentIndex.Classify(path));

        [Theory]
        [InlineData("data/edit/sub/music.json")]          // EditData: direct children only
        [InlineData("data/edit/notes.txt")]               // not JSON
        [InlineData("audio/ambient/old/rain.ogg")]        // audio folders: direct children only
        [InlineData("audio/ambient/rain.mp3")]            // only OGG and WAV
        [InlineData("audio/se/Click.wav")]                // se/ needs system/ or timer/
        [InlineData("audio/se/other/Click.wav")]
        [InlineData("ui/readme.txt")]
        [InlineData("ui/_sprites.txt")]
        [InlineData("ui/sub/_sprites.json")]              // the rules file lives at ui/ only
        [InlineData("data/subtitles/th.json")]            // still Other: the subtitle reader picks it by path
        [InlineData("data/triggers/extra.json")]
        public void Leaves_near_misses_as_Other(string path) =>
            Assert.Equal(PackContentKind.Other, PackContentIndex.Classify(path));

        [Fact]
        public void Existing_conventions_are_unchanged()
        {
            Assert.Equal(PackContentKind.Music, PackContentIndex.Classify("audio/music/lofi_01.ogg"));
            Assert.Equal(PackContentKind.Music, PackContentIndex.Classify("audio/music/anything.mp3"));
            Assert.Equal(PackContentKind.Texture, PackContentIndex.Classify("textures/@mt_Body/_MainTex.png"));
            Assert.Equal(PackContentKind.Other, PackContentIndex.Classify("textures/_rules.json"));
            Assert.Equal(PackContentKind.Voice, PackContentIndex.Classify("audio/voice/Voice_New.wav"));
        }

        [Fact]
        public void The_index_exposes_every_new_kind()
        {
            var index = PackContentIndex.Build(new[]
            {
                "manifest.json",
                "data/edit/a.json",
                "data/edit/b.json",
                "data/triggers.json",
                "audio/ambient/Rain.ogg",
                "audio/ambient_se/Thunder.ogg",
                "audio/se/system/Click.wav",
                "audio/se/timer/Bell.ogg",
                "audio/direction_music/Story.ogg",
                "audio/music/_tracks.json",
                "audio/music/song.ogg",
                "ui/_sprites.json",
                "ui/icons/play.png",
            });

            Assert.Equal(new[] { "data/edit/a.json", "data/edit/b.json" }, new[] { index.EditData[0].Path, index.EditData[1].Path });
            Assert.Equal("data/triggers.json", index.Triggers!.Path);
            Assert.Single(index.AmbientAudio);
            Assert.Single(index.AmbientSeAudio);
            Assert.Single(index.SystemSeAudio);
            Assert.Single(index.TimerSeAudio);
            Assert.Single(index.DirectionMusicAudio);
            Assert.Equal("audio/music/_tracks.json", index.MusicTracks!.Path);
            Assert.Equal("audio/music/song.ogg", Assert.Single(index.Music).Path);
            Assert.Equal("ui/_sprites.json", index.UiSpriteRules!.Path);
            Assert.Equal("ui/icons/play.png", Assert.Single(index.UiSprites).Path);
            Assert.Empty(index.Other);
        }

        [Fact]
        public void An_empty_index_has_none_of_them()
        {
            Assert.Empty(PackContentIndex.Empty.EditData);
            Assert.Null(PackContentIndex.Empty.Triggers);
            Assert.Null(PackContentIndex.Empty.MusicTracks);
            Assert.Null(PackContentIndex.Empty.UiSpriteRules);
            Assert.Empty(PackContentIndex.Empty.UiSprites);
        }

        [Theory]
        [InlineData("C:/x.png")]
        [InlineData("C:x.png")]
        [InlineData("c:\\windows\\x.png")]
        [InlineData("/C:/x.png")]
        [InlineData("textures/a.png:hidden")]
        [InlineData("textures/con.png")]
        [InlineData("audio/voice/NUL.ogg")]
        [InlineData("ui/COM1.png")]
        [InlineData("aux/readme.md")]
        [InlineData("textures/lpt9")]
        [InlineData("textures/a.png.")]
        [InlineData("textures/folder./a.png")]
        [InlineData("textures/folder /a.png")]
        public void Normalise_refuses_paths_that_mean_something_else_on_Windows(string raw) =>
            Assert.Null(PackContentIndex.Normalise(raw));

        [Fact]
        public void Whitespace_around_the_whole_path_is_still_trimmed_first()
        {
            Assert.Equal("textures/a.png", PackContentIndex.Normalise("  textures/a.png "));
        }

        [Theory]
        [InlineData("textures/console.png", "textures/console.png")]
        [InlineData("audio/voice/null.ogg", "audio/voice/null.ogg")]
        [InlineData("ui/com10.png", "ui/com10.png")]
        [InlineData("ui/com0.png", "ui/com0.png")]
        [InlineData("audio/voice/lpt.ogg", "audio/voice/lpt.ogg")]
        [InlineData("./data/a.b.json", "data/a.b.json")]
        public void Normalise_keeps_names_that_only_look_like_devices(string raw, string expected) =>
            Assert.Equal(expected, PackContentIndex.Normalise(raw));

        [Fact]
        public void A_listing_with_an_unsafe_entry_drops_only_that_entry()
        {
            var index = PackContentIndex.Build(new[] { "textures/con.png", "textures/body.png", "C:/evil.png" });

            Assert.Equal("textures/body.png", Assert.Single(index.Files).Path);
        }

        [Theory]
        [InlineData("con", true)]
        [InlineData("CON", true)]
        [InlineData("con.txt", true)]
        [InlineData("Nul.tar.gz", true)]
        [InlineData("aux", true)]
        [InlineData("prn.json", true)]
        [InlineData("com1", true)]
        [InlineData("COM9.log", true)]
        [InlineData("lpt5.x", true)]
        [InlineData("com0", false)]
        [InlineData("com10", false)]
        [InlineData("console", false)]
        [InlineData("connect.json", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void Device_names_are_recognised_with_any_extension(string? name, bool expected) =>
            Assert.Equal(expected, SafeFileNames.IsReservedDeviceName(name));

        [Theory]
        [InlineData("save.json", null)]
        [InlineData("a:b", "':'")]
        [InlineData("nul.json", "device name")]
        [InlineData("save.", "dot or a space")]
        [InlineData("save ", "dot or a space")]
        [InlineData("..", null)]
        public void ProblemWith_names_the_rule(string segment, string? fragment)
        {
            var problem = SafeFileNames.ProblemWith(segment);
            if (fragment is null)
            {
                Assert.Null(problem);
            }
            else
            {
                Assert.Contains(fragment, problem);
            }
        }
    }
}
