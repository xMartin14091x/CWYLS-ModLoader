using System;
using System.IO;
using System.IO.Compression;
using System.Threading;

namespace CwyLs.ModLoader.Core.Trace
{
    /// <summary>CRC-32 (IEEE 802.3, the one gzip uses), table-driven. Used to compare contents, not for security.</summary>
    public static class Crc32
    {
        /// <summary>The running state before any byte has been added.</summary>
        public const uint Initial = 0xFFFFFFFFu;

        private static readonly uint[] Table = BuildTable();

        /// <summary>Adds bytes to a running state.</summary>
        /// <param name="state">The state so far; start from <see cref="Initial"/>.</param>
        /// <param name="buffer">The bytes.</param>
        /// <param name="offset">First byte to add.</param>
        /// <param name="count">How many bytes to add.</param>
        /// <returns>The new state.</returns>
        public static uint Update(uint state, byte[] buffer, int offset, int count)
        {
            for (var i = offset; i < offset + count; i++)
            {
                state = Table[(state ^ buffer[i]) & 0xFF] ^ (state >> 8);
            }

            return state;
        }

        /// <summary>Turns a running state into the published checksum value.</summary>
        /// <param name="state">The state after the last byte.</param>
        public static uint Finish(uint state) => state ^ 0xFFFFFFFFu;

        /// <summary>The checksum of a whole buffer.</summary>
        /// <param name="data">The bytes.</param>
        public static uint Compute(byte[] data) => Finish(Update(Initial, data, 0, data.Length));

        private static uint[] BuildTable()
        {
            var table = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                var c = n;
                for (var k = 0; k < 8; k++)
                {
                    c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                }

                table[n] = c;
            }

            return table;
        }
    }

    /// <summary>Sizes of one archived part.</summary>
    /// <param name="OriginalBytes">The plain part's size.</param>
    /// <param name="ArchiveBytes">The archive's size.</param>
    /// <param name="Note">A non-fatal problem worth recording (e.g. the timestamp could not be kept), or null.</param>
    public sealed record TraceArchiveResult(long OriginalBytes, long ArchiveBytes, string? Note);

    /// <summary>
    /// Archives one trace part with gzip, archive-first: the original is deleted only after the
    /// archive has been flushed to disk, decompressed again, and matched on length and CRC-32.
    /// </summary>
    /// <remarks>
    /// Crash safety, step by step: the archive is written as <c>.gz.tmp</c> (a crash leaves the
    /// original plus a temp that the next run clears); the temp is renamed only once verified (a
    /// crash after that leaves the original plus a good archive, and the next run redoes the part);
    /// the original is removed last, and only if its size and timestamp are what was compressed.
    /// </remarks>
    public static class TraceArchive
    {
        /// <summary>Read/write buffer size.</summary>
        public const int BufferSize = 81920;

        /// <summary>
        /// Compresses <paramref name="sourcePath"/> to <paramref name="archivePath"/>, verifies the
        /// archive, then deletes the source.
        /// </summary>
        /// <param name="sourcePath">The plain part.</param>
        /// <param name="archivePath">Where the archive goes; an existing file there is replaced once the new one verifies.</param>
        /// <param name="cancel">Checked between buffers; a cancelled run removes its temp and keeps the original.</param>
        /// <returns>The sizes, and a note for anything non-fatal.</returns>
        /// <exception cref="OperationCanceledException"><paramref name="cancel"/> fired; the original is intact.</exception>
        /// <exception cref="InvalidDataException">The archive did not verify; the original is intact.</exception>
        /// <exception cref="IOException">Reading, writing, or the final checks failed; the original is intact.</exception>
        public static TraceArchiveResult CompressAndReplace(string sourcePath, string archivePath, CancellationToken cancel)
        {
            var tempPath = archivePath + TraceFileName.TempExtension;
            var source = new FileInfo(sourcePath);
            var originalBytes = source.Length;
            var modifiedUtc = source.LastWriteTimeUtc;

            try
            {
                var (read, crc) = WriteCompressed(sourcePath, tempPath, cancel);
                if (read != originalBytes)
                {
                    throw new IOException(
                        $"{source.Name} was {originalBytes} bytes but {read} were read; it is still being written");
                }

                Verify(tempPath, read, crc, cancel);

                if (File.Exists(archivePath))
                {
                    File.Delete(archivePath);
                }

                File.Move(tempPath, archivePath);
            }
            catch (Exception)
            {
                // Not swallowed: the caller records the failure. Only the half-written temp goes.
                DeleteQuietly(tempPath);
                throw;
            }

            string? note = null;
            try
            {
                // The archive keeps the session's own time, so a listing sorted by date still reads right.
                File.SetLastWriteTimeUtc(archivePath, modifiedUtc);
            }
            catch (Exception ex)
            {
                note = "archive timestamp not kept: " + ex.GetType().Name + ": " + ex.Message;
            }

            var now = new FileInfo(sourcePath);
            if (now.Length != originalBytes || now.LastWriteTimeUtc != modifiedUtc)
            {
                throw new IOException(
                    $"{source.Name} changed after it was compressed; the original is kept next to the archive");
            }

            File.Delete(sourcePath);
            return new TraceArchiveResult(originalBytes, new FileInfo(archivePath).Length, note);
        }

        /// <summary>Decompresses an archive to the end and checks its length and content checksum.</summary>
        /// <param name="archivePath">The gzip file.</param>
        /// <param name="expectedBytes">The original's length.</param>
        /// <param name="expectedCrcState">The original's running CRC-32 state (not <see cref="Crc32.Finish"/>ed).</param>
        /// <param name="cancel">Checked between buffers.</param>
        /// <exception cref="InvalidDataException">Length or checksum differ, or the gzip stream is corrupt.</exception>
        /// <exception cref="OperationCanceledException"><paramref name="cancel"/> fired.</exception>
        public static void Verify(string archivePath, long expectedBytes, uint expectedCrcState, CancellationToken cancel)
        {
            var buffer = new byte[BufferSize];
            long total = 0;
            var crc = Crc32.Initial;

            using (var input = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize))
            using (var gzip = new GZipStream(input, CompressionMode.Decompress))
            {
                int n;
                while ((n = gzip.Read(buffer, 0, buffer.Length)) > 0)
                {
                    cancel.ThrowIfCancellationRequested();
                    crc = Crc32.Update(crc, buffer, 0, n);
                    total += n;
                }
            }

            if (total != expectedBytes)
            {
                throw new InvalidDataException(
                    $"{Path.GetFileName(archivePath)} decompressed to {total} bytes, expected {expectedBytes}");
            }

            if (crc != expectedCrcState)
            {
                throw new InvalidDataException(
                    $"{Path.GetFileName(archivePath)} decompressed to different content (CRC-32 mismatch)");
            }
        }

        /// <summary>Streams the source through gzip into the temp file and forces it to disk.</summary>
        /// <returns>Bytes read from the source and their running CRC-32 state.</returns>
        private static (long Read, uint Crc) WriteCompressed(string sourcePath, string tempPath, CancellationToken cancel)
        {
            var buffer = new byte[BufferSize];
            long total = 0;
            var crc = Crc32.Initial;

            using (var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, BufferSize))
            using (var output = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize))
            {
                using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
                {
                    int n;
                    while ((n = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        cancel.ThrowIfCancellationRequested();
                        crc = Crc32.Update(crc, buffer, 0, n);
                        gzip.Write(buffer, 0, n);
                        total += n;
                    }
                }

                // Durable before anything is deleted: the original goes only after this returns.
                output.Flush(flushToDisk: true);
            }

            return (total, crc);
        }

        private static void DeleteQuietly(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception ex)
            {
                // The temp is our own incomplete copy; the next run clears it. Reported, not fatal.
                TraceDiagnostics.Report("TRACE: could not remove the unfinished archive " + path, ex);
            }
        }
    }
}
