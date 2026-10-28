using CwyLs.ModLoader.Core.Voice;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>P1-4: what counts as a voice name, and how one is derived from a file or a path.</summary>
    public class VoiceNameRulesTests
    {
        [Theory]
        [InlineData("Voice_Tutorial_1_001")]
        [InlineData("Voice_CWY_Test")]
        [InlineData("a")]
        [InlineData("A-B_c-9")]
        [InlineData("0123456789")]
        [InlineData("Voice_MainScenario_12(5)_001")] // the game's own name (1.17.3, episode 12)
        public void Accepts_plain_ascii_names(string name) => Assert.True(VoiceNameRules.IsValid(name));

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData(" ")]
        [InlineData("Voice Test")]
        [InlineData("Voice.Test")]
        [InlineData("Voice/Test")]
        [InlineData("Voice+Test")]
        [InlineData("こえ")]
        [InlineData("Voice_Test\n")]
        public void Refuses_anything_else(string? name) => Assert.False(VoiceNameRules.IsValid(name));

        [Theory]
        [InlineData("Voice_CWY_Test.wav", "Voice_CWY_Test")]
        [InlineData("Voice_CWY_Test.OGG", "Voice_CWY_Test")]
        [InlineData("audio/voice/Voice_CWY_Test.ogg", "Voice_CWY_Test")]
        [InlineData("audio\\voice\\Voice_CWY_Test.ogg", "Voice_CWY_Test")]
        [InlineData("Voice_CWY_Test", "Voice_CWY_Test")]
        public void Derives_the_name_from_the_file_name(string fileName, string expected)
        {
            Assert.True(VoiceNameRules.TryFromFileName(fileName, out var name, out var reason));
            Assert.Equal(expected, name);
            Assert.Null(reason);
        }

        [Fact]
        public void Only_the_last_extension_is_dropped()
        {
            // "Voice.v2.ogg" would become "Voice.v2", which the character rule then refuses - the
            // point being that the reason names the offending candidate, not the original file.
            Assert.False(VoiceNameRules.TryFromFileName("Voice.v2.ogg", out var name, out var reason));
            Assert.Null(name);
            Assert.Contains("Voice.v2", reason);
            Assert.Contains(VoiceNameRules.Pattern, reason);
        }

        [Theory]
        [InlineData("my voice.wav")]
        [InlineData(".keep")]
        [InlineData("   ")]
        [InlineData(null)]
        public void Refuses_a_file_name_that_is_not_a_voice_name(string? fileName)
        {
            Assert.False(VoiceNameRules.TryFromFileName(fileName, out var name, out var reason));
            Assert.Null(name);
            Assert.False(string.IsNullOrWhiteSpace(reason));
        }

        [Theory]
        [InlineData("Voice_Tutorial_1_001", "Voice_Tutorial_1_001")]
        [InlineData("Voice/Voice_Tutorial_1_001.ogg", "Voice_Tutorial_1_001")]
        [InlineData("Voice_Tutorial_1_001.ogg", "Voice_Tutorial_1_001")]
        [InlineData("", null)]
        [InlineData(null, null)]
        public void Key_from_audio_path_mirrors_PathToName(string? audioPath, string? expected) =>
            Assert.Equal(expected, VoiceNameRules.KeyFromAudioPath(audioPath));
    }
}
