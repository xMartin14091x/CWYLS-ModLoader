using System.Collections.Generic;
using CwyLs.ModLoader.Core.Conditions;
using CwyLs.ModLoader.Core.Json;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>W0-CONTRACTS: the <c>when</c> grammar (AND keys, comma OR, leading '!', ranges, comparisons).</summary>
    public class ConditionsParserTests
    {
        private static ConditionSet Parse(string json) => ConditionParser.Parse(JsonReader.Parse(json));

        private static IReadOnlyList<string> Errors(string json)
        {
            Assert.False(ConditionParser.TryParse(JsonReader.Parse(json), out var set, out var errors));
            Assert.True(set.IsEmpty);
            return errors;
        }

        [Fact]
        public void Null_and_an_empty_object_are_the_empty_set()
        {
            Assert.True(ConditionParser.TryParse(null, out var none, out var noErrors));
            Assert.True(none.IsEmpty);
            Assert.Empty(noErrors);
            Assert.True(Parse("{}").IsEmpty);
            Assert.Equal("(always)", ConditionSet.Empty.ToString());
        }

        [Fact]
        public void Keys_are_clauses_in_document_order_and_commas_are_alternatives()
        {
            var set = Parse("{\"weekday\": \"Sat, Sun\", \"hour\": \"22..23\"}");

            Assert.Equal(2, set.Clauses.Count);
            var weekday = set.Clauses[0];
            Assert.Equal("weekday", weekday.Token);
            Assert.False(weekday.Negated);
            Assert.Equal(new[] { "Sat", "Sun" }, new[] { weekday.Alternatives[0].Value, weekday.Alternatives[1].Value });
            Assert.All(weekday.Alternatives, a => Assert.Equal(ConditionOperator.Equal, a.Operator));

            var hour = set.Clauses[1].Alternatives[0];
            Assert.Equal(ConditionOperator.Range, hour.Operator);
            Assert.Equal("22", hour.Value);
            Assert.Equal("23", hour.High);
            Assert.Equal("weekday: Sat, Sun; hour: 22..23", set.ToString());
        }

        [Fact]
        public void A_leading_bang_negates_the_whole_list()
        {
            var clause = Parse("{\"weekday\": \" ! Sat, Sun\"}").Clauses[0];

            Assert.True(clause.Negated);
            Assert.Equal(2, clause.Alternatives.Count);
            Assert.Equal("weekday: !Sat, Sun", clause.ToString());
        }

        [Theory]
        [InlineData(">=5", ConditionOperator.GreaterOrEqual, "5")]
        [InlineData("> 5", ConditionOperator.Greater, "5")]
        [InlineData("<=10", ConditionOperator.LessOrEqual, "10")]
        [InlineData("<30", ConditionOperator.Less, "30")]
        [InlineData("work", ConditionOperator.Equal, "work")]
        public void Comparisons_are_parsed(string text, ConditionOperator op, string operand)
        {
            Assert.True(ConditionParser.TryParseAlternative(text, out var alternative, out var error), error);
            Assert.Equal(op, alternative!.Operator);
            Assert.Equal(operand, alternative.Value);
            Assert.Null(alternative.High);
        }

        [Theory]
        [InlineData("22:00..06:00", "22:00", "06:00")]
        [InlineData("12-24..01-02", "12-24", "01-02")]
        [InlineData(" 1 .. 5 ", "1", "5")]
        public void A_range_may_run_backwards_for_midnight_and_new_year(string text, string low, string high)
        {
            Assert.True(ConditionParser.TryParseAlternative(text, out var alternative, out _));
            Assert.Equal(ConditionOperator.Range, alternative!.Operator);
            Assert.Equal(low, alternative.Value);
            Assert.Equal(high, alternative.High);
        }

        [Fact]
        public void A_date_with_hyphens_is_a_literal_not_a_range()
        {
            var alternative = Parse("{\"date\": \"2026-12-24\"}").Clauses[0].Alternatives[0];

            Assert.Equal(ConditionOperator.Equal, alternative.Operator);
            Assert.Equal("2026-12-24", alternative.Value);
        }

        [Fact]
        public void Numbers_booleans_and_lists_become_invariant_alternatives()
        {
            var set = Parse("{\"level\": 5, \"flag:metSatone\": true, \"story\": 12.5, \"language\": [\"th\", \"en\"], \"hour\": [22, \"0..5\"]}");

            Assert.Equal("5", set.Clauses[0].Alternatives[0].Value);
            Assert.Equal("true", set.Clauses[1].Alternatives[0].Value);
            Assert.Equal("12.5", set.Clauses[2].Alternatives[0].Value);
            Assert.Equal(new[] { "th", "en" }, new[] { set.Clauses[3].Alternatives[0].Value, set.Clauses[3].Alternatives[1].Value });
            Assert.Equal(ConditionOperator.Range, set.Clauses[4].Alternatives[1].Operator);
        }

        [Fact]
        public void A_list_item_is_one_alternative_even_with_a_comma()
        {
            var clause = Parse("{\"scenery\": [\"a, b\"]}").Clauses[0];

            Assert.Equal("a, b", Assert.Single(clause.Alternatives).Value);
        }

        [Fact]
        public void Keys_carry_an_argument_and_a_mod_namespace()
        {
            var set = Parse("{\"flag:met.satone-1\": \"true\", \"my.mod/mood\": \"happy\"}");

            Assert.Equal("flag", set.Clauses[0].Token);
            Assert.Equal("met.satone-1", set.Clauses[0].Argument);
            Assert.Equal("flag:met.satone-1", set.Clauses[0].Key);
            Assert.Null(set.Clauses[0].Namespace);

            Assert.Equal("my.mod/mood", set.Clauses[1].Token);
            Assert.Equal("my.mod", set.Clauses[1].Namespace);
            Assert.Equal("mood", set.Clauses[1].BareName);
        }

        [Theory]
        [InlineData("{\"weekday\": \"\"}", "empty")]
        [InlineData("{\"weekday\": \"Sat,,Sun\"}", "empty alternative")]
        [InlineData("{\"weekday\": \"Sat,\"}", "empty alternative")]
        [InlineData("{\"weekday\": \"Sat, !Sun\"}", "must come first")]
        [InlineData("{\"weekday\": \"!\"}", "negates nothing")]
        [InlineData("{\"hour\": \"..5\"}", "both ends")]
        [InlineData("{\"hour\": \"1..\"}", "both ends")]
        [InlineData("{\"hour\": \"1..2..3\"}", "more than one")]
        [InlineData("{\"level\": \">=\"}", "compares with nothing")]
        [InlineData("{\"level\": \">=1..3\"}", "mixes")]
        [InlineData("{\"level\": \">>3\"}", "mixes")]
        [InlineData("{\"level\": null}", "needs a value")]
        [InlineData("{\"level\": {\"a\": 1}}", "not an object")]
        [InlineData("{\"level\": []}", "list is empty")]
        [InlineData("{\"level\": [[1]]}", "list item")]
        [InlineData("{\"level\": [\"!1\"]}", "cannot be negated item by item")]
        [InlineData("{\"!weekday\": \"Sat\"}", "negate a condition in its value")]
        [InlineData("{\"week day\": \"Sat\"}", "not a token name")]
        [InlineData("{\"1st\": \"x\"}", "not a token name")]
        [InlineData("{\"flag:\": \"true\"}", "argument after ':'")]
        [InlineData("{\"flag:a b\": \"true\"}", "argument after ':'")]
        [InlineData("{\"Bad.Mod/x\": \"1\"}", "not a token name")]
        [InlineData("{\"weekday\": \"Sat\", \"Weekday\": \"Sun\"}", "appears twice")]
        public void Syntax_errors_name_the_key_and_the_rule(string json, string fragment)
        {
            var errors = Errors(json);

            Assert.NotEmpty(errors);
            Assert.Contains(errors, e => e.Contains(fragment));
        }

        [Fact]
        public void Every_problem_is_reported_not_just_the_first()
        {
            var errors = Errors("{\"weekday\": \"\", \"hour\": \"..\", \"level\": \"ok\"}");

            Assert.Equal(2, errors.Count);
        }

        [Fact]
        public void A_non_object_block_is_refused()
        {
            Assert.False(ConditionParser.TryParse("weekday=Sat", out _, out var errors));
            Assert.Contains("JSON object", Assert.Single(errors));
            Assert.False(ConditionParser.TryParse(new List<object?> { "x" }, out _, out _));
        }

        [Fact]
        public void Parse_throws_with_every_error_and_Read_returns_them()
        {
            var ex = Assert.Throws<ConditionSyntaxException>(() => ConditionParser.Parse(JsonReader.Parse("{\"a\": \"\", \"b\": \"x,\"}")));
            Assert.Equal(2, ex.Errors.Count);

            var bad = ConditionParser.Read(JsonReader.Parse("{\"a\": \"\"}"));
            Assert.False(bad.IsValid);
            Assert.True(bad.Conditions.IsEmpty);

            var good = ConditionParser.Read(JsonReader.Parse("{\"a\": \"x\"}"));
            Assert.True(good.IsValid);
            Assert.Equal("a: x", good.ToString());
        }

        [Fact]
        public void A_plain_dictionary_is_accepted_too()
        {
            var set = ConditionParser.Parse(new Dictionary<string, object?> { ["pomodoro"] = "work, break" });

            Assert.Equal(2, set.Clauses[0].Alternatives.Count);
        }
    }
}
