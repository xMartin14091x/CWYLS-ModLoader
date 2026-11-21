using System;
using System.Globalization;

namespace CwyLs.ModLoader.Core.Api.Maker.Editor
{
    /// <summary>What <see cref="MakerWav.Inspect"/> found in an uploaded dub.</summary>
    public sealed class MakerWavReport
    {
        private MakerWavReport(
            string? error, string message, int channels, int sampleRate, int frames, int dataOffset, string format)
        {
            Error = error;
            Message = message;
            Channels = channels;
            SampleRate = sampleRate;
            Frames = frames;
            DataOffset = dataOffset;
            Format = format;
        }

        /// <summary>True when the file is a dub Pack Maker accepts.</summary>
        public bool Ok => Error == null;

        /// <summary>A <see cref="MakerErrorCodes"/> value, or null when accepted.</summary>
        public string? Error { get; }

        /// <summary>One sentence for the creator: what the file is and, when refused, what to export instead.</summary>
        public string Message { get; }

        /// <summary>1 or 2.</summary>
        public int Channels { get; }

        /// <summary>Frames per second.</summary>
        public int SampleRate { get; }

        /// <summary>Sample frames (per channel).</summary>
        public int Frames { get; }

        /// <summary>Where the samples start in the uploaded bytes.</summary>
        public int DataOffset { get; }

        /// <summary>The container format as found, e.g. <c>PCM</c> or <c>WAVE_FORMAT_EXTENSIBLE (PCM)</c>.</summary>
        public string Format { get; }

        /// <summary>Length in seconds.</summary>
        public double DurationSec => SampleRate > 0 ? (double)Frames / SampleRate : 0d;

        internal static MakerWavReport Refuse(string error, string message) =>
            new MakerWavReport(error, message, 0, 0, 0, -1, string.Empty);

        internal static MakerWavReport Accept(int channels, int sampleRate, int frames, int dataOffset, string format) =>
            new MakerWavReport(
                null,
                string.Format(
                    CultureInfo.InvariantCulture,
                    "16-bit {0}, {1} Hz, {2}, {3:0.00} s",
                    format,
                    sampleRate,
                    channels == 1 ? "mono" : "stereo",
                    sampleRate > 0 ? (double)frames / sampleRate : 0d),
                channels,
                sampleRate,
                frames,
                dataOffset,
                format);
    }

    /// <summary>
    /// Checks an uploaded dub before anything is written: a RIFF/WAVE file of 16-bit PCM, mono or
    /// stereo, 8-48 kHz, at most 60 s and 16 MiB (CONTRACT.md "Request rules" and "Routes"); and
    /// rewrites an accepted one as a canonical 44-byte-header PCM file.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every refusal names what the file actually is - "24-bit PCM", "32-bit float",
    /// "WAVE_FORMAT_EXTENSIBLE, 24-bit", "an Ogg Vorbis file" - so the fix in the creator's audio
    /// editor is obvious. A WAVE_FORMAT_EXTENSIBLE file whose sub-format is 16-bit PCM is the same
    /// samples under a longer header (several editors write it), so it is accepted and normalised.
    /// </para>
    /// <para>
    /// <b>Strict header (DS-10a).</b> Exactly one <c>fmt </c> and one <c>data</c> chunk, every chunk
    /// inside the size the RIFF header declares, byte rate = sample rate x block align, block align =
    /// channels x 2, and a <c>data</c> chunk of whole sample frames. Anything else is refused as
    /// <c>unsupported_media_type</c>: a file that two readers could read two ways never reaches the
    /// disk, and the length checked here is the length the game plays.
    /// </para>
    /// <para>
    /// The canonical rewrite (<see cref="Canonical"/>) keeps the samples byte for byte and drops
    /// every other chunk (<c>LIST</c>, <c>bext</c>, cue points), so both the loader's own WAV parser
    /// and the engine's decoder read the saved file the same way.
    /// </para>
    /// </remarks>
    public static class MakerWav
    {
        private const int PcmTag = 1;
        private const int FloatTag = 3;
        private const int ExtensibleTag = 0xFFFE;

        /// <summary>The tail every standard WAVE sub-format GUID shares after its two-byte tag.</summary>
        private static readonly byte[] GuidTail =
            { 0x00, 0x00, 0x00, 0x00, 0x10, 0x00, 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71 };

        /// <summary>Validates an uploaded dub. Never throws for bad input.</summary>
        /// <param name="bytes">The request body.</param>
        public static MakerWavReport Inspect(byte[]? bytes)
        {
            if (bytes is null || bytes.Length == 0)
            {
                return MakerWavReport.Refuse(MakerErrorCodes.BadRequest, "the request carried no audio");
            }

            if (bytes.Length > MakerEditRules.MaxVoiceBytes)
            {
                return MakerWavReport.Refuse(
                    MakerErrorCodes.TooLarge,
                    "the file is " + Megabytes(bytes.Length) + "; the limit is " + Megabytes(MakerEditRules.MaxVoiceBytes));
            }

            if (bytes.Length < 12 || !Ascii(bytes, 0, "RIFF") || !Ascii(bytes, 8, "WAVE"))
            {
                return MakerWavReport.Refuse(
                    MakerErrorCodes.UnsupportedMedia,
                    "this is " + Sniff(bytes) + ", not a WAV; export the dub as a 16-bit PCM WAV");
            }

            var formatTag = -1;
            var subFormat = -1;
            var channels = 0;
            var sampleRate = 0;
            uint byteRate = 0;
            var blockAlign = 0;
            var bits = 0;
            var validBits = 0;
            var dataOffset = -1;
            var dataLength = 0;

            // DS-10a: the RIFF header's own size bounds every chunk, so a chunk appended after it (a
            // second, shorter 'data' or a faster 'fmt ') cannot hide inside a file the header ends earlier.
            var riffEnd = 8L + UInt32(bytes, 4);

            var position = 12;
            while (position + 8 <= bytes.Length)
            {
                var declared = UInt32(bytes, position + 4);
                var body = position + 8;
                var remaining = (uint)(bytes.Length - body);
                var isData = Ascii(bytes, position, "data");
                var isFormat = Ascii(bytes, position, "fmt ");

                if (declared > remaining)
                {
                    return MakerWavReport.Refuse(
                        MakerErrorCodes.UnsupportedMedia,
                        "the file is cut short: its '" + Id(bytes, position) + "' chunk claims "
                        + declared.ToString(CultureInfo.InvariantCulture) + " bytes but only "
                        + remaining.ToString(CultureInfo.InvariantCulture) + " follow; export it again");
                }

                if (body + (long)declared > riffEnd)
                {
                    return MakerWavReport.Refuse(
                        MakerErrorCodes.UnsupportedMedia,
                        "its header does not add up (the RIFF size ends the file before its '" + Id(bytes, position)
                        + "' chunk does); export it again");
                }

                // DS-10a: one format and one data chunk. With two, which one counts depends on the reader
                // (this check, the catalogue, the engine), so the length checked here is not the one played.
                if ((isFormat && formatTag >= 0) || (isData && dataOffset >= 0))
                {
                    return MakerWavReport.Refuse(
                        MakerErrorCodes.UnsupportedMedia,
                        "the WAV has two '" + Id(bytes, position) + "' chunks; export it again");
                }

                var size = (int)declared;
                if (isFormat)
                {
                    if (size < 16)
                    {
                        return MakerWavReport.Refuse(MakerErrorCodes.UnsupportedMedia, "its format chunk is damaged; export it again");
                    }

                    formatTag = UInt16(bytes, body);
                    channels = UInt16(bytes, body + 2);
                    var rate = UInt32(bytes, body + 4);
                    sampleRate = rate > int.MaxValue ? 0 : (int)rate;
                    byteRate = UInt32(bytes, body + 8);
                    blockAlign = UInt16(bytes, body + 12);
                    bits = UInt16(bytes, body + 14);
                    validBits = bits;

                    if (formatTag == ExtensibleTag && size >= 40)
                    {
                        validBits = UInt16(bytes, body + 18);
                        subFormat = IsStandardGuid(bytes, body + 24) ? UInt16(bytes, body + 24) : -2;
                    }
                }
                else if (isData)
                {
                    dataOffset = body;
                    dataLength = size;
                }

                position = body + size + (size & 1);
            }

            if (formatTag < 0)
            {
                return MakerWavReport.Refuse(MakerErrorCodes.UnsupportedMedia, "the WAV has no format chunk; export it again");
            }

            var described = Describe(formatTag, subFormat, bits, validBits);
            var pcm16 = bits == 16 && validBits == 16
                        && (formatTag == PcmTag || (formatTag == ExtensibleTag && subFormat == PcmTag));
            if (!pcm16)
            {
                return MakerWavReport.Refuse(
                    MakerErrorCodes.UnsupportedMedia,
                    "this WAV is " + described + "; export it as 16-bit PCM (not 24-bit, not 32-bit float)");
            }

            if (channels < 1 || channels > 2)
            {
                return MakerWavReport.Refuse(
                    MakerErrorCodes.UnsupportedMedia,
                    "this WAV has " + channels.ToString(CultureInfo.InvariantCulture) + " channels; export it as mono or stereo");
            }

            if (sampleRate < MakerEditRules.MinSampleRate || sampleRate > MakerEditRules.MaxSampleRate)
            {
                return MakerWavReport.Refuse(
                    MakerErrorCodes.UnsupportedMedia,
                    "this WAV is " + sampleRate.ToString(CultureInfo.InvariantCulture)
                    + " Hz; export it at 8000-48000 Hz (44100 or 48000 are usual)");
            }

            if (blockAlign != channels * 2)
            {
                return MakerWavReport.Refuse(
                    MakerErrorCodes.UnsupportedMedia, "its header does not add up (block size " + blockAlign + "); export it again");
            }

            if (byteRate != (uint)(sampleRate * blockAlign))
            {
                return MakerWavReport.Refuse(
                    MakerErrorCodes.UnsupportedMedia,
                    "its header does not add up (byte rate " + byteRate.ToString(CultureInfo.InvariantCulture)
                    + ", not " + (sampleRate * blockAlign).ToString(CultureInfo.InvariantCulture) + "); export it again");
            }

            if (dataOffset < 0)
            {
                return MakerWavReport.Refuse(MakerErrorCodes.UnsupportedMedia, "the WAV has no audio data chunk; export it again");
            }

            if (dataLength % blockAlign != 0)
            {
                return MakerWavReport.Refuse(
                    MakerErrorCodes.UnsupportedMedia,
                    "its header does not add up (the audio ends in the middle of a sample frame); export it again");
            }

            var frames = dataLength / blockAlign;
            if (frames == 0)
            {
                return MakerWavReport.Refuse(MakerErrorCodes.UnsupportedMedia, "the WAV holds no audio");
            }

            var seconds = (double)frames / sampleRate;
            if (seconds > MakerEditRules.MaxVoiceSeconds)
            {
                return MakerWavReport.Refuse(
                    MakerErrorCodes.TooLarge,
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "the dub is {0:0.0} s; the limit is {1:0} s",
                        seconds,
                        MakerEditRules.MaxVoiceSeconds));
            }

            return MakerWavReport.Accept(
                channels, sampleRate, frames, dataOffset, formatTag == PcmTag ? "PCM" : "WAVE_FORMAT_EXTENSIBLE (PCM)");
        }

        /// <summary>
        /// The accepted dub as a canonical file: a 44-byte RIFF/fmt/data header and the samples,
        /// unchanged.
        /// </summary>
        /// <param name="bytes">The uploaded bytes <paramref name="check"/> was made from.</param>
        /// <param name="check">An accepted check.</param>
        /// <exception cref="ArgumentException">The check is not an accepted one.</exception>
        public static byte[] Canonical(byte[] bytes, MakerWavReport check)
        {
            if (bytes is null)
            {
                throw new ArgumentNullException(nameof(bytes));
            }

            if (check is null || !check.Ok)
            {
                throw new ArgumentException("only an accepted dub can be normalised", nameof(check));
            }

            var blockAlign = check.Channels * 2;
            var dataBytes = check.Frames * blockAlign;
            var file = new byte[44 + dataBytes];

            Put(file, 0, "RIFF");
            PutUInt32(file, 4, (uint)(36 + dataBytes));
            Put(file, 8, "WAVE");
            Put(file, 12, "fmt ");
            PutUInt32(file, 16, 16);
            PutUInt16(file, 20, PcmTag);
            PutUInt16(file, 22, check.Channels);
            PutUInt32(file, 24, (uint)check.SampleRate);
            PutUInt32(file, 28, (uint)(check.SampleRate * blockAlign));
            PutUInt16(file, 32, blockAlign);
            PutUInt16(file, 34, 16);
            Put(file, 36, "data");
            PutUInt32(file, 40, (uint)dataBytes);
            Buffer.BlockCopy(bytes, check.DataOffset, file, 44, dataBytes);
            return file;
        }

        private static string Describe(int formatTag, int subFormat, int bits, int validBits)
        {
            var depth = (validBits > 0 && validBits != bits ? validBits : bits).ToString(CultureInfo.InvariantCulture) + "-bit";
            switch (formatTag)
            {
                case PcmTag:
                    return depth + " PCM";
                case FloatTag:
                    return depth + " float";
                case ExtensibleTag:
                    var inner = subFormat == PcmTag ? "PCM" : subFormat == FloatTag ? "float" : "an unknown sub-format";
                    return "WAVE_FORMAT_EXTENSIBLE, " + depth + " " + inner;
                case 2:
                case 0x11:
                    return "ADPCM-compressed";
                case 6:
                case 7:
                    return "telephone-compressed (A-law/mu-law)";
                case 0x55:
                    return "MP3 inside a WAV";
                default:
                    return "format " + formatTag.ToString(CultureInfo.InvariantCulture);
            }
        }

        /// <summary>What a non-WAV upload looks like, by its first bytes.</summary>
        private static string Sniff(byte[] bytes)
        {
            if (bytes.Length >= 4 && Ascii(bytes, 0, "OggS"))
            {
                return "an Ogg file";
            }

            if (bytes.Length >= 4 && Ascii(bytes, 0, "fLaC"))
            {
                return "a FLAC file";
            }

            if (bytes.Length >= 4 && Ascii(bytes, 0, "RF64"))
            {
                return "an RF64 (large WAV) file";
            }

            if ((bytes.Length >= 3 && Ascii(bytes, 0, "ID3")) || (bytes.Length >= 2 && bytes[0] == 0xFF && (bytes[1] & 0xE0) == 0xE0))
            {
                return "an MP3 file";
            }

            if (bytes.Length >= 12 && Ascii(bytes, 4, "ftyp"))
            {
                return "an MP4/M4A file";
            }

            return "not a RIFF/WAVE file";
        }

        private static bool IsStandardGuid(byte[] bytes, int offset)
        {
            if (offset + 16 > bytes.Length)
            {
                return false;
            }

            for (var i = 0; i < GuidTail.Length; i++)
            {
                if (bytes[offset + 2 + i] != GuidTail[i])
                {
                    return false;
                }
            }

            return true;
        }

        private static string Megabytes(long bytes) =>
            (bytes / (1024d * 1024d)).ToString("0.0", CultureInfo.InvariantCulture) + " MiB";

        private static string Id(byte[] bytes, int offset) =>
            new string(new[] { (char)bytes[offset], (char)bytes[offset + 1], (char)bytes[offset + 2], (char)bytes[offset + 3] });

        private static bool Ascii(byte[] bytes, int offset, string expected)
        {
            if (offset + expected.Length > bytes.Length)
            {
                return false;
            }

            for (var i = 0; i < expected.Length; i++)
            {
                if (bytes[offset + i] != (byte)expected[i])
                {
                    return false;
                }
            }

            return true;
        }

        private static uint UInt32(byte[] bytes, int offset) =>
            (uint)(bytes[offset] | (bytes[offset + 1] << 8) | (bytes[offset + 2] << 16) | (bytes[offset + 3] << 24));

        private static int UInt16(byte[] bytes, int offset) => bytes[offset] | (bytes[offset + 1] << 8);

        private static void Put(byte[] file, int offset, string ascii)
        {
            for (var i = 0; i < ascii.Length; i++)
            {
                file[offset + i] = (byte)ascii[i];
            }
        }

        private static void PutUInt32(byte[] file, int offset, uint value)
        {
            file[offset] = (byte)value;
            file[offset + 1] = (byte)(value >> 8);
            file[offset + 2] = (byte)(value >> 16);
            file[offset + 3] = (byte)(value >> 24);
        }

        private static void PutUInt16(byte[] file, int offset, int value)
        {
            file[offset] = (byte)value;
            file[offset + 1] = (byte)(value >> 8);
        }
    }
}
