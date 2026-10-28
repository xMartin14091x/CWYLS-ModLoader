using System;
using System.Collections.Generic;

namespace CwyLs.ModLoader.Core.Data
{
    /// <summary>
    /// Which <c>Bulbul.SaveDataManager</c> methods the framework hooks, and what each one means to
    /// its two consumers: the typed event bus (<c>SaveSection</c>) and the data bus
    /// (<c>GameData.Changed</c>). Pure, so the method set and the section table are unit-tested
    /// against the game's real method list.
    /// </summary>
    /// <remarks>
    /// <para>
    /// There is exactly one Harmony postfix per hooked method (<c>SaveDataHook</c> in the game
    /// adapter), and both consumers listen to it. Earlier, the event bus and the data bus each
    /// patched the <c>Save*</c> family themselves, so every save ran two postfixes.
    /// </para>
    /// <para>
    /// Names ending in <c>Throttled</c> are never hooked: they only reserve a save that arrives
    /// shortly as the real method, and reporting both would announce one change twice.
    /// </para>
    /// </remarks>
    public static class SaveMethodRules
    {
        /// <summary>Prefix of a method that writes a save section.</summary>
        public const string SavePrefix = "Save";

        /// <summary>Prefix of a method that deletes save data.</summary>
        public const string DeletePrefix = "Delete";

        /// <summary>Suffix of a method that only schedules a later save.</summary>
        public const string ThrottledSuffix = "Throttled";

        /// <summary>
        /// Save/Delete method name to the data section it changes. A game update that adds a
        /// method is reported as <see cref="GameDataSection.Unknown"/> until this table names it.
        /// </summary>
        private static readonly Dictionary<string, GameDataSection> DataSections =
            new Dictionary<string, GameDataSection>(StringComparer.Ordinal)
            {
                ["SaveAll"] = GameDataSection.All,
                ["DeleteAllSaveData"] = GameDataSection.All,

                ["SaveAccountData"] = GameDataSection.Account,
                ["DeleteAccountData"] = GameDataSection.Account,
                ["DeleteSafeSaveKey"] = GameDataSection.Account,

                // Cloud-sync bookkeeping, not a section of the player's data: listed so it is not
                // reported as a surprise every time a game update is suspected.
                ["SaveFilesSnapshotData"] = GameDataSection.Unknown,

                ["SavePlayerData"] = GameDataSection.Player,
                ["SaveRedundantPlayerData"] = GameDataSection.Player,
                ["SaveReadBuiltinNewsID"] = GameDataSection.Player,

                ["SavePomodoroData"] = GameDataSection.Pomodoro,

                ["SaveTodo"] = GameDataSection.Todos,
                ["SaveTodoList"] = GameDataSection.Todos,
                ["DeleteTodoList"] = GameDataSection.Todos,

                ["SaveHabitHeaders"] = GameDataSection.Habits,
                ["SaveHabitDeadPeriods"] = GameDataSection.Habits,
                ["SaveHabitsMonthlyData"] = GameDataSection.Habits,

                ["SaveNote"] = GameDataSection.Notes,
                ["SaveNoteList"] = GameDataSection.Notes,
                ["SavePageData"] = GameDataSection.Notes,
                ["SavePageCache"] = GameDataSection.Notes,
                ["DeletePageData"] = GameDataSection.Notes,

                ["SaveSetting"] = GameDataSection.Settings,
                ["SaveAllPlatformSetting"] = GameDataSection.Settings,

                ["SaveStory"] = GameDataSection.Story,
                ["SaveHeroineData"] = GameDataSection.Heroine,

                ["SaveMusicSetting"] = GameDataSection.Music,
                ["SaveLocalMusicSetting"] = GameDataSection.Music,

                ["SaveEnviroment"] = GameDataSection.Environment,
                ["SaveEnviromentPreset"] = GameDataSection.Environment,

                ["SaveDecoration"] = GameDataSection.Decorations,
                ["SaveDecorationPreset"] = GameDataSection.Decorations,
                ["SaveDecorationParameter"] = GameDataSection.Decorations,

                ["SaveCostumeChangeSaveData"] = GameDataSection.Costume,
                ["SaveHairLotteryData"] = GameDataSection.Costume,

                ["SaveScenarioProgressData"] = GameDataSection.ScenarioProgress,
                ["SaveEnvironmentProgressData"] = GameDataSection.UnlockProgress,
                ["SaveDecorationProgressData"] = GameDataSection.UnlockProgress,

                ["SavePointPurchaseData"] = GameDataSection.Points,
                ["SaveCollaborationData"] = GameDataSection.Collaboration,
                ["SaveLimitedTimeEventData"] = GameDataSection.LimitedTimeEvent,
                ["SaveAutoTimeWindowChangeData"] = GameDataSection.AutoTimeWindow,
                ["SaveAchievementSaveData"] = GameDataSection.Achievements,
                ["SaveProfileData"] = GameDataSection.Profile,

                ["SaveCalenderData"] = GameDataSection.Calendar,
                ["SaveCalenderMonth"] = GameDataSection.Calendar,
            };

        /// <summary>Every method name the section table knows, for diagnostics and tests.</summary>
        public static IReadOnlyCollection<string> MappedNames => DataSections.Keys;

        /// <summary>
        /// True for a method the shared hook patches: a <c>Save*</c> or <c>Delete*</c> method that
        /// is not a <c>*Throttled</c> reservation. The caller still filters out abstract and open
        /// generic methods, which Harmony cannot patch.
        /// </summary>
        /// <param name="methodName">A public instance method name of <c>SaveDataManager</c>.</param>
        public static bool IsHooked(string? methodName) =>
            methodName != null
            && !methodName.EndsWith(ThrottledSuffix, StringComparison.Ordinal)
            && (methodName.StartsWith(SavePrefix, StringComparison.Ordinal)
                || methodName.StartsWith(DeletePrefix, StringComparison.Ordinal));

        /// <summary>
        /// True for a hooked method that writes a section, which is what the event bus reports as a
        /// <c>SaveSection</c>. <c>Delete*</c> methods feed the data bus only.
        /// </summary>
        /// <param name="methodName">The hooked method's name.</param>
        public static bool IsSave(string? methodName) =>
            IsHooked(methodName) && methodName!.StartsWith(SavePrefix, StringComparison.Ordinal);

        /// <summary>
        /// The section name a <c>SaveSection</c> event carries: the method name without its
        /// <c>Save</c> prefix, e.g. <c>SaveTodoList</c> becomes <c>TodoList</c>. A name that is only
        /// the prefix, or does not start with it, is returned unchanged.
        /// </summary>
        /// <param name="methodName">The save method's name.</param>
        public static string EventSection(string methodName)
        {
            if (methodName is null)
            {
                throw new ArgumentNullException(nameof(methodName));
            }

            return methodName.Length > SavePrefix.Length
                && methodName.StartsWith(SavePrefix, StringComparison.Ordinal)
                ? methodName.Substring(SavePrefix.Length)
                : methodName;
        }

        /// <summary>
        /// The data section a hooked method changes. False when the table does not name the method
        /// (a game update probably added it); <paramref name="section"/> is then
        /// <see cref="GameDataSection.Unknown"/>.
        /// </summary>
        /// <param name="methodName">The hooked method's name.</param>
        /// <param name="section">The section, or <see cref="GameDataSection.Unknown"/>.</param>
        public static bool TryDataSection(string? methodName, out GameDataSection section)
        {
            if (methodName != null && DataSections.TryGetValue(methodName, out section))
            {
                return true;
            }

            section = GameDataSection.Unknown;
            return false;
        }
    }
}
