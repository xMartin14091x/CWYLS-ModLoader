using System.Collections.Generic;
using CwyLs.ModLoader.Core.Events;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// A2: one <c>ScenarioEnded</c> per started scenario. The sequences below are the ones the
    /// one recorded play session produced (its console log and trace files): 35 starts,
    /// 37 ends, the two extra ends coming from HeroineStateGameStartDirection.EndSpeak
    /// (HeroineStateGameStartDirection) and UIManagerForPC.OnClickExitButton
    /// (UIManagerForPC).
    /// </summary>
    public class EventsScenarioEndTrackerTests
    {
        [Fact]
        public void Game_start_greeting_ended_twice_in_consecutive_frames_publishes_once()
        {
            // 12:22:02.851 start, 12:22:09.252 end (reader, frame 11495), 12:22:09.274 end (EndSpeak, frame 11496).
            var tracker = new ScenarioEndTracker();

            tracker.Start("game_start_02_005");
            var first = tracker.End("game_start_02_005");
            var second = tracker.End("game_start_02_005");

            Assert.True(first.Publish);
            Assert.Equal("game_start_02_005", first.GroupId);
            Assert.Null(first.Reason);

            Assert.False(second.Publish);
            Assert.Equal("game_start_02_005", second.GroupId);
            Assert.Equal(ScenarioEndReason.AlreadyClosed, second.Reason);

            Assert.Equal(1L, tracker.Published);
            Assert.Equal(1L, tracker.Suppressed);
            Assert.Equal(0, tracker.OpenCount);
        }

        [Fact]
        public void Exit_button_end_after_a_click_reaction_is_suppressed_and_the_goodbye_still_publishes()
        {
            // 19:25:00.135 start clickheroine_normal_018, 19:25:02.340 end, 19:25:03.253 end from
            // OnClickExitButton (the reader still reports the stale id), then the GameEnd scene.
            var tracker = new ScenarioEndTracker();

            tracker.Start("clickheroine_normal_018");
            Assert.True(tracker.End("clickheroine_normal_018").Publish);

            var stray = tracker.End("clickheroine_normal_018");
            Assert.False(stray.Publish);
            Assert.Equal(ScenarioEndReason.AlreadyClosed, stray.Reason);

            tracker.Start("game_end_01_001");
            Assert.True(tracker.End("game_end_01_001").Publish);

            Assert.Equal(2L, tracker.Published);
            Assert.Equal(1L, tracker.Suppressed);
        }

        [Fact]
        public void An_end_with_no_matching_start_is_suppressed_as_never_opened()
        {
            // The exit button on a fresh reader, or a group the tracker never saw start.
            var tracker = new ScenarioEndTracker();

            var unknown = tracker.End("clickheroine_normal_018");
            var none = tracker.End(null);
            var empty = tracker.End(string.Empty);

            Assert.False(unknown.Publish);
            Assert.Equal(ScenarioEndReason.NeverOpened, unknown.Reason);

            Assert.False(none.Publish);
            Assert.Null(none.GroupId);
            Assert.Equal(ScenarioEndReason.NeverOpened, none.Reason);

            Assert.False(empty.Publish);
            Assert.Null(empty.GroupId);

            Assert.Equal(0L, tracker.Published);
            Assert.Equal(3L, tracker.Suppressed);
        }

        [Fact]
        public void Replaying_the_whole_2026_09_24_session_publishes_35_ends_for_35_starts()
        {
            var tracker = new ScenarioEndTracker();
            var published = new List<string>();
            var suppressed = new List<(int Index, string Id, string? Reason)>();

            for (var i = 0; i < Session20260924.Length; i++)
            {
                var (kind, id) = Session20260924[i];
                if (kind == 's')
                {
                    tracker.Start(id);
                    continue;
                }

                var decision = tracker.End(id);
                if (decision.Publish)
                {
                    published.Add(id);
                }
                else
                {
                    suppressed.Add((i, id, decision.Reason));
                }
            }

            Assert.Equal(35, published.Count);
            Assert.Equal(35L, tracker.Published);
            Assert.Equal(0, tracker.OpenCount);

            Assert.Equal(
                new[]
                {
                    (2, "game_start_02_005", (string?)ScenarioEndReason.AlreadyClosed),
                    (69, "clickheroine_normal_018", (string?)ScenarioEndReason.AlreadyClosed),
                },
                suppressed.ToArray());
        }

        [Fact]
        public void The_same_id_started_and_ended_twice_publishes_twice()
        {
            // main_11 really did this in the session: 12:25:04 start, 12:26:36 end, 12:28:18 start, 12:28:21 end.
            var tracker = new ScenarioEndTracker();

            tracker.Start("main_11");
            Assert.True(tracker.End("main_11").Publish);
            tracker.Start("main_11");
            Assert.True(tracker.End("main_11").Publish);

            Assert.Equal(2L, tracker.Published);
            Assert.Equal(0L, tracker.Suppressed);
        }

        [Fact]
        public void Interleaved_ids_close_independently()
        {
            // A mod's Say scenario under a game scene: each closes on its own first end.
            var tracker = new ScenarioEndTracker();

            tracker.Start("smalltalk_05");
            tracker.Start("cwy_say_3");
            Assert.Equal(2, tracker.OpenCount);

            Assert.True(tracker.End("smalltalk_05").Publish);
            Assert.True(tracker.IsOpen("cwy_say_3"));
            Assert.False(tracker.IsOpen("smalltalk_05"));

            Assert.True(tracker.End("cwy_say_3").Publish);

            var repeatA = tracker.End("smalltalk_05");
            var repeatB = tracker.End("cwy_say_3");
            Assert.False(repeatA.Publish);
            Assert.False(repeatB.Publish);
            Assert.Equal(ScenarioEndReason.AlreadyClosed, repeatA.Reason);
            Assert.Equal(ScenarioEndReason.AlreadyClosed, repeatB.Reason);

            Assert.Equal(2L, tracker.Published);
            Assert.Equal(2L, tracker.Suppressed);
        }

        [Fact]
        public void Restarting_an_open_id_does_not_nest_it()
        {
            // One reader: a second StartReady for the same group replaces the run, so one end follows.
            var tracker = new ScenarioEndTracker();

            tracker.Start("main_12");
            tracker.Start("main_12");

            Assert.Equal(1, tracker.OpenCount);
            Assert.True(tracker.End("main_12").Publish);
            Assert.False(tracker.End("main_12").Publish);
        }

        [Fact]
        public void Null_and_empty_ids_are_the_same_scenario()
        {
            var tracker = new ScenarioEndTracker();

            tracker.Start(string.Empty);
            var decision = tracker.End(null);

            Assert.True(decision.Publish);
            Assert.Null(decision.GroupId);
        }

        [Fact]
        public void Forget_drops_open_scenarios_but_keeps_the_counters()
        {
            var tracker = new ScenarioEndTracker();

            tracker.Start("main_09");
            Assert.True(tracker.End("main_09").Publish);
            tracker.Start("main_10");

            tracker.Forget();

            Assert.Equal(0, tracker.OpenCount);
            var afterForget = tracker.End("main_10");
            Assert.False(afterForget.Publish);
            Assert.Equal(ScenarioEndReason.NeverOpened, afterForget.Reason);

            // The previously closed id is forgotten too, so its stray end reads as never opened.
            Assert.Equal(ScenarioEndReason.NeverOpened, tracker.End("main_09").Reason);

            Assert.Equal(1L, tracker.Published);
            Assert.Equal(2L, tracker.Suppressed);
        }

        /// <summary>
        /// Every SCENARIO line of the recorded session's console log, in order
        /// ('s' = start, 'e' = end). The trace parts agree: 35 ScenarioStarted, 37 ScenarioEnded.
        /// </summary>
        private static readonly (char Kind, string Id)[] Session20260924 =
        {
            ('s', "game_start_02_005"), ('e', "game_start_02_005"), ('e', "game_start_02_005"),
            ('s', "main_09"), ('e', "main_09"),
            ('s', "main_10"), ('e', "main_10"),
            ('s', "main_11"), ('e', "main_11"),
            ('s', "clickheroine_normal_011"), ('e', "clickheroine_normal_011"),
            ('s', "main_11"), ('e', "main_11"),
            ('s', "countupstart_word_009"), ('e', "countupstart_word_009"),
            ('s', "clickheroine_work_noon_003"), ('e', "clickheroine_work_noon_003"),
            ('s', "clickheroine_work_noon_005"), ('e', "clickheroine_work_noon_005"),
            ('s', "clickheroine_work_noon_001"), ('e', "clickheroine_work_noon_001"),
            ('s', "clickheroine_work_010"), ('e', "clickheroine_work_010"),
            ('s', "countupfinish_shortwork_word_001"), ('e', "countupfinish_shortwork_word_001"),
            ('s', "main_12"), ('e', "main_12"),
            ('s', "countupstart_continuous_word_004"), ('e', "countupstart_continuous_word_004"),
            ('s', "countupfinish_word_006"), ('e', "countupfinish_word_006"),
            ('s', "special_magicalprincess_scenario_001"), ('e', "special_magicalprincess_scenario_001"),
            ('s', "countupstart_word_015"), ('e', "countupstart_word_015"),
            ('s', "countupfinish_word_013"), ('e', "countupfinish_word_013"),
            ('s', "countupfinish_shortwork_word_002"), ('e', "countupfinish_shortwork_word_002"),
            ('s', "leave_chair_word_004"), ('e', "leave_chair_word_004"),
            ('s', "snack_eat_start_word_002"), ('e', "snack_eat_start_word_002"),
            ('s', "clickheroine_normal_019"), ('e', "clickheroine_normal_019"),
            ('s', "countupstart_word_004"), ('e', "countupstart_word_004"),
            ('s', "clickheroine_work_noon_002"), ('e', "clickheroine_work_noon_002"),
            ('s', "clickheroine_work_noon_004"), ('e', "clickheroine_work_noon_004"),
            ('s', "countupfinish_word_010"), ('e', "countupfinish_word_010"),
            ('s', "smalltalk_05"), ('e', "smalltalk_05"),
            ('s', "countupstart_word_013"), ('e', "countupstart_word_013"),
            ('s', "countupfinish_word_007"), ('e', "countupfinish_word_007"),
            ('s', "special_magicalprincess_scenario_002"), ('e', "special_magicalprincess_scenario_002"),
            ('s', "clickheroine_normal_013"), ('e', "clickheroine_normal_013"),
            ('s', "leave_chair_word_001"), ('e', "leave_chair_word_001"),
            ('s', "clickheroine_normal_004"), ('e', "clickheroine_normal_004"),
            ('s', "clickheroine_normal_018"), ('e', "clickheroine_normal_018"), ('e', "clickheroine_normal_018"),
            ('s', "game_end_01_001"), ('e', "game_end_01_001"),
        };
    }
}
