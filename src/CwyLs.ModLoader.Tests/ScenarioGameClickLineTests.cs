using CwyLs.ModLoader.Core.Scenario;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// <c>scene play</c> also starts the game's own click lines (the identical line in two languages,
    /// for screenshots); every other game scene stays refused.
    /// </summary>
    public class ScenarioGameClickLineTests
    {
        [Theory]
        [InlineData(8, true)]     // HeroineClickNormal
        [InlineData(9, true)]     // HeroineClickWork
        [InlineData(10, true)]    // HeroineClickBreak
        [InlineData(501, true)]   // HeroineClickWork_Noon
        [InlineData(553, true)]   // HeroineClickBreak_Night
        [InlineData(18, false)]   // Tutorial
        [InlineData(24, false)]   // a long story type
        [InlineData(20, false)]   // self-talk: not a click line
        [InlineData(504, false)]
        public void Only_click_line_types_qualify(int scenario, bool expected) =>
            Assert.Equal(expected, ScenePlayPlanner.IsGameClickType(scenario));

        [Fact]
        public void A_game_click_line_plays_as_a_click_talk_and_says_nothing_is_saved()
        {
            var plan = ScenePlayPlanner.Plan(Facts(gameClick: true, scenario: 8));
            Assert.Equal(ScenePlayPath.ClickTalk, plan.Path);
            Assert.Contains("nothing is saved", plan.Reason);
        }

        [Fact]
        public void Any_other_game_scene_is_still_refused()
        {
            var plan = ScenePlayPlanner.Plan(Facts(gameClick: false, scenario: 20)); // self-talk: not a click line, not a story
            Assert.Equal(ScenePlayPath.Refused, plan.Path);
            Assert.Contains("click lines", plan.Reason);
        }

        [Fact]
        public void A_game_click_line_still_waits_for_an_idle_room_and_a_free_satone()
        {
            Assert.Equal(ScenePlayPath.Refused, ScenePlayPlanner.Plan(Facts(gameClick: true, scenario: 8, idle: false)).Path);
            Assert.Equal(ScenePlayPath.Refused, ScenePlayPlanner.Plan(Facts(gameClick: true, scenario: 8, satone: false)).Path);
        }

        [Fact]
        public void A_seen_game_story_replays_through_the_history_path()
        {
            var plan = ScenePlayPlanner.Plan(Facts(gameClick: false, scenario: 1, replayable: true));
            Assert.Equal(ScenePlayPath.GameReplay, plan.Path);
        }

        [Fact]
        public void An_unseen_game_story_is_refused_and_says_why()
        {
            var plan = ScenePlayPlanner.Plan(Facts(gameClick: false, scenario: 1, replayable: false));
            Assert.Equal(ScenePlayPath.Refused, plan.Path);
            Assert.Contains("has not been seen yet", plan.Reason);
        }

        [Fact]
        public void A_seen_game_story_still_waits_for_an_idle_room()
        {
            Assert.Equal(ScenePlayPath.Refused, ScenePlayPlanner.Plan(Facts(gameClick: false, scenario: 52, replayable: true, idle: false)).Path);
        }

        [Fact]
        public void An_unseen_game_story_previews_only_with_the_toggle_on()
        {
            var on = ScenePlayPlanner.Plan(Facts(gameClick: false, scenario: 1, gameStory: true, allowUnseen: true));
            Assert.Equal(ScenePlayPath.GamePreview, on.Path);
            Assert.Contains("backed up", on.Reason);

            var off = ScenePlayPlanner.Plan(Facts(gameClick: false, scenario: 1, gameStory: true, allowUnseen: false));
            Assert.Equal(ScenePlayPath.Refused, off.Path);
            Assert.Contains("scene unseen on", off.Reason);
        }

        [Fact]
        public void A_seen_game_story_replays_even_with_the_toggle_on()
        {
            var plan = ScenePlayPlanner.Plan(Facts(gameClick: false, scenario: 1, replayable: true, gameStory: true, allowUnseen: true));
            Assert.Equal(ScenePlayPath.GameReplay, plan.Path);
        }

        [Fact]
        public void The_toggle_never_opens_other_game_scenes()
        {
            // a short game scene that is not a click line (self-talk), toggle on
            var plan = ScenePlayPlanner.Plan(Facts(gameClick: false, scenario: 20, gameStory: false, allowUnseen: true));
            Assert.Equal(ScenePlayPath.Refused, plan.Path);
        }

        private static ScenePlayFacts Facts(bool gameClick, int scenario, bool idle = true, bool satone = true, bool replayable = false, bool gameStory = false, bool allowUnseen = false) =>
            new ScenePlayFacts
            {
                GroupId = "clickheroine_normal_003",
                Found = true,
                IsPackScene = false,
                IsGameClickLine = gameClick,
                IsGameReplayable = replayable,
                IsGameStory = gameStory,
                AllowUnseenGameStories = allowUnseen,
                Injected = true,
                Scenario = scenario,
                RoomReady = true,
                RoomIdle = idle,
                SatoneFree = satone,
                SaveGuardActive = true,
            };
    }
}
