using CwyLs.ModLoader.Core.Console;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    public class ConsoleParserTests
    {
        [Fact]
        public void A_plain_line_splits_into_a_name_and_arguments()
        {
            var parsed = CommandLineParser.Parse("packs   list  cwy.example");

            Assert.NotNull(parsed);
            Assert.Equal("packs", parsed!.Name);
            Assert.Equal(new[] { "list", "cwy.example" }, parsed.Arguments);
            Assert.Equal("packs   list  cwy.example", parsed.Raw);
        }

        [Fact]
        public void A_blank_line_is_not_a_command()
        {
            Assert.Null(CommandLineParser.Parse(null));
            Assert.Null(CommandLineParser.Parse("   \t "));
            Assert.Empty(CommandLineParser.Tokenize(string.Empty));
        }

        [Fact]
        public void Double_quotes_keep_spaces_in_one_argument()
        {
            var tokens = CommandLineParser.Tokenize("subtitle \"hello there\" th");

            Assert.Equal(new[] { "subtitle", "hello there", "th" }, tokens);
        }

        [Fact]
        public void An_empty_quoted_argument_survives_as_a_token()
        {
            var tokens = CommandLineParser.Tokenize("log \"\" after");

            Assert.Equal(new[] { "log", string.Empty, "after" }, tokens);
        }

        [Fact]
        public void A_backslash_escapes_a_quote_and_a_backslash()
        {
            var tokens = CommandLineParser.Tokenize("log \"she said \\\"hi\\\"\" c:\\\\temp");

            Assert.Equal(new[] { "log", "she said \"hi\"", "c:\\temp" }, tokens);
        }

        [Fact]
        public void Single_quotes_are_literal_inside_so_a_path_stays_readable()
        {
            var tokens = CommandLineParser.Tokenize("tex list 'C:\\Program Files\\game'");

            Assert.Equal(new[] { "tex", "list", "C:\\Program Files\\game" }, tokens);
        }

        [Fact]
        public void An_unterminated_quote_is_tolerated_but_reported()
        {
            var ok = CommandLineParser.TryTokenize("say \"never closed", out var tokens, out var error);

            Assert.False(ok);
            Assert.Equal(CommandLineParser.UnterminatedQuoteError, error);
            Assert.Equal(new[] { "say", "never closed" }, tokens);
        }

        [Fact]
        public void A_line_ending_in_a_lone_backslash_is_reported_not_thrown()
        {
            var ok = CommandLineParser.TryTokenize("log tail\\", out var tokens, out var error);

            Assert.False(ok);
            Assert.Equal(CommandLineParser.DanglingEscapeError, error);
            Assert.Equal(new[] { "log", "tail" }, tokens);
        }

        [Fact]
        public void Join_rebuilds_free_text_from_the_tokens_after_the_name()
        {
            var parsed = CommandLineParser.Parse("subtitle sawatdee khrap th");

            Assert.Equal("sawatdee khrap th", CommandLineParser.Join(parsed!.Arguments));
            Assert.Equal("khrap th", CommandLineParser.Join(parsed.Arguments, 1));
            Assert.Equal(string.Empty, CommandLineParser.Join(parsed.Arguments, 9));
            Assert.Equal(string.Empty, CommandLineParser.Join(null));
        }
    }
}
