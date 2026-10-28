using System;

namespace CwyLs.ModLoader.Core.Conditions
{
    /// <summary>
    /// The moments at which a condition's answer can change, so a consumer re-evaluates only
    /// when it matters (like the "update rate" of Stardew Valley's Content Patcher). A flags enum:
    /// a token lists every point that can change its value, and a <see cref="ConditionSet"/>'s
    /// points are the union of its tokens' (<see cref="ConditionRegistry.UpdatePointsOf"/>).
    /// </summary>
    /// <remarks>
    /// The condition provider raises <see cref="ConditionsChanged"/> with exactly one
    /// point per change through <see cref="ConditionRegistry.RaiseChanged"/>. Values are
    /// explicit and never renumbered.
    /// </remarks>
    [Flags]
    public enum UpdatePoint
    {
        /// <summary>Never changes (a constant), or nothing changed.</summary>
        None = 0,

        /// <summary>The packs were read at startup.</summary>
        Startup = 1 << 0,

        /// <summary>The room is up and the player's save is loaded (level, story and scenery become known).</summary>
        RoomReady = 1 << 1,

        /// <summary>The local calendar date changed (weekday, date, daily random roll).</summary>
        DayStarted = 1 << 2,

        /// <summary>The local clock moved to a new minute (time, hour, time of day).</summary>
        TimeChanged = 1 << 3,

        /// <summary>The pomodoro or count-up timer changed state.</summary>
        PomodoroChanged = 1 << 4,

        /// <summary>The display language changed.</summary>
        LanguageChanged = 1 << 5,

        /// <summary>A window view, the weather or the scenery changed.</summary>
        SceneryChanged = 1 << 6,

        /// <summary>A story scene ended or the story progress was saved.</summary>
        StoryProgressed = 1 << 7,

        /// <summary>The player's level changed.</summary>
        LevelChanged = 1 << 8,

        /// <summary>A flag was set or cleared (<c>cwy.flag</c>, or a mod).</summary>
        FlagChanged = 1 << 9,

        /// <summary>The packs were rescanned or reloaded.</summary>
        PacksChanged = 1 << 10,

        /// <summary>A mod said one of its own tokens changed.</summary>
        ModToken = 1 << 11,

        /// <summary>Someone asked for a re-evaluation (console <c>cond override</c>, a test).</summary>
        Manual = 1 << 12,

        /// <summary>Every point: what an unknown token is assumed to depend on.</summary>
        All = Startup | RoomReady | DayStarted | TimeChanged | PomodoroChanged | LanguageChanged
              | SceneryChanged | StoryProgressed | LevelChanged | FlagChanged | PacksChanged | ModToken | Manual,
    }
}
