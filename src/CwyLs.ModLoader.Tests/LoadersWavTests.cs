using System;
using CwyLs.ModLoader.Core.Loaders;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// <see cref="WavWriter"/> and <see cref="WavParser"/> are each other's oracle: no binary
    /// fixture is checked in, and the same pair is what the in-game <c>p1.loaders.wav_sync</c>
    /// smoke check exercises through <c>AudioClip.Create</c>.
    /// </summary>
    public class LoadersWavTests
    {
        /// <summary>One quantisation step of 16-bit audio, with room for the 32767/32768 asymmetry.</summary>
        private const float Tolerance = 1e-4f;

        [Fact]
        public void Mono_round_trips_through_the_writer_and_the_parser()
        {
            var samples = new[] { 0f, 0.5f, -0.5f, 0.999f, -0.999f, 0.125f };

            var parsed = WavParser.Parse(WavWriter.Write(samples, channels: 1, sampleRate: 44100));

            Assert.Equal(1, parsed.Channels);
            Assert.Equal(44100, parsed.SampleRate);
            Assert.Equal(16, parsed.BitsPerSample);
            Assert.Equal(samples.Length, parsed.Frames);
            for (var i = 0; i < samples.Length; i++)
            {
                Assert.InRange(parsed.Samples[i], samples[i] - Tolerance, samples[i] + Tolerance);
            }
        }

        [Fact]
        public void Stereo_round_trips_with_the_channels_still_interleaved()
        {
            // Left ramps up, right ramps down: a swapped or de-interleaved pair would be obvious.
            var samples = new[] { 0.1f, -0.1f, 0.2f, -0.2f, 0.3f, -0.3f, 0.4f, -0.4f };

            var parsed = WavParser.Parse(WavWriter.Write(samples, channels: 2, sampleRate: 48000));

            Assert.Equal(2, parsed.Channels);
            Assert.Equal(48000, parsed.SampleRate);
            Assert.Equal(4, parsed.Frames);
            Assert.Equal(8, parsed.Samples.Length);
            Assert.InRange(parsed.Samples[0], 0.1f - Tolerance, 0.1f + Tolerance);
            Assert.InRange(parsed.Samples[1], -0.1f - Tolerance, -0.1f + Tolerance);
            Assert.InRange(parsed.Samples[6], 0.4f - Tolerance, 0.4f + Tolerance);
            Assert.InRange(parsed.Samples[7], -0.4f - Tolerance, -0.4f + Tolerance);
        }

        [Fact]
        public void A_smoke_check_tone_is_exactly_the_length_the_check_asserts()
        {
            // p1.loaders.wav_sync asserts 8820 frames at 44100 Hz; if this drifts, so does the check.
            var parsed = WavParser.Parse(WavWriter.Tone(frequencyHz: 440, seconds: 0.2, sampleRate: 44100, channels: 1));

            Assert.Equal(8820, parsed.Frames);
            Assert.Equal(1, parsed.Channels);
            Assert.Equal(44100, parsed.SampleRate);
            Assert.Equal(0.2, parsed.DurationSeconds, 6);
            Assert.Equal(WavWriter.HeaderBytes + (8820 * 2), WavWriter.Tone(440, 0.2).Length);

            // A 440 Hz sine at amplitude 0.5 must actually reach near its peak somewhere.
            var peak = 0f;
            foreach (var sample in parsed.Samples)
            {
                peak = Math.Max(peak, Math.Abs(sample));
            }

            Assert.InRange(peak, 0.49f, 0.51f);
        }

        [Fact]
        public void Out_of_range_samples_are_clamped_rather_than_wrapped()
        {
            // A wrapped sample is a full-scale click; a clamped one is merely flat.
            var parsed = WavParser.Parse(WavWriter.Write(new[] { 2f, -2f }, channels: 1, sampleRate: 8000));

            Assert.InRange(parsed.Samples[0], 0.99f, 1f);
            Assert.InRange(parsed.Samples[1], -1f, -0.99f);
        }

        [Theory]
        [InlineData(8, "8-bit WAV is not supported")]
        [InlineData(24, "24-bit WAV is not supported")]
        public void An_unsupported_bit_depth_is_refused_by_name(int bits, string expected)
        {
            var file = BuildWav(formatTag: 1, channels: 1, sampleRate: 44100, bits: bits, dataBytes: 64);

            var error = Assert.Throws<WavFormatException>(() => WavParser.Parse(file));

            Assert.Contains(expected, error.Message);
            Assert.Contains("16-bit PCM", error.Message);
        }

        [Fact]
        public void A_32_bit_float_wav_says_it_is_float()
        {
            var file = BuildWav(formatTag: 3, channels: 1, sampleRate: 44100, bits: 32, dataBytes: 64);

            var error = Assert.Throws<WavFormatException>(() => WavParser.Parse(file));

            Assert.Contains("32-bit WAV is not supported", error.Message);
            Assert.Contains("IEEE float", error.Message);
        }

        [Fact]
        public void Format_extensible_is_refused_by_name_even_at_16_bit()
        {
            var file = BuildWav(formatTag: 0xFFFE, channels: 1, sampleRate: 44100, bits: 16, dataBytes: 64);

            var error = Assert.Throws<WavFormatException>(() => WavParser.Parse(file));

            Assert.Contains("WAVE_FORMAT_EXTENSIBLE", error.Message);
        }

        [Theory]
        [InlineData("OggS", "not a RIFF container")]
        [InlineData("AVI ", "RIFF container is not WAVE")]
        public void A_file_that_is_not_a_wave_is_refused(string marker, string expected)
        {
            var file = BuildWav(1, 1, 44100, 16, 8);
            if (marker == "OggS")
            {
                WriteAscii(file, 0, marker);
            }
            else
            {
                WriteAscii(file, 8, marker);
            }

            var error = Assert.Throws<WavFormatException>(() => WavParser.Parse(file));

            Assert.Contains(expected, error.Message);
        }

        [Fact]
        public void A_missing_data_chunk_is_refused()
        {
            // 36 bytes = RIFF/WAVE header + a complete fmt chunk, and nothing after it.
            var file = new byte[36];
            Array.Copy(BuildWav(1, 1, 44100, 16, 8), file, 36);
            WriteUInt32(file, 4, 28);

            var error = Assert.Throws<WavFormatException>(() => WavParser.Parse(file));

            Assert.Contains("no 'data' chunk", error.Message);
        }

        [Fact]
        public void A_chunk_that_claims_more_bytes_than_the_file_holds_is_refused()
        {
            var file = BuildWav(1, 1, 44100, 16, 8);
            WriteUInt32(file, 40, 99999);

            var error = Assert.Throws<WavFormatException>(() => WavParser.Parse(file));

            Assert.Contains("truncated", error.Message);
        }

        [Fact]
        public void An_empty_data_chunk_is_refused_because_AudioClip_Create_needs_a_frame()
        {
            var file = BuildWav(1, 1, 44100, 16, 0);

            var error = Assert.Throws<WavFormatException>(() => WavParser.Parse(file));

            Assert.Contains("less than one frame", error.Message);
        }

        [Fact]
        public void A_metadata_chunk_before_the_data_is_skipped_padding_and_all()
        {
            // Audacity and Reaper both write an odd-length LIST/INFO chunk here, which is only
            // survivable if the RIFF pad byte is honoured.
            var tail = WavParser.Parse(WithListChunk(WavWriter.Write(new[] { 0.25f, -0.25f }, 1, 22050)));

            Assert.Equal(2, tail.Frames);
            Assert.Equal(22050, tail.SampleRate);
            Assert.InRange(tail.Samples[0], 0.25f - Tolerance, 0.25f + Tolerance);
        }

        [Fact]
        public void TryParse_reports_instead_of_throwing()
        {
            Assert.False(WavParser.TryParse(null, out var nothing, out var noBytes));
            Assert.Null(nothing);
            Assert.Equal("no bytes", noBytes);

            Assert.False(WavParser.TryParse(new byte[] { 1, 2, 3 }, out var tooSmall, out var smallError));
            Assert.Null(tooSmall);
            Assert.Contains("too short", smallError);

            Assert.True(WavParser.TryParse(WavWriter.Tone(440, 0.01), out var ok, out var noError));
            Assert.Null(noError);
            Assert.NotNull(ok);
            Assert.Equal(441, ok!.Frames);
        }

        [Fact]
        public void The_writer_refuses_a_sample_count_that_is_not_whole_frames()
        {
            Assert.Throws<ArgumentException>(() => WavWriter.Write(new[] { 0f, 1f, 0f }, channels: 2, sampleRate: 44100));
            Assert.Throws<ArgumentNullException>(() => WavWriter.Write(null!, 1, 44100));
            Assert.Throws<ArgumentOutOfRangeException>(() => WavWriter.Write(new[] { 0f }, channels: 0, sampleRate: 44100));
            Assert.Throws<ArgumentOutOfRangeException>(() => WavWriter.Tone(440, seconds: 0));
        }

        /// <summary>Builds a canonical 44-byte-header WAV with arbitrary format fields.</summary>
        private static byte[] BuildWav(int formatTag, int channels, int sampleRate, int bits, int dataBytes)
        {
            var blockAlign = channels * (bits / 8);
            var file = new byte[44 + dataBytes];

            WriteAscii(file, 0, "RIFF");
            WriteUInt32(file, 4, (uint)(36 + dataBytes));
            WriteAscii(file, 8, "WAVE");
            WriteAscii(file, 12, "fmt ");
            WriteUInt32(file, 16, 16);
            WriteUInt16(file, 20, formatTag);
            WriteUInt16(file, 22, channels);
            WriteUInt32(file, 24, (uint)sampleRate);
            WriteUInt32(file, 28, (uint)(sampleRate * blockAlign));
            WriteUInt16(file, 32, blockAlign);
            WriteUInt16(file, 34, bits);
            WriteAscii(file, 36, "data");
            WriteUInt32(file, 40, (uint)dataBytes);

            return file;
        }

        /// <summary>Splices an odd-length LIST chunk (plus its RIFF pad byte) before the data chunk.</summary>
        private static byte[] WithListChunk(byte[] canonical)
        {
            const int listBody = 5;
            const int listChunk = 8 + listBody + 1;

            var result = new byte[canonical.Length + listChunk];
            Array.Copy(canonical, 0, result, 0, 36);
            WriteAscii(result, 36, "LIST");
            WriteUInt32(result, 40, listBody);
            WriteAscii(result, 44, "INFO");
            Array.Copy(canonical, 36, result, 36 + listChunk, canonical.Length - 36);
            WriteUInt32(result, 4, (uint)(result.Length - 8));

            return result;
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
