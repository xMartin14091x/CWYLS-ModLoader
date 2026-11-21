using System;
using System.IO;
using System.Text;
using CwyLs.ModLoader.Core.Api;
using CwyLs.ModLoader.Core.Api.Maker;
using CwyLs.ModLoader.Core.Api.Maker.Studio;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    public sealed class MakerHistorySnapshotRegressionTests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void External_table_edit_after_replacement_is_derived_is_preserved(bool bom)
        {
            using var fx = new MakerProjectFixture();
            const string path = "data/subtitles/th.json";
            fx.Write(path, "{\"one\":\"old\",\"two\":\"old\"}");
            File.WriteAllText(fx.Store.Files.Resolve(path), fx.Read(path), new UTF8Encoding(bom));
            var clock = new InterleavingClock();
            var project = new MakerProjectStore(fx.Root, clock);
            var revision = project.ReadLine("th", "one").Value!.Revision;
            // AddHistory asks for time after Apply has derived the replacement, before Target is built.
            clock.Once = () => fx.Write(path, "{\"one\":\"old\",\"two\":\"external\"}");

            var result = new MakerHistoryStore(project).Save(Request(fx, 1, revision, "studio"));

            Assert.False(result.Ok);
            Assert.Equal("recovery_required", result.Error);
            Assert.Equal("external", project.CurrentSubtitle("th", "two"));
            Assert.Equal("old", project.CurrentSubtitle("th", "one"));
            Assert.True(project.Files.Exists(MakerStudioPaths.Pending(fx.Operation(1))));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Intervening_sidecar_creation_or_edit_is_preserved(bool existing)
        {
            using var fx = new MakerProjectFixture();
            fx.Write("data/subtitles/th.json", "{\"one\":\"old\"}");
            if (existing)
            {
                var first = new MakerHistoryStore(fx.Store).Save(Request(fx, 10,
                    fx.Store.ReadLine("th", "one").Value!.Revision, "seed"));
                Assert.True(first.Ok, first.Message);
            }
            var clock = new InterleavingClock();
            var project = new MakerProjectStore(fx.Root, clock);
            var revision = project.ReadLine("th", "one").Value!.Revision;
            var before = project.CurrentSubtitle("th", "one");
            var path = MakerStudioPaths.Line("th", "one");
            var external = "{\"schema\":1,\"revision\":\"external-sidecar\",\"subtitleFingerprint\":\""
                + project.SubtitleFingerprint(before) + "\",\"workflow\":{\"status\":\"done\",\"starred\":true},\"takes\":[],\"history\":[]}";
            clock.Once = () => fx.Write(path, external);

            var result = new MakerHistoryStore(project).Save(Request(fx, 1, revision, "studio"));

            Assert.False(result.Ok);
            Assert.Equal("recovery_required", result.Error);
            Assert.Equal(external, fx.Read(path));
            Assert.Equal(before, project.CurrentSubtitle("th", "one"));
        }

        [Fact]
        public void Glossary_target_uses_the_bytes_that_were_parsed()
        {
            using var fx = new MakerProjectFixture();
            const string path = ".maker/glossary/th.json";
            fx.Write(path, "{\"schema\":1,\"revision\":\"before\",\"entries\":[]}");
            var glossary = fx.Store.ReadGlossary("th").Value!;
            glossary.Revision = "studio";
            const string external = "{\"schema\":1,\"revision\":\"external\",\"entries\":[]}";
            fx.Write(path, external);

            var target = fx.Store.Target(path, MakerProjectStore.Utf8(
                "{\"schema\":1,\"revision\":\"" + glossary.Revision + "\",\"entries\":[]}"));
            var result = fx.Store.Commit(fx.Operation(1), new[] { target }, "glossary");

            Assert.False(result.Ok);
            Assert.Equal("recovery_required", result.Error);
            Assert.Equal(external, fx.Read(path));
        }

        [Fact]
        public void Best_effort_v1_capture_preserves_an_intervening_sidecar_edit()
        {
            using var fx = new MakerProjectFixture();
            var seed = new MakerHistoryStore(fx.Store).Save(Request(fx, 10,
                fx.Store.ReadLine("th", "one").Value!.Revision, "seed"));
            Assert.True(seed.Ok, seed.Message);
            var path = MakerStudioPaths.Line("th", "one");
            var external = fx.Read(path).Replace("\"starred\":false", "\"starred\":true");
            Assert.NotEqual(fx.Read(path), external);
            fx.Write("data/subtitles/th.json", "{\"one\":\"v1\"}");
            var clock = new InterleavingClock { Once = () => fx.Write(path, external) };

            new MakerHistoryStore(new MakerProjectStore(fx.Root, clock)).CaptureV1("th", "one", "seed", "v1");

            Assert.Equal(external, fx.Read(path));
            Assert.Equal("v1", fx.Store.CurrentSubtitle("th", "one"));
            Assert.False(fx.Store.Files.Exists(MakerStudioPaths.Pending(fx.Operation(10))));
        }

        internal static MakerStudioSubtitleRequest Request(MakerProjectFixture fx, int op, string revision, string? text) =>
            new MakerStudioSubtitleRequest
            {
                PackId = "sample.pack", Lang = "th", Id = "one", ExpectedRevision = revision,
                OperationId = fx.Operation(op), Text = text,
            };

        private sealed class InterleavingClock : IApiClock
        {
            internal Action? Once { get; set; }
            public double MonotonicSeconds => 0;
            public DateTimeOffset Now
            {
                get
                {
                    var action = Once;
                    Once = null;
                    action?.Invoke();
                    return new DateTimeOffset(2026, 11, 11, 7, 0, 0, TimeSpan.Zero);
                }
            }
        }
    }
}
