namespace CwyLs.ModLoader.Core.Data
{
    /// <summary>
    /// One addressable area of the player's save data. The names mirror the properties on the
    /// game's <c>Bulbul.SaveDataManager</c>, which is the single root of everything the player
    /// owns. The mapping from each <c>SaveDataManager.Save*</c>/<c>Delete*</c> method name to a
    /// section is <see cref="SaveMethodRules.TryDataSection"/>: plain strings, so it stays
    /// Unity-free and is unit-tested against the game's method list.
    /// </summary>
    /// <remarks>
    /// Values are stable: the JSON snapshot and <see cref="GameDataChanged"/> both name sections
    /// by their enum name, so renaming one is a breaking change for modders and sidecar tools.
    /// </remarks>
    public enum GameDataSection
    {
        /// <summary>Nothing in particular; used when a change cannot be attributed.</summary>
        Unknown = 0,

        /// <summary>Every section at once (the game called <c>SaveAll</c>).</summary>
        All,

        /// <summary>Level, exp, login count, accumulated work time (<c>PlayerDataV3</c>).</summary>
        Player,

        /// <summary>Birthday, icon choice, presence status (<c>ProfileData</c>).</summary>
        Profile,

        /// <summary>Timer lengths, presets, current mode (<c>PomodoroData</c>).</summary>
        Pomodoro,

        /// <summary>Todo lists and their items (<c>TodoAllData</c>).</summary>
        Todos,

        /// <summary>Note pages and their order (<c>NoteDataV2</c>).</summary>
        Notes,

        /// <summary>Habit definitions and their per-day completion (<c>HabitAll*Data</c>).</summary>
        Habits,

        /// <summary>Per-day diary, work seconds and completed todos (<c>CalendarData</c>).</summary>
        Calendar,

        /// <summary>Volumes, language, window and notification settings (<c>SettingData</c>).</summary>
        Settings,

        /// <summary>Playlist settings and the imported local library (<c>MusicSettingV2</c>).</summary>
        Music,

        /// <summary>Window view and ambient sound state (<c>EnviromentData</c>).</summary>
        Environment,

        /// <summary>Room decoration state and presets (<c>Decorations*Data</c>).</summary>
        Decorations,

        /// <summary>Outfit and hair state (<c>CostumeChangeSaveData</c>, <c>HairLotteryData</c>).</summary>
        Costume,

        /// <summary>Story playback preferences (<c>StoryData</c>).</summary>
        Story,

        /// <summary>Heroine start-direction state (<c>HeroineData</c>).</summary>
        Heroine,

        /// <summary>Episode progress and played scenario groups (<c>ScenarioProgressData</c>).</summary>
        ScenarioProgress,

        /// <summary>Unlocked environments and decorations (<c>*ProgressData</c>).</summary>
        UnlockProgress,

        /// <summary>Unlocked achievement ids (<c>AchievementSaveData</c>).</summary>
        Achievements,

        /// <summary>Point purchases (<c>PointPurchaseDataV3</c>).</summary>
        Points,

        /// <summary>Collaboration event save state (<c>CollaborationSaveData</c>).</summary>
        Collaboration,

        /// <summary>Limited-time event save state (<c>LimitedTimeEventSaveData</c>).</summary>
        LimitedTimeEvent,

        /// <summary>Automatic window-view schedule (<c>AutoTimeWindowChangeData</c>).</summary>
        AutoTimeWindow,

        /// <summary>Device/account record. Never published in full - see the privacy rules.</summary>
        Account,

        /// <summary>A section published by a mod through <see cref="IGameDataProvider"/>.</summary>
        Extra,
    }
}
