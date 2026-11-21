using System;
using System.IO;
using System.Threading.Tasks;
using CwyLs.ModLoader.Core.Api;
using CwyLs.ModLoader.Core.Api.Maker;
using CwyLs.ModLoader.Core.Json;
using CwyLs.ModLoader.Core.Api.Maker.Studio;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    public sealed class MakerR1StorageTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "cwy-r1-storage-" + Guid.NewGuid().ToString("N"));
        private string Pack => Path.Combine(_root, "lab.release");
        private string Exports => Path.Combine(_root, ".maker-exports");
        public MakerR1StorageTests()
        {
            Directory.CreateDirectory(Pack);
            File.WriteAllText(Path.Combine(Pack, "manifest.json"), "{\"id\":\"lab.release\",\"name\":\"Release lab\",\"version\":\"0.1.0\",\"loaderVersion\":\"0.9.2\"}");
        }
        public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
        private MakerPackExporter Exporter() => new MakerPackExporter(
            (_, __) => MakerResult<MakerProjectStore>.Success(new MakerProjectStore(Pack)), _ => null, () => Exports,
            publish: (_, __) => { });
        private static MakerExportRequest Request() => new MakerExportRequest
        { PackId = "lab.release", OperationId = new string('a', 32), ExpectedVersion = "0.1.0", Bump = "patch" };

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Other_pack_and_legacy_exports_do_not_spend_this_packs_budget(bool bytes)
        {
            Directory.CreateDirectory(Exports);
            if (bytes)
            {
                using var sparse = File.Create(Path.Combine(Exports, "other-v0.1.0.zip"));
                sparse.SetLength(4L * 1024 * 1024 * 1024);
            }
            else for (var i = 0; i < 100; i++) File.WriteAllText(Path.Combine(Exports, "other-" + i + ".zip"), "keep");
            var result = await Exporter().ExportAsync(Request(), default);
            Assert.True(result.Ok, result.Message);
            Assert.True(File.Exists(Path.Combine(Exports, "lab.release", result.Value!.FileName)));
            Assert.Equal(bytes ? 1 : 100, Directory.GetFiles(Exports).Length);
            Assert.True((await Exporter().ReadExportAsync("lab.release", result.Value.FileName, default)).Ok);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Per_pack_export_limit_refuses_before_mutation_and_names_the_actual_folder(bool bytes)
        {
            var folder = Path.Combine(Exports, "lab.release");
            Directory.CreateDirectory(folder);
            if (bytes)
            {
                using var sparse = File.Create(Path.Combine(folder, "retained.zip"));
                sparse.SetLength(4L * 1024 * 1024 * 1024);
            }
            else for (var i = 0; i < 100; i++) File.WriteAllText(Path.Combine(folder, "retained-" + i + ".zip"), "keep");
            var before = File.ReadAllText(Path.Combine(Pack, "manifest.json"));
            var result = await Exporter().ExportAsync(Request(), default);
            Assert.Equal(MakerStudioErrorCodes.StorageLimit, result.Error);
            Assert.Contains(folder, result.Message);
            Assert.Contains("100 files and 4 GiB per pack", result.Message);
            Assert.Equal(before, File.ReadAllText(Path.Combine(Pack, "manifest.json")));
            Assert.False(File.Exists(Path.Combine(Pack, ".maker/exports/pending.json")));
            Assert.Equal(bytes ? 1 : 100, Directory.GetFiles(folder).Length);
        }

        [Fact]
        public async Task A_full_other_pack_directory_does_not_block_export()
        {
            var other = Path.Combine(Exports, "lab.release-extra");
            Directory.CreateDirectory(other);
            for (var i = 0; i < 100; i++) File.WriteAllText(Path.Combine(other, i + ".zip"), "keep");
            var result = await Exporter().ExportAsync(Request(), default);
            Assert.True(result.Ok, result.Message);
            Assert.Equal(100, Directory.GetFiles(other).Length);
        }
        [Fact]
        public async Task Legacy_download_is_preserved_when_new_export_uses_the_same_version()
        {
            Directory.CreateDirectory(Exports);
            var name = "lab.release-v0.1.1.zip";
            var oldBytes = new byte[] { 80, 75, 3, 4, 42 };
            File.WriteAllBytes(Path.Combine(Exports, name), oldBytes);
            var result = await Exporter().ExportAsync(Request(), default);
            Assert.True(result.Ok, result.Message);
            Assert.NotEqual(name, result.Value!.FileName);
            var old = await Exporter().ReadExportAsync("lab.release", name, default);
            Assert.True(old.Ok, old.Message);
            Assert.Equal(oldBytes, old.Value!.Bytes);
            Assert.Equal(oldBytes, File.ReadAllBytes(Path.Combine(Exports, name)));
        }

        [Fact]
        public async Task Legacy_pending_intent_recovers_its_archive_in_place()
        {
            var exporter = Exporter();
            var boundary = typeof(MakerPackExporter).GetField("_exportBoundary", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            boundary.SetValue(exporter, (Action<string>)(p => { if (p == "after-intent") throw new IOException("interruption"); }));
            Assert.False((await exporter.ExportAsync(Request(), default)).Ok);
            var folder = Path.Combine(Exports, "lab.release");
            var stage = Assert.Single(Directory.GetFiles(folder, "*.tmp"));
            File.Move(stage, Path.Combine(Exports, Path.GetFileName(stage)));
            var intentPath = Path.Combine(Pack, ".maker/exports/pending.json");
            Assert.True(JsonReader.TryParse(File.ReadAllText(intentPath), out var parsed, out _));
            var intent = (JsonObject)parsed!;
            intent.Remove("archiveFolder");
            File.WriteAllText(intentPath, ApiJson.Write(intent));
            var result = await Exporter().ExportAsync(Request(), default);
            Assert.True(result.Ok, result.Message);
            Assert.True(File.Exists(Path.Combine(Exports, result.Value!.FileName)));
            Assert.Equal("0.1.1", result.Value.Version);
            Assert.Empty(Directory.GetFiles(folder));
            Assert.True((await Exporter().ReadExportAsync("lab.release", result.Value.FileName, default)).Ok);
        }

        [Fact]
        public async Task Linked_pack_export_folder_is_refused_without_touching_the_target()
        {
            var outside = Path.Combine(_root, "outside");
            Directory.CreateDirectory(outside);
            File.WriteAllText(Path.Combine(outside, "keep.txt"), "keep");
            Directory.CreateDirectory(Exports);
            var folder = Path.Combine(Exports, "lab.release");
            Directory.CreateSymbolicLink(folder, outside);
            var result = await Exporter().ExportAsync(Request(), default);
            Assert.Equal(MakerStudioErrorCodes.StorageLimit, result.Error);
            Assert.Contains(folder, result.Message);
            Assert.Contains("link", result.Message);
            Assert.Equal("keep", File.ReadAllText(Path.Combine(outside, "keep.txt")));
            Assert.Single(Directory.GetFiles(outside));
        }
    }
}
