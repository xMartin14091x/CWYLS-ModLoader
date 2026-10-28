using CwyLs.ModLoader.Core.Packs;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>P1-1: the pack folder conventions, decided from paths alone.</summary>
    public class PacksContentIndexTests
    {
        [Theory]
        [InlineData("manifest.json", PackContentKind.Manifest)]
        [InlineData("textures/Body/_MainTex.png", PackContentKind.Texture)]
        [InlineData("textures/top.png", PackContentKind.Texture)]
        [InlineData("textures/a/b/c/deep.PNG", PackContentKind.Texture)]
        [InlineData("audio/voice/Voice_Tutorial_1_001.ogg", PackContentKind.Voice)]
        [InlineData("audio/voice/Voice_New.wav", PackContentKind.Voice)]
        [InlineData("audio/music/lofi_01.ogg", PackContentKind.Music)]
        [InlineData("audio/music/anything.mp3", PackContentKind.Music)]
        [InlineData("data/localization.json", PackContentKind.Localization)]
        [InlineData("data/novel.json", PackContentKind.Novel)]
        [InlineData("data/scenario_groups.json", PackContentKind.ScenarioGroups)]
        [InlineData("README.md", PackContentKind.Other)]
        [InlineData("textures/source.psd", PackContentKind.Other)]
        [InlineData("audio/voice/notes.txt", PackContentKind.Other)]
        [InlineData("audio/voice/nested/deep.ogg", PackContentKind.Other)]
        [InlineData("data/extra.json", PackContentKind.Other)]
        [InlineData("packs/inner/thing.png", PackContentKind.Other)]
        public void Classifies_by_convention(string path, PackContentKind expected) =>
            Assert.Equal(expected, PackContentIndex.Classify(path));

        [Fact]
        public void Groups_the_listing_by_kind()
        {
            var index = PackContentIndex.Build(new[]
            {
                "manifest.json",
                "textures/Body/_MainTex.png",
                "textures/Face/_MainTex.png",
                "audio/voice/A.ogg",
                "audio/music/track.ogg",
                "data/localization.json",
                "data/novel.json",
                "data/scenario_groups.json",
                "README.md",
            });

            Assert.Equal(9, index.Count);
            Assert.Equal(2, index.Textures.Count);
            Assert.Single(index.Voices);
            Assert.Single(index.Music);
            Assert.Single(index.Other);
            Assert.True(index.HasManifest);
            Assert.Equal("data/localization.json", index.Localization!.Path);
            Assert.Equal("data/novel.json", index.Novel!.Path);
            Assert.Equal("data/scenario_groups.json", index.ScenarioGroups!.Path);
        }

        [Fact]
        public void Normalises_separators_and_leading_segments()
        {
            var index = PackContentIndex.Build(new[]
            {
                "textures\\Body\\_MainTex.png",
                "./audio/voice/A.ogg",
                "/data/novel.json",
            });

            Assert.Equal(
                new[] { "audio/voice/A.ogg", "data/novel.json", "textures/Body/_MainTex.png" },
                new[] { index.Files[0].Path, index.Files[1].Path, index.Files[2].Path });
            Assert.Single(index.Textures);
            Assert.Single(index.Voices);
            Assert.NotNull(index.Novel);
        }

        [Fact]
        public void Drops_blank_duplicate_and_escaping_entries()
        {
            var index = PackContentIndex.Build(new string?[]
            {
                "audio/voice/A.ogg",
                "audio/voice/a.ogg",
                "   ",
                null,
                "../outside.png",
                "textures/../../outside.png",
                "textures/",
            });

            var file = Assert.Single(index.Files);
            Assert.Equal("audio/voice/A.ogg", file.Path);
        }

        [Fact]
        public void Orders_files_so_two_scans_agree()
        {
            var first = PackContentIndex.Build(new[] { "b.txt", "a.txt", "c.txt" });
            var second = PackContentIndex.Build(new[] { "c.txt", "b.txt", "a.txt" });

            Assert.Equal(
                new[] { "a.txt", "b.txt", "c.txt" },
                new[] { first.Files[0].Path, first.Files[1].Path, first.Files[2].Path });
            Assert.Equal(first.Files[0].Path, second.Files[0].Path);
            Assert.Equal(first.Files[2].Path, second.Files[2].Path);
        }

        [Fact]
        public void Splits_a_file_into_name_and_extension()
        {
            var file = Assert.Single(PackContentIndex.Build(new[] { "audio/voice/Voice_Tutorial_1_001.OGG" }).Files);

            Assert.Equal("Voice_Tutorial_1_001.OGG", file.FileName);
            Assert.Equal("Voice_Tutorial_1_001", file.Name);
            Assert.Equal(".ogg", file.Extension);
            Assert.Equal(PackContentKind.Voice, file.Kind);
        }

        [Fact]
        public void An_empty_listing_yields_an_empty_index()
        {
            Assert.Empty(PackContentIndex.Build(new string[0]).Files);
            Assert.Empty(PackContentIndex.Build(null).Files);
            Assert.Empty(PackContentIndex.Empty.Files);
            Assert.False(PackContentIndex.Empty.HasManifest);
            Assert.Null(PackContentIndex.Empty.Localization);
            Assert.Empty(PackContentIndex.Empty.OfKind(PackContentKind.Texture));
        }
    }
}
