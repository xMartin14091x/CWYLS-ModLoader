using System;
using CwyLs.ModLoader.Core.Loaders;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// <see cref="AssetKey.Name"/> is what P1-3 writes into <c>Texture2D.name</c> and what P1-4
    /// registers a clip under in the game's voice cache, so these are behaviour tests, not
    /// string-formatting trivia.
    /// </summary>
    public class LoadersAssetKeyTests
    {
        [Fact]
        public void FromPath_takes_the_name_without_the_extension()
        {
            var key = AssetKey.FromPath("audio/voice/Voice_Tutorial_1_001.ogg");

            Assert.Equal("Voice_Tutorial_1_001", key.Name);
            Assert.Equal("audio/voice/Voice_Tutorial_1_001.ogg", key.Path);
            Assert.Equal(".ogg", key.Extension);
        }

        [Fact]
        public void FromPath_normalises_backslashes_and_redundant_segments()
        {
            var key = AssetKey.FromPath(@".\textures\\Body/_MainTex.png");

            Assert.Equal("textures/Body/_MainTex.png", key.Path);
            Assert.Equal("_MainTex", key.Name);
        }

        [Fact]
        public void FromPath_keeps_a_leading_slash_on_an_absolute_path()
        {
            var key = AssetKey.FromPath("/Users/x/CWYLS Mods/demo/textures/Top1.PNG");

            Assert.Equal("/Users/x/CWYLS Mods/demo/textures/Top1.PNG", key.Path);
            Assert.Equal("Top1", key.Name);

            // Casing is preserved in the name but folded in the extension, which is only ever
            // compared against a fixed list.
            Assert.Equal(".png", key.Extension);
        }

        [Fact]
        public void FromPath_only_strips_the_last_extension()
        {
            var key = AssetKey.FromPath("audio/Voice.v2.wav");

            Assert.Equal("Voice.v2", key.Name);
            Assert.Equal(".wav", key.Extension);
        }

        [Fact]
        public void FromPath_treats_a_leading_dot_as_part_of_the_name()
        {
            var key = AssetKey.FromPath("packs/.keep");

            Assert.Equal(".keep", key.Name);
            Assert.Equal(string.Empty, key.Extension);
        }

        [Fact]
        public void FromPath_handles_a_bare_file_name_with_no_extension()
        {
            var key = AssetKey.FromPath("README");

            Assert.Equal("README", key.Name);
            Assert.Equal("README", key.Path);
            Assert.Equal(string.Empty, key.Extension);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("textures/")]
        [InlineData("/")]
        [InlineData(".")]
        public void FromPath_rejects_a_path_with_no_file_name(string path)
        {
            Assert.Throws<ArgumentException>(() => AssetKey.FromPath(path));
        }

        [Fact]
        public void Keys_are_equal_when_the_normalised_paths_match_and_case_sensitive_otherwise()
        {
            Assert.Equal(AssetKey.FromPath(@"textures\Body\_MainTex.png"), AssetKey.FromPath("textures/Body/_MainTex.png"));
            Assert.NotEqual(AssetKey.FromPath("textures/body/_MainTex.png"), AssetKey.FromPath("textures/Body/_MainTex.png"));
            Assert.True(AssetKey.FromPath("a/b.png") == AssetKey.FromPath("./a/b.png"));
            Assert.True(AssetKey.FromPath("a/b.png") != AssetKey.FromPath("a/c.png"));
        }
    }
}
