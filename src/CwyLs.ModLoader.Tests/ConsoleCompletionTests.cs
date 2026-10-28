using CwyLs.ModLoader.Core.Console;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    public class ConsoleCompletionTests
    {
        private static readonly string[] Commands =
        {
            "help", "packs", "smoke", "targets", "keys", "trace", "bus", "log", "clear", "masterdata"
        };

        [Fact]
        public void A_prefix_with_one_match_completes_outright()
        {
            var result = Completion.Complete("ta", Commands);

            Assert.True(result.IsUnique);
            Assert.False(result.IsFuzzy);
            Assert.Equal("targets", result.Text);
        }

        [Fact]
        public void Several_prefix_matches_complete_to_what_they_share()
        {
            var result = Completion.Complete("t", new[] { "trace", "track", "trap" });

            Assert.Equal(3, result.Matches.Count);
            Assert.Equal("tra", result.Text);
        }

        [Fact]
        public void An_empty_prefix_offers_everything_sorted()
        {
            var result = Completion.Complete(string.Empty, new[] { "smoke", "bus", "keys" });

            Assert.Equal(new[] { "bus", "keys", "smoke" }, result.Matches);
            Assert.False(result.IsFuzzy);
        }

        [Fact]
        public void Matching_ignores_case()
        {
            var result = Completion.Complete("PA", Commands);

            Assert.Equal(new[] { "packs" }, result.Matches);
        }

        [Fact]
        public void Fuzzy_matching_only_runs_when_no_prefix_matches()
        {
            var result = Completion.Complete("mdt", Commands);

            Assert.True(result.IsFuzzy);
            Assert.Equal(new[] { "masterdata" }, result.Matches);
            Assert.Equal("masterdata", result.Text);
        }

        [Fact]
        public void Several_fuzzy_matches_leave_the_typed_word_alone()
        {
            var result = Completion.Complete("as", new[] { "packs", "targets", "help" });

            Assert.True(result.IsFuzzy);
            Assert.Equal(2, result.Matches.Count);
            Assert.Equal("as", result.Text);
        }

        [Fact]
        public void Nothing_matching_leaves_the_input_untouched()
        {
            var result = Completion.Complete("zzz", Commands);

            Assert.True(result.IsEmpty);
            Assert.Equal(string.Empty, result.Text);
            Assert.True(Completion.Complete("x", null).IsEmpty);
        }

        [Fact]
        public void A_fuzzy_match_needs_the_letters_in_order()
        {
            Assert.True(Completion.IsFuzzyMatch("mdt", "masterdata"));
            Assert.False(Completion.IsFuzzyMatch("tdm", "masterdata"));
            Assert.False(Completion.IsFuzzyMatch("masterdataa", "masterdata"));
        }

        [Fact]
        public void The_common_prefix_is_never_shorter_than_what_was_typed()
        {
            Assert.Equal("keys", Completion.CommonPrefix(new[] { "keys" }, "ke"));
            Assert.Equal("ke", Completion.CommonPrefix(new[] { "keys", "kettle" }, "ke"));
            Assert.Equal("ab", Completion.CommonPrefix(null, "ab"));
        }
    }
}

namespace CwyLs.ModLoader.Tests
{
    public class ConsoleUsageHintTests
    {
        [Fact]
        public void The_literal_words_of_a_usage_line_become_argument_completions()
        {
            Assert.Equal(new[] { "list", "rescan" }, UsageHints.Parse("packs list|rescan"));
            Assert.Equal(
                new[] { "level", "off", "events", "trace" },
                UsageHints.Parse("trace level off|events|trace"));
        }

        [Fact]
        public void A_placeholder_is_a_value_the_user_types_so_it_is_never_offered()
        {
            Assert.Equal(new[] { "play" }, UsageHints.Parse("voice play <name>"));
            Assert.Empty(UsageHints.Parse("log <text>"));
            Assert.Equal(new[] { "last" }, UsageHints.Parse("events last [n]"));
            Assert.Empty(UsageHints.Parse(null));
        }
    }
}
