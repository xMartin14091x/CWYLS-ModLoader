using System;
using System.Collections.Generic;

namespace CwyLs.ModLoader.Core.Data
{
    /// <summary>One day on the player's calendar.</summary>
    public sealed class CalendarDayView
    {
        /// <summary>The day, at midnight local time.</summary>
        public DateTime Date { get; init; }

        /// <summary>Seconds the player worked that day, as the game's statistics screen counts it.</summary>
        public double WorkSeconds { get; init; }

        /// <summary>How many todos the player completed that day.</summary>
        public int CompletedTodoCount { get; init; }

        /// <summary>
        /// The diary entry for that day, or <c>null</c> when the adapter was told not to publish
        /// note/diary text (<c>Data.IncludeNoteText</c>). Empty string means "the player wrote nothing".
        /// </summary>
        public string? DiaryText { get; init; }
    }

    /// <summary>
    /// A bounded window of recent calendar days. The game stores one file per month and loads
    /// them lazily, so the bus publishes a recent window rather than the full history.
    /// </summary>
    public sealed class CalendarView
    {
        /// <summary>Days in the window, oldest first.</summary>
        public IReadOnlyList<CalendarDayView> RecentDays { get; init; } = Array.Empty<CalendarDayView>();

        /// <summary>Total work seconds across <see cref="RecentDays"/>.</summary>
        public double RecentWorkSeconds
        {
            get
            {
                var total = 0d;
                foreach (var day in RecentDays)
                {
                    total += day.WorkSeconds;
                }

                return total;
            }
        }
    }
}
