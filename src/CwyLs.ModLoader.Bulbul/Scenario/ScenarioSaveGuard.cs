using System;
using System.Reflection;
using CwyLs.ModLoader.Bulbul.MasterData;
using CwyLs.ModLoader.Core.Logging;
using CwyLs.ModLoader.Core.MasterData;
using CwyLs.ModLoader.Core.Scenario;
using HarmonyLib;
using Game = global::Bulbul;
using Master = global::Bulbul.MasterData;

namespace CwyLs.ModLoader.Bulbul.Scenario
{
    /// <summary>
    /// The save-log guard: a pack scene played through the game's long-story pipeline is not logged
    /// as played, so <c>ScenarioProgressData</c> in the cloud-synced save never names it and the
    /// story/collaboration progress stays the game's own (<see cref="ScenarioSaveRule"/>, the same
    /// rule the pack-lines feature applies at <c>SaveScenarioProgressData</c>).
    /// </summary>
    internal static class ScenarioSaveGuard
    {
        private static FieldInfo? _scenarioType;
        private static MasterDataBaseline _baseline = MasterDataBaseline.Empty;

        /// <summary>The unseen game story <c>scene play</c> is previewing; its played log is skipped once.</summary>
        internal static string? PreviewGroup { get; set; }

        /// <summary>Pack scenes kept out of the save this session.</summary>
        internal static int Skipped { get; private set; }

        /// <summary>The last group kept out, or empty.</summary>
        internal static string LastSkipped { get; private set; } = string.Empty;

        /// <summary>The game's own ids the rule compares against (refreshed at every injection).</summary>
        internal static MasterDataBaseline Baseline => _baseline;

        /// <summary>Binds the private field the guard reads. False leaves the guard off.</summary>
        /// <param name="problem">Why not.</param>
        internal static bool Prepare(out string problem)
        {
            _scenarioType = AccessTools.Field(typeof(Game.FacilityStory), ScenarioPatchTargets.StoryScenarioTypeField);
            if (_scenarioType is null || _scenarioType.FieldType != typeof(Master.ScenarioType))
            {
                problem = "FacilityStory." + ScenarioPatchTargets.StoryScenarioTypeField + " is missing or not a ScenarioType";
                return false;
            }

            problem = string.Empty;
            return true;
        }

        /// <summary>Takes the game's ids from the master-data baseline (called after every injection).</summary>
        internal static void Refresh() => _baseline = MasterDataInjector.Baseline();

        /// <summary>
        /// True when the scene the facility is about to log is a pack scene. Finds it exactly as the
        /// method does (the first group with the facility's type and episode).
        /// </summary>
        internal static bool ShouldSkip(Game.FacilityStory story)
        {
            var groupId = FindGroup(story, out var type, out var episode);
            if (groupId != null && string.Equals(groupId, PreviewGroup, StringComparison.Ordinal))
            {
                // scene play previewed an unseen game story: not recorded (backup made before it started).
                PreviewGroup = null;
                Skipped++;
                LastSkipped = groupId;
                ConsoleEvents.Emit(
                    ScenarioRuntime.Owner,
                    "guard",
                    ConsoleTags.Script,
                    groupId + " not logged as played (an unseen game story previewed by scene play; still unseen)",
                    null,
                    ("group", groupId),
                    ("type", type),
                    ("episode", episode));
                return true;
            }

            if (groupId is null || !ScenarioSaveRule.IsPackScene(groupId, _baseline))
            {
                return false;
            }

            Skipped++;
            LastSkipped = groupId;
            ConsoleEvents.Emit(
                ScenarioRuntime.Owner,
                "guard",
                ConsoleTags.Script,
                groupId + " not logged as played (pack scene; the save stays the game's own)",
                null,
                ("group", groupId),
                ("type", type),
                ("episode", episode));
            return true;
        }

        /// <summary>The group the facility would log, or null.</summary>
        internal static string? FindGroup(Game.FacilityStory story, out string type, out float episode)
        {
            type = string.Empty;
            episode = story.EpisodeNumber;
            if (_scenarioType is null || !(_scenarioType.GetValue(story) is Master.ScenarioType scenario))
            {
                return null;
            }

            type = scenario.ToString();
            var loader = MasterDataFeature.ResolveLoader();
            var groups = loader?.ScenarioGroupMasterList;
            if (groups is null)
            {
                return null;
            }

            foreach (var group in groups)
            {
                if (group != null && group.Scenario == scenario && group.EpisodeNumber == episode)
                {
                    return group.ID;
                }
            }

            return null;
        }
    }
}
