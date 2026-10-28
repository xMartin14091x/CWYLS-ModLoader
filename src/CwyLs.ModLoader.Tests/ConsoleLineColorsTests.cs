using System;
using CwyLs.ModLoader.Core.Console;
using CwyLs.ModLoader.Core.Logging;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// B-UI: the game-style console colours a line by its tag, in the same families as the terminal
    /// viewer (tools/mac/watch-log.py TAGS), with colours from the game's own UI palette.
    /// </summary>
    public class ConsoleLineColorsTests
    {
        [Theory]
        [InlineData(ConsoleTags.Speech, ConsoleColorRole.Satone)]
        [InlineData(ConsoleTags.Say, ConsoleColorRole.Satone)]
        [InlineData(ConsoleTags.State, ConsoleColorRole.Satone)]
        [InlineData(ConsoleTags.Subtitle, ConsoleColorRole.Satone)]
        [InlineData(ConsoleTags.Face, ConsoleColorRole.Satone)]
        [InlineData(ConsoleTags.Motion, ConsoleColorRole.Satone)]
        [InlineData(ConsoleTags.Look, ConsoleColorRole.Satone)]
        [InlineData(ConsoleTags.Restore, ConsoleColorRole.Satone)]
        [InlineData(ConsoleTags.Music, ConsoleColorRole.World)]
        [InlineData(ConsoleTags.Pomodoro, ConsoleColorRole.World)]
        [InlineData(ConsoleTags.Scenario, ConsoleColorRole.World)]
        [InlineData(ConsoleTags.Scene, ConsoleColorRole.World)]
        [InlineData(ConsoleTags.Window, ConsoleColorRole.World)]
        [InlineData(ConsoleTags.Language, ConsoleColorRole.World)]
        [InlineData(ConsoleTags.Ready, ConsoleColorRole.Player)]
        [InlineData(ConsoleTags.Button, ConsoleColorRole.Player)]
        [InlineData(ConsoleTags.Typed, ConsoleColorRole.Player)]
        [InlineData(ConsoleTags.Click, ConsoleColorRole.Player)]
        [InlineData(ConsoleTags.Smoke, ConsoleColorRole.Player)]
        [InlineData(ConsoleTags.Health, ConsoleColorRole.Warning)]
        [InlineData(ConsoleTags.Warn, ConsoleColorRole.Warning)]
        [InlineData(ConsoleTags.Unity, ConsoleColorRole.Error)]
        [InlineData(ConsoleTags.Error, ConsoleColorRole.Error)]
        [InlineData(ConsoleTags.Data, ConsoleColorRole.Content)]
        [InlineData(ConsoleTags.Voice, ConsoleColorRole.Content)]
        [InlineData(ConsoleTags.Texture, ConsoleColorRole.Content)]
        [InlineData(ConsoleTags.MasterData, ConsoleColorRole.Content)]
        [InlineData(ConsoleTags.Script, ConsoleColorRole.World)]
        [InlineData(ConsoleTags.Trigger, ConsoleColorRole.World)]
        [InlineData(ConsoleTags.Camera, ConsoleColorRole.World)]
        [InlineData(ConsoleTags.Action, ConsoleColorRole.Player)]
        [InlineData(ConsoleTags.Api, ConsoleColorRole.Player)]
        [InlineData(ConsoleTags.Mic, ConsoleColorRole.Player)]
        [InlineData(ConsoleTags.Mod, ConsoleColorRole.Content)]
        [InlineData(ConsoleTags.Mods, ConsoleColorRole.Content)]
        [InlineData(ConsoleTags.Audio, ConsoleColorRole.Content)]
        [InlineData(ConsoleTags.UiSkin, ConsoleColorRole.Content)]
        [InlineData(ConsoleTags.EditData, ConsoleColorRole.Content)]
        [InlineData(ConsoleTags.Cond, ConsoleColorRole.Framework)]
        [InlineData(ConsoleTags.Console, ConsoleColorRole.Framework)]
        [InlineData(ConsoleTags.Setup, ConsoleColorRole.Framework)]
        [InlineData(ConsoleTags.Trace, ConsoleColorRole.Framework)]
        [InlineData(ConsoleTags.Keys, ConsoleColorRole.Framework)]
        [InlineData(ConsoleTags.Packs, ConsoleColorRole.Framework)]
        [InlineData(ConsoleTags.Unknown, ConsoleColorRole.Framework)]
        public void Every_tag_has_the_viewers_family(string tag, ConsoleColorRole expected)
        {
            Assert.Equal(expected, ConsoleLineColors.RoleOfTag(tag, "anything"));
        }

        [Theory]
        [InlineData("cwy.sample.hello failed in Entry: NullReferenceException", ConsoleColorRole.Error)]
        [InlineData("cwy.sample.hello@0.1.0 loaded", ConsoleColorRole.Content)]
        public void Mods_turns_red_when_a_mod_failed(string primary, ConsoleColorRole expected)
        {
            Assert.Equal(expected, ConsoleLineColors.RoleOfTag(ConsoleTags.Mods, primary));
        }

        [Fact]
        public void A_tag_nobody_mapped_yet_is_framework_coloured_not_an_error()
        {
            Assert.Equal(ConsoleColorRole.Framework, ConsoleLineColors.RoleOfTag("FONT", "Base_TH"));
            Assert.Equal(ConsoleColorRole.Framework, ConsoleLineColors.RoleOfTag(null, null));
        }

        [Theory]
        [InlineData("spike 180ms", ConsoleColorRole.Error)]
        [InlineData("lowFps 12.4", ConsoleColorRole.Error)]
        [InlineData("lowMemory", ConsoleColorRole.Error)]
        [InlineData("focus off", ConsoleColorRole.Warning)]
        [InlineData("spikey", ConsoleColorRole.Warning)]
        public void Health_turns_red_for_the_readings_that_mean_the_game_struggles(string primary, ConsoleColorRole expected)
        {
            Assert.Equal(expected, ConsoleLineColors.RoleOfTag(ConsoleTags.Health, primary));
        }

        [Theory]
        [InlineData("FAIL p2.console.ready", ConsoleColorRole.Error)]
        [InlineData("ok=false", ConsoleColorRole.Error)]
        [InlineData("ok=true 49/49", ConsoleColorRole.Player)]
        public void Smoke_turns_red_when_it_failed(string primary, ConsoleColorRole expected)
        {
            Assert.Equal(expected, ConsoleLineColors.RoleOfTag(ConsoleTags.Smoke, primary));
        }

        [Fact]
        public void The_line_kind_wins_for_echo_output_and_errors()
        {
            Assert.Equal(ConsoleColorRole.Echo, ConsoleLineColors.RoleOf(ConsoleLineKind.Echo, "> help"));
            Assert.Equal(ConsoleColorRole.Output, ConsoleLineColors.RoleOf(ConsoleLineKind.Output, "SPEECH  looks tagged"));
            Assert.Equal(ConsoleColorRole.Error, ConsoleLineColors.RoleOf(ConsoleLineKind.Error, "SPEECH  but logged as error"));
        }

        [Fact]
        public void A_warning_line_stays_a_warning_unless_its_tag_says_error()
        {
            Assert.Equal(ConsoleColorRole.Warning, ConsoleLineColors.RoleOf(ConsoleLineKind.Warning, "WARN  disk nearly full"));
            Assert.Equal(ConsoleColorRole.Warning, ConsoleLineColors.RoleOf(ConsoleLineKind.Warning, "plain warning text"));
            Assert.Equal(ConsoleColorRole.Error, ConsoleLineColors.RoleOf(ConsoleLineKind.Warning, "HEALTH  spike 220ms  frame=10"));
        }

        [Fact]
        public void An_info_line_is_coloured_by_its_tag_and_an_untagged_one_is_dim()
        {
            Assert.Equal(ConsoleColorRole.Satone, ConsoleLineColors.RoleOf(ConsoleLineKind.Info, "SPEECH  \"Welcome back\"  voice=Voice_1"));
            Assert.Equal(ConsoleColorRole.Framework, ConsoleLineColors.RoleOf(ConsoleLineKind.Info, "CWY console ready. 22 command(s)"));
        }

        [Fact]
        public void Split_reads_the_three_columns_and_skips_a_stamp()
        {
            Assert.True(ConsoleLineColors.TrySplit("MUSIC  \"Rainy\"  index=3 shuffle=false", out var tag, out var primary, out var fields));
            Assert.Equal("MUSIC", tag);
            Assert.Equal("\"Rainy\"", primary);
            Assert.Equal("index=3 shuffle=false", fields);

            Assert.True(ConsoleLineColors.TrySplit("12:01:02.345  SCENE  RoomScene loaded", out tag, out primary, out fields));
            Assert.Equal("SCENE", tag);
            Assert.Equal("RoomScene loaded", primary);
            Assert.Equal(string.Empty, fields);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("lowercase  text")]
        [InlineData("A  single letter is not a tag")]
        [InlineData("12  starts with a digit")]
        [InlineData("PACKS: 1 loaded")]
        public void Split_refuses_what_is_not_a_record(string? text)
        {
            Assert.False(ConsoleLineColors.TrySplit(text, out _, out _, out _));
        }

        [Fact]
        public void Rich_text_colours_the_tag_and_keeps_the_body_literal()
        {
            var rich = ConsoleLineColors.ToRichText(ConsoleLineKind.Info, "SAY  voice play <name>  id=cwy_say_n_001");

            Assert.StartsWith("<color=" + ConsoleLineColors.Colour(ConsoleColorRole.Satone).ToHex() + "><b>SAY</b></color>", rich);
            Assert.Contains("<noparse>voice play <name></noparse>", rich);
            Assert.Contains("<color=" + ConsoleLineColors.FieldText.ToHex() + "><noparse>id=cwy_say_n_001</noparse></color>", rich);
        }

        [Fact]
        public void Rich_text_for_an_echo_is_one_cyan_run()
        {
            Assert.Equal(
                "<color=#00D5EF><noparse>> help</noparse></color>",
                ConsoleLineColors.ToRichText(ConsoleLineKind.Echo, "> help"));
        }

        [Fact]
        public void An_empty_line_renders_as_nothing()
        {
            Assert.Equal(string.Empty, ConsoleLineColors.ToRichText(ConsoleLineKind.Output, string.Empty));
            Assert.Equal(string.Empty, ConsoleLineColors.ToRichText(ConsoleLineKind.Info, null));
        }

        [Fact]
        public void A_closing_noparse_inside_the_text_cannot_end_the_literal_run()
        {
            var escaped = ConsoleLineColors.Escape("a</noparse><color=red>b</NOPARSE>c");

            Assert.StartsWith("<noparse>", escaped);
            Assert.EndsWith("</noparse>", escaped);
            var inner = escaped.Substring("<noparse>".Length, escaped.Length - "<noparse></noparse>".Length);
            Assert.DoesNotContain("</noparse>", inner, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("<color=red>", inner);
        }

        [Fact]
        public void The_palette_keeps_the_games_own_accents()
        {
            Assert.Equal("#00D5EF", ConsoleLineColors.GameCyan.ToHex());
            Assert.Equal("#EA00B2", ConsoleLineColors.GameMagenta.ToHex());
            Assert.Equal("#9999BF", ConsoleLineColors.GameLavender.ToHex());
            Assert.Equal(ConsoleLineColors.GameCyan, ConsoleLineColors.Colour(ConsoleColorRole.Echo));
        }

        [Fact]
        public void Every_family_has_its_own_colour()
        {
            var roles = (ConsoleColorRole[])Enum.GetValues(typeof(ConsoleColorRole));
            for (var i = 0; i < roles.Length; i++)
            {
                for (var j = i + 1; j < roles.Length; j++)
                {
                    Assert.NotEqual(ConsoleLineColors.Colour(roles[i]), ConsoleLineColors.Colour(roles[j]));
                }
            }
        }
    }
}
