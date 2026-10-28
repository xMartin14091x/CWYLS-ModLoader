using CwyLs.ModLoader.Core.Loaders;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// Content beats extension, and every rejection has to name the actual problem: a pack is
    /// hand-assembled, so "this .png is really a JPEG" must reach the person who made it.
    /// </summary>
    public class LoadersFileKindDetectorTests
    {
        private static readonly byte[] Png =
            { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D };

        private static readonly byte[] Jpeg =
            { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01 };

        private static readonly byte[] Ogg =
            { 0x4F, 0x67, 0x67, 0x53, 0x00, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };

        private static readonly byte[] Wav =
            { 0x52, 0x49, 0x46, 0x46, 0x24, 0x08, 0x00, 0x00, 0x57, 0x41, 0x56, 0x45 };

        private static readonly byte[] RiffAvi =
            { 0x52, 0x49, 0x46, 0x46, 0x24, 0x08, 0x00, 0x00, 0x41, 0x56, 0x49, 0x20 };

        [Fact]
        public void A_png_is_a_texture()
        {
            var detection = FileKindDetector.Detect("textures/Body/_MainTex.png", Png);

            Assert.True(detection.Supported);
            Assert.Equal(AssetFormat.Png, detection.Format);
            Assert.Equal(AssetKind.Texture, detection.Kind);
            Assert.Equal("PNG header", detection.Reason);
        }

        [Fact]
        public void An_ogg_is_audio()
        {
            var detection = FileKindDetector.Detect("audio/voice/Voice_Tutorial_1_001.ogg", Ogg);

            Assert.True(detection.Supported);
            Assert.Equal(AssetFormat.OggVorbis, detection.Format);
            Assert.Equal(AssetKind.Audio, detection.Kind);
        }

        [Fact]
        public void A_wav_is_audio()
        {
            var detection = FileKindDetector.Detect("audio/probe.wav", Wav);

            Assert.True(detection.Supported);
            Assert.Equal(AssetFormat.Wav, detection.Format);
            Assert.Equal(AssetKind.Audio, detection.Kind);
        }

        [Fact]
        public void A_jpeg_renamed_to_png_is_rejected_as_a_jpeg()
        {
            // The single most likely mistake in a hand-made texture pack.
            var detection = FileKindDetector.Detect("textures/Body/_MainTex.png", Jpeg);

            Assert.False(detection.Supported);
            Assert.Equal(AssetFormat.Jpeg, detection.Format);
            Assert.Contains("JPEG is not supported", detection.Reason);
            Assert.Contains("_MainTex", detection.Reason);
        }

        [Fact]
        public void A_jpg_extension_is_rejected_even_with_no_header_to_look_at()
        {
            var detection = FileKindDetector.Detect("textures/Body/_MainTex.jpg");

            Assert.False(detection.Supported);
            Assert.Equal(AssetFormat.Jpeg, detection.Format);
            Assert.Contains("JPEG is not supported", detection.Reason);
        }

        [Fact]
        public void A_riff_container_that_is_not_wave_is_rejected_and_says_so()
        {
            var detection = FileKindDetector.Detect("audio/clip.wav", RiffAvi);

            Assert.False(detection.Supported);
            Assert.Equal(AssetFormat.Unknown, detection.Format);
            Assert.Contains("RIFF container, but not WAVE", detection.Reason);
            Assert.Contains("52 49 46 46", detection.Reason);
        }

        [Fact]
        public void An_unrecognised_header_reports_the_bytes_it_saw()
        {
            var detection = FileKindDetector.Detect("textures/Body/_MainTex.png", new byte[] { 0x42, 0x4D, 0x36, 0x00 });

            Assert.False(detection.Supported);
            Assert.Contains("unrecognised file header 42 4D 36 00", detection.Reason);
            Assert.DoesNotContain("RIFF", detection.Reason);
        }

        [Fact]
        public void A_header_too_short_to_match_anything_is_rejected_rather_than_guessed()
        {
            var detection = FileKindDetector.Detect("textures/x.png", new byte[] { 0x89, 0x50 });

            Assert.False(detection.Supported);
            Assert.Contains("unrecognised file header 89 50", detection.Reason);
        }

        [Fact]
        public void With_no_header_the_extension_decides_and_the_reason_admits_it()
        {
            var detection = FileKindDetector.Detect("audio/voice/Voice_New_001.ogg");

            Assert.True(detection.Supported);
            Assert.Equal(AssetFormat.OggVorbis, detection.Format);
            Assert.Contains("no header bytes were read", detection.Reason);
        }

        [Fact]
        public void Content_beats_a_wrong_extension_but_the_mismatch_is_noted()
        {
            // A Vorbis file saved as .wav still plays through the engine's decoder; refusing it
            // would be pedantry. Reporting it is not.
            var detection = FileKindDetector.Detect("audio/voice/Voice_New_001.wav", Ogg);

            Assert.True(detection.Supported);
            Assert.Equal(AssetFormat.OggVorbis, detection.Format);
            Assert.Contains("extension '.wav' says otherwise", detection.Reason);
        }

        [Theory]
        [InlineData("data/rows.json", "unsupported extension '.json'")]
        [InlineData("notes/README", "no extension")]
        public void An_extension_no_loader_handles_is_rejected(string path, string expected)
        {
            var detection = FileKindDetector.Detect(path);

            Assert.False(detection.Supported);
            Assert.Equal(AssetKind.Unknown, detection.Kind);
            Assert.Contains(expected, detection.Reason);
        }

        [Fact]
        public void An_empty_byte_array_is_treated_as_no_header_at_all()
        {
            var detection = FileKindDetector.Detect("textures/x.png", new byte[0]);

            Assert.True(detection.Supported);
            Assert.Contains("no header bytes were read", detection.Reason);
        }
    }
}
