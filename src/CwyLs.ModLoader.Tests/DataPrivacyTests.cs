using CwyLs.ModLoader.Core.Data;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// The redaction rules are the part of the bus that cannot be allowed to regress quietly, so
    /// they live in Core as pure functions and are tested here rather than being trusted to a code
    /// review of the adapter.
    /// </summary>
    public class DataPrivacyTests
    {
        [Theory]
        [InlineData("2f1c0e9a-device", true)]
        [InlineData("x", true)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void RedactAccount_reports_only_whether_a_device_id_exists(string? deviceId, bool expected)
        {
            Assert.Equal(expected, GameDataPrivacy.RedactAccount(deviceId).HasDeviceId);
        }

        [Fact]
        public void RedactAccount_never_carries_the_id_in_any_form()
        {
            // Not hashed, not truncated: a stable pseudonym is still a correlatable identifier.
            const string deviceId = "2f1c0e9a-device";
            var json = JsonWriter.Write(GameDataPrivacy.RedactAccount(deviceId));

            Assert.DoesNotContain("2f1c0e9a", json, System.StringComparison.Ordinal);
            Assert.Equal("{\"hasDeviceId\":true}", json);
        }

        [Theory]
        [InlineData("/Users/someone/Music/lofi beat.ogg", "lofi beat.ogg")]
        [InlineData(@"C:\Users\someone\Music\lofi beat.ogg", "lofi beat.ogg")]
        [InlineData("lofi.ogg", "lofi.ogg")]
        [InlineData("", "")]
        [InlineData(null, "")]
        public void RedactLocalMusicPath_keeps_only_the_file_name(string? path, string expected)
        {
            // Both separators regardless of the running OS: a save written on Windows is read on
            // macOS, and the directory part leaks the player's account name.
            Assert.Equal(expected, GameDataPrivacy.RedactLocalMusicPath(path));
        }

        [Fact]
        public void RedactLocalMusicPath_of_a_trailing_separator_is_empty_not_the_directory()
        {
            Assert.Equal(string.Empty, GameDataPrivacy.RedactLocalMusicPath("/Users/someone/Music/"));
        }

        [Fact]
        public void ApplyTextPolicy_distinguishes_withheld_from_empty()
        {
            // null means "the bus was told not to publish this"; "" means "the player wrote nothing".
            Assert.Null(GameDataPrivacy.ApplyTextPolicy("a private thought", include: false));
            Assert.Equal("a private thought", GameDataPrivacy.ApplyTextPolicy("a private thought", include: true));
            Assert.Equal(string.Empty, GameDataPrivacy.ApplyTextPolicy(string.Empty, include: true));
            Assert.Null(GameDataPrivacy.ApplyTextPolicy(null, include: true));
        }
    }
}
