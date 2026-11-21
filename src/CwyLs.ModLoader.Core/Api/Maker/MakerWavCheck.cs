using System;
using System.Globalization;

namespace CwyLs.ModLoader.Core.Api.Maker
{
    /// <summary>How a voice body fared against the request rules.</summary>
    public enum MakerWavVerdict
    {
        /// <summary>A 16-bit PCM WAV, mono or stereo, 8-48 kHz, at most 60 s.</summary>
        Ok = 0,

        /// <summary>
        /// Not a supported WAV: not RIFF/WAVE at all, a truncated or malformed header, or a format the game cannot
        /// take (encoding, bit depth, channels, rate): 415 <c>unsupported_media_type</c> (contract ruling, DS-01).
        /// </summary>
        Unsupported = 2,

        /// <summary>Longer than <see cref="MakerDefaults.MaxWavSeconds"/>: 413 <c>too_large</c>.</summary>
        TooLong = 3,
    }

    /// <summary>
    /// The voice body check of the request rules: reads the RIFF chunk headers only (no samples are
    /// decoded, so a 16 MiB upload costs no second copy here) and decides format and length. A header that
    /// does not add up (two 'fmt ' or 'data' chunks, chunks past the RIFF size, a byte rate or block align
    /// that contradicts the format, a partial last frame) is 415, like any other unsupported body (DS-10a). The editor
    /// (DS-03) decodes the clip itself; this check only keeps what can never be a dub away from it. An empty
    /// body is the router's 400 (a malformed request) and never reaches this check. Pure; never throws.
    /// </summary>
    public static class MakerWavCheck
    {
        private const int PcmFormatTag = 1;

        /// <summary>Checks a voice body.</summary>
        /// <param name="bytes">The whole body.</param>
        /// <param name="durationSec">The playing time when the format is readable; 0 otherwise.</param>
        /// <param name="problem">One sentence for a creator when the verdict is not <see cref="MakerWavVerdict.Ok"/>.</param>
        public static MakerWavVerdict Check(byte[]? bytes, out double durationSec, out string problem)
        {
            // One acceptance rule with the editor (integration, 2026-11-07; DS-10a): Editor.MakerWav.Inspect decides the
            // verdict (it also takes 16-bit PCM in WAVE_FORMAT_EXTENSIBLE, which audio editors export), so the HTTP layer
            // never refuses a dub the editor would save and never forwards one it would refuse. This method only words
            // the refusal: its own sentence when it reached the same verdict, the editor's otherwise.
            var report = Editor.MakerWav.Inspect(bytes);
            var own = CheckOwn(bytes, out var ownSeconds, out var ownProblem);
            if (report.Ok)
            {
                durationSec = report.DurationSec;
                if (durationSec > MakerDefaults.MaxWavSeconds)
                {
                    problem = "the dub is longer than " + MakerDefaults.MaxWavSeconds.ToString(CultureInfo.InvariantCulture) + " s";
                    return MakerWavVerdict.TooLong;
                }

                problem = string.Empty;
                return MakerWavVerdict.Ok;
            }

            var verdict = report.Error == MakerErrorCodes.TooLarge ? MakerWavVerdict.TooLong : MakerWavVerdict.Unsupported;
            durationSec = verdict == MakerWavVerdict.TooLong && own == MakerWavVerdict.TooLong ? ownSeconds : 0;
            problem = own == verdict && ownProblem.Length > 0 ? ownProblem : report.Message;
            return verdict;
        }

        private static MakerWavVerdict CheckOwn(byte[]? bytes, out double durationSec, out string problem)
        {
            durationSec = 0;
            if (bytes is null || bytes.Length < 12 || !Ascii(bytes, 0, "RIFF") || !Ascii(bytes, 8, "WAVE"))
            {
                problem = "the body is not a WAV file (no RIFF/WAVE header)";
                return MakerWavVerdict.Unsupported;
            }

            var formatTag = -1;
            var channels = 0;
            long sampleRate = 0;
            long byteRate = 0;
            var blockAlign = 0;
            var bits = 0;
            long dataLength = -1;
            var riffEnd = 8L + ReadUInt32(bytes, 4);
            var position = 12;
            while (position + 8 <= bytes.Length)
            {
                var declared = (long)ReadUInt32(bytes, position + 4);
                var body = position + 8;
                var isFormat = Ascii(bytes, position, "fmt ");
                var isData = Ascii(bytes, position, "data");
                if (declared > bytes.Length - body)
                {
                    problem = "the WAV file is truncated (a chunk claims " + declared.ToString(CultureInfo.InvariantCulture) + " bytes that are not there)";
                    return MakerWavVerdict.Unsupported;
                }

                if (body + declared > riffEnd)
                {
                    problem = "the WAV header is inconsistent (its RIFF size " + (riffEnd - 8).ToString(CultureInfo.InvariantCulture) +
                              " is smaller than the chunks in the file)";
                    return MakerWavVerdict.Unsupported;
                }

                if ((isFormat && formatTag >= 0) || (isData && dataLength >= 0))
                {
                    problem = "the WAV file has more than one '" + (isFormat ? "fmt " : "data") + "' chunk";
                    return MakerWavVerdict.Unsupported;
                }

                if (isFormat)
                {
                    if (declared < 16)
                    {
                        problem = "the WAV 'fmt ' chunk is shorter than 16 bytes";
                        return MakerWavVerdict.Unsupported;
                    }

                    formatTag = ReadUInt16(bytes, body);
                    channels = ReadUInt16(bytes, body + 2);
                    sampleRate = ReadUInt32(bytes, body + 4);
                    byteRate = ReadUInt32(bytes, body + 8);
                    blockAlign = ReadUInt16(bytes, body + 12);
                    bits = ReadUInt16(bytes, body + 14);
                }
                else if (isData)
                {
                    dataLength = declared;
                }

                position = body + (int)declared + (int)(declared & 1);
            }

            if (formatTag < 0 || dataLength < 0)
            {
                problem = "the WAV file has no " + (formatTag < 0 ? "'fmt '" : "'data'") + " chunk";
                return MakerWavVerdict.Unsupported;
            }

            if (formatTag != PcmFormatTag || bits != 16)
            {
                problem = "save the dub as 16-bit PCM WAV (this one is " + bits.ToString(CultureInfo.InvariantCulture) + "-bit, format " +
                          formatTag.ToString(CultureInfo.InvariantCulture) + ")";
                return MakerWavVerdict.Unsupported;
            }

            if (channels < 1 || channels > 2)
            {
                problem = "a dub must be mono or stereo (this one has " + channels.ToString(CultureInfo.InvariantCulture) + " channels)";
                return MakerWavVerdict.Unsupported;
            }

            if (sampleRate < MakerDefaults.MinWavSampleRate || sampleRate > MakerDefaults.MaxWavSampleRate)
            {
                problem = "a dub's sample rate must be 8000-48000 Hz (this one is " + sampleRate.ToString(CultureInfo.InvariantCulture) + " Hz)";
                return MakerWavVerdict.Unsupported;
            }

            if (blockAlign != channels * 2)
            {
                problem = "the WAV header is inconsistent (block align " + blockAlign.ToString(CultureInfo.InvariantCulture) +
                          ", not " + (channels * 2).ToString(CultureInfo.InvariantCulture) + " for " + channels.ToString(CultureInfo.InvariantCulture) +
                          " channel(s) of 16-bit samples)";
                return MakerWavVerdict.Unsupported;
            }

            if (byteRate != sampleRate * blockAlign)
            {
                problem = "the WAV header is inconsistent (byte rate " + byteRate.ToString(CultureInfo.InvariantCulture) + ", not " +
                          (sampleRate * blockAlign).ToString(CultureInfo.InvariantCulture) + " for " + sampleRate.ToString(CultureInfo.InvariantCulture) +
                          " Hz x block align " + blockAlign.ToString(CultureInfo.InvariantCulture) + ")";
                return MakerWavVerdict.Unsupported;
            }

            if (dataLength % blockAlign != 0)
            {
                problem = "the WAV 'data' chunk is not a whole number of sample frames (" + dataLength.ToString(CultureInfo.InvariantCulture) +
                          " bytes in frames of " + blockAlign.ToString(CultureInfo.InvariantCulture) + ")";
                return MakerWavVerdict.Unsupported;
            }

            var frames = dataLength / blockAlign;
            if (frames == 0)
            {
                problem = "the WAV file holds no sound";
                return MakerWavVerdict.Unsupported;
            }

            durationSec = frames / (double)sampleRate;
            if (durationSec > MakerDefaults.MaxWavSeconds)
            {
                problem = "a dub may be at most " + MakerDefaults.MaxWavSeconds.ToString(CultureInfo.InvariantCulture) + " s long (this one is " +
                          durationSec.ToString("0.#", CultureInfo.InvariantCulture) + " s)";
                return MakerWavVerdict.TooLong;
            }

            problem = string.Empty;
            return MakerWavVerdict.Ok;
        }

        private static uint ReadUInt32(byte[] b, int offset) =>
            (uint)(b[offset] | (b[offset + 1] << 8) | (b[offset + 2] << 16) | (b[offset + 3] << 24));

        private static int ReadUInt16(byte[] b, int offset) => b[offset] | (b[offset + 1] << 8);

        private static bool Ascii(byte[] b, int offset, string expected)
        {
            for (var i = 0; i < expected.Length; i++)
            {
                if (b[offset + i] != (byte)expected[i])
                {
                    return false;
                }
            }

            return true;
        }
    }
}
