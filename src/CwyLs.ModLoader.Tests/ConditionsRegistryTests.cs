using System;
using System.Collections.Generic;
using CwyLs.ModLoader.Core.Conditions;
using CwyLs.ModLoader.Core.Json;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>W0-CONTRACTS: the token table, the registry's ownership rules and the "not available" evaluator.</summary>
    public class ConditionsRegistryTests
    {
        private sealed class FixedSource : IConditionSource
        {
            public bool TryGetValues(ConditionTokenInfo token, string? argument, out IReadOnlyList<string> values)
            {
                values = new[] { "x" };
                return true;
            }
        }

        private sealed class ThrowingEvaluator : IConditionEvaluator
        {
            public ConditionResult Evaluate(ConditionSet conditions) => throw new InvalidOperationException("boom");
        }

        private sealed class MetEvaluator : IConditionEvaluator
        {
            public ConditionResult Evaluate(ConditionSet conditions) => ConditionResult.Met;
        }

        private static ConditionSet Set(string json) => ConditionParser.Parse(JsonReader.Parse(json));

        [Fact]
        public void The_built_in_table_has_the_planned_tokens_once_each()
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var token in ConditionTokens.BuiltIn)
            {
                Assert.True(names.Add(token.Name), token.Name + " repeats");
                Assert.Null(token.Namespace);
                Assert.False(string.IsNullOrWhiteSpace(token.Description));
            }

            foreach (var planned in new[] { "time", "hour", "weekday", "date", "pomodoro", "language", "scenery", "timeOfDay", "story", "level", "hasPack", "flag", "random" })
            {
                Assert.True(ConditionTokens.IsBuiltIn(planned), planned);
            }

            Assert.True(ConditionTokens.TryGetBuiltIn("WEEKDAY", out var weekday));
            Assert.Equal(UpdatePoint.DayStarted, weekday!.UpdatePoints);
            Assert.True(ConditionTokens.TryGetBuiltIn("flag", out var flag));
            Assert.Equal(ConditionArgumentRule.Required, flag!.Argument);
            Assert.False(ConditionTokens.IsBuiltIn("nope"));
        }

        [Theory]
        [InlineData("weekday", true, null, "weekday")]
        [InlineData("my.mod/mood", true, "my.mod", "mood")]
        [InlineData("My.Mod/mood", false, null, "")]
        [InlineData("my.mod/", false, null, "")]
        [InlineData("/mood", false, null, "")]
        [InlineData("a/b/c", false, null, "")]
        [InlineData("_x", false, null, "")]
        [InlineData("", false, null, "")]
        public void Token_keys_follow_the_name_rule(string key, bool valid, string? ns, string name)
        {
            Assert.Equal(valid, ConditionTokenInfo.IsValidKey(key, out var gotNs, out var gotName));
            Assert.Equal(ns, gotNs);
            Assert.Equal(name, gotName);
        }

        [Fact]
        public void A_token_needs_a_description_and_a_valid_name()
        {
            Assert.Throws<ArgumentException>(() => new ConditionTokenInfo("x", ConditionValueKind.Text, UpdatePoint.None, " "));
            Assert.Throws<ArgumentException>(() => new ConditionTokenInfo("1x", ConditionValueKind.Text, UpdatePoint.None, "d"));
        }

        [Fact]
        public void The_framework_registers_bare_names_and_a_mod_only_its_own_namespace()
        {
            var registry = new ConditionRegistry();
            var source = new FixedSource();

            Assert.True(registry.TryRegister("cwy", ConditionTokens.BuiltIn[0], source, out _, out _));
            Assert.True(registry.TryRegister("my.mod", new ConditionTokenInfo("my.mod/mood", ConditionValueKind.Text, UpdatePoint.ModToken, "d"), source, out _, out _));

            Assert.False(registry.TryRegister("my.mod", new ConditionTokenInfo("hour", ConditionValueKind.Integer, UpdatePoint.None, "d"), source, out _, out var bare));
            Assert.Contains("my.mod/<name>", bare);
            Assert.False(registry.TryRegister("my.mod", new ConditionTokenInfo("other.mod/mood", ConditionValueKind.Text, UpdatePoint.None, "d"), source, out _, out _));
            Assert.False(registry.TryRegister("cwy", new ConditionTokenInfo("my.mod/x", ConditionValueKind.Text, UpdatePoint.None, "d"), source, out _, out var prefixed));
            Assert.Contains("no \"<modId>/\" prefix", prefixed);
            Assert.False(registry.TryRegister("Not An Id", new ConditionTokenInfo("x", ConditionValueKind.Text, UpdatePoint.None, "d"), source, out _, out _));
        }

        [Fact]
        public void A_duplicate_token_is_refused_until_the_first_is_disposed()
        {
            var registry = new ConditionRegistry();
            var info = new ConditionTokenInfo("weekday", ConditionValueKind.Weekday, UpdatePoint.DayStarted, "d");

            Assert.True(registry.TryRegister("cwy", info, new FixedSource(), out var first, out _));
            Assert.False(registry.TryRegister("cwy", new ConditionTokenInfo("WEEKDAY", ConditionValueKind.Text, UpdatePoint.None, "d"), new FixedSource(), out var second, out var problem));
            Assert.Null(second);
            Assert.Contains("already registered by cwy", problem);
            Assert.Throws<InvalidOperationException>(() => registry.Register("cwy", info, new FixedSource()));

            first!.Dispose();
            first.Dispose();
            Assert.False(registry.TryGetToken("weekday", out _));
            Assert.True(registry.TryRegister("cwy", info, new FixedSource(), out _, out _));
        }

        [Fact]
        public void Without_an_evaluator_only_the_empty_set_is_met()
        {
            var registry = new ConditionRegistry();

            Assert.False(registry.HasEvaluator);
            Assert.Same(UnavailableConditionEvaluator.Instance, registry.Evaluator);
            Assert.True(registry.Evaluate(ConditionSet.Empty).IsMet);
            Assert.True(registry.Evaluate(null).IsMet);

            var result = registry.Evaluate(Set("{\"weekday\": \"Sat\"}"));
            Assert.Equal(ConditionOutcome.Unavailable, result.Outcome);
            Assert.False(result.IsMet);
            Assert.Equal(UnavailableConditionEvaluator.Reason, result.Reason);
            Assert.Equal("weekday", result.Clause);
        }

        [Fact]
        public void An_installed_evaluator_answers_and_a_throwing_one_is_contained()
        {
            var registry = new ConditionRegistry();
            var sunk = new List<string>();
            registry.ErrorSink = sunk.Add;

            registry.UseEvaluator(new MetEvaluator());
            Assert.True(registry.HasEvaluator);
            Assert.True(registry.Evaluate(Set("{\"weekday\": \"Sat\"}")).IsMet);

            registry.UseEvaluator(new ThrowingEvaluator());
            var result = registry.Evaluate(Set("{\"weekday\": \"Sat\"}"));
            Assert.Equal(ConditionOutcome.Invalid, result.Outcome);
            Assert.Contains("boom", result.Reason);
            Assert.Contains("boom", Assert.Single(sunk));

            Assert.Throws<ArgumentNullException>(() => registry.UseEvaluator(null!));
        }

        [Fact]
        public void Update_points_are_the_union_and_an_unknown_token_is_All()
        {
            var registry = new ConditionRegistry();
            foreach (var token in ConditionTokens.BuiltIn)
            {
                registry.Register("cwy", token, new FixedSource());
            }

            Assert.Equal(UpdatePoint.None, registry.UpdatePointsOf(ConditionSet.Empty));
            Assert.Equal(
                UpdatePoint.DayStarted | UpdatePoint.PomodoroChanged,
                registry.UpdatePointsOf(Set("{\"weekday\": \"Sat\", \"pomodoro\": \"work\"}")));
            Assert.Equal(UpdatePoint.All, registry.UpdatePointsOf(Set("{\"nobody\": \"x\"}")));
        }

        [Fact]
        public void Validate_reports_unknown_tokens_and_argument_rules()
        {
            var registry = new ConditionRegistry();
            foreach (var token in ConditionTokens.BuiltIn)
            {
                registry.Register("cwy", token, new FixedSource());
            }

            Assert.Empty(registry.Validate(Set("{\"weekday\": \"Sat\", \"flag:met\": \"true\"}")));

            var problems = registry.Validate(Set("{\"nobody\": \"x\", \"flag\": \"true\", \"hour:x\": \"1\"}"));
            Assert.Equal(3, problems.Count);
            Assert.Contains(problems, p => p.Contains("no token named"));
            Assert.Contains(problems, p => p.Contains("needs an argument"));
            Assert.Contains(problems, p => p.Contains("takes no argument"));
        }

        [Fact]
        public void Changed_reaches_every_subscriber_even_when_one_throws()
        {
            var registry = new ConditionRegistry();
            var sunk = new List<string>();
            registry.ErrorSink = sunk.Add;
            var seen = new List<ConditionsChanged>();

            using (registry.Subscribe(_ => throw new InvalidOperationException("bad handler")))
            using (registry.Subscribe(seen.Add))
            {
                registry.RaiseChanged(new ConditionsChanged(UpdatePoint.FlagChanged, "flag:met"));
            }

            registry.RaiseChanged(new ConditionsChanged(UpdatePoint.Manual));

            var change = Assert.Single(seen);
            Assert.Equal(UpdatePoint.FlagChanged, change.Point);
            Assert.Equal("FlagChanged flag:met", change.Describe());
            Assert.Equal("FlagChanged", change.Fields()["point"]);
            Assert.Contains("bad handler", Assert.Single(sunk));
            Assert.Throws<ArgumentNullException>(() => registry.RaiseChanged(null!));
        }

        [Fact]
        public void Results_other_than_Met_need_a_reason()
        {
            Assert.Throws<ArgumentException>(() => ConditionResult.NotMet(" "));
            Assert.Equal("NotMet: weekday is Mon", ConditionResult.NotMet("weekday is Mon").ToString());
            Assert.Equal("Met", ConditionResult.Met.ToString());
        }
    }
}
