using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CwyLs.ModLoader.Core.Api.Maker;
using CwyLs.ModLoader.Core.Api.Maker.Editor;
using CwyLs.ModLoader.Core.Api.Maker.Studio;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    public sealed class MakerZipLimitRegressionTests
    {
        [Fact]
        public void The_last_non_zip64_entry_is_supported_and_the_next_is_refused_without_writing()
        {
            using var output = new MemoryStream();
            using var zip = new MakerZipWriter(output, new DateTime(2026, 11, 11));
            for (var i = 0; i < ushort.MaxValue; i++) zip.Add("a/" + i, Array.Empty<byte>(), false);
            var before = output.Length;
            Assert.Throws<IOException>(() => zip.Add("a/overflow", Array.Empty<byte>(), false));
            Assert.Equal(before, output.Length);
            zip.Finish();
            using var reader = new System.IO.Compression.ZipArchive(output, System.IO.Compression.ZipArchiveMode.Read, leaveOpen: true);
            Assert.Equal(ushort.MaxValue, reader.Entries.Count);
        }

        [Fact]
        public void Utf8_name_width_is_checked_before_a_local_header_is_written()
        {
            using var output = new MemoryStream();
            using var zip = new MakerZipWriter(output, new DateTime(2026, 11, 11));
            zip.Add(new string('a', ushort.MaxValue), Array.Empty<byte>(), false);
            var before = output.Length;
            Assert.Throws<IOException>(() => zip.Add(new string('ก', 21846), Array.Empty<byte>(), false));
            Assert.Equal(before, output.Length);
        }

        [Fact]
        public void Preflight_refuses_zip_count_and_name_widths_before_opening_pack_content()
        {
            var nonexistent = new MakerPackFiles(Path.Combine(Path.GetTempPath(), "cwy-not-created-" + Guid.NewGuid().ToString("N")));
            var set = new MakerPackFileSet();
            for (var i = 0; i <= ushort.MaxValue; i++) set.Included.Add(new KeyValuePair<string, long>("tiny/" + i, 0));
            var checks = MakerPackChecks.Run(nonexistent, "lab.release", set, null, out _);
            Assert.Equal(MakerExportSeverity.Error, checks.Single(c => c.Id == "size").Severity);
            AssertContentNotChecked(checks);
            set = new MakerPackFileSet();
            set.Included.Add(new KeyValuePair<string, long>(new string('ก', 21846), 0));
            checks = MakerPackChecks.Run(nonexistent, "lab.release", set, null, out _);
            Assert.Equal(MakerExportSeverity.Error, checks.Single(c => c.Id == "size").Severity);
            AssertContentNotChecked(checks);
        }

        // HN-045 / milestone-1 P3 B2: AddStream deflated empty inputs (method 8, 0 compressed bytes); one empty file in a pack
        // made `unzip -t` report "invalid compressed data to inflate" and macOS `ditto -x -k` extract nothing.
        [Fact]
        public void An_empty_file_requested_as_deflated_is_stored_instead_so_the_entry_stays_a_valid_archive()
        {
            using var output = new MemoryStream();
            using var zip = new MakerZipWriter(output, new DateTime(2026, 11, 11));
            zip.Add("notes.txt", Array.Empty<byte>(), deflate: true);
            zip.Finish();

            // Local file header: signature(4) version(2) flags(2) method(2) time(2) date(2) crc(4) compressed(4) size(4) nameLen(2) extraLen(2).
            var header = output.ToArray();
            var method = BitConverter.ToUInt16(header, 8);
            var compressed = BitConverter.ToUInt32(header, 18);

            // .NET's DeflateStream writes 0 bytes for an empty input (verified), so an entry that claims
            // method 8 (deflate) with 0 compressed bytes is not a valid terminated deflate stream.
            Assert.Equal(0u, compressed);
            Assert.Equal((ushort)0, method); // was 8 (deflate) before the fix
        }

        // Milestone-1 P3 T5: on a missing folder the content checks fail either way, so assert they were never run.
        private static void AssertContentNotChecked(List<MakerExportCheck> checks)
        {
            foreach (var id in new[] { "manifest", "subtitles", "audio", "provenance" })
                Assert.Equal("Not checked until the size limits pass.", checks.Single(c => c.Id == id).Message);
        }

        [Fact]
        public void A_non_empty_file_requested_as_deflated_is_still_deflated_beside_a_stored_empty_one()
        {
            using var output = new MemoryStream();
            var text = System.Text.Encoding.UTF8.GetBytes(new string('a', 4096));
            using (var zip = new MakerZipWriter(output, new DateTime(2026, 11, 11)))
            {
                zip.Add("notes.txt", Array.Empty<byte>(), deflate: true);
                zip.Add("readme.txt", text, deflate: true);
                zip.Finish();
            }

            var bytes = output.ToArray();
            var second = 30 + "notes.txt".Length; // the empty entry is stored, so its data is 0 bytes long
            Assert.Equal(0x04034b50u, BitConverter.ToUInt32(bytes, second));
            Assert.Equal((ushort)8, BitConverter.ToUInt16(bytes, second + 8));
            using var reader = new System.IO.Compression.ZipArchive(new MemoryStream(bytes), System.IO.Compression.ZipArchiveMode.Read);
            Assert.Equal(0, reader.GetEntry("notes.txt")!.Length);
            using var stream = reader.GetEntry("readme.txt")!.Open();
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            Assert.Equal(text, copy.ToArray());
        }
    }
}
