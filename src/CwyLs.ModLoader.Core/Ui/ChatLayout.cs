using System;
using System.Collections.Generic;

namespace CwyLs.ModLoader.Core.Ui
{
    /// <summary>
    /// Bubble geometry for the chat view: widths, padding, where each bubble sits. Text measurement
    /// is the engine's; everything after it is here, pure and unit-tested. Units are canvas units.
    /// </summary>
    public static class ChatLayout
    {
        /// <summary>Space between the content's edges and the bubbles.</summary>
        public const float Margin = 10f;

        /// <summary>Vertical space between two bubbles.</summary>
        public const float Spacing = 10f;

        /// <summary>Largest share of the row a character or player bubble takes.</summary>
        public const float BubbleShare = 0.8f;

        /// <summary>Text inset inside a bubble, left and right.</summary>
        public const float PadX = 14f;

        /// <summary>
        /// Text inset inside a bubble, top and bottom. Also the room Thai tone marks stacked above an
        /// upper vowel need above the first line (about 4 units over the line box).
        /// </summary>
        public const float PadY = 10f;

        /// <summary>Height of a bubble button.</summary>
        public const float ButtonHeight = 28f;

        /// <summary>Space above the button row and between two bubble buttons.</summary>
        public const float ButtonGap = 6f;

        /// <summary>Extra width given to measured text so the engine never wraps it one word earlier.</summary>
        public const float WrapSlack = 2f;

        /// <summary>The widest a bubble's text may be.</summary>
        /// <param name="viewportWidth">The chat view's inner width.</param>
        /// <param name="role">Whose bubble.</param>
        public static float TextMaxWidth(float viewportWidth, CwyChatRole role)
        {
            var row = Math.Max(0f, viewportWidth - (2f * Margin));
            var bubble = role == CwyChatRole.System ? row : row * BubbleShare;
            return Math.Max(1f, bubble - (2f * PadX));
        }

        /// <summary>A bubble's size from its measured text and its buttons.</summary>
        /// <param name="textWidth">Measured text width (at most <see cref="TextMaxWidth"/>).</param>
        /// <param name="textHeight">Measured text height.</param>
        /// <param name="buttonsWidth">Width of the button row (buttons and gaps), 0 for none.</param>
        /// <param name="hasButtons">True when the bubble has buttons.</param>
        /// <param name="maxTextWidth">The limit the text was measured at.</param>
        /// <param name="width">The bubble's width.</param>
        /// <param name="height">The bubble's height.</param>
        public static void BubbleSize(
            float textWidth,
            float textHeight,
            float buttonsWidth,
            bool hasButtons,
            float maxTextWidth,
            out float width,
            out float height)
        {
            var inner = Math.Min(maxTextWidth, Math.Max(textWidth + WrapSlack, hasButtons ? buttonsWidth : 0f));
            width = inner + (2f * PadX);
            height = Math.Max(0f, textHeight) + (2f * PadY) + (hasButtons ? ButtonGap + ButtonHeight : 0f);
        }

        /// <summary>The bubble's left edge: the character's on the left, the player's on the right, system notes centred.</summary>
        /// <param name="role">Whose bubble.</param>
        /// <param name="viewportWidth">The chat view's inner width.</param>
        /// <param name="bubbleWidth">The bubble's width.</param>
        public static float Left(CwyChatRole role, float viewportWidth, float bubbleWidth)
        {
            switch (role)
            {
                case CwyChatRole.Player:
                    return Math.Max(Margin, viewportWidth - Margin - bubbleWidth);
                case CwyChatRole.System:
                    return Math.Max(Margin, (viewportWidth - bubbleWidth) * 0.5f);
                default:
                    return Margin;
            }
        }

        /// <summary>Stacks bubbles top to bottom.</summary>
        /// <param name="heights">Each bubble's height, oldest first.</param>
        /// <param name="tops">Receives each bubble's top (same length as <paramref name="heights"/>).</param>
        /// <returns>The content's height.</returns>
        public static float Stack(IReadOnlyList<float> heights, float[] tops)
        {
            if (heights is null)
            {
                throw new ArgumentNullException(nameof(heights));
            }

            if (tops is null || tops.Length < heights.Count)
            {
                throw new ArgumentException("one top per bubble is needed", nameof(tops));
            }

            var y = Margin;
            for (var i = 0; i < heights.Count; i++)
            {
                if (i > 0)
                {
                    y += Spacing;
                }

                tops[i] = y;
                y += Math.Max(0f, heights[i]);
            }

            return heights.Count == 0 ? 0f : y + Margin;
        }
    }

    /// <summary>The typing indicator: one, two, three dots, a step every <see cref="StepSeconds"/>.</summary>
    public static class TypingDots
    {
        /// <summary>Seconds per step.</summary>
        public const float StepSeconds = 0.4f;

        private static readonly string[] Frames = { "•", "• •", "• • •" };

        /// <summary>The dots to show at a time.</summary>
        /// <param name="seconds">Any running clock in seconds.</param>
        public static string At(double seconds)
        {
            if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0d)
            {
                seconds = 0d;
            }

            var step = (long)Math.Floor(seconds / StepSeconds);
            return Frames[(int)(step % Frames.Length)];
        }

        /// <summary>A bubble's display text: its text, then the dots while typing.</summary>
        /// <param name="text">The bubble's text.</param>
        /// <param name="typing">True while the typing indicator is on.</param>
        /// <param name="seconds">Any running clock in seconds.</param>
        public static string Display(string text, bool typing, double seconds)
        {
            if (!typing)
            {
                return string.IsNullOrEmpty(text) ? " " : text;
            }

            var dots = At(seconds);
            return string.IsNullOrEmpty(text) ? dots : text + " " + dots;
        }
    }
}
