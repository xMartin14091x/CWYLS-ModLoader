using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CwyLs.ModLoader.Core.Ui;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// S-UI chat model - what a mod's chat window needs: append a bubble and stream
    /// text into it token by token from any thread, a typing indicator, bubble buttons ("replay
    /// voice"), auto-scroll requests, and a cap that drops the oldest bubbles.
    /// </summary>
    public class UiChatModelTests
    {
        private readonly List<string> _ran = new List<string>();

        private ChatViewModel Chat(int max = 200) =>
            new ChatViewModel(new CwyChatSpec("log") { MaxBubbles = max }, (label, handler) =>
            {
                _ran.Add(label);
                handler();
                return true;
            });

        [Fact]
        public void A_reply_streams_into_its_bubble()
        {
            var chat = Chat();
            var bubble = chat.Add(CwyChatRole.Character);
            bubble.Typing = true;
            foreach (var token in new[] { "สวัส", "ดี", "ค่ะ" })
            {
                bubble.Append(token);
            }

            bubble.Typing = false;
            Assert.Equal("สวัสดีค่ะ", bubble.Text);
            var state = chat.Snapshot().Single();
            Assert.Equal("สวัสดีค่ะ", state.Text);
            Assert.False(state.Typing);
            Assert.Equal(CwyChatRole.Character, state.Role);
        }

        [Fact]
        public void Every_change_moves_the_part_and_the_bubble_version()
        {
            var chat = Chat();
            var bubble = chat.Add(CwyChatRole.Character, "a");
            var partVersion = chat.Version;
            var bubbleVersion = chat.Snapshot()[0].Version;

            bubble.Append("b");
            Assert.True(chat.Version > partVersion);
            Assert.True(chat.Snapshot()[0].Version > bubbleVersion);

            partVersion = chat.Version;
            bubble.Append(string.Empty);
            bubble.Typing = false;
            Assert.Equal(partVersion, chat.Version);
        }

        [Fact]
        public void Ids_increase_and_the_cap_drops_the_oldest()
        {
            var chat = Chat(max: 2);
            var first = chat.Add(CwyChatRole.Player, "1");
            var second = chat.Add(CwyChatRole.Character, "2");
            var third = chat.Add(CwyChatRole.System, "3");

            Assert.True(second.Id > first.Id && third.Id > second.Id);
            Assert.Equal(2, chat.Count);
            Assert.False(first.IsAlive);
            Assert.True(second.IsAlive);
            Assert.Equal(new[] { second.Id, third.Id }, chat.Snapshot().Select(s => s.Id));

            // A dropped bubble ignores late tokens.
            first.Append("late");
            Assert.Equal("1", first.Text);
        }

        [Fact]
        public void Clear_kills_every_bubble()
        {
            var chat = Chat();
            var bubble = chat.Add(CwyChatRole.Character, "x");
            chat.Clear();
            Assert.Equal(0, chat.Count);
            Assert.False(bubble.IsAlive);
            Assert.Empty(chat.Bubbles);
        }

        [Fact]
        public void A_player_bubble_jumps_to_the_end_a_character_bubble_does_not()
        {
            var chat = Chat();
            chat.Add(CwyChatRole.Character, "hi");
            Assert.False(chat.TakeScrollToEnd());
            chat.Add(CwyChatRole.Player, "hello");
            Assert.True(chat.TakeScrollToEnd());
            Assert.False(chat.TakeScrollToEnd());
            chat.ScrollToEnd();
            Assert.True(chat.TakeScrollToEnd());
        }

        [Fact]
        public void Text_past_the_limit_is_dropped_and_flagged()
        {
            var chat = Chat();
            var bubble = chat.Add(CwyChatRole.Character, new string('a', ChatViewModel.MaxBubbleChars - 2));
            Assert.False(bubble.IsTruncated);
            bubble.Append("bcdef");
            Assert.True(bubble.IsTruncated);
            Assert.Equal(ChatViewModel.MaxBubbleChars, bubble.Text.Length);
            Assert.EndsWith("bc", bubble.Text);

            bubble.SetText("fresh");
            Assert.False(bubble.IsTruncated);
            Assert.Equal("fresh", bubble.Text);
        }

        [Fact]
        public void The_limit_never_splits_a_surrogate_pair()
        {
            var chat = Chat();
            var bubble = chat.Add(CwyChatRole.Character, new string('a', ChatViewModel.MaxBubbleChars - 1));
            bubble.Append("\U0001F600");
            Assert.Equal(ChatViewModel.MaxBubbleChars - 1, bubble.Text.Length);
            Assert.True(bubble.IsTruncated);
        }

        [Fact]
        public void Carriage_returns_are_dropped_line_breaks_kept()
        {
            var chat = Chat();
            var bubble = chat.Add(CwyChatRole.Player, "a\r\nb");
            Assert.Equal("a\nb", bubble.Text);
        }

        [Fact]
        public void Bubble_buttons_add_relabel_remove_and_click()
        {
            var chat = Chat();
            var bubble = chat.Add(CwyChatRole.Character, "line");
            ICwyChatBubble? clickedBubble = null;
            string? clickedButton = null;
            chat.ButtonClicked += (b, id) =>
            {
                clickedBubble = b;
                clickedButton = id;
            };

            bubble.AddButton("replay", "replay");
            bubble.AddButton("REPLAY", "again");
            Assert.Single(bubble.Buttons);
            Assert.Equal("again", bubble.Buttons[0].Label);
            Assert.Equal("replay", chat.Snapshot()[0].Buttons[0].Id);

            chat.Click(bubble.Id, "replay");
            Assert.Same(bubble, clickedBubble);
            Assert.Equal("replay", clickedButton);
            Assert.Contains("log ButtonClicked", _ran);

            Assert.True(bubble.RemoveButton("replay"));
            Assert.False(bubble.RemoveButton("replay"));
            Assert.Empty(bubble.Buttons);
        }

        [Fact]
        public void Bubble_button_rules()
        {
            var chat = Chat();
            var bubble = chat.Add(CwyChatRole.Character, "line");
            Assert.Throws<ArgumentException>(() => bubble.AddButton("no spaces", "x"));
            for (var i = 0; i < ChatViewModel.MaxBubbleButtons; i++)
            {
                bubble.AddButton("b" + i, "b");
            }

            Assert.Throws<InvalidOperationException>(() => bubble.AddButton("one-more", "x"));
            Assert.Equal(ChatViewModel.MaxBubbleButtons, bubble.Buttons.Count);
        }

        [Fact]
        public void A_click_on_a_dropped_bubble_does_nothing()
        {
            var chat = Chat(max: 1);
            var old = chat.Add(CwyChatRole.Character, "old");
            chat.Add(CwyChatRole.Character, "new");
            var clicks = 0;
            chat.ButtonClicked += (_, __) => clicks++;
            chat.Click(old.Id, "replay");
            Assert.Equal(0, clicks);
        }

        [Fact]
        public void The_tag_carries_the_mods_own_data()
        {
            var chat = Chat();
            var bubble = chat.Add(CwyChatRole.Character);
            bubble.Tag = "clip-17";
            Assert.Equal("clip-17", chat.Bubbles[0].Tag);
        }

        [Fact]
        public void Streaming_from_many_threads_loses_nothing()
        {
            var chat = Chat();
            var bubbles = Enumerable.Range(0, 4).Select(_ => chat.Add(CwyChatRole.Character)).ToArray();

            Parallel.For(0, 4, i =>
            {
                for (var t = 0; t < 500; t++)
                {
                    bubbles[i].Append("x");
                    if (t % 50 == 0)
                    {
                        chat.Snapshot();
                    }
                }
            });

            Assert.All(bubbles, b => Assert.Equal(500, b.Text.Length));
            Assert.All(chat.Snapshot(), s => Assert.Equal(500, s.Text.Length));
        }

        [Fact]
        public void Following_is_what_the_view_reported()
        {
            var chat = Chat();
            Assert.True(chat.IsFollowing);
            chat.ReportFollowing(false);
            Assert.False(chat.IsFollowing);
        }
    }
}
