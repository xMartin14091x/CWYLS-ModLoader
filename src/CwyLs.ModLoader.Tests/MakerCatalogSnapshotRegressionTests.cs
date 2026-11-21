using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using CwyLs.ModLoader.Core.Api.Maker;
using CwyLs.ModLoader.Core.Api.Maker.Catalog;
using CwyLs.ModLoader.Core.Api.Maker.Studio;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    public sealed class MakerCatalogSnapshotRegressionTests
    {
        [Theory]
        [InlineData("recovered")]
        [InlineData(null)]
        public async Task Exact_line_after_pending_recovery_pairs_recovered_text_and_revision(string? intended)
        {
            using var fx = new MakerProjectFixture();
            fx.Write("data/subtitles/th.json", "{\"one\":\"old\"}");
            var revision = fx.Store.ReadLine("th", "one").Value!.Revision;
            var boundary = typeof(MakerProjectStore).GetField("_commitBoundary", BindingFlags.Instance | BindingFlags.NonPublic)!;
            boundary.SetValue(fx.Store, (Action<string>)(p =>
            {
                if (p == "after-intent") throw new InvalidOperationException("simulated interruption");
            }));
            Assert.Throws<InvalidOperationException>(() => new MakerHistoryStore(fx.Store).Save(
                MakerHistorySnapshotRegressionTests.Request(fx, 1, revision, intended)));
            boundary.SetValue(fx.Store, null);
            Assert.Equal("old", fx.Store.CurrentSubtitle("th", "one"));
            var catalog = new MakerStudioCatalog(new Source(Snapshot(fx)));

            var result = await catalog.LineAsync("sample.pack", "th", "one", null, CancellationToken.None);

            Assert.True(result.Ok, result.Message);
            Assert.Equal(intended, result.Value!.OwnedSubtitle);
            Assert.Equal(fx.Store.ReadLine("th", "one").Value!.Revision, result.Value.Revision);
            var unchanged = new MakerHistoryStore(fx.Store).Save(MakerHistorySnapshotRegressionTests.Request(
                fx, 2, result.Value.Revision, result.Value.OwnedSubtitle));
            Assert.True(unchanged.Ok, unchanged.Message);
            Assert.False(unchanged.Value!.Saved.Saved);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Save_between_catalog_open_and_authoritative_build_keeps_text_with_its_revision(bool exact)
        {
            using var fx = new MakerProjectFixture();
            fx.Write("data/subtitles/th.json", "{\"one\":\"old\"}");
            if (!exact)
            {
                var created = new MakerTakeStore(fx.Store).Create(new MakerStudioCreateTakeRequest
                {
                    PackId = "sample.pack", Lang = "th", Id = "one", OperationId = fx.Operation(10),
                    ExpectedRevision = fx.Store.ReadLine("th", "one").Value!.Revision, Origin = "own",
                    Consent = new MakerStudioConsent { Confirmed = true, StatementVersion = MakerStudioContract.ConsentVersion },
                });
                Assert.True(created.Ok, created.Message);
            }
            var snapshot = Snapshot(fx);
            var catalog = new MakerStudioCatalog(new Source(snapshot));
            // Pin the existing Open/Build boundary, without a timing race or a new production test hook.
            var open = typeof(MakerStudioCatalog).GetMethod("Open", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var opened = open.Invoke(catalog, new object[] { snapshot, "sample.pack", "th" })!;
            var scope = opened.GetType().GetProperty("Value")!.GetValue(opened)!;
            var save = new MakerHistoryStore(fx.Store).Save(MakerHistorySnapshotRegressionTests.Request(fx, 1,
                fx.Store.ReadLine("th", "one").Value!.Revision, "newer"));
            Assert.True(save.Ok, save.Message);
            snapshot.View.Game.TryFind("one", out var row);
            var build = scope.GetType().GetMethod("Build", BindingFlags.Instance | BindingFlags.NonPublic)!;

            var result = (MakerResult<MakerStudioLine>)build.Invoke(scope, new object?[] { row, null, exact })!;

            Assert.True(result.Ok, result.Message);
            Assert.Equal("newer", result.Value!.OwnedSubtitle);
            Assert.Equal(save.Value!.Saved.Revision, result.Value.Revision);
        }

        private static MakerStudioCatalogSnapshot Snapshot(MakerProjectFixture fx)
        {
            var sources = new[] { new MakerPackSource("sample.pack", "Sample", "1.0.0", fx.Root, false, new[] { "th" }) };
            var view = new MakerCatalogView(new MakerGameData(new[] { new MakerGameRow("one", null, "morning", "ja", "en") }, null, null,
                new[] { new MakerGameNovelRow("n1", "morning", "one", "Heroine", null, null) }, null),
                MakerPackScanner.Scan(sources));
            return new MakerStudioCatalogSnapshot(view, new[] { new MakerPack
            {
                Id = "sample.pack", Name = "Sample", Version = "1.0.0", Editable = true, Languages = new List<string> { "th" },
            } }, sources, null);
        }

        private sealed class Source : IMakerStudioCatalogSource
        {
            private readonly MakerStudioCatalogSnapshot _snapshot;
            internal Source(MakerStudioCatalogSnapshot snapshot) => _snapshot = snapshot;
            public Task<MakerResult<MakerStudioCatalogSnapshot>> SnapshotAsync(bool editorVerdicts, CancellationToken cancel) =>
                Task.FromResult(MakerResult<MakerStudioCatalogSnapshot>.Success(_snapshot));
        }
    }
}
