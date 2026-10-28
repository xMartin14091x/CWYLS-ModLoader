using CwyLs.ModLoader.Core.MasterData;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// P2-1: the game enums a pack may name, as observed in game v1.17.2.
    /// These tests are the tripwire for a stale transcription.
    /// </summary>
    public class MasterDataEnumTests
    {
        [Theory]
        [InlineData("Text", 0)]
        [InlineData("EndScenario", 12)]
        [InlineData("VoiceText", 20)]
        [InlineData("ChangeMotion", 21)]
        [InlineData("PomodoroVoiceText", 22)]
        [InlineData("ChangeHat", 1000)]
        [InlineData("voicetext", 20)]
        [InlineData("VOICETEXT", 20)]
        public void Command_members_match_the_game(string name, int expected)
        {
            Assert.True(MasterDataEnums.Command.TryParseName(name, out var value));
            Assert.Equal(expected, value);
        }

        [Theory]
        [InlineData("None", 0)]
        [InlineData("MainScenario", 1)]
        [InlineData("Tutorial", 18)]
        [InlineData("MotionScenario", 22)]
        [InlineData("SmallTalk", 23)]
        [InlineData("SpeakWord_Countup_LongWorkFinish", 3202)]
        public void Scenario_members_match_the_game(string name, int expected)
        {
            Assert.True(MasterDataEnums.Scenario.TryParseName(name, out var value));
            Assert.Equal(expected, value);
        }

        [Fact]
        public void Scenario_keeps_the_games_birthday_alias()
        {
            Assert.True(MasterDataEnums.Scenario.TryParseName("Event_Player_HappyBirthday", out var happy));
            Assert.True(MasterDataEnums.Scenario.TryParseName("Event_Player_Birthday", out var birthday));
            Assert.Equal(103, happy);
            Assert.Equal(happy, birthday);

            // The first declared name is the one a number maps back to.
            Assert.True(MasterDataEnums.Scenario.TryName(103, out var name));
            Assert.Equal("Event_Player_HappyBirthday", name);
        }

        [Fact]
        public void Talker_and_ease_match_the_game()
        {
            Assert.True(MasterDataEnums.Talker.TryParseName("Heroine", out var heroine));
            Assert.Equal(1, heroine);
            Assert.Equal(2, MasterDataEnums.Talker.Count);

            // DOTween's Ease starts at Unset, so the game's stored 0 is Unset and not Linear.
            Assert.True(MasterDataEnums.Ease.TryName(0, out var zero));
            Assert.Equal("Unset", zero);
            Assert.True(MasterDataEnums.Ease.TryParseName("InOutSine", out var sine));
            Assert.Equal(4, sine);
        }

        [Fact]
        public void The_tables_have_the_member_counts_the_dump_has()
        {
            Assert.Equal(50, MasterDataEnums.Command.Count);
            Assert.Equal(89, MasterDataEnums.Scenario.Count);
            Assert.Equal(2, MasterDataEnums.Talker.Count);
            Assert.Equal(38, MasterDataEnums.Ease.Count);
            Assert.Equal(4, MasterDataEnums.All.Length);
        }

        [Fact]
        public void A_text_token_may_be_a_name_or_a_declared_number()
        {
            Assert.True(MasterDataEnums.Command.TryParseText("VoiceText", out var byName));
            Assert.True(MasterDataEnums.Command.TryParseText(" 20 ", out var byNumber));
            Assert.Equal(20, byName);
            Assert.Equal(20, byNumber);

            Assert.False(MasterDataEnums.Command.TryParseText("9999", out _));
            Assert.False(MasterDataEnums.Command.TryParseText("nope", out _));
            Assert.False(MasterDataEnums.Command.TryParseText(null, out _));
        }

        [Fact]
        public void Is_defined_only_accepts_declared_numbers()
        {
            Assert.True(MasterDataEnums.Command.IsDefined(20));
            Assert.False(MasterDataEnums.Command.IsDefined(23));
            Assert.False(MasterDataEnums.Command.IsDefined(-1));
            Assert.False(MasterDataEnums.Scenario.TryName(9999, out var missing));
            Assert.Null(missing);
        }

        [Fact]
        public void Describe_lists_the_first_members_and_the_total()
        {
            var described = MasterDataEnums.Command.Describe(3);

            Assert.StartsWith("Text, WaitForSeconds, WaitInput", described);
            Assert.Contains("(50 members)", described);
        }
    }
}
