using System;

namespace CwyLs.ModLoader.Core.Scenario
{
    /// <summary>How <c>scene play</c> starts a scene.</summary>
    public enum ScenePlayPath
    {
        /// <summary>It does not.</summary>
        Refused = 0,

        /// <summary>Through the game's own long-story pipeline (<c>IStoryController.StartStory</c>): fade, music pause, UI, skip, tidy.</summary>
        LongStory = 1,

        /// <summary>As a click-style talk (<c>StartReady</c> + <c>StartClickHeroineReaction</c>), the way a click on Satone plays one.</summary>
        ClickTalk = 2,

        /// <summary>
        /// A story episode the player has already seen, replayed exactly as the game's history screen
        /// does (<c>StorySelectUI</c> starts it as a talk-log replay).
        /// </summary>
        GameReplay = 3,

        /// <summary>
        /// A game story episode the player has not seen, played only while <c>[Script]
        /// AllowUnseenGameStories</c> is on: the save folder is backed up first, it plays as a rewatch
        /// (<c>isTalkLog: true</c>), and the played log is skipped so it is not recorded as seen.
        /// </summary>
        GamePreview = 4,
    }

    /// <summary>What the game side found out about a <c>scene play</c> request. Immutable once built.</summary>
    public sealed class ScenePlayFacts
    {
        /// <summary>The group asked for.</summary>
        public string GroupId { get; init; } = string.Empty;

        /// <summary>True when the packs have a group with that id.</summary>
        public bool Found { get; init; }

        /// <summary>True when it is a group a pack added (<see cref="ScenarioSaveRule.IsPackScene"/>).</summary>
        public bool IsPackScene { get; init; }

        /// <summary>
        /// True when it is one of the game's own click lines (<see cref="ScenePlayPlanner.IsGameClickType"/>):
        /// the one kind of game scene <c>scene play</c> also starts.
        /// </summary>
        public bool IsGameClickLine { get; init; }

        /// <summary>
        /// True when it is a game story episode in the game's own replay list
        /// (<c>FacilityStory.PlayableScenario</c>, what the history screen offers): already seen.
        /// </summary>
        public bool IsGameReplayable { get; init; }

        /// <summary>True when it is one of the game's own long-story episodes (seen or not).</summary>
        public bool IsGameStory { get; init; }

        /// <summary><c>[Script] AllowUnseenGameStories</c> (console <c>scene unseen on|off</c>).</summary>
        public bool AllowUnseenGameStories { get; init; }

        /// <summary>True when the group is in the game's master data now (injected, not held back).</summary>
        public bool Injected { get; init; }

        /// <summary>Its <c>ScenarioType</c> number.</summary>
        public int Scenario { get; init; }

        /// <summary>The group the game would find for this group's (type, episode); null when none.</summary>
        public string? EpisodeOwner { get; init; }

        /// <summary>The scene's first lint error, or null.</summary>
        public string? FirstLintError { get; init; }

        /// <summary>RoomScene is loaded and its services resolve.</summary>
        public bool RoomReady { get; init; }

        /// <summary>A scene is playing (<c>ScenarioReader.IsPlayingScenario</c>).</summary>
        public bool ScenePlaying { get; init; }

        /// <summary>The room's main state is <c>Idle</c>.</summary>
        public bool RoomIdle { get; init; }

        /// <summary>Satone can be talked to now (<c>HeroineService.IsPossibleTalk</c>).</summary>
        public bool SatoneFree { get; init; }

        /// <summary>The save-log guard is installed (a long pack scene must never reach the save).</summary>
        public bool SaveGuardActive { get; init; }
    }

    /// <summary>The decision of <see cref="ScenePlayPlanner.Plan"/>. Immutable.</summary>
    public sealed class ScenePlayPlan
    {
        internal ScenePlayPlan(ScenePlayPath path, string reason)
        {
            Path = path;
            Reason = reason;
        }

        /// <summary>How the scene starts, or <see cref="ScenePlayPath.Refused"/>.</summary>
        public ScenePlayPath Path { get; }

        /// <summary>Why it was refused, or which pipeline it takes.</summary>
        public string Reason { get; }

        /// <summary>True unless refused.</summary>
        public bool IsAccepted => Path != ScenePlayPath.Refused;
    }

    /// <summary>
    /// The rules of <c>scene play</c> (and the <c>scene.play</c> action, and the Scenes facet): only
    /// pack scenes without lint errors, only when the room is idle and Satone can talk; long types
    /// through the game's own pipeline and only with the save guard in place, everything else as a
    /// click-style talk. Pure.
    /// </summary>
    public static class ScenePlayPlanner
    {
        /// <summary><c>ScenarioType.Tutorial</c>: never started by hand (it drives the tutorial service).</summary>
        public const int TutorialScenario = 18;

        /// <summary>
        /// The game's click-line types (<c>ScenarioType</c>: HeroineClickNormal 8, HeroineClickWork 9,
        /// HeroineClickBreak 10, the time-of-day work and break variants 500-503 and 550-553). Clicking
        /// Satone plays these anyway, they write nothing to the save, and starting one by id does not
        /// touch the game's shuffled list - so <c>scene play</c> may start them.
        /// </summary>
        public static bool IsGameClickType(int scenario) =>
            scenario == 8 || scenario == 9 || scenario == 10
            || (scenario >= 500 && scenario <= 503)
            || (scenario >= 550 && scenario <= 553);

        /// <summary>Decides.</summary>
        /// <param name="facts">What the game side found.</param>
        /// <returns>The decision.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="facts"/> is null.</exception>
        public static ScenePlayPlan Plan(ScenePlayFacts facts)
        {
            if (facts is null)
            {
                throw new ArgumentNullException(nameof(facts));
            }

            var id = facts.GroupId;
            if (!facts.Found)
            {
                return Refuse("no scene \"" + id + "\": not a pack scene (scene list shows them), a game click line or a game story episode (main_01 ... later_03, special_*)");
            }

            var unseenPreview = facts.IsGameStory && facts.AllowUnseenGameStories;
            if (!facts.IsPackScene && !facts.IsGameClickLine && !facts.IsGameReplayable && !unseenPreview)
            {
                return Refuse(ScenarioLint.IsLongStory(facts.Scenario)
                    ? id + " is one of the game's own story episodes and has not been seen yet; scene play replays a game story once the player has seen it (as the game's history does). To preview it anyway, turn on 'scene unseen on': the save is backed up first and the episode is not recorded as seen"
                    : id + " is one of the game's own scenes; scene play starts pack scenes, the game's own click lines and story episodes already seen, so the game's progress and save stay its own");
            }

            if (!facts.Injected)
            {
                return Refuse(id + " is not in the game's data now (held back or refused at injection; see scene lint and the MASTERDATA lines)");
            }

            if (facts.FirstLintError != null)
            {
                return Refuse(id + " has lint errors; fix them first: " + facts.FirstLintError);
            }

            if (facts.Scenario == TutorialScenario)
            {
                return Refuse(id + " is a Tutorial scene; the tutorial is started by the game only");
            }

            if (!facts.RoomReady)
            {
                return Refuse("the room is not ready (RoomScene not loaded yet)");
            }

            if (facts.ScenePlaying)
            {
                return Refuse("a scene is playing; try again when it has ended");
            }

            if (!facts.RoomIdle)
            {
                return Refuse("the room is busy (a story, the tutorial or the exit is running)");
            }

            if (!facts.SatoneFree)
            {
                return Refuse("Satone cannot talk right now (asleep, away from the desk or busy)");
            }

            if (facts.IsGameReplayable && !facts.IsPackScene)
            {
                return new ScenePlayPlan(ScenePlayPath.GameReplay, "the game's own history replay (already seen)");
            }

            if (unseenPreview && !facts.IsPackScene)
            {
                return new ScenePlayPlan(ScenePlayPath.GamePreview, "an unseen game story, previewed (save backed up first; not recorded as seen)");
            }

            if (ScenarioLint.IsLongStory(facts.Scenario))
            {
                if (!facts.SaveGuardActive)
                {
                    return Refuse("the save-log guard is not installed, so a long pack scene would be written into the game's save");
                }

                if (!string.Equals(facts.EpisodeOwner, id, StringComparison.Ordinal))
                {
                    return Refuse("the game would play " + (facts.EpisodeOwner ?? "nothing") + " for this scene's type and episode (scene lint: S03)");
                }

                return new ScenePlayPlan(ScenePlayPath.LongStory, "the game's long-story pipeline");
            }

            return new ScenePlayPlan(
                ScenePlayPath.ClickTalk,
                facts.IsPackScene ? "a click-style talk" : "the game's own click line (a click-style talk; nothing is saved)");
        }

        private static ScenePlayPlan Refuse(string reason) => new ScenePlayPlan(ScenePlayPath.Refused, reason);
    }
}
