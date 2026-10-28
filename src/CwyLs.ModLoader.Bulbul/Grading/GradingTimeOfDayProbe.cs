using System;
using CwyLs.ModLoader.Core.Grading;

namespace CwyLs.ModLoader.Bulbul.Grading
{
    /// <summary>
    /// Which of the game's looks is on: <c>Bulbul.WindowViewService.CurrentTimeOfDayType</c>,
    /// set by <c>ChangeWeatherAndTime</c> together with the <c>DateTime/{day,sunset,night,Cloudy}</c>
    /// objects that carry the game's volumes.
    /// Changes arrive as <c>WindowChanged</c> events (<c>Day</c>/<c>Sunset</c>/<c>Night</c>/<c>Cloudy</c>,
    /// on); this probe answers the question once, when the room comes up.
    /// </summary>
    internal static class GradingTimeOfDayProbe
    {
        /// <summary>The current look, or Unknown when the service is not up (or has not chosen yet).</summary>
        internal static GradingTimeOfDay Current()
        {
            try
            {
                var service = UnityEngine.Object.FindObjectOfType<global::Bulbul.WindowViewService>();
                if (service == null)
                {
                    return GradingTimeOfDay.Unknown;
                }

                // Before the first ChangeWeatherAndTime the property holds default(WindowTimeOfDayType) = 0,
                // which is not a named value: that reads as Unknown.
                return GradingTimesOfDay.FromName(service.CurrentTimeOfDayType.CurrentValue.ToString());
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogDebug("GRADING: could not read the time of day: " + ex.GetType().Name + ": " + ex.Message);
                return GradingTimeOfDay.Unknown;
            }
        }
    }
}
