using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CwyLs.ModLoader.Core.Api.Maker;
using CwyLs.ModLoader.Core.Api.Maker.Editor;
using CwyLs.ModLoader.Core.Api.Maker.Studio;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    public sealed class MakerExportRecoveryRegressionTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "cwy-export-recovery-" + Guid.NewGuid().ToString("N"));
        private string Pack => Path.Combine(_root, "lab.release");
        private string SharedExports => Path.Combine(_root, ".maker-exports");
        private const string Manifest = "{\"id\":\"lab.release\",\"name\":\"Release lab\",\"version\":\"0.1.0\",\"loaderVersion\":\"0.9.2\"}";
        public MakerExportRecoveryRegressionTests()
        {
            Directory.CreateDirectory(Pack);
            File.WriteAllText(Path.Combine(Pack, "manifest.json"), Manifest);
        }
        private string Exports => Path.Combine(SharedExports, "lab.release");

        public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
        private MakerPackExporter Exporter() => new MakerPackExporter(
            (id, edit) => MakerResult<MakerProjectStore>.Success(new MakerProjectStore(Pack)), _ => null, () => SharedExports,
            publish: (_, __) => { });
        private static MakerExportRequest Request(char op = 'a', string bump = "patch") => new MakerExportRequest
        { PackId = "lab.release", OperationId = new string(op, 32), ExpectedVersion = "0.1.0", Bump = bump, Notes = "Release notes." };

        [Fact]
        public async Task Failed_zip_install_retries_the_original_request_after_restart_without_another_bump()
        {
            var destination = Path.Combine(Exports, "lab.release-v0.1.1.zip");
            Directory.CreateDirectory(destination);
            var failed = await Exporter().ExportAsync(Request(), default);
            Assert.False(failed.Ok);
            Directory.Delete(destination);
            var retry = await Exporter().ExportAsync(Request(), default);
            Assert.True(retry.Ok, retry.Message);
            Assert.Equal("0.1.1", retry.Value!.Version);
            Assert.Equal("0.1.0", retry.Value.PreviousVersion);
            Assert.Equal(1, File.ReadAllText(Path.Combine(Pack, "CHANGELOG.md")).Split("## 0.1.1").Length - 1);
            Assert.True(File.Exists(Path.Combine(Exports, retry.Value.FileName)));
        }

        [Fact]
        public async Task Retained_receipts_download_their_original_bytes_after_another_same_version_export()
        {
            var exporter = Exporter();
            var first = await exporter.ExportAsync(Request(bump: "none"), default);
            Assert.True(first.Ok, first.Message);
            File.WriteAllText(Path.Combine(Pack, "extra.txt"), "A different release at the same version.");
            var second = await exporter.ExportAsync(Request('b', "none"), default);
            Assert.True(second.Ok, second.Message);
            var replay = await Exporter().ExportAsync(Request(bump: "none"), default);
            Assert.True(replay.Ok, replay.Message);
            var original = await exporter.ReadExportAsync("lab.release", replay.Value!.FileName, default);
            Assert.True(original.Ok, original.Message);
            Assert.Equal(first.Value!.Sha256, MakerVoiceProvenance.Sha256(original.Value!.Bytes!));
            Assert.NotEqual(first.Value.FileName, second.Value!.FileName);
        }

        [Theory]
        [InlineData(".maker/project.json", "{\"schema\":999}", MakerStudioErrorCodes.MetadataUnavailable)]
        [InlineData(".maker/transactions/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.pending.json", "{broken", MakerStudioErrorCodes.RecoveryRequired)]
        public async Task Loaded_project_checks_block_export_and_icon_changes(string path, string text, string error)
        {
            var files = new MakerPackFiles(Pack);
            files.WriteTextAtomic(path, text, false);
            var exporter = Exporter();
            Assert.Equal(error, (await exporter.CheckAsync("lab.release", default)).Error);
            Assert.Equal(error, (await exporter.ExportAsync(Request(), default)).Error);
            Assert.Equal(error, (await exporter.SaveIconAsync("lab.release", new byte[33], default)).Error);
            Assert.Equal(Manifest, File.ReadAllText(Path.Combine(Pack, "manifest.json")));
            Assert.False(Directory.Exists(Exports));
            Assert.False(File.Exists(Path.Combine(Pack, "icon.png")));
        }

        [Fact]
        public async Task Recoverable_subtitle_intent_settles_before_the_release_snapshot()
        {
            var files = new MakerPackFiles(Pack);
            files.WriteTextAtomic("data/subtitles/th.json", "{\"line\":\"before\"}", false);
            var before = files.ReadBytes("data/subtitles/th.json")!;
            var commit = new MakerCommit(files, kind: "subtitle", boundary: p => { if (p == "after-intent") throw new InvalidOperationException("interruption"); });
            Assert.Throws<InvalidOperationException>(() => commit.Execute(new string('c', 32), new[]
            {
                new MakerCommitFile { Path = "data/subtitles/th.json", BeforeFingerprint = MakerPackFiles.Fingerprint(before), Bytes = Encoding.UTF8.GetBytes("{\"line\":\"recovered\"}") },
            }));
            var result = await Exporter().ExportAsync(Request(), default);
            Assert.True(result.Ok, result.Message);
            Assert.Contains("recovered", files.ReadText("data/subtitles/th.json", out _));
            using var zip = System.IO.Compression.ZipFile.OpenRead(Path.Combine(Exports, result.Value!.FileName));
            using var reader = new StreamReader(zip.GetEntry("lab.release/data/subtitles/th.json")!.Open());
            Assert.Contains("recovered", reader.ReadToEnd());
        }

        [Fact]
        public async Task Oversized_metadata_is_refused_before_whole_file_allocations()
        {
            var path = Path.Combine(Pack, "data", "subtitles", "th.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (var sparse = File.Create(path)) sparse.SetLength(8 * 1024 * 1024);
            var allocated = GC.GetAllocatedBytesForCurrentThread();
            var check = await Exporter().CheckAsync("lab.release", default);
            allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
            Assert.True(check.Ok, check.Message);
            Assert.Equal(MakerExportSeverity.Error, check.Value!.Checks.Single(c => c.Id == "size").Severity);
            Assert.True(allocated < 2 * 1024 * 1024, "Check allocated " + allocated + " bytes for oversized metadata.");
        }

        // Milestone-1 P3 T4: the receipt and intent reads are bounded (MakerPackChecks.ReadMetadata) before any whole-file read.
        [Theory]
        [InlineData(".maker/exports/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.json", 8)] // Request('a')'s receipt; limit 4 MiB
        [InlineData(".maker/exports/pending.json", 40)] // the export intent; limit 32 MiB
        public async Task Oversized_export_receipts_and_intents_are_refused_before_whole_file_reads(string path, int mib)
        {
            var full = Path.Combine(Pack, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            using (var sparse = File.Create(full)) sparse.SetLength(mib * 1024L * 1024);
            var exporter = Exporter();
            var request = Request();
            var allocated = GC.GetAllocatedBytesForCurrentThread();
            var result = await exporter.ExportAsync(request, default);
            allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
            Assert.Equal(MakerErrorCodes.Unavailable, result.Error);
            Assert.Contains("exceeds the metadata size limit", result.Message);
            Assert.True(allocated < 2 * 1024 * 1024, "Export allocated " + allocated + " bytes for an oversized " + path);
            Assert.Equal(Manifest, File.ReadAllText(Path.Combine(Pack, "manifest.json")));
            Assert.False(Directory.Exists(Exports));
        }

        [Fact]
        public async Task Export_streams_file_compression_and_archive_hashing_with_bounded_allocations()
        {
            var bytes = new byte[8 * 1024 * 1024];
            new Random(41).NextBytes(bytes);
            File.WriteAllBytes(Path.Combine(Pack, "large.bin"), bytes);
            var allocated = GC.GetAllocatedBytesForCurrentThread();
            var exported = await Exporter().ExportAsync(Request(), default);
            allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
            Assert.True(exported.Ok, exported.Message);
            Assert.True(allocated < 2 * 1024 * 1024, "Export allocated " + allocated + " bytes for an 8 MiB entry.");
            using var zip = System.IO.Compression.ZipFile.OpenRead(Path.Combine(Exports, exported.Value!.FileName));
            using var stream = zip.GetEntry("lab.release/large.bin")!.Open();
            using var extracted = new MemoryStream();
            stream.CopyTo(extracted);
            Assert.Equal(bytes, extracted.ToArray());
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Export_storage_is_bounded_before_pack_mutation(bool byteLimit)
        {
            Directory.CreateDirectory(Exports);
            if (byteLimit)
            {
                using var sparse = File.Create(Path.Combine(Exports, "retained.zip"));
                sparse.SetLength(4L * 1024 * 1024 * 1024);
            }
            else for (var i = 0; i < 100; i++) File.WriteAllText(Path.Combine(Exports, "retained-" + i + ".zip"), "saved");
            var exported = await Exporter().ExportAsync(Request(), default);
            Assert.Equal(MakerStudioErrorCodes.StorageLimit, exported.Error);
            Assert.Equal(Manifest, File.ReadAllText(Path.Combine(Pack, "manifest.json")));
            Assert.Equal(byteLimit ? 1 : 100, Directory.GetFiles(Exports).Length);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Icon_backup_storage_is_bounded_without_displacing_the_current_icon(bool byteLimit)
        {
            var png = new byte[33];
            new byte[] { 137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 13, 73, 72, 68, 82 }.CopyTo(png, 0);
            png[19] = png[23] = 64;
            var files = new MakerPackFiles(Pack);
            files.WriteAtomic("icon.png", png);
            if (byteLimit)
            {
                files.WriteTextAtomic("_maker_backup/retained.png", "", false);
                using var sparse = File.OpenWrite(files.Resolve("_maker_backup/retained.png"));
                sparse.SetLength(32L * 1024 * 1024);
            }
            else for (var i = 0; i < 128; i++) files.WriteTextAtomic("_maker_backup/icon-" + i + ".png", "saved", false);
            var replacement = (byte[])png.Clone();
            replacement[32] = 1;
            var result = await Exporter().SaveIconAsync("lab.release", replacement, default);
            Assert.Equal(MakerStudioErrorCodes.StorageLimit, result.Error);
            Assert.Equal(png, files.ReadBytes("icon.png"));
            Assert.Equal(byteLimit ? 1 : 128, files.ListFiles("_maker_backup").Count);
        }

        [Fact]
        public async Task A_failed_receipt_write_keeps_the_installed_archive_replayable()
        {
            var receipt = Path.Combine(Pack, ".maker", "exports", new string('a', 32) + ".json");
            Directory.CreateDirectory(receipt);
            var failed = await Exporter().ExportAsync(Request(), default);
            Assert.False(failed.Ok);
            Assert.Single(Directory.GetFiles(Exports, "*.zip"));
            Directory.Delete(receipt);
            var replay = await Exporter().ExportAsync(Request(), default);
            Assert.True(replay.Ok, replay.Message);
            Assert.Equal("0.1.1", replay.Value!.Version);
            Assert.Single(Directory.GetFiles(Exports, "*.zip"));
        }

        [Fact]
        public async Task Failed_last_export_bookkeeping_still_returns_the_durable_success_receipt()
        {
            Directory.CreateDirectory(Path.Combine(Pack, ".maker", "exports", "last.json"));
            var first = await Exporter().ExportAsync(Request(), default);
            Assert.True(first.Ok, first.Message);
            var replay = await Exporter().ExportAsync(Request(), default);
            Assert.True(replay.Ok, replay.Message);
            Assert.Equal(first.Value!.Sha256, replay.Value!.Sha256);
        }

        [Fact]
        public async Task Oversized_inputs_do_not_reach_the_optional_catalog_parser()
        {
            var files = new MakerPackFiles(Pack);
            files.WriteTextAtomic("data/subtitles/th.json", "", false);
            using (var sparse = File.OpenWrite(files.Resolve("data/subtitles/th.json"))) sparse.SetLength(8 * 1024 * 1024);
            var called = false;
            var exporter = new MakerPackExporter((_, __) => MakerResult<MakerProjectStore>.Success(new MakerProjectStore(Pack)),
                _ => null, () => SharedExports, progress: (_, __, ___) =>
                {
                    called = true;
                    return Task.FromResult(MakerResult<MakerStudioGroups>.Fail("not_ready", "parser must not run"));
                });
            await exporter.CheckAsync("lab.release", default);
            Assert.False(called);
        }

        [Fact]
        public async Task Generated_release_metadata_must_fit_the_same_recoverable_size_limit()
        {
            File.WriteAllText(Path.Combine(Pack, "CHANGELOG.md"), new string('x', 4 * 1024 * 1024));
            var result = await Exporter().ExportAsync(Request(), default);
            Assert.Equal(MakerStudioErrorCodes.ExportBlocked, result.Error);
            Assert.Equal(Manifest, File.ReadAllText(Path.Combine(Pack, "manifest.json")));
            Assert.False(Directory.Exists(Exports));
            Assert.False(File.Exists(Path.Combine(Pack, ".maker", "exports", "pending.json")));
        }

        [Theory]
        [InlineData("after-intent")]
        [InlineData("after-changelog")]
        [InlineData("after-manifest")]
        [InlineData("after-zip")]
        [InlineData("before-receipt")]
        [InlineData("after-receipt")]
        public async Task Every_durable_export_boundary_is_replayable_after_a_fresh_exporter(string point)
        {
            var exporter = Exporter();
            var boundary = typeof(MakerPackExporter).GetField("_exportBoundary", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.NotNull(boundary);
            boundary.SetValue(exporter, (Action<string>)(p => { if (p == point) throw new IOException("injected interruption"); }));
            var first = await exporter.ExportAsync(Request(), default);
            if (point == "after-receipt") Assert.True(first.Ok, first.Message);
            else Assert.False(first.Ok);
            var replay = await Exporter().ExportAsync(Request(), default);
            Assert.True(replay.Ok, replay.Message);
            Assert.Equal(("0.1.0", "0.1.1"), (replay.Value!.PreviousVersion, replay.Value.Version));
            Assert.Single(Directory.GetFiles(Exports, "*.zip"));
            Assert.Empty(Directory.GetFiles(Exports, ".*"));
        }
    }
}
