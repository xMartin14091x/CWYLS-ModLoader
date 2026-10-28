using CwyLs.ModLoader.Core.Ui;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>S-UI: the window body's stack layout, the chat bubble geometry and the typing dots.</summary>
    public class UiLayoutTests
    {
        [Fact]
        public void Fixed_parts_keep_their_height_and_fill_parts_share_the_rest()
        {
            // tabs 40, chat fill, input 96, buttons 44 in a body of 600 with gaps of 12.
            var slots = StackLayout.Plan(600f, 12f, new[] { 40f, 0f, 96f, 44f }, new[] { true, true, true, true });
            Assert.Equal(new StackSlot(0f, 40f, true), slots[0]);
            Assert.Equal(new StackSlot(52f, 600f - 40f - 96f - 44f - 36f, true), slots[1]);
            Assert.Equal(slots[1].Top + slots[1].Height + 12f, slots[2].Top);
            Assert.Equal(600f, slots[3].Top + slots[3].Height, 3);
        }

        [Fact]
        public void Two_fill_parts_split_the_room_equally()
        {
            var slots = StackLayout.Plan(412f, 12f, new[] { 0f, 0f }, new[] { true, true });
            Assert.Equal(200f, slots[0].Height);
            Assert.Equal(200f, slots[1].Height);
            Assert.Equal(212f, slots[1].Top);
        }

        [Fact]
        public void Hidden_parts_take_no_room_and_no_gap()
        {
            var slots = StackLayout.Plan(300f, 12f, new[] { 44f, 0f, 44f }, new[] { true, false, true });
            Assert.False(slots[1].Visible);
            Assert.Equal(0f, slots[1].Height);
            Assert.Equal(56f, slots[2].Top);
        }

        [Fact]
        public void A_body_too_small_gives_fill_parts_nothing_never_negative()
        {
            var slots = StackLayout.Plan(50f, 12f, new[] { 44f, 0f, 44f }, new[] { true, true, true });
            Assert.Equal(0f, slots[1].Height);
            Assert.Equal(44f, slots[0].Height);
        }

        [Fact]
        public void The_minimum_height_takes_the_busiest_tab()
        {
            // tabs strip (every tab), tab 0: chat + input; tab 1: list only.
            var heights = new[] { 40f, 0f, 44f, 0f };
            var tabs = new[] { -1, 0, 0, 1 };
            var min = StackLayout.MinimumHeight(heights, tabs, 2, 12f, 60f);
            Assert.Equal(40f + 60f + 44f + 24f, min);
        }

        [Fact]
        public void Without_tabs_every_part_counts()
        {
            var min = StackLayout.MinimumHeight(new[] { 0f, 44f }, new[] { -1, -1 }, 0, 12f, 60f);
            Assert.Equal(116f, min);
        }

        [Fact]
        public void Bubble_text_width_keeps_room_for_the_other_side()
        {
            var character = ChatLayout.TextMaxWidth(600f, CwyChatRole.Character);
            var system = ChatLayout.TextMaxWidth(600f, CwyChatRole.System);
            Assert.Equal(((600f - 20f) * 0.8f) - 28f, character, 3);
            Assert.Equal(600f - 20f - 28f, system, 3);
            Assert.True(ChatLayout.TextMaxWidth(0f, CwyChatRole.Player) >= 1f);
        }

        [Fact]
        public void Bubble_size_from_text_and_buttons()
        {
            ChatLayout.BubbleSize(100f, 24f, 0f, false, 400f, out var w, out var h);
            Assert.Equal(100f + ChatLayout.WrapSlack + (2 * ChatLayout.PadX), w);
            Assert.Equal(24f + (2 * ChatLayout.PadY), h);

            // A short reply with a wide button row grows to the buttons, never past the limit.
            ChatLayout.BubbleSize(20f, 24f, 300f, true, 250f, out w, out h);
            Assert.Equal(250f + (2 * ChatLayout.PadX), w);
            Assert.Equal(24f + (2 * ChatLayout.PadY) + ChatLayout.ButtonGap + ChatLayout.ButtonHeight, h);
        }

        [Fact]
        public void Bubbles_sit_left_right_or_centred_by_role()
        {
            Assert.Equal(ChatLayout.Margin, ChatLayout.Left(CwyChatRole.Character, 600f, 200f));
            Assert.Equal(600f - ChatLayout.Margin - 200f, ChatLayout.Left(CwyChatRole.Player, 600f, 200f));
            Assert.Equal(200f, ChatLayout.Left(CwyChatRole.System, 600f, 200f));
            Assert.Equal(ChatLayout.Margin, ChatLayout.Left(CwyChatRole.Player, 100f, 400f));
        }

        [Fact]
        public void Bubbles_stack_with_margins_and_spacing()
        {
            var tops = new float[3];
            var content = ChatLayout.Stack(new[] { 40f, 60f, 20f }, tops);
            Assert.Equal(new[] { 10f, 60f, 130f }, tops);
            Assert.Equal(160f, content);
            Assert.Equal(0f, ChatLayout.Stack(new float[0], new float[0]));
        }

        [Fact]
        public void Typing_dots_cycle_one_two_three()
        {
            Assert.Equal(1, Dots(TypingDots.At(0.0)));
            Assert.Equal(2, Dots(TypingDots.At(0.45)));
            Assert.Equal(3, Dots(TypingDots.At(0.85)));
            Assert.Equal(1, Dots(TypingDots.At(1.25)));
            Assert.Equal(1, Dots(TypingDots.At(double.NaN)));
        }

        [Fact]
        public void Display_text_adds_dots_only_while_typing()
        {
            Assert.Equal("hi", TypingDots.Display("hi", false, 0));
            Assert.Equal(" ", TypingDots.Display(string.Empty, false, 0));
            Assert.Equal(TypingDots.At(0), TypingDots.Display(string.Empty, true, 0));
            Assert.Equal("hi " + TypingDots.At(0), TypingDots.Display("hi", true, 0));
        }

        private static int Dots(string frame) => frame.Split((char)0x2022).Length - 1;
    }
}
