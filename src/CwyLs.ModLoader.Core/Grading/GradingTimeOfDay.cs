using System;

namespace CwyLs.ModLoader.Core.Grading
{
    /// <summary>
    /// The game's own time-of-day looks, as far as a grading preset is concerned.
    /// <para>
    /// The game switches its look with <c>Bulbul.WindowViewService.ChangeWeatherAndTime</c>
    /// (game 1.17.3), which turns one of four objects on -
    /// <c>Paremt/EnviromentObject/DateTime/{day,sunset,night,Cloudy}</c> in <c>RoomScene</c>, each
    /// carrying a global <c>Volume</c> with its own Beautify + LiftGammaGain profile
    /// (<c>Volume_day</c>, <c>Volume_sunset</c>, <c>Volume_night</c>, <c>Volume_cloud</c>) - and
    /// publishes the choice as <c>WindowViewService.CurrentTimeOfDayType</c>
    /// (<c>Bulbul.WindowTimeOfDayType</c>: Day = 11, Sunset, Night, Cloudy).
    /// </para>
    /// </summary>
    public enum GradingTimeOfDay
    {
        /// <summary>Not known yet (before the room is up), or a value this build does not recognise.</summary>
        Unknown = 0,

        /// <summary><c>WindowTimeOfDayType.Day</c>.</summary>
        Day,

        /// <summary><c>WindowTimeOfDayType.Sunset</c>.</summary>
        Sunset,

        /// <summary><c>WindowTimeOfDayType.Night</c>.</summary>
        Night,

        /// <summary><c>WindowTimeOfDayType.Cloudy</c>.</summary>
        Cloudy,
    }

    /// <summary>Names and parsing for <see cref="GradingTimeOfDay"/>.</summary>
    public static class GradingTimesOfDay
    {
        /// <summary>The four known looks, in the order the game declares them.</summary>
        public static GradingTimeOfDay[] Known { get; } =
        {
            GradingTimeOfDay.Day, GradingTimeOfDay.Sunset, GradingTimeOfDay.Night, GradingTimeOfDay.Cloudy,
        };

        /// <summary>
        /// Reads a game name (<c>Day</c>, <c>Sunset</c>, <c>Night</c>, <c>Cloudy</c>, any case) or a
        /// preset block name (<c>day</c> ...). Anything else - <c>Sakura</c>, <c>LightRain</c>, null -
        /// is <see cref="GradingTimeOfDay.Unknown"/>.
        /// </summary>
        /// <param name="name">A <c>WindowViewType</c>/<c>WindowTimeOfDayType</c> name, or a block name.</param>
        public static GradingTimeOfDay FromName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return GradingTimeOfDay.Unknown;
            }

            foreach (var known in Known)
            {
                if (string.Equals(name!.Trim(), known.ToString(), StringComparison.OrdinalIgnoreCase))
                {
                    return known;
                }
            }

            return GradingTimeOfDay.Unknown;
        }

        /// <summary>
        /// The name of the preset block that applies while this look is active: <c>day</c>,
        /// <c>sunset</c>, <c>night</c>, <c>cloudy</c>. Empty for <see cref="GradingTimeOfDay.Unknown"/>.
        /// </summary>
        /// <param name="timeOfDay">The look.</param>
        public static string BlockName(GradingTimeOfDay timeOfDay) =>
            timeOfDay == GradingTimeOfDay.Unknown ? string.Empty : timeOfDay.ToString().ToLowerInvariant();
    }
}
