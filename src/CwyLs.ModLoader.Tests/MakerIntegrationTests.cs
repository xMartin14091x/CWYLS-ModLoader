using System;
using System.IO;
using System.Text;
using CwyLs.ModLoader.Core.Api.Maker;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>Checks added when DS-01..05 were merged (2026-11-07): the HTTP layer and the editor agree on WAVs.</summary>
    public class MakerIntegrationTests
    {
        private static byte[] Wav(bool extensible, int channels = 1, int rate = 48000, int bits = 16, double seconds = 0.5)
        {
            var frames = (int)(rate * seconds);
            var data = new byte[frames * channels * (bits / 8)];
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms, Encoding.ASCII);
            var fmtSize = extensible ? 40 : 16;
            w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(4 + 8 + fmtSize + 8 + data.Length); w.Write(Encoding.ASCII.GetBytes("WAVE"));
            w.Write(Encoding.ASCII.GetBytes("fmt ")); w.Write(fmtSize);
            w.Write((short)(extensible ? 0xFFFE : 1)); w.Write((short)channels); w.Write(rate);
            w.Write(rate * channels * bits / 8); w.Write((short)(channels * bits / 8)); w.Write((short)bits);
            if (extensible)
            {
                w.Write((short)22); w.Write((short)bits); w.Write(channels == 1 ? 4 : 3);
                // KSDATAFORMAT_SUBTYPE_PCM 00000001-0000-0010-8000-00aa00389b71
                w.Write(new byte[] { 1, 0, 0, 0, 0, 0, 0x10, 0, 0x80, 0, 0, 0xAA, 0, 0x38, 0x9B, 0x71 });
            }

            w.Write(Encoding.ASCII.GetBytes("data")); w.Write(data.Length); w.Write(data);
            return ms.ToArray();
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Http_layer_and_editor_both_accept_16_bit_PCM_plain_or_extensible(bool extensible)
        {
            var wav = Wav(extensible);
            Assert.True(Core.Api.Maker.Editor.MakerWav.Inspect(wav).Ok);
            Assert.Equal(MakerWavVerdict.Ok, MakerWavCheck.Check(wav, out var seconds, out var problem));
            Assert.Equal(0.5, seconds, 2);
            Assert.Equal(string.Empty, problem);
        }

        [Fact]
        public void Http_layer_and_editor_both_refuse_24_bit()
        {
            var wav = Wav(extensible: false, bits: 24);
            Assert.False(Core.Api.Maker.Editor.MakerWav.Inspect(wav).Ok);
            Assert.Equal(MakerWavVerdict.Unsupported, MakerWavCheck.Check(wav, out _, out var problem));
            Assert.NotEqual(string.Empty, problem);
        }
    }
}
