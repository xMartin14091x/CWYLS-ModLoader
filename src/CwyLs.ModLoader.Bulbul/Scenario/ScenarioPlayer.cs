using System;
using CwyLs.ModLoader.Bulbul.Events;
using CwyLs.ModLoader.Bulbul.MasterData;
using CwyLs.ModLoader.Bulbul.Satone.Motion;
using CwyLs.ModLoader.Core.Logging;
using CwyLs.ModLoader.Core.MasterData;
using CwyLs.ModLoader.Core.Scenario;
using CwyLs.ModLoader.Threading;
using HarmonyLib;
using Game = global::Bulbul;
using Master = global::Bulbul.MasterData;

namespace CwyLs.ModLoader.Bulbul.Scenario
{
    /// <summary>
    /// Plays a pack scene on request (<c>scene play</c>, the <c>scene.play</c> action, the Scenes facet):
    /// long story types through the game's own pipeline, everything else as a click-style talk. The
    /// rules are <see cref="ScenePlayPlanner"/>'s; this class gathers the facts and starts the scene.
    /// </summary>
    internal static class ScenarioPlayer
    {
        /// <summary>The last scene started, or empty.</summary>
        internal static string LastPlayed { get; private set; } = string.Empty;

        /// <summary>
        /// Plays a pack scene, or one of the game's own click lines, now. Main thread only.
        /// </summary>
        /// <param name="groupOrRowId">A pack scene's group id or the id of one of its rows (the scene starts from its beginning), or a game click line's group id (e.g. <c>clickheroine_normal_003</c>).</param>
        /// <param name="by">Who asks (for the log): <c>console</c>, <c>mod:&lt;id&gt;</c>, <c>action:&lt;caller&gt;</c>.</param>
        /// <returns>What happened, or why not.</returns>
        internal static ScenarioResult Play(string? groupOrRowId, string by)
        {
            if (!MainThread.IsMainThread)
            {
                return ScenarioResult.Refused("scene play runs on the game's main thread");
            }

            if (!ScenarioFeature.IsActive)
            {
                return ScenarioResult.Refused("script commands are off: " + ScenarioFeature.InactiveReason);
            }

            var id = Resolve(groupOrRowId?.Trim() ?? string.Empty);
            ScenePlayFacts facts;
            Master.ScenarioGroupData? group;
            Game.ScenarioReader? reader;
            Game.IStoryController? story;
            try
            {
                facts = Gather(id, out group, out reader, out story);
            }
            catch (Exception ex)
            {
                // A game member the room checks read moved (seamcheck would name it): refuse, never throw.
                return Refuse(id, "the room could not be checked: " + ex.GetType().Name + ": " + ex.Message, by);
            }

            var plan = ScenePlayPlanner.Plan(facts);
            if (!plan.IsAccepted)
            {
                return Refuse(id, plan.Reason, by);
            }

            try
            {
                if (plan.Path == ScenePlayPath.GamePreview)
                {
                    // Unseen game story (behind a config toggle): back the save up first, play it as a rewatch, and
                    // skip its played log once (ScenarioSaveGuard.PreviewGroup) so it stays unseen.
                    if (!ScenePreviewBackupFolder.TryMake(id, out var backup))
                    {
                        return Refuse(id, "the save backup failed, so the preview did not start: " + backup, by);
                    }

                    ScenarioSaveGuard.PreviewGroup = id;
                    ConsoleEvents.Emit(ScenarioRuntime.Owner, "backup", ConsoleTags.Script, "save backed up before previewing " + id + ": " + backup, "path", ("group", id), ("path", backup));
                    story!.StartStory((Master.ScenarioType)facts.Scenario, group!.EpisodeNumber, true);
                }
                else if (plan.Path == ScenePlayPath.GameReplay)
                {
                    // Exactly the history screen's call (StorySelectUI): the game treats it as a
                    // rewatch (no new-story achievement, no collaboration progress; FinishReadEpisode only
                    // moves the main progress for the next unread episode, which a seen one never is).
                    story!.StartStory((Master.ScenarioType)facts.Scenario, group!.EpisodeNumber, true);
                }
                else if (plan.Path == ScenePlayPath.LongStory)
                {
                    // The same call the game's WantTalk click makes (RoomGameManager ->
                    // FacilityManagerForPC): RoomGameManager picks it up in its Idle state
                    // and runs the fade, Ready, StartStory, end and tidy itself.
                    story!.StartStory((Master.ScenarioType)facts.Scenario, group!.EpisodeNumber, false);
                }
                else
                {
                    // A click reaction's pair (as FacilityClickHeroine starts one), and its end handling
                    // (FacilityClickHeroine, RoomGameManager.OnEndPlayedReaction) once the scene ends.
                    reader!.StartReady((Master.ScenarioType)facts.Scenario, id, Game.ScenarioReader.ScenarioReadMode.Auto, false, false, false);
                    reader.StartClickHeroineReaction();
                    var started = reader;
                    reader.AddEndCallback(() => MainThread.Post("script scene end " + id, () => EndClickTalk(started, id), ScenarioRuntime.Owner));
                }
            }
            catch (Exception ex)
            {
                return Refuse(id, "the game refused to start it: " + ex.GetType().Name + ": " + ex.Message, by);
            }

            LastPlayed = id;
            ConsoleEvents.Emit(
                ScenarioRuntime.Owner,
                "play",
                ConsoleTags.Script,
                "playing " + id + " (" + plan.Reason + ")",
                "by",
                ("group", id),
                ("by", by),
                ("path", plan.Path.ToString()));
            return ScenarioResult.Ok("playing " + id + " as " + plan.Reason);
        }

        /// <summary>The facts <see cref="ScenePlayPlanner"/> needs, read from the packs and the room.</summary>
        internal static ScenePlayFacts Gather(string id, out Master.ScenarioGroupData? group, out Game.ScenarioReader? reader, out Game.IStoryController? story)
        {
            group = null;
            reader = null;
            story = null;
            var pack = FindPackGroup(id);
            var loader = MasterDataFeature.ResolveLoader();
            string? episodeOwner = null;
            if (pack != null && loader?.ScenarioGroupMasterList != null)
            {
                foreach (var candidate in loader.ScenarioGroupMasterList)
                {
                    if (candidate is null)
                    {
                        continue;
                    }

                    if (group is null && string.Equals(candidate.ID, id, StringComparison.Ordinal))
                    {
                        group = candidate;
                    }

                    if (episodeOwner is null && (int)candidate.Scenario == pack.Scenario && candidate.EpisodeNumber == pack.EpisodeNumber)
                    {
                        episodeOwner = candidate.ID;
                    }
                }
            }

            // Not a pack scene: one of the game's own click lines may still be played by id.
            var gameClick = false;
            if (pack is null && loader?.ScenarioGroupMasterList != null)
            {
                foreach (var candidate in loader.ScenarioGroupMasterList)
                {
                    if (candidate != null && string.Equals(candidate.ID, id, StringComparison.Ordinal))
                    {
                        group = candidate;
                        gameClick = ScenePlayPlanner.IsGameClickType((int)candidate.Scenario);
                        break;
                    }
                }
            }

            // A game story episode: replayable when the game's own history screen would offer it.
            var gameStory = pack is null && group != null && ScenarioLint.IsLongStory((int)group.Scenario);
            var replayable = false;
            if (gameStory && GameScopes.TryRoom<Game.IStoryController>(out var storyForList) && storyForList != null)
            {
                replayable = IsInHistory(storyForList, id);
            }

            var roomReady = GameScopes.TryRoom<Game.ScenarioReader>(out reader) && reader != null
                            && GameScopes.TryRoom<Game.IStoryController>(out story) && story != null;
            var room = roomReady && GameScopes.TryRoom<Game.RoomGameManager>(out var manager) && manager != null
                ? manager
                : null;
            var satoneFree = SatoneSeam.TryHeroine(out var heroine) && heroine != null && heroine.IsPossibleTalk();
            var firstError = ScenarioFeature.Lint.FirstErrorOf(id);

            return new ScenePlayFacts
            {
                GroupId = id,
                Found = pack != null || gameClick || gameStory,
                IsPackScene = pack != null && ScenarioSaveRule.IsPackScene(id, ScenarioSaveGuard.Baseline),
                IsGameClickLine = gameClick,
                IsGameReplayable = replayable,
                IsGameStory = gameStory,
                AllowUnseenGameStories = ScenarioFeature.AllowUnseenGameStories,
                Injected = group != null,
                Scenario = pack?.Scenario ?? (group != null ? (int)group.Scenario : 0),
                EpisodeOwner = episodeOwner,
                FirstLintError = firstError?.ToString(),
                RoomReady = room != null,
                ScenePlaying = reader != null && reader.IsPlayingScenario(),
                RoomIdle = room != null && room.CurrentMainState == Game.RoomGameManager.MainState.Idle,
                SatoneFree = satoneFree,
                SaveGuardActive = ScenarioPatches.GuardApplied,
            };
        }

        /// <summary>True when the game's replay list (<c>FacilityStory.PlayableScenario</c>) holds the group.</summary>
        private static bool IsInHistory(Game.IStoryController story, string id)
        {
            var facility = GameScopes.TryRoom<Game.FacilityStory>(out var found) && found != null
                ? found
                : AccessTools.Field(story.GetType(), ScenarioPatchTargets.ManagerStoryField)?.GetValue(story) as Game.FacilityStory;
            // Read as a plain IEnumerable: the property's type lives in ObservableCollections, which this
            // assembly does not reference.
            if (facility is null
                || !(AccessTools.Property(facility.GetType(), "PlayableScenario")?.GetValue(facility) is System.Collections.IEnumerable list))
            {
                return false;
            }

            foreach (var item in list)
            {
                if (item is Master.ScenarioGroupData data && string.Equals(data.ID, id, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>The pack row for a group id, or null.</summary>
        internal static ScenarioGroupRow? FindPackGroup(string id)
        {
            foreach (var group in MasterDataFeature.Merged.ScenarioGroups)
            {
                if (string.Equals(group.Id, id, StringComparison.Ordinal))
                {
                    return group;
                }
            }

            return null;
        }

        private static string Resolve(string id)
        {
            if (id.Length == 0 || FindPackGroup(id) != null)
            {
                return id;
            }

            foreach (var row in MasterDataFeature.Merged.Novel)
            {
                if (string.Equals(row.Id, id, StringComparison.Ordinal))
                {
                    return row.ScenarioGroupId;
                }
            }

            return id;
        }

        /// <summary>After a click-style scene: Satone's click state ends and her look and face reset, as after a click reaction.</summary>
        private static void EndClickTalk(Game.ScenarioReader reader, string id)
        {
            try
            {
                reader.EndClickHeroineReaction();
                if (SatoneSeam.TryHeroine(out var heroine) && heroine != null)
                {
                    heroine.LookInitSlowly();
                    heroine.InitHeroineFacialAfterDelay(0.77f);
                }

                ConsoleEvents.File(ScenarioRuntime.Owner, "playEnd", ("group", id));
            }
            catch (Exception ex)
            {
                ScenarioRuntime.WarnAlways("ending the talk " + id + " failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static ScenarioResult Refuse(string id, string reason, string by)
        {
            ConsoleEvents.File(ScenarioRuntime.Owner, "playRefused", ("group", id), ("by", by), ("reason", reason));
            return ScenarioResult.Refused(reason);
        }
    }
}
