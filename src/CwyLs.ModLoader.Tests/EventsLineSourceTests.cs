using CwyLs.ModLoader.Core.Events;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    public class EventsLineSourceTests
    {
        [Theory]
        // Every name below is a real member of Bulbul.MasterData.ScenarioType in game v1.17.2,
        // read out of the shipped Assembly-CSharp rather than invented.
        [InlineData("Tutorial", LineSource.Tutorial)]
        [InlineData("SpeakWord_PomodoroStart", LineSource.Pomodoro)]
        [InlineData("SpeakWord_PomodoroBreak", LineSource.Pomodoro)]
        [InlineData("SpeakWord_Pomodoro_WorkLongStart", LineSource.Pomodoro)]
        [InlineData("HeroineClickNormal", LineSource.ClickReaction)]
        [InlineData("HeroineClickWork_Morning", LineSource.ClickReaction)]
        [InlineData("HeroineSelfShortTalkBreak", LineSource.SelfTalk)]
        [InlineData("ShortConversation", LineSource.SelfTalk)]
        [InlineData("SmallTalk", LineSource.SelfTalk)]
        [InlineData("GameStart_First_CameraTouch", LineSource.SelfTalk)]
        [InlineData("GameEnd", LineSource.SelfTalk)]
        [InlineData("Photograph_GameStart_CameraTouch", LineSource.SelfTalk)]
        [InlineData("MainScenario", LineSource.Story)]
        [InlineData("AfterScenario", LineSource.Story)]
        [InlineData("DLCScenario", LineSource.Story)]
        [InlineData("ExtraScenario", LineSource.Story)]
        [InlineData("MotionScenario", LineSource.Story)]
        [InlineData("Special_BearsRestaurant", LineSource.Story)]
        [InlineData("Event_2026_AprilFool", LineSource.Story)]
        [InlineData("GameDemo_Talk", LineSource.Story)]
        [InlineData("None", LineSource.Other)]
        public void Every_shipped_scenario_type_classifies_as_expected(string scenarioType, LineSource expected) =>
            Assert.Equal(expected, LineSourceClassifier.Classify(scenarioType));

        [Fact]
        public void No_scenario_playing_means_no_known_source()
        {
            Assert.Equal(LineSource.Other, LineSourceClassifier.Classify(null));
            Assert.Equal(LineSource.Other, LineSourceClassifier.Classify(string.Empty));
        }

        [Fact]
        public void A_scenario_type_a_future_update_adds_still_classifies_by_its_prefix()
        {
            Assert.Equal(LineSource.ClickReaction, LineSourceClassifier.Classify("HeroineClickSomethingNew"));
            Assert.Equal(LineSource.Story, LineSourceClassifier.Classify("Special_Whatever2027"));
            Assert.Equal(LineSource.Pomodoro, LineSourceClassifier.Classify("SpeakWord_Pomodoro_BrandNew"));
            Assert.Equal(LineSource.Other, LineSourceClassifier.Classify("SomethingNobodyAnticipated"));
        }

        [Fact]
        public void Tutorial_and_pomodoro_win_over_the_broader_prefixes()
        {
            // A name can satisfy more than one rule; the specific reason is the useful one.
            Assert.Equal(LineSource.Tutorial, LineSourceClassifier.Classify("GameStart_Tutorial"));
            Assert.Equal(LineSource.Pomodoro, LineSourceClassifier.Classify("HeroineClickPomodoroFinish"));
        }
    }
}
