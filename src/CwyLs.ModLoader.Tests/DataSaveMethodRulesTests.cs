using System;
using System.Collections.Generic;
using System.Linq;
using CwyLs.ModLoader.Core.Data;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// C5: one save hook feeds both buses. These tests pin the hook's method set against the
    /// game's real <c>SaveDataManager</c> and prove it is exactly what the two separate postfixes
    /// covered before - so consolidating them changed who observes a save, not which saves are seen.
    /// </summary>
    public class DataSaveMethodRulesTests
    {
        /// <summary>
        /// Every public instance method of <c>Bulbul.SaveDataManager</c> in game 1.17.3, overloads
        /// listed once each.
        /// </summary>
        private static readonly string[] GameMethods =
        {
            "Load", "SaveAll", "DeleteAllSaveData", "SaveAccountData", "LoadAccountData", "DeleteAccountData",
            "SavePlayerData", "LoadPlayerData", "SaveRedundantPlayerData", "LoadRedundantPlayerData",
            "SavePomodoroData", "SaveTodo", "SaveTodoList", "DeleteTodoList", "SaveHabitHeaders",
            "SaveHabitHeadersThrottled", "SaveHabitDeadPeriods", "SaveHabitDeadPeriodsThrottled",
            "TryGetOrLoadHabitsMonthlyData", "AddHabitsMonthlyData", "SaveHabitsMonthlyData", "SaveNote",
            "SaveNoteList", "SavePageData", "SavePageCache", "LoadPageData", "DeletePageData", "SaveSetting",
            "LoadSetting", "SaveAllPlatformSetting", "LoadAllPlatformSetting", "SaveStory", "SaveHeroineData",
            "SaveMusicSetting", "SaveLocalMusicSetting", "SaveEnviroment", "SaveEnviromentThrottled",
            "SaveEnviromentPreset", "SaveEnviromentPresetThrottled", "SaveDecoration", "SaveDecorationThrottled",
            "SaveDecorationPreset", "SaveDecorationPresetThrottled", "SaveDecorationParameter",
            "SaveCostumeChangeSaveData", "SaveHairLotteryData", "SaveScenarioProgressData",
            "SaveEnvironmentProgressData", "SaveDecorationProgressData", "SavePointPurchaseData",
            "SaveCollaborationData", "SaveLimitedTimeEventData", "SaveAchievementSaveData", "SaveProfileData",
            "SaveFilesSnapshotData", "TryLoadFilesSnapshotData", "SetSafeSaveMark", "HasSafeSaveKey",
            "DeleteSafeSaveKey", "SaveReadBuiltinNewsID", "TryGetReadBuiltinNewsID", "SetSaveWarningNeverShowMark",
            "HasSaveWarningNeverShowKey", "SaveAutoTimeWindowChangeData", "LoadCalenderData", "TryLoadCalenderData",
            "SaveCalenderData", "SaveCalenderMonth",
        };

        /// <summary>The event bus's filter before C5 (EventPatches.PatchSaveMethods).</summary>
        private static bool OldEventFilter(string name) =>
            name.StartsWith("Save", StringComparison.Ordinal) && !name.EndsWith("Throttled", StringComparison.Ordinal);

        /// <summary>The data bus's filter before C5 (SaveDataPatches.IsChangeMethod).</summary>
        private static bool OldDataFilter(string name) =>
            !name.EndsWith("Throttled", StringComparison.Ordinal)
            && (name.StartsWith("Save", StringComparison.Ordinal) || name.StartsWith("Delete", StringComparison.Ordinal));

        [Fact]
        public void The_hook_covers_exactly_what_the_data_bus_patched_before()
        {
            foreach (var name in GameMethods)
            {
                Assert.Equal(OldDataFilter(name), SaveMethodRules.IsHooked(name));
            }
        }

        [Fact]
        public void SaveSection_is_published_for_exactly_what_the_event_bus_patched_before()
        {
            foreach (var name in GameMethods)
            {
                Assert.Equal(OldEventFilter(name), SaveMethodRules.IsSave(name));
            }
        }

        [Fact]
        public void Every_method_the_event_bus_needs_is_on_the_one_hook()
        {
            // The consolidation is only safe if the event bus's set is a subset of the hook's set:
            // then one postfix per hooked method serves both listeners.
            foreach (var name in GameMethods.Where(SaveMethodRules.IsSave))
            {
                Assert.True(SaveMethodRules.IsHooked(name), name);
            }
        }

        [Fact]
        public void The_game_has_45_hooked_names_40_of_them_saves_and_5_deletes()
        {
            // SaveCalenderData has two overloads, so the running game patches 46 methods (41 saves):
            // the numbers the data SETUP line (patched=46) and the events line (saves=41) print.
            var hooked = GameMethods.Where(SaveMethodRules.IsHooked).ToList();
            var saves = GameMethods.Where(SaveMethodRules.IsSave).ToList();

            Assert.Equal(45, hooked.Count);
            Assert.Equal(40, saves.Count);
            Assert.Equal(5, hooked.Count(n => n.StartsWith("Delete", StringComparison.Ordinal)));
        }

        [Theory]
        [InlineData("SaveHabitHeadersThrottled")]
        [InlineData("SaveEnviromentThrottled")]
        [InlineData("SaveDecorationPresetThrottled")]
        [InlineData("LoadSetting")]
        [InlineData("LoadAllPlatformSetting")]
        [InlineData("SetSafeSaveMark")]
        [InlineData("HasSaveWarningNeverShowKey")]
        [InlineData("TryLoadFilesSnapshotData")]
        [InlineData("")]
        [InlineData(null)]
        public void Throttled_reservations_loads_and_queries_are_not_hooked(string? name)
        {
            Assert.False(SaveMethodRules.IsHooked(name));
            Assert.False(SaveMethodRules.IsSave(name));
        }

        [Theory]
        [InlineData("DeleteTodoList")]
        [InlineData("DeletePageData")]
        [InlineData("DeleteAllSaveData")]
        public void Deletes_feed_the_data_bus_but_are_not_a_SaveSection(string name)
        {
            Assert.True(SaveMethodRules.IsHooked(name));
            Assert.False(SaveMethodRules.IsSave(name));
        }

        [Fact]
        public void Every_hooked_game_method_has_a_data_section()
        {
            var unmapped = GameMethods
                .Where(SaveMethodRules.IsHooked)
                .Where(n => !SaveMethodRules.TryDataSection(n, out _))
                .ToList();

            Assert.Empty(unmapped);
        }

        [Fact]
        public void The_section_table_names_no_method_the_game_does_not_have()
        {
            var game = new HashSet<string>(GameMethods, StringComparer.Ordinal);
            foreach (var name in SaveMethodRules.MappedNames)
            {
                Assert.Contains(name, game);
                Assert.True(SaveMethodRules.IsHooked(name), name);
            }
        }

        [Theory]
        [InlineData("SaveTodoList", GameDataSection.Todos)]
        [InlineData("DeleteTodoList", GameDataSection.Todos)]
        [InlineData("SaveAll", GameDataSection.All)]
        [InlineData("SaveNoteList", GameDataSection.Notes)]
        [InlineData("SaveCalenderData", GameDataSection.Calendar)]
        [InlineData("SaveFilesSnapshotData", GameDataSection.Unknown)]
        public void Sections_are_the_ones_the_data_bus_reported_before(string name, GameDataSection expected)
        {
            Assert.True(SaveMethodRules.TryDataSection(name, out var section));
            Assert.Equal(expected, section);
        }

        [Fact]
        public void A_method_a_game_update_adds_is_Unknown_and_reported_as_unmapped()
        {
            Assert.False(SaveMethodRules.TryDataSection("SaveBrandNewThing", out var section));
            Assert.Equal(GameDataSection.Unknown, section);
            Assert.False(SaveMethodRules.TryDataSection(null, out section));
            Assert.Equal(GameDataSection.Unknown, section);
        }

        [Theory]
        [InlineData("SaveTodoList", "TodoList")]
        [InlineData("SavePomodoroData", "PomodoroData")]
        [InlineData("SaveAll", "All")]
        [InlineData("Save", "Save")]
        [InlineData("DeleteTodoList", "DeleteTodoList")]
        public void The_event_section_is_the_name_without_Save(string name, string expected)
        {
            Assert.Equal(expected, SaveMethodRules.EventSection(name));
        }

        [Fact]
        public void The_event_section_refuses_null()
        {
            Assert.Throws<ArgumentNullException>(() => SaveMethodRules.EventSection(null!));
        }
    }
}
