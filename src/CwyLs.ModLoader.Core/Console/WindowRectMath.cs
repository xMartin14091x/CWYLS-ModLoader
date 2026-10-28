using System;
using System.Globalization;

namespace CwyLs.ModLoader.Core.Console
{
    /// <summary>
    /// A window's place on its canvas, in canvas units, measured from the canvas's top-left corner
    /// with Y growing downwards - the way a person describes a window, and the way a uGUI
    /// RectTransform anchored and pivoted at its top-left corner is placed (anchoredPosition =
    /// (<see cref="Left"/>, -<see cref="Top"/>)).
    /// </summary>
    public readonly struct WindowRect : IEquatable<WindowRect>
    {
        /// <summary>Creates a rectangle.</summary>
        /// <param name="left">Distance from the canvas's left edge.</param>
        /// <param name="top">Distance from the canvas's top edge.</param>
        /// <param name="width">Width.</param>
        /// <param name="height">Height.</param>
        public WindowRect(float left, float top, float width, float height)
        {
            Left = left;
            Top = top;
            Width = width;
            Height = height;
        }

        /// <summary>Distance from the canvas's left edge.</summary>
        public float Left { get; }

        /// <summary>Distance from the canvas's top edge.</summary>
        public float Top { get; }

        /// <summary>Width.</summary>
        public float Width { get; }

        /// <summary>Height.</summary>
        public float Height { get; }

        /// <summary>Distance of the right edge from the canvas's left edge.</summary>
        public float Right => Left + Width;

        /// <summary>Distance of the bottom edge from the canvas's top edge.</summary>
        public float Bottom => Top + Height;

        /// <inheritdoc />
        public bool Equals(WindowRect other) =>
            Left.Equals(other.Left) && Top.Equals(other.Top)
            && Width.Equals(other.Width) && Height.Equals(other.Height);

        /// <inheritdoc />
        public override bool Equals(object? obj) => obj is WindowRect other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode()
        {
            unchecked
            {
                var hash = Left.GetHashCode();
                hash = (hash * 397) ^ Top.GetHashCode();
                hash = (hash * 397) ^ Width.GetHashCode();
                return (hash * 397) ^ Height.GetHashCode();
            }
        }

        /// <inheritdoc />
        public override string ToString() =>
            "(" + Left.ToString("0.#", CultureInfo.InvariantCulture) + ", "
            + Top.ToString("0.#", CultureInfo.InvariantCulture) + ", "
            + Width.ToString("0.#", CultureInfo.InvariantCulture) + "x"
            + Height.ToString("0.#", CultureInfo.InvariantCulture) + ")";
    }

    /// <summary>
    /// Placement rules for a draggable, resizable window. Pure, so the rules can be tested without
    /// an engine; the game-style window applies them to its RectTransform.
    /// </summary>
    /// <remarks>
    /// The game keeps a dragged window entirely on screen (<c>DraggableUI.AdjustPos</c> clamps the
    /// window's half-extents against the screen edges), so these rules do the same: a window
    /// never leaves the canvas, and one larger than the canvas is shrunk to fit before it is
    /// placed. Candidate for the mod-windows toolkit.
    /// </remarks>
    public static class WindowRectMath
    {
        /// <summary>Share of the canvas width a new window takes (the IMGUI console used the same).</summary>
        public const float DefaultWidthFraction = 0.70f;

        /// <summary>Share of the canvas height a new window takes.</summary>
        public const float DefaultHeightFraction = 0.62f;

        /// <summary>Gap kept between a new window and the canvas edge.</summary>
        public const float DefaultMargin = 24f;

        /// <summary>
        /// Where a window opens the first time: the default share of the canvas, horizontally
        /// centred, a quarter of the free height down from the top.
        /// </summary>
        /// <param name="canvasWidth">Canvas width in canvas units.</param>
        /// <param name="canvasHeight">Canvas height in canvas units.</param>
        /// <param name="minWidth">Smallest width the window's layout works at.</param>
        /// <param name="minHeight">Smallest height the window's layout works at.</param>
        public static WindowRect Default(float canvasWidth, float canvasHeight, float minWidth, float minHeight)
        {
            var width = Math.Max(minWidth, canvasWidth * DefaultWidthFraction);
            var height = Math.Max(minHeight, canvasHeight * DefaultHeightFraction);

            width = Math.Min(width, Math.Max(minWidth, canvasWidth - (DefaultMargin * 2f)));
            height = Math.Min(height, Math.Max(minHeight, canvasHeight - (DefaultMargin * 2f)));

            var left = (canvasWidth - width) * 0.5f;
            var top = Math.Max(DefaultMargin, (canvasHeight - height) * 0.25f);

            return Clamp(new WindowRect(left, top, width, height), canvasWidth, canvasHeight, minWidth, minHeight);
        }

        /// <summary>
        /// Keeps a window on the canvas: first shrinks it to fit (never below the minimum), then
        /// moves it so no edge is outside. A window still larger than the canvas at its minimum
        /// size is pinned to the top-left corner, so its title bar and close button stay reachable.
        /// </summary>
        /// <param name="rect">The window.</param>
        /// <param name="canvasWidth">Canvas width.</param>
        /// <param name="canvasHeight">Canvas height.</param>
        /// <param name="minWidth">Smallest width.</param>
        /// <param name="minHeight">Smallest height.</param>
        public static WindowRect Clamp(WindowRect rect, float canvasWidth, float canvasHeight, float minWidth, float minHeight)
        {
            var width = Math.Max(minWidth, Math.Min(rect.Width, canvasWidth));
            var height = Math.Max(minHeight, Math.Min(rect.Height, canvasHeight));

            var left = Math.Min(rect.Left, canvasWidth - width);
            var top = Math.Min(rect.Top, canvasHeight - height);

            left = Math.Max(0f, left);
            top = Math.Max(0f, top);

            return new WindowRect(left, top, width, height);
        }

        /// <summary>A window dragged by the pointer, kept on the canvas.</summary>
        /// <param name="rect">The window before the drag step.</param>
        /// <param name="deltaX">Pointer movement right, in canvas units.</param>
        /// <param name="deltaY">Pointer movement down, in canvas units.</param>
        /// <param name="canvasWidth">Canvas width.</param>
        /// <param name="canvasHeight">Canvas height.</param>
        /// <param name="minWidth">Smallest width.</param>
        /// <param name="minHeight">Smallest height.</param>
        public static WindowRect Move(
            WindowRect rect,
            float deltaX,
            float deltaY,
            float canvasWidth,
            float canvasHeight,
            float minWidth,
            float minHeight) =>
            Clamp(
                new WindowRect(rect.Left + deltaX, rect.Top + deltaY, rect.Width, rect.Height),
                canvasWidth,
                canvasHeight,
                minWidth,
                minHeight);

        /// <summary>
        /// A window resized from its bottom-right grip: the top-left corner stays put, the size
        /// follows the pointer, never below the minimum and never past the canvas edge.
        /// </summary>
        /// <param name="rect">The window before the resize step.</param>
        /// <param name="deltaWidth">Pointer movement right, in canvas units.</param>
        /// <param name="deltaHeight">Pointer movement down, in canvas units.</param>
        /// <param name="canvasWidth">Canvas width.</param>
        /// <param name="canvasHeight">Canvas height.</param>
        /// <param name="minWidth">Smallest width.</param>
        /// <param name="minHeight">Smallest height.</param>
        public static WindowRect Resize(
            WindowRect rect,
            float deltaWidth,
            float deltaHeight,
            float canvasWidth,
            float canvasHeight,
            float minWidth,
            float minHeight)
        {
            var width = Math.Min(rect.Width + deltaWidth, canvasWidth - rect.Left);
            var height = Math.Min(rect.Height + deltaHeight, canvasHeight - rect.Top);

            width = Math.Max(minWidth, width);
            height = Math.Max(minHeight, height);

            return Clamp(new WindowRect(rect.Left, rect.Top, width, height), canvasWidth, canvasHeight, minWidth, minHeight);
        }

        /// <summary>
        /// Converts a pointer movement in screen pixels (Y up, as Unity reports it) into canvas
        /// units with Y down.
        /// </summary>
        /// <param name="screenDeltaX">Pointer movement right, in pixels.</param>
        /// <param name="screenDeltaY">Pointer movement up, in pixels.</param>
        /// <param name="scaleFactor">The canvas's scale factor; zero or less is treated as 1.</param>
        /// <param name="deltaX">Movement right, in canvas units.</param>
        /// <param name="deltaY">Movement down, in canvas units.</param>
        public static void ScreenToCanvasDelta(
            float screenDeltaX,
            float screenDeltaY,
            float scaleFactor,
            out float deltaX,
            out float deltaY)
        {
            var scale = scaleFactor > 0f ? scaleFactor : 1f;
            deltaX = screenDeltaX / scale;
            deltaY = -screenDeltaY / scale;
        }
    }
}
