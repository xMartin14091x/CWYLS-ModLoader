using System;
using System.Collections.Generic;
using System.IO;

namespace CwyLs.ModLoader.Core.Api.Maker.Catalog
{
    /// <summary>
    /// The length of a pack's voice file, read from its headers without decoding any audio: a WAV's
    /// <c>fmt </c> and <c>data</c> chunks, an Ogg Vorbis file's identification header and the granule
    /// position of its last page. Used for <see cref="MakerLine.DubDurationSec"/>.
    /// </summary>
    /// <remarks>
    /// Only the two formats the voice feature loads (<c>.wav</c>, <c>.ogg</c> Vorbis). Anything else,
    /// and any header that does not add up (for a WAV also a second <c>fmt </c> or <c>data</c> chunk), is
    /// "unknown" (false) rather than a guess. No checksum is
    /// verified: this answers "how long", not "is it valid", which the loader decides when it loads.
    /// </remarks>
    public static class MakerAudioLength
    {
        /// <summary>The first tail read when looking for the last Ogg page.</summary>
        public const int ShortTail = 8 * 1024;

        /// <summary>The longest an Ogg page can be (27 + 255 + 255 x 255), plus slack.</summary>
        public const int LongTail = 66 * 1024;

        private const int MaxWavChunks = 64;

        /// <summary>Reads a file's length by its extension (<c>.wav</c> or <c>.ogg</c>).</summary>
        /// <param name="path">An absolute file path.</param>
        /// <param name="seconds">The length in seconds, rounded to the millisecond.</param>
        /// <returns>False when the file is missing, unreadable, not one of the two formats, or its header does not add up.</returns>
        public static bool TryRead(string? path, out double seconds)
        {
            seconds = 0d;
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            try
            {
                var extension = Path.GetExtension(path!);
                using (var stream = new FileStream(path!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    if (string.Equals(extension, ".wav", StringComparison.OrdinalIgnoreCase))
                    {
                        return TryWav(stream, out seconds);
                    }

                    if (string.Equals(extension, ".ogg", StringComparison.OrdinalIgnoreCase))
                    {
                        return TryOggVorbis(stream, out seconds);
                    }

                    return false;
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException)
            {
                seconds = 0d;
                return false;
            }
        }

        /// <summary>The length of a RIFF/WAVE stream: <c>data</c> bytes over (sample rate x block align).</summary>
        /// <param name="stream">A readable, seekable stream positioned anywhere.</param>
        /// <param name="seconds">The length in seconds, rounded to the millisecond.</param>
        /// <returns>False when the headers are missing or do not add up.</returns>
        public static bool TryWav(Stream stream, out double seconds)
        {
            seconds = 0d;
            if (stream is null || !stream.CanRead || !stream.CanSeek)
            {
                return false;
            }

            stream.Position = 0;
            var riff = new byte[12];
            if (!ReadExactly(stream, riff, riff.Length) || !Is(riff, 0, "RIFF") || !Is(riff, 8, "WAVE"))
            {
                return false;
            }

            long sampleRate = 0;
            long blockAlign = 0;
            long dataStart = -1;
            long dataSize = -1;
            var chunk = new byte[8];

            for (var i = 0; i < MaxWavChunks && ReadExactly(stream, chunk, chunk.Length); i++)
            {
                var size = (long)ReadUInt32(chunk, 4);
                var bodyStart = stream.Position;

                var isFormat = Is(chunk, 0, "fmt ");
                var isData = Is(chunk, 0, "data");

                // DS-10a: two 'fmt ' or two 'data' chunks can be read two ways (the upload check refuses
                // them for that reason), so such a file has no length here rather than a guessed one.
                if ((isFormat && sampleRate != 0) || (isData && dataStart >= 0))
                {
                    return false;
                }

                if (isFormat)
                {
                    var fmt = new byte[16];
                    if (size < fmt.Length || !ReadExactly(stream, fmt, fmt.Length))
                    {
                        return false;
                    }

                    var channels = ReadUInt16(fmt, 2);
                    sampleRate = ReadUInt32(fmt, 4);
                    if (sampleRate == 0)
                    {
                        return false;
                    }

                    blockAlign = ReadUInt16(fmt, 12);
                    if (blockAlign == 0)
                    {
                        blockAlign = channels * ((ReadUInt16(fmt, 14) + 7) / 8);
                    }
                }
                else if (isData)
                {
                    // Not the end of the walk: a later duplicate must still be seen (seeking past the
                    // samples reads none of them).
                    dataStart = bodyStart;
                    dataSize = size;
                }

                // Chunks are padded to an even size.
                var next = bodyStart + size + (size & 1L);
                if (next > stream.Length)
                {
                    break;
                }

                stream.Position = next;
            }

            if (sampleRate <= 0 || blockAlign <= 0 || dataStart < 0)
            {
                return false;
            }

            // A streamed or truncated file can claim more than it holds (0xFFFFFFFF is common):
            // count what is really there.
            var available = Math.Max(0L, stream.Length - dataStart);
            if (dataSize > available)
            {
                dataSize = available;
            }

            var length = dataSize / (double)(sampleRate * blockAlign);
            if (length <= 0d)
            {
                return false;
            }

            seconds = Math.Round(length, 3);
            return true;
        }

        /// <summary>
        /// The length of an Ogg Vorbis stream: the granule position (samples) of its last page over
        /// the sample rate in its identification header.
        /// </summary>
        /// <param name="stream">A readable, seekable stream positioned anywhere.</param>
        /// <param name="seconds">The length in seconds, rounded to the millisecond.</param>
        /// <returns>False for another codec (Opus, FLAC) or a header that does not add up.</returns>
        public static bool TryOggVorbis(Stream stream, out double seconds)
        {
            seconds = 0d;
            if (stream is null || !stream.CanRead || !stream.CanSeek)
            {
                return false;
            }

            stream.Position = 0;
            var head = new byte[Math.Min(4096L, stream.Length)];
            var headRead = Read(stream, head, head.Length);
            if (headRead < 28 || !Is(head, 0, "OggS"))
            {
                return false;
            }

            var segments = head[26];
            var packet = 27 + segments;

            // Identification header: 0x01 "vorbis" version(4) channels(1) rate(4).
            if (packet + 16 > headRead || head[packet] != 0x01 || !Is(head, packet + 1, "vorbis"))
            {
                return false;
            }

            var serial = ReadUInt32(head, 14);
            var rate = ReadUInt32(head, packet + 12);
            if (rate == 0)
            {
                return false;
            }

            if (!TryLastGranule(stream, serial, ShortTail, out var granule)
                && !TryLastGranule(stream, serial, LongTail, out granule))
            {
                return false;
            }

            var length = granule / (double)rate;
            if (length <= 0d)
            {
                return false;
            }

            seconds = Math.Round(length, 3);
            return true;
        }

        private static bool TryLastGranule(Stream stream, uint serial, int tailSize, out long granule)
        {
            granule = 0;
            var size = (int)Math.Min(stream.Length, tailSize);
            var tail = new byte[size];
            stream.Position = stream.Length - size;
            var read = Read(stream, tail, size);

            for (var i = read - 27; i >= 0; i--)
            {
                if (tail[i] != (byte)'O' || !Is(tail, i, "OggS") || tail[i + 4] != 0 || ReadUInt32(tail, i + 14) != serial)
                {
                    continue;
                }

                var value = ReadInt64(tail, i + 6);

                // -1: no packet ends on this page; keep looking further back.
                if (value > 0)
                {
                    granule = value;
                    return true;
                }
            }

            return false;
        }

        private static bool ReadExactly(Stream stream, byte[] buffer, int count) => Read(stream, buffer, count) == count;

        private static int Read(Stream stream, byte[] buffer, int count)
        {
            var total = 0;
            while (total < count)
            {
                var n = stream.Read(buffer, total, count - total);
                if (n <= 0)
                {
                    break;
                }

                total += n;
            }

            return total;
        }

        private static bool Is(byte[] buffer, int offset, string ascii)
        {
            if (offset < 0 || offset + ascii.Length > buffer.Length)
            {
                return false;
            }

            for (var i = 0; i < ascii.Length; i++)
            {
                if (buffer[offset + i] != (byte)ascii[i])
                {
                    return false;
                }
            }

            return true;
        }

        private static ushort ReadUInt16(byte[] b, int o) => (ushort)(b[o] | (b[o + 1] << 8));

        private static uint ReadUInt32(byte[] b, int o) =>
            (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));

        private static long ReadInt64(byte[] b, int o) =>
            (long)((ulong)ReadUInt32(b, o) | ((ulong)ReadUInt32(b, o + 4) << 32));
    }

    /// <summary>
    /// Remembers <see cref="MakerAudioLength"/> answers per file, keyed by its size and last write
    /// time, so a page of lines reads each dub's headers once until the file changes. Safe from
    /// any thread.
    /// </summary>
    public sealed class MakerDubLengths
    {
        /// <summary>The most files remembered; the cache starts over beyond this.</summary>
        public const int MaxEntries = 20000;

        private readonly object _gate = new object();
        private readonly Dictionary<string, Entry> _byPath = new Dictionary<string, Entry>(StringComparer.Ordinal);

        /// <summary>How many files are remembered.</summary>
        public int Count
        {
            get { lock (_gate) { return _byPath.Count; } }
        }

        /// <summary>The length of a dub file in seconds, or null when it is missing or its header is not readable.</summary>
        /// <param name="fullPath">An absolute file path.</param>
        public double? LengthOf(string? fullPath)
        {
            if (string.IsNullOrWhiteSpace(fullPath))
            {
                return null;
            }

            long size;
            long stamp;
            try
            {
                var info = new FileInfo(fullPath!);
                if (!info.Exists)
                {
                    return null;
                }

                size = info.Length;
                stamp = info.LastWriteTimeUtc.Ticks;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException)
            {
                return null;
            }

            lock (_gate)
            {
                if (_byPath.TryGetValue(fullPath!, out var known) && known.Size == size && known.Stamp == stamp)
                {
                    return known.Seconds;
                }
            }

            double? seconds = MakerAudioLength.TryRead(fullPath, out var read) ? read : (double?)null;

            lock (_gate)
            {
                if (_byPath.Count >= MaxEntries)
                {
                    _byPath.Clear();
                }

                _byPath[fullPath!] = new Entry(size, stamp, seconds);
            }

            return seconds;
        }

        private readonly struct Entry
        {
            internal Entry(long size, long stamp, double? seconds)
            {
                Size = size;
                Stamp = stamp;
                Seconds = seconds;
            }

            internal long Size { get; }

            internal long Stamp { get; }

            internal double? Seconds { get; }
        }
    }
}
