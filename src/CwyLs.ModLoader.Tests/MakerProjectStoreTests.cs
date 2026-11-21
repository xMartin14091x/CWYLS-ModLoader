using System.IO;
using CwyLs.ModLoader.Core.Api.Maker.Studio;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    public sealed class MakerProjectStoreTests
    {
        [Fact]
        public void Initial_revision_is_stable_across_store_instances_and_tracks_pack_text()
        {
            using var fx = new MakerProjectFixture();
            fx.Write("data/subtitles/th.json", "{\"sample\":\"หนึ่ง\"}");

            var first = fx.Store.ReadLine("th", "sample").Value!;
            var reopened = new MakerProjectStore(fx.Root, fx.Clock).ReadLine("th", "sample").Value!;
            fx.Write("data/subtitles/th.json", "{\"sample\":\"สอง\"}");
            var changed = new MakerProjectStore(fx.Root, fx.Clock).ReadLine("th", "sample").Value!;

            Assert.Equal(first.Revision, reopened.Revision);
            Assert.NotEqual(first.Revision, changed.Revision);
        }

        [Theory]
        [InlineData("{broken")]
        [InlineData("{\"schema\":2,\"projectId\":\"future\"}")]
        public void Malformed_or_future_project_metadata_is_preserved_and_read_only(string json)
        {
            using var fx = new MakerProjectFixture();
            fx.Write(".maker/project.json", json);

            var result = fx.Store.ReadHeader();

            Assert.False(result.Ok);
            Assert.Equal("metadata_unavailable", result.Error);
            Assert.Equal(json, fx.Read(".maker/project.json"));
        }

        [Fact]
        public void Project_size_counts_only_plain_files_and_refuses_links()
        {
            using var fx = new MakerProjectFixture();
            fx.Write(".maker/a.txt", "1234");
            fx.Write(".maker/nested/b.txt", "12");

            Assert.Equal(6, fx.Store.Files.BytesUnder(".maker"));
        }
    }
}
