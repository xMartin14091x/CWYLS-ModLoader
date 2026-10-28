using System;
using CwyLs.ModLoader.Core.Actions;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>E-ACT-CORE: the schema step (argument checks) and the parameter extensions (bounds, private).</summary>
    public class ActionsValidationTests
    {
        private static readonly ActionDescriptor Volume = new ActionDescriptor(
            "music.volume", ActionTier.Act, "Sets the music volume.",
            new[]
            {
                new ActionParameter("level", ActionValueKind.Number, true, "0 to 1.", minimum: 0, maximum: 1),
                new ActionParameter("steps", ActionValueKind.Integer, false, "Fade steps.", minimum: 1, maximum: 10),
                new ActionParameter("mode", ActionValueKind.String, false, "How.", new[] { "fade", "jump" }),
                new ActionParameter("save", ActionValueKind.Boolean, false, "Persist it."),
                new ActionParameter("note", ActionValueKind.String, false, "Free text.", isPrivate: true),
            });

        private static ActionArgsValidation Check(params (string, object?)[] args) =>
            ActionArgsValidation.Check(Volume, ActionArgs.Of(args));

        [Fact]
        public void Typed_values_and_text_from_the_console_both_pass()
        {
            Assert.True(Check(("level", 0.5), ("steps", 3), ("mode", "FADE"), ("save", true)).IsValid);
            Assert.True(Check(("level", "0.5"), ("steps", "3"), ("mode", "jump"), ("save", "0")).IsValid);
            Assert.True(Check(("level", 1L), ("steps", 3.0)).IsValid);
            Assert.True(Check(("level", 0), ("note", 42)).IsValid);
        }

        [Fact]
        public void An_optional_null_is_absent_but_a_required_null_is_missing()
        {
            Assert.True(Check(("level", 0.1), ("steps", null)).IsValid);

            var missing = Check(("level", null));
            Assert.False(missing.IsValid);
            Assert.Equal("missing required argument \"level\" (number)", Assert.Single(missing.Problems));
        }

        [Theory]
        [InlineData("steps", "4.5", "\"steps\" must be a whole number")]
        [InlineData("steps", "0", "\"steps\" must be at least 1")]
        [InlineData("steps", "11", "\"steps\" must be at most 10")]
        [InlineData("mode", "slide", "\"mode\" must be one of fade, jump")]
        [InlineData("save", "yes", "\"save\" must be true or false")]
        public void Wrong_values_are_named_without_repeating_them(string name, string value, string expected)
        {
            var result = Check(("level", 0.5), (name, value));

            Assert.Equal(expected, Assert.Single(result.Problems));
            Assert.DoesNotContain(value, result.Message);
        }

        [Fact]
        public void A_number_must_parse_and_stay_in_bounds()
        {
            Assert.Equal("\"level\" must be a number", Assert.Single(Check(("level", "loud")).Problems));
            Assert.Equal("\"level\" must be at most 1", Assert.Single(Check(("level", 1.5)).Problems));
            Assert.Equal("\"level\" must be at least 0", Assert.Single(Check(("level", -0.25)).Problems));
        }

        [Fact]
        public void Unknown_names_are_refused_and_every_problem_is_listed()
        {
            var result = Check(("level", 2), ("speed", 1), ("note", "dear diary, today"));

            Assert.False(result.IsValid);
            Assert.Equal(2, result.Problems.Count);
            Assert.Contains("unknown argument \"speed\" (music.volume takes level, steps, mode, save, note)", result.Problems);
            Assert.DoesNotContain("diary", result.Message);

            var invalid = result.ToResult();
            Assert.NotNull(invalid);
            Assert.Equal(ActionStatus.InvalidArgs, invalid!.Status);
            Assert.StartsWith("music.volume: ", invalid.Message);
        }

        [Fact]
        public void An_action_without_parameters_refuses_any_argument()
        {
            var stop = new ActionDescriptor("music.stop", ActionTier.Act, "Stops.");

            Assert.True(ActionArgsValidation.Check(stop, null).IsValid);
            Assert.Null(ActionArgsValidation.Check(stop, ActionArgs.Empty).ToResult());
            Assert.Contains("music.stop takes no arguments", ActionArgsValidation.Check(stop, ActionArgs.Of(("x", 1))).Message);
            Assert.Throws<ArgumentNullException>(() => ActionArgsValidation.Check(null!, null));
        }

        [Fact]
        public void Parameters_carry_bounds_and_the_private_flag()
        {
            var level = Volume.FindParameter("level")!;
            Assert.Equal(0d, level.Minimum!.Value);
            Assert.Equal(1d, level.Maximum!.Value);
            Assert.False(level.IsPrivate);
            Assert.True(Volume.FindParameter("note")!.IsPrivate);
            Assert.Null(Volume.FindParameter("mode")!.Minimum);

            Assert.Throws<ArgumentException>(() => new ActionParameter("x", ActionValueKind.String, false, "d", minimum: 0));
            Assert.Throws<ArgumentException>(() => new ActionParameter("x", ActionValueKind.Boolean, false, "d", maximum: 1));
            Assert.Throws<ArgumentException>(() => new ActionParameter("x", ActionValueKind.Number, false, "d", minimum: 2, maximum: 1));
            Assert.Throws<ArgumentException>(() => new ActionParameter("x", ActionValueKind.Number, false, "d", minimum: double.NaN));
            Assert.Throws<ArgumentException>(() => new ActionParameter("x", ActionValueKind.Integer, false, "d", maximum: double.PositiveInfinity));
        }

        [Theory]
        [InlineData(ActionValueKind.String, "string")]
        [InlineData(ActionValueKind.Integer, "integer")]
        [InlineData(ActionValueKind.Number, "number")]
        [InlineData(ActionValueKind.Boolean, "boolean")]
        public void Kinds_have_their_json_type_names(ActionValueKind kind, string json) =>
            Assert.Equal(json, ActionArgsValidation.Kind(kind));
    }
}
