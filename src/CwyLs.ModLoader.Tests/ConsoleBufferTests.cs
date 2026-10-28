using System;
using CwyLs.ModLoader.Core.Console;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    public class ConsoleBufferTests
    {
        [Fact]
        public void Lines_come_back_in_the_order_they_were_written()
        {
            var buffer = new ConsoleBuffer();
            buffer.Add("first", ConsoleLineKind.Echo);
            buffer.Add("second");

            var lines = buffer.Snapshot();

            Assert.Equal(2, lines.Count);
            Assert.Equal("first", lines[0].Text);
            Assert.Equal(ConsoleLineKind.Echo, lines[0].Kind);
            Assert.Equal(ConsoleLineKind.Output, lines[1].Kind);
        }

        [Fact]
        public void The_ring_keeps_only_the_last_lines_but_keeps_counting()
        {
            var buffer = new ConsoleBuffer(3);
            for (var i = 0; i < 10; i++)
            {
                buffer.Add("line " + i);
            }

            Assert.Equal(3, buffer.Count);
            Assert.Equal(10L, buffer.TotalWritten);
            Assert.Equal("line 7", buffer.Snapshot()[0].Text);
        }

        [Fact]
        public void A_multi_line_result_is_split_so_it_scrolls_line_by_line()
        {
            var buffer = new ConsoleBuffer();

            buffer.Add("one\ntwo\r\nthree", ConsoleLineKind.Info);

            var lines = buffer.Snapshot();
            Assert.Equal(3, lines.Count);
            Assert.Equal("two", lines[1].Text);
            Assert.All(lines, line => Assert.Equal(ConsoleLineKind.Info, line.Kind));
        }

        [Fact]
        public void The_sequence_number_only_grows_so_the_renderer_can_see_new_output()
        {
            var buffer = new ConsoleBuffer(2);
            buffer.Add("a");
            var afterFirst = buffer.LastSequence;
            buffer.Add("b");
            buffer.Add("c");

            Assert.True(buffer.LastSequence > afterFirst);
            Assert.Equal(3L, buffer.LastSequence);
        }

        [Fact]
        public void Tail_takes_the_newest_lines_only()
        {
            var buffer = new ConsoleBuffer();
            buffer.Add("a");
            buffer.Add("b");
            buffer.Add("c");

            Assert.Equal(new[] { "b", "c" }, new[] { buffer.Tail(2)[0].Text, buffer.Tail(2)[1].Text });
            Assert.Empty(buffer.Tail(0));
            Assert.Equal(3, buffer.Tail(99).Count);
        }

        [Fact]
        public void Clearing_empties_the_view_without_resetting_the_session_count()
        {
            var buffer = new ConsoleBuffer();
            buffer.Add("a");
            buffer.Clear();

            Assert.Equal(0, buffer.Count);
            Assert.Equal(1L, buffer.TotalWritten);
            Assert.Equal(string.Empty, buffer.ToText());
        }

        [Fact]
        public void A_null_line_is_still_a_line()
        {
            var buffer = new ConsoleBuffer();
            buffer.Add(null);

            Assert.Equal(1, buffer.Count);
            Assert.Equal(string.Empty, buffer.Snapshot()[0].Text);
        }

        [Fact]
        public void A_buffer_must_keep_at_least_one_line()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new ConsoleBuffer(0));
        }
    }
}
