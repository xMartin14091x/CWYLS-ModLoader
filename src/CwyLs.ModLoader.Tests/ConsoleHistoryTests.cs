using System;
using CwyLs.ModLoader.Core.Console;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    public class ConsoleHistoryTests
    {
        [Fact]
        public void Up_walks_back_through_the_lines_newest_first()
        {
            var history = new CommandHistory();
            history.Add("packs list");
            history.Add("smoke run");

            Assert.Equal("smoke run", history.Previous(string.Empty));
            Assert.Equal("packs list", history.Previous(string.Empty));
        }

        [Fact]
        public void Up_at_the_oldest_line_stays_there()
        {
            var history = new CommandHistory();
            history.Add("one");

            Assert.Equal("one", history.Previous(string.Empty));
            Assert.Equal("one", history.Previous(string.Empty));
        }

        [Fact]
        public void Down_past_the_newest_line_restores_the_half_typed_draft()
        {
            var history = new CommandHistory();
            history.Add("keys");

            Assert.Equal("keys", history.Previous("tex ap"));
            Assert.Equal("tex ap", history.Next());
            Assert.Null(history.Next());
        }

        [Fact]
        public void An_empty_history_has_nothing_to_offer()
        {
            var history = new CommandHistory();

            Assert.Null(history.Previous("draft"));
            Assert.Null(history.Next());
            Assert.Equal(0, history.Count);
        }

        [Fact]
        public void A_blank_line_and_a_repeat_of_the_last_line_are_not_recorded()
        {
            var history = new CommandHistory();

            Assert.True(history.Add("smoke run"));
            Assert.False(history.Add("smoke run"));
            Assert.False(history.Add("   "));
            Assert.False(history.Add(null));
            Assert.Equal(1, history.Count);
        }

        [Fact]
        public void The_ring_drops_the_oldest_line_when_it_is_full()
        {
            var history = new CommandHistory(2);
            history.Add("a");
            history.Add("b");
            history.Add("c");

            Assert.Equal(2, history.Count);
            Assert.Equal(new[] { "b", "c" }, history.Entries);
        }

        [Fact]
        public void Adding_a_line_puts_the_cursor_back_on_the_live_line()
        {
            var history = new CommandHistory();
            history.Add("a");
            history.Previous(string.Empty);

            history.Add("b");

            Assert.Equal("b", history.Previous(string.Empty));
        }

        [Fact]
        public void A_history_must_keep_at_least_one_line()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new CommandHistory(0));
        }
    }
}
