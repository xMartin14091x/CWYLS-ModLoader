using System;

namespace CwyLs.ModLoader.Core.Loaders
{
    /// <summary>
    /// A decoded WAV file: the three numbers <c>AudioClip.Create</c> needs, plus the samples.
    /// </summary>
    public sealed class WavData
    {
        /// <summary>Creates decoded WAV data.</summary>
        /// <param name="channels">1 or more interleaved channels.</param>
        /// <param name="sampleRate">Frames per second, e.g. 44100.</param>
        /// <param name="bitsPerSample">Bit depth of the source file; always 16 for now.</param>
        /// <param name="samples">
        /// Interleaved samples in -1..1, length <c>Frames * Channels</c>. Taken by reference,
        /// not copied: this array is handed straight to <c>AudioClip.SetData</c>.
        /// </param>
        /// <exception cref="ArgumentNullException"><paramref name="samples"/> is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">A count or rate is not positive.</exception>
        /// <exception cref="ArgumentException">The sample count is not a whole number of frames.</exception>
        public WavData(int channels, int sampleRate, int bitsPerSample, float[] samples)
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

            if (bitsPerSample <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(bitsPerSample), bitsPerSample, "bits per sample must be positive");
            }

            if (samples.Length % channels != 0)
            {
                throw new ArgumentException(
                    "sample count " + samples.Length + " is not a whole number of " + channels + "-channel frames",
                    nameof(samples));
            }

            Channels = channels;
            SampleRate = sampleRate;
            BitsPerSample = bitsPerSample;
            Samples = samples;
            Frames = samples.Length / channels;
        }

        /// <summary>Interleaved channel count.</summary>
        public int Channels { get; }

        /// <summary>Frames per second.</summary>
        public int SampleRate { get; }

        /// <summary>Bit depth of the file this came from.</summary>
        public int BitsPerSample { get; }

        /// <summary>
        /// Frames, i.e. samples per channel. This is what <c>AudioClip.Create</c> calls
        /// <c>lengthSamples</c> and what <c>AudioClip.samples</c> reports back.
        /// </summary>
        public int Frames { get; }

        /// <summary>Interleaved samples in -1..1; length is <see cref="Frames"/> * <see cref="Channels"/>.</summary>
        public float[] Samples { get; }

        /// <summary>Playing time in seconds.</summary>
        public double DurationSeconds => Frames / (double)SampleRate;

        /// <summary>A one-line description for a report or log line.</summary>
        public override string ToString() =>
            Frames + " frames x" + Channels + " @" + SampleRate + "Hz " + BitsPerSample + "-bit";
    }
}
