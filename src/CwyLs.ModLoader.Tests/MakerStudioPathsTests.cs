using System;
using System.IO;
using System.Linq;
using CwyLs.ModLoader.Core.Api.Maker.Studio;
using CwyLs.ModLoader.Core.Packs;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    public sealed class MakerStudioPathsTests
    {
        [Fact]
        public void Paths_are_constructed_only_from_validated_identifiers()
        {
            Assert.Equal(".maker/lines/th/sample_01.json", MakerStudioPaths.Line("th", "sample_01"));
            Assert.Equal(".maker/glossary/pt-br.json", MakerStudioPaths.Glossary("pt-br"));
            Assert.Equal(".maker/transactions/00000000000000000000000000000001.json", MakerStudioPaths.Receipt("00000000000000000000000000000001"));
            Assert.Throws<ArgumentException>(() => MakerStudioPaths.Line("../th", "x"));
            Assert.Throws<ArgumentException>(() => MakerStudioPaths.Line("th", "../x"));
            Assert.Throws<ArgumentException>(() => MakerStudioPaths.Receipt("ABC"));
        }

        [Fact]
        public void Case_colliding_line_sidecars_are_refused()
        {
            using var fx = new MakerProjectFixture();
            fx.Write(".maker/lines/th/Sample.json", "{}");

            var result = fx.Store.ReadLine("th", "sample");

            Assert.False(result.Ok);
            Assert.Equal("conflict", result.Error);
            Assert.Contains("collides by case", result.Message);
        }

        [Fact]
        public void Links_inside_the_project_are_never_followed()
        {
            using var fx = new MakerProjectFixture();
            var outside = Path.Combine(Path.GetDirectoryName(fx.Root)!, "outside");
            Directory.CreateDirectory(outside);
            var link = Path.Combine(fx.Root, ".maker");
            try
            {
                Directory.CreateSymbolicLink(link, outside);
            }
            catch (Exception ex) when (ex is PlatformNotSupportedException || ex is UnauthorizedAccessException || ex is IOException)
            {
                return;
            }

            Assert.Throws<UnauthorizedAccessException>(() => fx.Store.Files.ListFiles(".maker"));
            Assert.False(File.Exists(Path.Combine(outside, "project.json")));
        }

        [Fact]
        public void Runtime_pack_index_keeps_maker_sidecars_in_other_only()
        {
            var index = PackContentIndex.Build(new[]
            {
                "manifest.json", "data/subtitles/th.json", "textures/a.png",
                ".maker/project.json", ".maker/lines/th/sample.json", "voice-provenance.json",
            });

            Assert.Contains(index.Other, f => f.Path == ".maker/project.json");
            Assert.Contains(index.Other, f => f.Path == ".maker/lines/th/sample.json");
            Assert.Contains(index.Other, f => f.Path == "voice-provenance.json");
            Assert.DoesNotContain(index.Textures, f => f.Path.StartsWith(".maker", StringComparison.Ordinal));
            Assert.Single(index.Textures);
        }
    }
}
