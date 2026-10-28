using System;
using System.Collections.Generic;

namespace CwyLs.ModLoader.Core.Ui
{
    /// <summary>Where one part of a window goes: its top edge below the body's top, and its height.</summary>
    public readonly struct StackSlot : IEquatable<StackSlot>
    {
        /// <summary>Creates a slot.</summary>
        /// <param name="top">Distance from the body's top edge.</param>
        /// <param name="height">Height; 0 for a part that is not shown.</param>
        /// <param name="visible">True when the part is shown.</param>
        public StackSlot(float top, float height, bool visible)
        {
            Top = top;
            Height = height;
            Visible = visible;
        }

        /// <summary>Distance from the body's top edge.</summary>
        public float Top { get; }

        /// <summary>Height; 0 for a part that is not shown.</summary>
        public float Height { get; }

        /// <summary>True when the part is shown.</summary>
        public bool Visible { get; }

        /// <inheritdoc />
        public bool Equals(StackSlot other) =>
            Top.Equals(other.Top) && Height.Equals(other.Height) && Visible == other.Visible;

        /// <inheritdoc />
        public override bool Equals(object? obj) => obj is StackSlot other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode()
        {
            unchecked
            {
                return (((Top.GetHashCode() * 397) ^ Height.GetHashCode()) * 397) ^ Visible.GetHashCode();
            }
        }

        /// <summary><c>top+height</c> or <c>hidden</c>.</summary>
        public override string ToString() => Visible ? Top + "+" + Height : "hidden";
    }

    /// <summary>
    /// The window body's layout: parts stacked top to bottom with a gap between them. Fixed parts
    /// keep their height; the others share what is left equally. Pure, so it is unit-tested.
    /// </summary>
    public static class StackLayout
    {
        /// <summary>Gap between two parts (the console's).</summary>
        public const float Gap = 12f;

        /// <summary>Least height a sharing part should get; used for the window's minimum height.</summary>
        public const float MinFillHeight = 60f;

        /// <summary>Places the parts.</summary>
        /// <param name="available">The body's height.</param>
        /// <param name="gap">Gap between two shown parts.</param>
        /// <param name="fixedHeights">Each part's fixed height, or 0 to share the leftover room.</param>
        /// <param name="visible">Whether each part is shown (same length as <paramref name="fixedHeights"/>).</param>
        /// <returns>One slot per part, in order. Hidden parts get a zero-height slot at the current position.</returns>
        public static StackSlot[] Plan(float available, float gap, IReadOnlyList<float> fixedHeights, IReadOnlyList<bool> visible)
        {
            if (fixedHeights is null)
            {
                throw new ArgumentNullException(nameof(fixedHeights));
            }

            if (visible is null)
            {
                throw new ArgumentNullException(nameof(visible));
            }

            if (fixedHeights.Count != visible.Count)
            {
                throw new ArgumentException("one visibility flag per part is needed", nameof(visible));
            }

            var count = fixedHeights.Count;
            var slots = new StackSlot[count];
            var shown = 0;
            var fills = 0;
            var fixedTotal = 0f;
            for (var i = 0; i < count; i++)
            {
                if (!visible[i])
                {
                    continue;
                }

                shown++;
                if (fixedHeights[i] > 0f)
                {
                    fixedTotal += fixedHeights[i];
                }
                else
                {
                    fills++;
                }
            }

            var gaps = shown > 1 ? gap * (shown - 1) : 0f;
            var share = fills == 0 ? 0f : Math.Max(0f, available - fixedTotal - gaps) / fills;

            var top = 0f;
            var placed = 0;
            for (var i = 0; i < count; i++)
            {
                if (!visible[i])
                {
                    slots[i] = new StackSlot(top, 0f, false);
                    continue;
                }

                if (placed > 0)
                {
                    top += gap;
                }

                var height = fixedHeights[i] > 0f ? fixedHeights[i] : share;
                slots[i] = new StackSlot(top, height, true);
                top += height;
                placed++;
            }

            return slots;
        }

        /// <summary>
        /// The body height that fits the parts of the busiest tab: every fixed height, the gaps, and
        /// <paramref name="minFill"/> per sharing part.
        /// </summary>
        /// <param name="fixedHeights">Each part's fixed height, or 0 for a sharing part.</param>
        /// <param name="tabs">Each part's tab index, or -1 for every tab.</param>
        /// <param name="tabCount">Tabs in the window's strip; 0 when it has none.</param>
        /// <param name="gap">Gap between two parts.</param>
        /// <param name="minFill">Least height of a sharing part.</param>
        public static float MinimumHeight(IReadOnlyList<float> fixedHeights, IReadOnlyList<int> tabs, int tabCount, float gap, float minFill)
        {
            if (fixedHeights is null)
            {
                throw new ArgumentNullException(nameof(fixedHeights));
            }

            if (tabs is null)
            {
                throw new ArgumentNullException(nameof(tabs));
            }

            var best = 0f;
            var passes = Math.Max(1, tabCount);
            for (var tab = 0; tab < passes; tab++)
            {
                var shown = 0;
                var total = 0f;
                for (var i = 0; i < fixedHeights.Count; i++)
                {
                    var partTab = i < tabs.Count ? tabs[i] : -1;
                    if (partTab >= 0 && tabCount > 0 && partTab != tab)
                    {
                        continue;
                    }

                    shown++;
                    total += fixedHeights[i] > 0f ? fixedHeights[i] : minFill;
                }

                total += shown > 1 ? gap * (shown - 1) : 0f;
                best = Math.Max(best, total);
            }

            return best;
        }
    }
}
