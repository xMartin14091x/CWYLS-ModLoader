using System;
using System.Collections.Generic;
using System.Globalization;

namespace CwyLs.ModLoader.Core.Grading
{
    /// <summary>How a control behaves when a preset is blended toward the game's own look.</summary>
    public enum GradingBlendMode
    {
        /// <summary>An amount: blending moves it toward its neutral value (exposure, saturation, vignette amount ...).</summary>
        Strength = 0,

        /// <summary>
        /// A shape or a direction: blending keeps it as it is, because the amount that goes with it
        /// already fades (a hue angle, a vignette midpoint, a grain size).
        /// </summary>
        Shape,

        /// <summary>The blend weight itself; never blended.</summary>
        Weight,
    }

    /// <summary>
    /// One Lightroom-style slider of a grading preset: its id (which is also its JSON path), the
    /// range it is clamped to, and the value that means "do nothing".
    /// </summary>
    public sealed class GradingControl
    {
        private static readonly string[] NoAliases = new string[0];

        /// <summary>Creates a control description.</summary>
        /// <param name="id">Canonical id and JSON path, e.g. <c>basic.exposure</c>.</param>
        /// <param name="min">Smallest accepted value.</param>
        /// <param name="max">Largest accepted value.</param>
        /// <param name="neutral">The value at which the control changes nothing.</param>
        /// <param name="unit">Unit for people, e.g. <c>EV</c>, <c>deg</c>; empty for a plain -100..100 slider.</param>
        /// <param name="blend">How the control blends.</param>
        /// <param name="description">One line for <c>grade controls</c>.</param>
        /// <param name="aliases">Short names accepted by <c>grade set</c>, e.g. <c>exposure</c>.</param>
        /// <exception cref="ArgumentException">The id is blank or the range is empty or does not contain the neutral value.</exception>
        public GradingControl(
            string id,
            double min,
            double max,
            double neutral,
            string unit,
            GradingBlendMode blend,
            string description,
            params string[] aliases)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                throw new ArgumentException("a control id is required", nameof(id));
            }

            if (!(min < max) || neutral < min || neutral > max)
            {
                throw new ArgumentException(
                    "the range of '" + id + "' must be non-empty and contain its neutral value", nameof(min));
            }

            Id = id;
            Min = min;
            Max = max;
            Neutral = neutral;
            Unit = unit ?? string.Empty;
            Blend = blend;
            Description = description ?? string.Empty;
            Aliases = aliases ?? NoAliases;
        }

        /// <summary>Canonical id and JSON path, e.g. <c>hsl.blues.luminance</c>.</summary>
        public string Id { get; }

        /// <summary>Smallest accepted value.</summary>
        public double Min { get; }

        /// <summary>Largest accepted value.</summary>
        public double Max { get; }

        /// <summary>The value at which the control changes nothing.</summary>
        public double Neutral { get; }

        /// <summary>Unit for people; empty for a plain slider.</summary>
        public string Unit { get; }

        /// <summary>How the control behaves under <c>blend</c>.</summary>
        public GradingBlendMode Blend { get; }

        /// <summary>One line for people.</summary>
        public string Description { get; }

        /// <summary>Short names accepted by <c>grade set</c>. Never null.</summary>
        public IReadOnlyList<string> Aliases { get; }

        /// <summary>True for a hue angle (0..360), which wraps rather than clamps.</summary>
        public bool IsAngle => Unit == GradingControls.DegreesUnit;

        /// <summary>
        /// Puts a value into range: an angle wraps into [0, 360), anything else clamps.
        /// </summary>
        /// <param name="value">The value as given. Must be finite.</param>
        /// <param name="changed">True when the value had to be moved.</param>
        /// <returns>The value in range.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="value"/> is NaN or infinite.</exception>
        public double Clamp(double value, out bool changed)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "a control value must be a finite number");
            }

            double result;
            if (IsAngle)
            {
                result = value % 360.0;
                if (result < 0)
                {
                    result += 360.0;
                }
            }
            else
            {
                result = value < Min ? Min : value > Max ? Max : value;
            }

            changed = Math.Abs(result - value) > 1e-12;
            return result;
        }

        /// <summary>True when <paramref name="value"/> is (within 1e-9 of) <see cref="Neutral"/>.</summary>
        /// <param name="value">A value in range.</param>
        public bool IsNeutral(double value) => Math.Abs(value - Neutral) < 1e-9;

        /// <summary>The range for people: <c>-5..5 EV</c>, <c>0..360 deg</c>, <c>-100..100</c>.</summary>
        public string Range =>
            Format(Min) + ".." + Format(Max) + (Unit.Length == 0 ? string.Empty : " " + Unit);

        /// <summary>A value as the console prints it: invariant, at most two decimals, with a sign for signed ranges.</summary>
        /// <param name="value">The value.</param>
        public string FormatValue(double value) =>
            (Min < 0 && value > 0 ? "+" : string.Empty) + Format(value);

        /// <inheritdoc />
        public override string ToString() => Id + " (" + Range + ", neutral " + Format(Neutral) + ")";

        private static string Format(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
    }
}
