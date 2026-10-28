using System;
using System.Collections.Generic;
using CwyLs.ModLoader.Core.Actions;
using CwyLs.ModLoader.Core.Data;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>E-ACT-CORE: key=value words (console, scripts), the act list/result text, and the LLM tool schema.</summary>
    public class ActionsWordsTests
    {
        private static readonly ActionDescriptor Play = new ActionDescriptor(
            "music.play", ActionTier.Act, "Plays a track by its title.",
            new[]
            {
                new ActionParameter("title", ActionValueKind.String, true, "The track title."),
                new ActionParameter("volume", ActionValueKind.Number, false, "0 to 1.", minimum: 0, maximum: 1),
                new ActionParameter("mode", ActionValueKind.String, false, "Order.", new[] { "loop", "shuffle" }),
            });

        private static readonly ActionDescriptor Delete = new ActionDescriptor(
            "todo.delete", ActionTier.Write, "Deletes a todo.",
            new[]
            {
                new ActionParameter("id", ActionValueKind.Integer, true, "The todo id."),
                new ActionParameter("why", ActionValueKind.String, false, "A note.", isPrivate: true),
            },
            isDestructive: true);

        [Fact]
        public void Words_become_text_arguments()
        {
            var result = ActionWords.ParseArgs(new[] { "run", "music.play", "title=Rain on glass", "volume=0.5", "x=a=b", "empty=" }, 2);

            Assert.True(result.IsValid);
            Assert.Equal("Rain on glass", result.Args.GetString("title"));
            Assert.Equal("0.5", result.Args["volume"]);
            Assert.Equal("a=b", result.Args.GetString("x"));
            Assert.Equal(string.Empty, result.Args.GetString("empty"));
            Assert.Same(ActionArgs.Empty, ActionWords.ParseArgs(new[] { "run", "music.stop" }, 2).Args);
            Assert.Same(ActionArgs.Empty, ActionWords.ParseArgs(null).Args);
        }

        [Theory]
        [InlineData("title", "argument 1 is not key=value")]
        [InlineData("=Rain", "argument 1 is not key=value")]
        [InlineData(" =Rain", "argument 1 has no name before '='")]
        public void A_word_that_is_not_key_value_is_refused(string word, string problem)
        {
            var result = ActionWords.ParseArgs(new[] { word });

            Assert.False(result.IsValid);
            Assert.Equal(problem, result.Problem);
            Assert.Same(ActionArgs.Empty, result.Args);
        }

        [Fact]
        public void A_repeated_name_is_refused()
        {
            var result = ActionWords.ParseArgs(new[] { "title=a", "TITLE=b" });

            Assert.False(result.IsValid);
            Assert.Equal("the argument \"TITLE\" is given twice", result.Problem);
        }

        [Fact]
        public void The_list_shows_tier_description_and_each_parameter()
        {
            var text = ActionWords.FormatList(new[] { Play, Delete }, null);

            Assert.Equal(
                "ACTIONS  2 registered\n" +
                "  music.play  act  Plays a track by its title.\n" +
                "      title (string, required): The track title.\n" +
                "      volume (number): 0 to 1. [0..1]\n" +
                "      mode (string): Order. [values: loop|shuffle]\n" +
                "  todo.delete  write, destructive  Deletes a todo.\n" +
                "      id (integer, required): The todo id.\n" +
                "      why (string): A note. [private]",
                text);

            Assert.StartsWith("ACTIONS  1 of 2 match \"TODO\"\n  todo.delete", ActionWords.FormatList(new[] { Play, Delete }, "TODO"));
            Assert.Equal("ACTIONS  0 of 2 match \"camera\"\n  (try act list without a prefix)", ActionWords.FormatList(new[] { Play, Delete }, "camera"));
            Assert.Equal("ACTIONS  0 registered", ActionWords.FormatList(null, null));
        }

        [Fact]
        public void A_result_prints_its_status_path_message_and_data()
        {
            var ok = ActionResult.Ok("playing Rain", ActionPath.UiClick, new Dictionary<string, object?> { ["index"] = 3L, ["volume"] = 0.5, ["loop"] = true, ["next"] = null });

            Assert.Equal(
                "music.play: Ok via UiClick: playing Rain\n  index = 3\n  volume = 0.5\n  loop = true\n  next = null",
                ActionWords.FormatResult("music.play", ok));
            Assert.Equal("music.play: Denied: writes are off", ActionWords.FormatResult("music.play", ActionResult.Denied("writes are off")));
        }

        [Fact]
        public void The_tool_schema_has_everything_an_llm_tool_needs()
        {
            var tool = ActionToolSchema.Build(Play);

            Assert.Equal("music.play", tool["name"]);
            Assert.Equal("Plays a track by its title.", tool["description"]);
            Assert.Equal("act", tool["tier"]);
            Assert.Equal(false, tool["readOnly"]);
            Assert.Equal(false, tool["destructive"]);

            Assert.Equal(
                "{\"name\":\"music.play\",\"description\":\"Plays a track by its title.\",\"tier\":\"act\",\"readOnly\":false,\"destructive\":false," +
                "\"inputSchema\":{\"type\":\"object\",\"properties\":{" +
                "\"title\":{\"type\":\"string\",\"description\":\"The track title.\"}," +
                "\"volume\":{\"type\":\"number\",\"description\":\"0 to 1.\",\"minimum\":0,\"maximum\":1}," +
                "\"mode\":{\"type\":\"string\",\"description\":\"Order.\",\"enum\":[\"loop\",\"shuffle\"]}}," +
                "\"required\":[\"title\"],\"additionalProperties\":false}}",
                JsonWriter.Write(tool));
        }

        [Fact]
        public void A_destructive_write_and_a_read_carry_their_hints()
        {
            var delete = ActionToolSchema.Build(Delete);
            Assert.Equal("write", delete["tier"]);
            Assert.Equal(true, delete["destructive"]);

            var current = ActionToolSchema.Build(new ActionDescriptor("music.current", ActionTier.Read, "The current track."));
            Assert.Equal(true, current["readOnly"]);
            Assert.Equal("{\"type\":\"object\",\"properties\":{},\"required\":[],\"additionalProperties\":false}",
                JsonWriter.Write(current["inputSchema"]));

            var all = ActionToolSchema.BuildAll(new[] { Play, Delete });
            Assert.Equal(2, all.Count);
            Assert.Equal("todo.delete", all[1]["name"]);
            Assert.Empty(ActionToolSchema.BuildAll(null));
            Assert.Throws<ArgumentNullException>(() => ActionToolSchema.Build(null!));
        }
    }
}
