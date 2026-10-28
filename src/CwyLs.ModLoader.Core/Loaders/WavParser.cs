using System;
using System.Globalization;

namespace CwyLs.ModLoader.Core.Loaders
{
    /// <summary>
    /// A small, strict RIFF/WAVE reader for 16-bit PCM. Pure C#, so the WAV path is covered by
    /// unit tests rather than only by a launch of the game.
    /// <para>
    /// Deliberately narrow: packs are our own files, 16-bit PCM is what every editor exports by
    /// default, and a decoder that silently handled six bit depths would be six times the code and
    /// six times the ways to be subtly wrong about loudness. Anything else is refused by name so
    /// the fix is obvious. Ogg is not decoded here at all - the engine does that (see
    /// <c>AudioLoader.LoadAsync</c>).
    /// </para>
    /// </summary>
    public static class WavParser
    {
        /// <summary>The only bit depth that can be decoded.</summary>
        public const int SupportedBitsPerSample = 16;

        private const int PcmFormatTag = 1;
        private const int IeeeFloatFormatTag = 3;
        private const int ExtensibleFormatTag = 0xFFFE;

        /// <summary>Largest channel count accepted; beyond this the file is almost certainly not a voice clip.</summary>
        private const int MaxChannels = 8;

        /// <summary>Decodes a WAV file.</summary>
        /// <param name="bytes">The whole file.</param>
        /// <returns>The decoded samples and their format.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="bytes"/> is null.</exception>
        /// <exception cref="WavFormatException">The file is not a 16-bit PCM WAV we can read.</exception>
        public static WavData Parse(byte[] bytes)
        {
            if (bytes is null)
            {
                throw new ArgumentNullException(nameof(bytes));
            }

            if (bytes.Length < 12)
            {
                throw new WavFormatException(
                    "file is only " + bytes.Length + " bytes; too short to be a WAV");
            }

            if (!Ascii(bytes, 0, "RIFF"))
            {
                throw new WavFormatException("not a RIFF container");
            }

            if (!Ascii(bytes, 8, "WAVE"))
            {
                throw new WavFormatException("RIFF container is not WAVE");
            }

            var formatTag = -1;
            var channels = 0;
            var sampleRate = 0;
            var bitsPerSample = 0;
            var dataOffset = -1;
            var dataLength = 0;

            // Chunk walk. Every chunk is (4-byte id)(4-byte size)(size bytes)(pad to even).
            var position = 12;
            while (position + 8 <= bytes.Length)
            {
                var id = AsciiAt(bytes, position);
                var declared = ReadUInt32(bytes, position + 4);
                var body = position + 8;
                var remaining = (uint)(bytes.Length - body);

                if (declared > remaining)
                {
                    throw new WavFormatException(
                        "chunk '" + id + "' claims " + declared.ToString(CultureInfo.InvariantCulture) +
                        " bytes but only " + remaining.ToString(CultureInfo.InvariantCulture) +
                        " remain; the file is truncated");
                }

                var size = (int)declared;

                if (string.Equals(id, "fmt ", StringComparison.Ordinal))
                {
                    if (size < 16)
                    {
                        throw new WavFormatException("'fmt ' chunk is " + size + " bytes; 16 are required");
                    }

                    formatTag = ReadUInt16(bytes, body);
                    channels = ReadUInt16(bytes, body + 2);
                    var declaredRate = ReadUInt32(bytes, body + 4);
                    sampleRate = declaredRate > int.MaxValue ? 0 : (int)declaredRate;
                    bitsPerSample = ReadUInt16(bytes, body + 14);
                }
                else if (string.Equals(id, "data", StringComparison.Ordinal))
                {
                    dataOffset = body;
                    dataLength = size;
                }

                position = body + size + (size & 1);
            }

            if (formatTag < 0)
            {
                throw new WavFormatException("no 'fmt ' chunk");
            }

            if (dataOffset < 0)
            {
                throw new WavFormatException("no 'data' chunk");
            }

            // Bit depth first: it is the field a person can act on, and it is the one that is wrong
            // in practice (a 24-bit or 32-bit float export). The format tag check below then catches
            // the rarer container problems.
            if (bitsPerSample != SupportedBitsPerSample)
            {
                throw new WavFormatException(
                    bitsPerSample + "-bit WAV is not supported; save as " + SupportedBitsPerSample +
                    "-bit PCM" + (formatTag == IeeeFloatFormatTag ? " (this file is IEEE float)" : string.Empty));
            }

            if (formatTag != PcmFormatTag)
            {
                throw new WavFormatException(
                    formatTag == ExtensibleFormatTag
                        ? "WAVE_FORMAT_EXTENSIBLE (65534) is not supported; save as plain 16-bit PCM"
                        : "WAV format tag " + formatTag + " is not supported; save as 16-bit PCM (tag 1)");
            }

            if (channels < 1 || channels > MaxChannels)
            {
                throw new WavFormatException(
                    "channel count " + channels + " is out of range (1.." + MaxChannels + ")");
            }

            if (sampleRate <= 0)
            {
                throw new WavFormatException("sample rate " + sampleRate + " is not valid");
            }

            var bytesPerFrame = channels * (SupportedBitsPerSample / 8);
            var frames = dataLength / bytesPerFrame;
            if (frames == 0)
            {
                throw new WavFormatException(
                    "'data' chunk holds " + dataLength + " bytes, which is less than one frame");
            }

            // A trailing partial frame is dropped rather than refused: some encoders round the data
            // size up, and losing 1/44100 s is not worth failing a voice line over.
            var samples = new float[frames * channels];
            for (var i = 0; i < samples.Length; i++)
            {
                var offset = dataOffset + (i * 2);
                var raw = unchecked((short)(bytes[offset] | (bytes[offset + 1] << 8)));
                samples[i] = raw / 32768f;
            }

            return new WavData(channels, sampleRate, bitsPerSample, samples);
        }

        /// <summary>
        /// Decodes a WAV file without throwing - the shape a per-file loader wants.
        /// </summary>
        /// <param name="bytes">The whole file, or null.</param>
        /// <param name="data">The decoded data, or null on failure.</param>
        /// <param name="error">The reason on failure, or null on success.</param>
        /// <returns>True when <paramref name="data"/> was produced.</returns>
        public static bool TryParse(byte[]? bytes, out WavData? data, out string? error)
        {
            if (bytes is null)
            {
                data = null;
                error = "no bytes";
                return false;
            }

            try
            {
                data = Parse(bytes);
                error = null;
                return true;
            }
            catch (WavFormatException ex)
            {
                // Only the format exception is caught: an IndexOutOfRange here would be a bug in
                // this parser, and hiding it behind a "bad file" message would bury it.
                data = null;
                error = ex.Message;
                return false;
            }
        }

        private static uint ReadUInt32(byte[] bytes, int offset) =>
            (uint)(bytes[offset] | (bytes[offset + 1] << 8) | (bytes[offset + 2] << 16) | (bytes[offset + 3] << 24));

        private static int ReadUInt16(byte[] bytes, int offset) =>
            bytes[offset] | (bytes[offset + 1] << 8);

        private static string AsciiAt(byte[] bytes, int offset) =>
            new string(new[]
            {
                (char)bytes[offset], (char)bytes[offset + 1], (char)bytes[offset + 2], (char)bytes[offset + 3],
            });

        private static bool Ascii(byte[] bytes, int offset, string expected)
        {
            for (var i = 0; i < expected.Length; i++)
            {
                if (bytes[offset + i] != (byte)expected[i])
                {
                    return false;
                }
            }

            return true;
        }
    }
}
