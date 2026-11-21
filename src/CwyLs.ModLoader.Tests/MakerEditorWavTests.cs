using System;
using System.Collections.Generic;
using System.Text;
using CwyLs.ModLoader.Core.Api.Maker;
using CwyLs.ModLoader.Core.Api.Maker.Editor;
using CwyLs.ModLoader.Core.Loaders;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// <see cref="MakerWav"/>: only 16-bit PCM, mono or stereo, 8-48 kHz, at most 60 s; every refusal
    /// names what the file is. No binary fixture: the files are built here (invented tones and silence).
    /// </summary>
    public class MakerEditorWavTests
    {
        [Fact]
        public void A_plain_16_bit_pcm_wav_is_accepted_with_its_length()
        {
            var wav = WavWriter.Tone(frequencyHz: 440, seconds: 0.5, sampleRate: 44100, channels: 1);

            var check = MakerWav.Inspect(wav);

            Assert.True(check.Ok, check.Message);
            Assert.Equal(1, check.Channels);
            Assert.Equal(44100, check.SampleRate);
            Assert.Equal(22050, check.Frames);
            Assert.Equal(0.5, check.DurationSec, 3);
            Assert.Equal("PCM", check.Format);
        }

        [Fact]
        public void Stereo_at_48k_is_accepted()
        {
            var check = MakerWav.Inspect(Build(PcmFmt(2, 48000, 16), Silence(48000 / 10, 2)));

            Assert.True(check.Ok, check.Message);
            Assert.Equal(2, check.Channels);
        }

        [Fact]
        public void A_24_bit_export_is_refused_by_name()
        {
            var check = MakerWav.Inspect(Build(Fmt(1, 1, 44100, 24), new byte[300]));

            Assert.False(check.Ok);
            Assert.Equal(MakerErrorCodes.UnsupportedMedia, check.Error);
            Assert.Contains("24-bit PCM", check.Message);
            Assert.Contains("16-bit PCM", check.Message);
        }

        [Fact]
        public void A_32_bit_float_export_is_refused_by_name()
        {
            var check = MakerWav.Inspect(Build(Fmt(3, 1, 44100, 32), new byte[400]));

            Assert.False(check.Ok);
            Assert.Contains("32-bit float", check.Message);
        }

        [Fact]
        public void Wave_format_extensible_with_16_bit_pcm_is_accepted_and_normalised()
        {
            var samples = Silence(1000, 2);
            samples[0] = 0x34;
            samples[1] = 0x12;
            var upload = Build(ExtensibleFmt(2, 44100, 16, 16, pcm: true), samples);

            var check = MakerWav.Inspect(upload);
            Assert.True(check.Ok, check.Message);
            Assert.Equal("WAVE_FORMAT_EXTENSIBLE (PCM)", check.Format);

            var canonical = MakerWav.Canonical(upload, check);
            Assert.Equal(44 + samples.Length, canonical.Length);
            Assert.Equal(1, canonical[20] | (canonical[21] << 8)); // plain PCM tag
            var parsed = WavParser.Parse(canonical);
            Assert.Equal(1000, parsed.Frames);
            Assert.Equal(2, parsed.Channels);
            Assert.Equal(0x1234 / 32768f, parsed.Samples[0], 5);
        }

        [Fact]
        public void Wave_format_extensible_24_bit_is_refused_by_name()
        {
            var check = MakerWav.Inspect(Build(ExtensibleFmt(2, 48000, 24, 24, pcm: true), new byte[600]));

            Assert.False(check.Ok);
            Assert.Contains("WAVE_FORMAT_EXTENSIBLE, 24-bit PCM", check.Message);
        }

        [Fact]
        public void Wave_format_extensible_float_is_refused_by_name()
        {
            var check = MakerWav.Inspect(Build(ExtensibleFmt(1, 48000, 32, 32, pcm: false), new byte[400]));

            Assert.False(check.Ok);
            Assert.Contains("WAVE_FORMAT_EXTENSIBLE, 32-bit float", check.Message);
        }

        [Fact]
        public void More_than_sixty_seconds_is_too_large()
        {
            var check = MakerWav.Inspect(Build(PcmFmt(1, 8000, 16), Silence(8000 * 61, 1)));

            Assert.False(check.Ok);
            Assert.Equal(MakerErrorCodes.TooLarge, check.Error);
            Assert.Contains("61.0 s", check.Message);
        }

        [Fact]
        public void Exactly_sixty_seconds_is_accepted()
        {
            var check = MakerWav.Inspect(Build(PcmFmt(1, 8000, 16), Silence(8000 * 60, 1)));

            Assert.True(check.Ok, check.Message);
        }

        [Fact]
        public void A_body_over_sixteen_mebibytes_is_too_large_before_parsing()
        {
            var check = MakerWav.Inspect(new byte[MakerEditRules.MaxVoiceBytes + 1]);

            Assert.Equal(MakerErrorCodes.TooLarge, check.Error);
        }

        [Fact]
        public void Three_channels_and_odd_sample_rates_are_refused()
        {
            var surround = MakerWav.Inspect(Build(PcmFmt(3, 44100, 16), Silence(10, 3)));
            var tooFast = MakerWav.Inspect(Build(PcmFmt(1, 96000, 16), Silence(10, 1)));
            var tooSlow = MakerWav.Inspect(Build(PcmFmt(1, 4000, 16), Silence(10, 1)));

            Assert.Contains("3 channels", surround.Message);
            Assert.Contains("96000 Hz", tooFast.Message);
            Assert.Contains("4000 Hz", tooSlow.Message);
            Assert.Equal(MakerErrorCodes.UnsupportedMedia, tooSlow.Error);
        }

        [Fact]
        public void Other_formats_are_named()
        {
            Assert.Contains("Ogg", MakerWav.Inspect(Encoding.ASCII.GetBytes("OggS\0\u0002 rest of a page")).Message);
            Assert.Contains("MP3", MakerWav.Inspect(Encoding.ASCII.GetBytes("ID3\u0004 tag and frames")).Message);
            Assert.Contains("FLAC", MakerWav.Inspect(Encoding.ASCII.GetBytes("fLaC stream info....")).Message);
            Assert.Equal(MakerErrorCodes.UnsupportedMedia, MakerWav.Inspect(new byte[] { 1, 2, 3 }).Error);
        }

        [Fact]
        public void An_empty_body_is_a_bad_request()
        {
            Assert.Equal(MakerErrorCodes.BadRequest, MakerWav.Inspect(null).Error);
            Assert.Equal(MakerErrorCodes.BadRequest, MakerWav.Inspect(new byte[0]).Error);
        }

        [Fact]
        public void A_truncated_file_is_refused()
        {
            var whole = WavWriter.Tone(440, 0.1, 44100, 1);
            var cut = new byte[whole.Length - 100];
            Array.Copy(whole, cut, cut.Length);

            var check = MakerWav.Inspect(cut);

            Assert.False(check.Ok);
            Assert.Contains("cut short", check.Message);
        }

        [Fact]
        public void A_header_without_data_or_format_is_refused()
        {
            var noData = Build(PcmFmt(1, 44100, 16), null);
            var noFmt = Riff(Chunk("data", Silence(10, 1)));

            Assert.Contains("no audio data", MakerWav.Inspect(noData).Message);
            Assert.Contains("no format chunk", MakerWav.Inspect(noFmt).Message);
        }

        [Fact]
        public void An_inconsistent_block_size_is_refused()
        {
            var fmt = PcmFmt(2, 44100, 16);
            fmt[12] = 2; // block align says mono

            Assert.Contains("does not add up", MakerWav.Inspect(Build(fmt, Silence(10, 2))).Message);
        }

        [Fact]
        public void Extra_chunks_are_dropped_and_samples_kept_by_canonical()
        {
            var samples = Silence(5, 1);
            samples[2] = 0x7F;
            var upload = Riff(Chunk("fmt ", PcmFmt(1, 22050, 16)), Chunk("LIST", Encoding.ASCII.GetBytes("INFOISFT")), Chunk("data", samples));

            var check = MakerWav.Inspect(upload);
            var canonical = MakerWav.Canonical(upload, check);

            Assert.True(check.Ok, check.Message);
            Assert.Equal(44 + samples.Length, canonical.Length);
            Assert.Equal(samples, canonical[44..]);
            Assert.True(MakerWav.Inspect(canonical).Ok);
        }

        [Fact]
        public void A_trailing_partial_frame_is_refused()
        {
            // DS-10a: a data chunk that is not a whole number of frames is a header that does not add up.
            var samples = new byte[2 * 2 * 10 + 1]; // ten stereo frames and a stray byte (odd: pad follows)
            var upload = Build(PcmFmt(2, 44100, 16), samples);

            var check = MakerWav.Inspect(upload);

            Assert.False(check.Ok);
            Assert.Equal(MakerErrorCodes.UnsupportedMedia, check.Error);
            Assert.Contains("middle of a sample frame", check.Message);
        }

        [Fact]
        public void Canonical_refuses_a_refused_check()
        {
            var check = MakerWav.Inspect(new byte[] { 1 });

            Assert.Throws<ArgumentException>(() => MakerWav.Canonical(new byte[] { 1 }, check));
        }

        // ---- builders -----------------------------------------------------------------------

        private static byte[] Silence(int frames, int channels) => new byte[frames * channels * 2];

        private static byte[] PcmFmt(int channels, int rate, int bits) => Fmt(1, channels, rate, bits);

        private static byte[] Fmt(int tag, int channels, int rate, int bits)
        {
            var block = channels * bits / 8;
            var fmt = new byte[16];
            U16(fmt, 0, tag);
            U16(fmt, 2, channels);
            U32(fmt, 4, rate);
            U32(fmt, 8, rate * block);
            U16(fmt, 12, block);
            U16(fmt, 14, bits);
            return fmt;
        }

        private static byte[] ExtensibleFmt(int channels, int rate, int bits, int validBits, bool pcm)
        {
            var block = channels * bits / 8;
            var fmt = new byte[40];
            U16(fmt, 0, 0xFFFE);
            U16(fmt, 2, channels);
            U32(fmt, 4, rate);
            U32(fmt, 8, rate * block);
            U16(fmt, 12, block);
            U16(fmt, 14, bits);
            U16(fmt, 16, 22);
            U16(fmt, 18, validBits);
            U32(fmt, 20, channels == 1 ? 4 : 3);
            U16(fmt, 24, pcm ? 1 : 3);
            var tail = new byte[] { 0x00, 0x00, 0x00, 0x00, 0x10, 0x00, 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71 };
            Array.Copy(tail, 0, fmt, 26, tail.Length);
            return fmt;
        }

        private static byte[] Build(byte[] fmt, byte[]? data) =>
            data is null ? Riff(Chunk("fmt ", fmt)) : Riff(Chunk("fmt ", fmt), Chunk("data", data));

        private static byte[] Chunk(string id, byte[] body)
        {
            var chunk = new List<byte>(Encoding.ASCII.GetBytes(id));
            var size = new byte[4];
            U32(size, 0, body.Length);
            chunk.AddRange(size);
            chunk.AddRange(body);
            if ((body.Length & 1) == 1)
            {
                chunk.Add(0);
            }

            return chunk.ToArray();
        }

        private static byte[] Riff(params byte[][] chunks)
        {
            var body = new List<byte>(Encoding.ASCII.GetBytes("WAVE"));
            foreach (var chunk in chunks)
            {
                body.AddRange(chunk);
            }

            var file = new List<byte>(Encoding.ASCII.GetBytes("RIFF"));
            var size = new byte[4];
            U32(size, 0, body.Count);
            file.AddRange(size);
            file.AddRange(body);
            return file.ToArray();
        }

        private static void U16(byte[] b, int o, int v)
        {
            b[o] = (byte)v;
            b[o + 1] = (byte)(v >> 8);
        }

        private static void U32(byte[] b, int o, int v)
        {
            b[o] = (byte)v;
            b[o + 1] = (byte)(v >> 8);
            b[o + 2] = (byte)(v >> 16);
            b[o + 3] = (byte)(v >> 24);
        }
    }
}
