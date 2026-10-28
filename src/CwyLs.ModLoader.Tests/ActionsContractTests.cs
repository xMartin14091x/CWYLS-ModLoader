using System;
using System.Collections.Generic;
using CwyLs.ModLoader.Core.Actions;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>W0-CONTRACTS: action descriptors, parameters, arguments and results.</summary>
    public class ActionsContractTests
    {
        [Theory]
        [InlineData("music.play", true)]
        [InlineData("music.play_by_title", true)]
        [InlineData("my.mod.greet", true)]
        [InlineData("todo.add-2", true)]
        [InlineData("music", false)]
        [InlineData("Music.Play", false)]
        [InlineData("music..play", false)]
        [InlineData(".play", false)]
        [InlineData("music.", false)]
        [InlineData("music play", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void Names_are_lower_case_dotted_segments(string? name, bool valid) =>
            Assert.Equal(valid, ActionDescriptor.IsValidName(name));

        [Fact]
        public void A_name_longer_than_the_limit_is_refused()
        {
            var name = "a." + new string('b', ActionDescriptor.MaxNameLength);

            Assert.False(ActionDescriptor.IsValidName(name));
        }

        [Fact]
        public void The_family_is_everything_before_the_last_dot()
        {
            Assert.Equal("music", new ActionDescriptor("music.play", ActionTier.Act, "Plays a track.").Family);
            Assert.Equal("my.mod", new ActionDescriptor("my.mod.greet", ActionTier.Act, "Greets.").Family);
        }

        [Fact]
        public void A_descriptor_refuses_what_cannot_be_published()
        {
            Assert.Throws<ArgumentException>(() => new ActionDescriptor("Music.Play", ActionTier.Act, "d"));
            Assert.Throws<ArgumentException>(() => new ActionDescriptor("music.play", (ActionTier)9, "d"));
            Assert.Throws<ArgumentException>(() => new ActionDescriptor("music.play", ActionTier.Act, " "));
            Assert.Throws<ArgumentException>(() => new ActionDescriptor("music.current", ActionTier.Read, "d", isDestructive: true));
            Assert.Throws<ArgumentException>(() => new ActionDescriptor(
                "music.play", ActionTier.Act, "d",
                new[]
                {
                    new ActionParameter("title", ActionValueKind.String, true, "t"),
                    new ActionParameter("Title", ActionValueKind.String, false, "t"),
                }));
        }

        [Fact]
        public void Parameters_are_found_case_insensitively_and_keep_their_order()
        {
            var descriptor = new ActionDescriptor(
                "music.play", ActionTier.Act, "Plays a track.",
                new[]
                {
                    new ActionParameter("title", ActionValueKind.String, true, "The track title."),
                    new ActionParameter("volume", ActionValueKind.Number, false, "0 to 1."),
                });

            Assert.Equal("title", descriptor.Parameters[0].Name);
            Assert.Same(descriptor.Parameters[1], descriptor.FindParameter("VOLUME"));
            Assert.Null(descriptor.FindParameter("speed"));
            Assert.Equal("music.play (Act)", descriptor.ToString());
        }

        [Fact]
        public void A_parameter_checks_its_name_description_and_allowed_values()
        {
            Assert.Throws<ArgumentException>(() => new ActionParameter("1x", ActionValueKind.String, false, "d"));
            Assert.Throws<ArgumentException>(() => new ActionParameter("x", ActionValueKind.String, false, ""));
            Assert.Throws<ArgumentException>(() => new ActionParameter("x", ActionValueKind.Integer, false, "d", new[] { "a" }));
            Assert.Throws<ArgumentException>(() => new ActionParameter("x", ActionValueKind.String, false, "d", new[] { "a", "A" }));
            Assert.Throws<ArgumentException>(() => new ActionParameter("x", ActionValueKind.String, false, "d", new string[0]));

            var mode = new ActionParameter("mode", ActionValueKind.String, false, "d", new[] { "loop", "shuffle" });
            Assert.Equal(new[] { "loop", "shuffle" }, mode.AllowedValues);
        }

        [Fact]
        public void Args_normalise_numbers_and_read_text_in_invariant_culture()
        {
            var args = ActionArgs.Of(("count", 3), ("ratio", 0.25f), ("title", "Rain"), ("on", true), ("n", "42"), ("x", "1.5"), ("none", null));

            Assert.Equal(7, args.Count);
            Assert.IsType<long>(args["count"]);
            Assert.IsType<double>(args["ratio"]);
            Assert.True(args.TryGetInt64("COUNT", out var count));
            Assert.Equal(3, count);
            Assert.True(args.TryGetInt64("n", out var n));
            Assert.Equal(42, n);
            Assert.False(args.TryGetInt64("x", out _));
            Assert.True(args.TryGetDouble("x", out var x));
            Assert.Equal(1.5, x);
            Assert.True(args.TryGetBoolean("on", out var on));
            Assert.True(on);
            Assert.Equal("0.25", args.GetString("ratio"));
            Assert.Equal("true", args.GetString("on"));
            Assert.Null(args.GetString("none"));
            Assert.True(args.ContainsKey("none"));
            Assert.Null(args.GetString("missing"));
            Assert.Equal("count=3 ratio=0.25 title=Rain on=true n=42 x=1.5 none=null", args.ToString());
        }

        [Theory]
        [InlineData("true", true, true)]
        [InlineData("FALSE", true, false)]
        [InlineData("1", true, true)]
        [InlineData("0", true, false)]
        [InlineData("yes", false, false)]
        public void Boolean_text_is_strict(string text, bool parses, bool expected)
        {
            var args = ActionArgs.Of(("b", text));

            Assert.Equal(parses, args.TryGetBoolean("b", out var value));
            Assert.Equal(expected, value);
        }

        [Fact]
        public void Args_refuse_duplicates_blank_names_and_non_json_values()
        {
            Assert.Throws<ArgumentException>(() => ActionArgs.Of(("a", 1), ("A", 2)));
            Assert.Throws<ArgumentException>(() => ActionArgs.Of((" ", 1)));
            Assert.Throws<ArgumentException>(() => ActionArgs.Of(("a", new object())));
            Assert.Throws<ArgumentException>(() => ActionArgs.Of(("a", float.NaN)));
            Assert.Same(ActionArgs.Empty, ActionArgs.From(null));
            Assert.Same(ActionArgs.Empty, ActionArgs.From(new List<KeyValuePair<string, object?>>()));
        }

        [Fact]
        public void Results_carry_status_path_and_message()
        {
            var ok = ActionResult.Ok("playing \"Rain\"", ActionPath.UiClick, new Dictionary<string, object?> { ["index"] = 3L });
            Assert.True(ok.IsOk);
            Assert.Equal(ActionPath.UiClick, ok.Path);
            Assert.Equal(3L, ok.Data["index"]);
            Assert.Equal("Ok via UiClick: playing \"Rain\"", ok.ToString());

            Assert.Throws<ArgumentException>(() => ActionResult.Ok("x", ActionPath.None));
            Assert.Throws<ArgumentException>(() => ActionResult.Denied(" "));
            Assert.Equal(ActionStatus.NotFound, ActionResult.NotFound("music.nope").Status);
            Assert.Equal("no action named \"music.nope\"", ActionResult.NotFound("music.nope").Message);
            Assert.Empty(ActionResult.Failed("x").Data);
            Assert.False(ActionResult.NotAvailable("x").IsOk);
            Assert.Equal("Denied: writes are off", ActionResult.Denied("writes are off").ToString());
        }

        [Fact]
        public void A_request_needs_a_name_and_a_caller_id()
        {
            Assert.Throws<ArgumentException>(() => new ActionRequest(" ", null, ActionCaller.Console, "console"));
            Assert.Throws<ArgumentException>(() => new ActionRequest("music.play", null, ActionCaller.Console, " "));
            Assert.Throws<ArgumentException>(() => new ActionRequest("music.play", null, (ActionCaller)9, "x"));

            var request = new ActionRequest(" music.play ", null, ActionCaller.Api, "cwy-mcp", "req-1");
            Assert.Equal("music.play", request.Name);
            Assert.Same(ActionArgs.Empty, request.Args);
            Assert.Equal("music.play by Api(cwy-mcp)", request.ToString());

            var invocation = new ActionInvocation(new ActionDescriptor("music.play", ActionTier.Act, "d"), request, confirmed: false);
            Assert.Equal(ActionCaller.Api, invocation.Caller);
            Assert.Same(request.Args, invocation.Args);
        }
    }
}
