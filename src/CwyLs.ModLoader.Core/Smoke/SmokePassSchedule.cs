using System;

namespace CwyLs.ModLoader.Core.Smoke
{
    /// <summary>
    /// When the automatic second smoke pass runs, expressed as pure arithmetic so it can be
    /// unit-tested without an engine.
    /// <para>
    /// A single pass a few seconds after the scene loads cannot see anything the game has not done
    /// yet: checks that report "observed at least once this session"
    /// (<c>p1.events.satone_state</c> is the usual one) are red simply because the player has been
    /// in the room for three seconds. Running the whole set again a minute in fixes that — at the
    /// price of a second unannounced run, which is why it is off by default. Set
    /// <c>Smoke.SecondPassSeconds</c> to <see cref="SuggestedSecondPassSeconds"/> to get it back.
    /// </para>
    /// </summary>
    public static class SmokePassSchedule
    {
        /// <summary>
        /// Default of <c>Smoke.SecondPassSeconds</c>: off. The framework no longer runs
        /// itself unasked, because each pass plays the test clip and repaints the character.
        /// </summary>
        public const float DefaultSecondPassSeconds = 0f;

        /// <summary>
        /// The value that made a hands-off launch end green: one minute after the scene loads,
        /// late enough for the "observed this session" checks to have seen something. Not a
        /// default any more, but the number to put in the config when you want that behaviour.
        /// </summary>
        public const float SuggestedSecondPassSeconds = 60f;

        /// <summary>
        /// Shortest gap the framework will leave between the two passes. A second pass in the same
        /// frame as the first would re-read the same counters and prove nothing, so a configured
        /// value at or below the settle delay is pushed out to here instead of being refused.
        /// </summary>
        public const float MinimumGapSeconds = 0.5f;

        /// <summary>True when <paramref name="secondPassSeconds"/> asks for a second pass at all.</summary>
        /// <param name="secondPassSeconds">The configured <c>Smoke.SecondPassSeconds</c>.</param>
        /// <returns>False for 0, for a negative value, and for NaN/infinity - all of which mean "off".</returns>
        public static bool IsEnabled(float secondPassSeconds) =>
            secondPassSeconds > 0f && !float.IsNaN(secondPassSeconds) && !float.IsInfinity(secondPassSeconds);

        /// <summary>
        /// How long the runner waits after the first pass has finished before running the second.
        /// </summary>
        /// <param name="settleSeconds">Delay between the scene load and the first pass.</param>
        /// <param name="secondPassSeconds">Delay between the scene load and the second pass.</param>
        /// <param name="delayAfterFirstPass">
        /// Seconds to wait once the first pass is done; meaningful only when this returns true.
        /// </param>
        /// <returns>True when a second pass is wanted.</returns>
        public static bool TryPlanSecondPass(
            float settleSeconds, float secondPassSeconds, out float delayAfterFirstPass)
        {
            delayAfterFirstPass = 0f;

            if (!IsEnabled(secondPassSeconds))
            {
                return false;
            }

            // A negative or NaN settle delay is not this type's business to validate; treating it
            // as zero keeps the gap honest rather than producing a wait longer than requested.
            var settle = settleSeconds > 0f && !float.IsNaN(settleSeconds) ? settleSeconds : 0f;

            delayAfterFirstPass = Math.Max(MinimumGapSeconds, secondPassSeconds - settle);
            return true;
        }
    }
}
