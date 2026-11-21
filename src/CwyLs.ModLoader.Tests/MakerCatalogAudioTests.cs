using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using CwyLs.ModLoader.Core.Api.Maker.Catalog;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// Invented audio files for the Pack Maker catalog tests: PCM WAV and Ogg Vorbis byte layouts
    /// with silent (zero) payloads. No real audio, and nothing from the game.
    /// </summary>
    internal static class MakerAudioFixtures
    {
        /// <summary>A 16-bit PCM WAV of <paramref name="frames"/> silent frames.</summary>
        internal static byte[] Wav(
            int sampleRate,
            short channels,
            int frames,
            string? extraChunkId = null,
            int extraChunkSize = 0,
            uint? declaredDataSize = null)
        {
            var blockAlign = (short)(channels * 2);
            var data = new byte[frames * blockAlign];
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(Encoding.ASCII.GetBytes("RIFF"));
                w.Write(0u);
                w.Write(Encoding.ASCII.GetBytes("WAVE"));

                w.Write(Encoding.ASCII.GetBytes("fmt "));
                w.Write(16u);
                w.Write((short)1);
                w.Write(channels);
                w.Write(sampleRate);
                w.Write(sampleRate * blockAlign);
                w.Write(blockAlign);
                w.Write((short)16);

                if (extraChunkId != null)
                {
                    w.Write(Encoding.ASCII.GetBytes(extraChunkId));
                    w.Write((uint)extraChunkSize);
                    w.Write(new byte[extraChunkSize + (extraChunkSize & 1)]);
                }

                w.Write(Encoding.ASCII.GetBytes("data"));
                w.Write(declaredDataSize ?? (uint)data.Length);
                w.Write(data);
                w.Flush();

                var bytes = ms.ToArray();
                var riffSize = BitConverter.GetBytes((uint)(bytes.Length - 8));
                Array.Copy(riffSize, 0, bytes, 4, 4);
                return bytes;
            }
        }

        /// <summary>The Vorbis identification header packet.</summary>
        internal static byte[] VorbisIdHeader(uint rate, byte channels = 1)
        {
            var packet = new List<byte> { 0x01 };
            packet.AddRange(Encoding.ASCII.GetBytes("vorbis"));
            packet.AddRange(new byte[4]);
            packet.Add(channels);
            packet.AddRange(BitConverter.GetBytes(rate));
            packet.AddRange(new byte[12]);
            packet.Add(0xB8);
            packet.Add(0x01);
            return packet.ToArray();
        }

        /// <summary>One Ogg page (no CRC: the reader does not check it).</summary>
        internal static byte[] OggPage(uint serial, long granule, byte headerType, byte[] payload, uint sequence = 0)
        {
            var segments = new List<byte>();
            var remaining = payload.Length;
            while (remaining >= 255)
            {
                segments.Add(255);
                remaining -= 255;
            }

            segments.Add((byte)remaining);

            var page = new List<byte>();
            page.AddRange(Encoding.ASCII.GetBytes("OggS"));
            page.Add(0);
            page.Add(headerType);
            page.AddRange(BitConverter.GetBytes(granule));
            page.AddRange(BitConverter.GetBytes(serial));
            page.AddRange(BitConverter.GetBytes(sequence));
            page.AddRange(new byte[4]);
            page.Add((byte)segments.Count);
            page.AddRange(segments);
            page.AddRange(payload);
            return page.ToArray();
        }

        /// <summary>A minimal Ogg Vorbis stream: id page, a page with no finished packet, and a last page at <paramref name="samples"/>.</summary>
        internal static byte[] OggVorbis(uint rate, long samples, uint serial = 7, int lastPayload = 100)
        {
            var bytes = new List<byte>();
            bytes.AddRange(OggPage(serial, 0, 0x02, VorbisIdHeader(rate), 0));
            bytes.AddRange(OggPage(serial, -1, 0x00, new byte[300], 1));
            bytes.AddRange(OggPage(serial, samples, 0x04, new byte[lastPayload], 2));
            return bytes.ToArray();
        }
    }

    /// <summary>DS-02: a dub's length from its headers (WAV, Ogg Vorbis) and the per-file cache.</summary>
    public sealed class MakerCatalogAudioTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "cwy-maker-audio-tests-" + Guid.NewGuid().ToString("N"));

        public MakerCatalogAudioTests()
        {
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_dir))
                {
                    Directory.Delete(_dir, recursive: true);
                }
            }
            catch (IOException)
            {
                // A leftover temp folder is harmless.
            }
        }

        [Theory]
        [InlineData(8000, 1, 8000, 1.0)]
        [InlineData(48000, 2, 24000, 0.5)]
        [InlineData(22050, 1, 33075, 1.5)]
        public void A_pcm_wav_is_measured_from_its_data_chunk(int rate, short channels, int frames, double expected)
        {
            var bytes = MakerAudioFixtures.Wav(rate, channels, frames);

            Assert.True(MakerAudioLength.TryWav(new MemoryStream(bytes), out var seconds));
            Assert.Equal(expected, seconds, 3);
        }

        [Fact]
        public void Chunks_before_data_are_skipped_including_the_pad_byte_of_an_odd_size()
        {
            var bytes = MakerAudioFixtures.Wav(16000, 1, 16000, extraChunkId: "LIST", extraChunkSize: 37);

            Assert.True(MakerAudioLength.TryWav(new MemoryStream(bytes), out var seconds));
            Assert.Equal(1.0, seconds, 3);
        }

        [Fact]
        public void A_data_size_larger_than_the_file_counts_only_what_is_there()
        {
            var bytes = MakerAudioFixtures.Wav(8000, 1, 4000, declaredDataSize: 0xFFFFFFFF);

            Assert.True(MakerAudioLength.TryWav(new MemoryStream(bytes), out var seconds));
            Assert.Equal(0.5, seconds, 3);
        }

        [Fact]
        public void Broken_wav_headers_are_unknown_not_guessed()
        {
            var good = MakerAudioFixtures.Wav(8000, 1, 800);

            var notRiff = (byte[])good.Clone();
            notRiff[0] = (byte)'X';
            Assert.False(MakerAudioLength.TryWav(new MemoryStream(notRiff), out _));

            var notWave = (byte[])good.Clone();
            notWave[8] = (byte)'X';
            Assert.False(MakerAudioLength.TryWav(new MemoryStream(notWave), out _));

            // "fmt " renamed: no sample rate, so no length.
            var noFmt = (byte[])good.Clone();
            noFmt[12] = (byte)'x';
            Assert.False(MakerAudioLength.TryWav(new MemoryStream(noFmt), out _));

            Assert.False(MakerAudioLength.TryWav(new MemoryStream(new byte[] { 1, 2, 3 }), out _));
            Assert.False(MakerAudioLength.TryWav(new MemoryStream(new byte[0]), out _));

            // Header only: a data chunk with nothing in it is not a length.
            Assert.False(MakerAudioLength.TryWav(new MemoryStream(MakerAudioFixtures.Wav(8000, 1, 0)), out _));
        }

        [Fact]
        public void An_ogg_vorbis_stream_is_measured_from_its_last_page()
        {
            var bytes = MakerAudioFixtures.OggVorbis(48000, 72000);

            Assert.True(MakerAudioLength.TryOggVorbis(new MemoryStream(bytes), out var seconds));
            Assert.Equal(1.5, seconds, 3);
        }

        [Fact]
        public void A_last_page_longer_than_the_short_tail_is_still_found()
        {
            var bytes = MakerAudioFixtures.OggVorbis(44100, 88200, lastPayload: 12000);
            Assert.True(bytes.Length > MakerAudioLength.ShortTail);

            Assert.True(MakerAudioLength.TryOggVorbis(new MemoryStream(bytes), out var seconds));
            Assert.Equal(2.0, seconds, 3);
        }

        [Fact]
        public void Pages_of_another_stream_and_pages_without_a_finished_packet_are_skipped()
        {
            var bytes = new List<byte>(MakerAudioFixtures.OggVorbis(48000, 48000, serial: 7));
            bytes.AddRange(MakerAudioFixtures.OggPage(99, 999999, 0x04, new byte[50]));
            bytes.AddRange(MakerAudioFixtures.OggPage(7, -1, 0x00, new byte[50]));

            Assert.True(MakerAudioLength.TryOggVorbis(new MemoryStream(bytes.ToArray()), out var seconds));
            Assert.Equal(1.0, seconds, 3);
        }

        [Fact]
        public void Other_codecs_and_broken_ogg_files_are_unknown()
        {
            var opusHead = new List<byte>(Encoding.ASCII.GetBytes("OpusHead"));
            opusHead.AddRange(new byte[11]);
            var opus = new List<byte>(MakerAudioFixtures.OggPage(3, 0, 0x02, opusHead.ToArray()));
            opus.AddRange(MakerAudioFixtures.OggPage(3, 96000, 0x04, new byte[20]));
            Assert.False(MakerAudioLength.TryOggVorbis(new MemoryStream(opus.ToArray()), out _));

            Assert.False(MakerAudioLength.TryOggVorbis(new MemoryStream(Encoding.ASCII.GetBytes("not an ogg file at all, just text")), out _));

            // Rate 0 in the identification header.
            var zeroRate = MakerAudioFixtures.OggVorbis(0, 1000);
            Assert.False(MakerAudioLength.TryOggVorbis(new MemoryStream(zeroRate), out _));

            // Identification page only: no page carries a granule position.
            var headOnly = MakerAudioFixtures.OggPage(7, 0, 0x02, MakerAudioFixtures.VorbisIdHeader(48000));
            Assert.False(MakerAudioLength.TryOggVorbis(new MemoryStream(headOnly), out _));
        }

        [Fact]
        public void Files_are_read_by_extension()
        {
            var wav = Path.Combine(_dir, "Voice_Sample_001.wav");
            var ogg = Path.Combine(_dir, "Voice_Sample_002.ogg");
            var mp3 = Path.Combine(_dir, "Voice_Sample_003.mp3");
            File.WriteAllBytes(wav, MakerAudioFixtures.Wav(8000, 1, 2000));
            File.WriteAllBytes(ogg, MakerAudioFixtures.OggVorbis(48000, 24000));
            File.WriteAllBytes(mp3, MakerAudioFixtures.Wav(8000, 1, 2000));

            Assert.True(MakerAudioLength.TryRead(wav, out var wavSeconds));
            Assert.Equal(0.25, wavSeconds, 3);
            Assert.True(MakerAudioLength.TryRead(ogg, out var oggSeconds));
            Assert.Equal(0.5, oggSeconds, 3);
            Assert.False(MakerAudioLength.TryRead(mp3, out _));
            Assert.False(MakerAudioLength.TryRead(Path.Combine(_dir, "missing.wav"), out _));
            Assert.False(MakerAudioLength.TryRead(null, out _));
            Assert.False(MakerAudioLength.TryRead("  ", out _));
        }

        [Fact]
        public void The_length_cache_reads_a_file_again_when_it_changes()
        {
            var path = Path.Combine(_dir, "Voice_Sample_004.wav");
            File.WriteAllBytes(path, MakerAudioFixtures.Wav(8000, 1, 8000));
            var lengths = new MakerDubLengths();

            Assert.Equal(1.0, lengths.LengthOf(path));
            Assert.Equal(1.0, lengths.LengthOf(path));
            Assert.Equal(1, lengths.Count);

            File.WriteAllBytes(path, MakerAudioFixtures.Wav(8000, 1, 16000));
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
            Assert.Equal(2.0, lengths.LengthOf(path));

            File.Delete(path);
            Assert.Null(lengths.LengthOf(path));
            Assert.Null(lengths.LengthOf(null));
        }

        [Fact]
        public void An_unreadable_header_is_remembered_as_unknown()
        {
            var path = Path.Combine(_dir, "Voice_Sample_005.wav");
            File.WriteAllText(path, "this is not a wav file");
            var lengths = new MakerDubLengths();

            Assert.Null(lengths.LengthOf(path));
            Assert.Equal(1, lengths.Count);
        }
    }
}
