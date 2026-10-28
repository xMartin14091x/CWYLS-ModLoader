using System;
using System.Collections.Generic;

namespace CwyLs.ModLoader.Core.Data
{
    /// <summary>One day of a habit's history.</summary>
    public sealed class HabitDayView
    {
        /// <summary>The day, at midnight local time.</summary>
        public DateTime Date { get; init; }

        /// <summary>True when the player ticked the habit off on that day.</summary>
        public bool Completed { get; init; }
    }

    /// <summary>
    /// One tracked habit and its recent completion history. The game stores the definition in
    /// <c>HabitAllHeaderData</c> and the per-day ticks in a separate monthly file, so
    /// <see cref="RecentDays"/> is a bounded window (see <c>Data.HabitRecentDays</c>), not the
    /// whole history.
    /// </summary>
    public sealed class HabitView
    {
        /// <summary>Game-assigned habit id (a GUID string).</summary>
        public string Id { get; init; } = string.Empty;

        /// <summary>The habit's title as the player wrote it.</summary>
        public string Title { get; init; } = string.Empty;

        /// <summary>The day the habit was created.</summary>
        public DateTime CreatedDate { get; init; }

        /// <summary>
        /// Days of the week the habit is scheduled for, decoded from the game's bit flag.
        /// </summary>
        public IReadOnlyList<DayOfWeek> ScheduledDays { get; init; } = Array.Empty<DayOfWeek>();

        /// <summary>
        /// The most recent days, oldest first. Only days the game has a record for appear; a day
        /// the player skipped is present with <see cref="HabitDayView.Completed"/> false only when
        /// the game stored it.
        /// </summary>
        public IReadOnlyList<HabitDayView> RecentDays { get; init; } = Array.Empty<HabitDayView>();

        /// <summary>Number of <see cref="RecentDays"/> entries that are completed.</summary>
        public int RecentCompletedCount
        {
            get
            {
                var done = 0;
                foreach (var day in RecentDays)
                {
                    if (day.Completed)
                    {
                        done++;
                    }
                }

                return done;
            }
        }
    }
}
