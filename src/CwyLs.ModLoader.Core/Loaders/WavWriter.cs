using System;

namespace CwyLs.ModLoader.Core.Loaders
{
    /// <summary>
    /// Produces canonical 44-byte-header 16-bit PCM WAV files.
    /// <para>
    /// This exists so the WAV path can be proved without shipping a binary fixture: the unit tests
    /// round-trip <see cref="Write"/> through <see cref="WavParser"/>, and the in-game smoke checks
    /// synthesise a tone with <see cref="Tone"/> and load it back through the engine. It is not a
    /// general-purpose encoder and does not need to be.
    /// </para>
    /// </summary>
    public static class WavWriter
    {
        /// <summary>Size of the canonical RIFF/fmt /data header this writer emits.</summary>
        public const int HeaderBytes = 44;

        private const int PcmFormatTag = 1;
        private const int BitsPerSample = 16;

        /// <summary>
        /// Encodes interleaved samples as a 16-bit PCM WAV file.
        /// </summary>
        /// <param name="samples">
        /// Interleaved samples. Values outside -1..1 are clamped rather than allowed to wrap,
        /// because a wrapped sample is a loud click and a clamped one is merely flat.
        /// </param>
        /// <param name="channels">Interleaved channel count; 1 or more.</param>
        /// <param name="sampleRate">Frames per second, e.g. 44100.</param>
        /// <returns>The complete file.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="samples"/> is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">A count or rate is not positive.</exception>
        /// <exception cref="ArgumentException">The sample count is not a whole number of frames.</exception>
        public static byte[] Write(float[] samples, int channels, int sampleRate)
        {
            if (samples is null)
            {
                throw new ArgumentNullException(nameof(samples));
            }

            if (channels <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(channels), channels, "channels must be positive");
            }

            if (sampleRate <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(sampleRate), sampleRate, "sample rate must be positive");
            }

            if (samples.Length % channels != 0)
            {
                throw new ArgumentException(
                    "sample count " + samples.Length + " is not a whole number of " + channels + "-channel frames",
                    nameof(samples));
            }

            var blockAlign = channels * (BitsPerSample / 8);
            var dataBytes = samples.Length * (BitsPerSample / 8);
            var file = new byte[HeaderBytes + dataBytes];

            WriteAscii(file, 0, "RIFF");
            WriteUInt32(file, 4, (uint)(HeaderBytes - 8 + dataBytes));
            WriteAscii(file, 8, "WAVE");

            WriteAscii(file, 12, "fmt ");
            WriteUInt32(file, 16, 16);
            WriteUInt16(file, 20, PcmFormatTag);
            WriteUInt16(file, 22, channels);
            WriteUInt32(file, 24, (uint)sampleRate);
            WriteUInt32(file, 28, (uint)(sampleRate * blockAlign));
            WriteUInt16(file, 32, blockAlign);
            WriteUInt16(file, 34, BitsPerSample);

            WriteAscii(file, 36, "data");
            WriteUInt32(file, 40, (uint)dataBytes);

            for (var i = 0; i < samples.Length; i++)
            {
                // 32767 on the way out against 32768 on the way in (WavParser): the pair is
                // asymmetric by one step so that +1.0 encodes without overflowing short.MaxValue.
                var clamped = samples[i] < -1f ? -1f : samples[i] > 1f ? 1f : samples[i];
                var value = (short)Math.Round(clamped * 32767f, MidpointRounding.AwayFromZero);
                var offset = HeaderBytes + (i * 2);
                file[offset] = (byte)(value & 0xFF);
                file[offset + 1] = (byte)((value >> 8) & 0xFF);
            }

            return file;
        }

        /// <summary>
        /// Synthesises a sine tone as a 16-bit PCM WAV file. Used by the smoke checks and the
        /// unit tests; it is the "known good audio" no fixture file has to be shipped for.
        /// </summary>
        /// <param name="frequencyHz">Tone frequency, e.g. 440.</param>
        /// <param name="seconds">Length; rounded to the nearest whole frame.</param>
        /// <param name="sampleRate">Frames per second. Defaults to 44100.</param>
        /// <param name="channels">Interleaved channel count; every channel gets the same tone.</param>
        /// <param name="amplitude">Peak amplitude in 0..1.</param>
        /// <returns>The complete file.</returns>
        /// <exception cref="ArgumentOutOfRangeException">An argument is out of range.</exception>
        public static byte[] Tone(
            double frequencyHz,
            double seconds,
            int sampleRate = 44100,
            int channels = 1,
            float amplitude = 0.5f)
        {
            if (frequencyHz <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(frequencyHz), frequencyHz, "frequency must be positive");
            }

            if (seconds <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(seconds), seconds, "length must be positive");
            }

            if (sampleRate <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(sampleRate), sampleRate, "sample rate must be positive");
            }

            if (channels <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(channels), channels, "channels must be positive");
            }

            if (amplitude <= 0f || amplitude > 1f)
            {
                throw new ArgumentOutOfRangeException(nameof(amplitude), amplitude, "amplitude must be in (0..1]");
            }

            var frames = (int)Math.Round(seconds * sampleRate, MidpointRounding.AwayFromZero);
            if (frames <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(seconds), seconds, "length rounds to zero frames");
            }

            var samples = new float[frames * channels];
            var step = 2.0 * Math.PI * frequencyHz / sampleRate;
            for (var frame = 0; frame < frames; frame++)
            {
                var value = (float)(amplitude * Math.Sin(step * frame));
                for (var channel = 0; channel < channels; channel++)
                {
                    samples[(frame * channels) + channel] = value;
                }
            }

            return Write(samples, channels, sampleRate);
        }

        private static void WriteAscii(byte[] target, int offset, string text)
        {
            for (var i = 0; i < text.Length; i++)
            {
                target[offset + i] = (byte)text[i];
            }
        }

        private static void WriteUInt32(byte[] target, int offset, uint value)
        {
            target[offset] = (byte)(value & 0xFF);
            target[offset + 1] = (byte)((value >> 8) & 0xFF);
            target[offset + 2] = (byte)((value >> 16) & 0xFF);
            target[offset + 3] = (byte)((value >> 24) & 0xFF);
        }

        private static void WriteUInt16(byte[] target, int offset, int value)
        {
            target[offset] = (byte)(value & 0xFF);
            target[offset + 1] = (byte)((value >> 8) & 0xFF);
        }
    }
}
