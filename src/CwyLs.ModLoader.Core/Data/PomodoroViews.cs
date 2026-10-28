using System;
using System.Collections.Generic;

namespace CwyLs.ModLoader.Core.Data
{
    /// <summary>Which timer the player has selected; mirrors the game's <c>TimerMode</c>.</summary>
    public enum TimerModeView
    {
        /// <summary>Work/break loop with a fixed length.</summary>
        Pomodoro = 0,

        /// <summary>Open-ended stopwatch.</summary>
        Countup = 1,
    }

    /// <summary>One saved timer preset.</summary>
    public sealed class PomodoroPresetView
    {
        /// <summary>Preset id.</summary>
        public Guid Id { get; init; }

        /// <summary>The preset's name; empty when the player never named it.</summary>
        public string Name { get; init; } = string.Empty;

        /// <summary>How many work/break loops the preset runs.</summary>
        public int LoopCount { get; init; }

        /// <summary>Minutes of work per loop.</summary>
        public int WorkMinutes { get; init; }

        /// <summary>Minutes of break per loop.</summary>
        public int BreakMinutes { get; init; }
    }

    /// <summary>
    /// The player's timer configuration and its last recorded work slice, from the game's
    /// <c>PomodoroData</c>. Live countdown state belongs to <c>PomodoroService</c> and is not part
    /// of the save data, so it is not here: subscribe to the bus's change events for that.
    /// </summary>
    public sealed class PomodoroView
    {
        /// <summary>Configured number of loops.</summary>
        public int LoopCount { get; init; }

        /// <summary>Configured minutes of work per loop.</summary>
        public int WorkMinutes { get; init; }

        /// <summary>Configured minutes of break per loop.</summary>
        public int BreakMinutes { get; init; }

        /// <summary>Which timer is selected.</summary>
        public TimerModeView Mode { get; init; }

        /// <summary>When the last work slice was last written, or <c>null</c> if never.</summary>
        public DateTime? LastUpdatedWorkAt { get; init; }

        /// <summary>Seconds of work recorded against that slice.</summary>
        public double LastUpdatedWorkSeconds { get; init; }

        /// <summary>The player's saved presets, in slot order.</summary>
        public IReadOnlyList<PomodoroPresetView> Presets { get; init; } = Array.Empty<PomodoroPresetView>();
    }
}
