using CwyLs.ModLoader.Core.MasterData;
using CwyLs.ModLoader.Core.Scenario;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// W0-CONTRACTS: <c>MasterDataEnums.TryParseCommand</c> - the game's 50 commands first, then
    /// script commands by dotted name (5000-5999). Uses private registries so the shared one is
    /// never touched by a test.
    /// </summary>
    public class MasterDataScriptCommandTests
    {
        [Theory]
        [InlineData("VoiceText", 20)]
        [InlineData(" 20 ", 20)]
        [InlineData("ChangeHat", 1000)]
        public void Game_commands_resolve_as_before(string token, int expected)
        {
            Assert.True(MasterDataEnums.TryParseCommand(token, new ScenarioCommandRegistry(), out var value));
            Assert.Equal(expected, value);
        }

        [Fact]
        public void Built_in_script_commands_resolve_by_name_and_by_their_fixed_number()
        {
            var registry = new ScenarioCommandRegistry();

            Assert.True(MasterDataEnums.TryParseCommand("cwy.do", registry, out var byName));
            Assert.True(MasterDataEnums.TryParseCommand("5000", registry, out var byNumber));
            Assert.Equal(ScenarioCommandIds.Do, byName);
            Assert.Equal(ScenarioCommandIds.Do, byNumber);
            Assert.True(MasterDataEnums.IsCommandDefined(ScenarioCommandIds.SayStream, registry));
        }

        [Fact]
        public void A_mod_command_name_is_reserved_on_first_sight()
        {
            var registry = new ScenarioCommandRegistry();

            Assert.True(MasterDataEnums.TryParseCommand("my.mod.wave", registry, out var id));
            Assert.Equal(ScenarioCommandIds.DynamicFirst, id);
            Assert.True(MasterDataEnums.IsCommandDefined(id, registry));
            Assert.True(MasterDataEnums.TryParseCommand(id.ToString(System.Globalization.CultureInfo.InvariantCulture), registry, out var byNumber));
            Assert.Equal(id, byNumber);
        }

        [Theory]
        [InlineData("nope")]
        [InlineData("9999")]
        [InlineData("5500")]
        [InlineData("4999")]
        [InlineData("-5000")]
        [InlineData("my mod.wave")]
        [InlineData("")]
        [InlineData(null)]
        public void Everything_else_is_still_refused(string? token)
        {
            var registry = new ScenarioCommandRegistry();

            Assert.False(MasterDataEnums.TryParseCommand(token, registry, out var value));
            Assert.Equal(0, value);
            Assert.Equal(0, registry.DynamicCount);
        }

        [Fact]
        public void The_game_table_itself_is_unchanged()
        {
            Assert.Equal(50, MasterDataEnums.Command.Count);
            Assert.False(MasterDataEnums.Command.TryParseText("cwy.do", out _));
            Assert.False(MasterDataEnums.Command.IsDefined(5000));
        }

        [Fact]
        public void A_command_number_names_itself()
        {
            Assert.Equal("VoiceText", MasterDataEnums.CommandName(20));
            Assert.Equal("cwy.do", MasterDataEnums.CommandName(ScenarioCommandIds.Do));
            Assert.Equal("4242", MasterDataEnums.CommandName(4242));
        }
    }
}
