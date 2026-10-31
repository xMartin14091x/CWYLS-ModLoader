using System;
using System.Collections.Generic;

namespace CwyLs.ModLoader.Core.PackLines
{
    /// <summary>Which kind of game moment a pack-lines pool belongs to.</summary>
    public enum PackLineFamily
    {
        /// <summary>A click on Satone (<c>FacilityClickHeroine.OneWord</c>).</summary>
        ClickReaction,

        /// <summary>Satone talking to herself during a break, including the clock and worked-time lines.</summary>
        SelfTalk,

        /// <summary>A pomodoro or count-up timer moment: work start, break start, finish.</summary>
        TimerLine,

        /// <summary>The answer-choice talk after a completed pomodoro (<c>pomodoroend_*</c>, <c>ShortConversation</c>).</summary>
        TimerConversation,

        /// <summary>A small talk (<c>SmallTalk</c>): data-driven, weighted by the group's own odds.</summary>
        SmallTalk,

        /// <summary>
        /// The greeting after the startup screen (<c>GameStart_*</c>), picked by
        /// <c>GameStartTalkSelector.GetGameStartTalk</c> from decks kept in the save, so a pack line is
        /// never added to the deck: it replaces the pick for one launch.
        /// </summary>
        GameStart,
    }

    /// <summary>What a pool's groups must contain for the game to play them (checked by <see cref="PackLineScreen"/>).</summary>
    public enum PackLineShape
    {
        /// <summary>At least one spoken row: <c>Text</c>, <c>VoiceText</c> or <c>PomodoroVoiceText</c>.</summary>
        SpokenLine,

        /// <summary>A <c>PomodoroVoiceText</c> row with whole-number <c>Arg1</c>/<c>Arg3</c> (<c>HeroineAI</c> and <c>ScenarioReader</c> parse them as integers).</summary>
        TimerVoice,

        /// <summary>Spoken rows plus the small-talk columns <c>SmallTalkSelector</c> parses.</summary>
        SmallTalk,

        /// <summary>Spoken rows whose <c>Selection</c>/<c>Jump</c> targets are rows of the same group (<c>ScenarioReader</c> looks targets up inside the group).</summary>
        Conversation,
    }

    /// <summary>Which timer moment a <see cref="PackLineFamily.TimerLine"/> pool speaks at.</summary>
    public enum PackLineTimerPhase
    {
        /// <summary>Not a timer pool.</summary>
        None,

        /// <summary>A work session starts (<c>PomodoroVoiceSelector.TakeNextWorkStartVoice</c>).</summary>
        WorkStart,

        /// <summary>A break starts (<c>PomodoroVoiceSelector.TakeNextBreakStartVoice</c>).</summary>
        BreakStart,

        /// <summary>The timer completes (<c>PomodoroVoiceSelector.TakeNextFinishVoice</c>).</summary>
        Finish,
    }

    /// <summary>Which timer mode a <see cref="PackLineFamily.TimerLine"/> pool belongs to (<c>Bulbul.TimerMode</c>).</summary>
    public enum PackLineTimerMode
    {
        /// <summary>Not a timer pool.</summary>
        None,

        /// <summary><c>TimerMode.Pomodoro</c>.</summary>
        Pomodoro,

        /// <summary><c>TimerMode.Countup</c>.</summary>
        Countup,
    }

    /// <summary>
    /// One pool the game draws a line from, and that a pack line can join: its id, the
    /// <c>ScenarioType</c> its groups carry, the game's own episode range, and where the game keeps
    /// it. Pure data; <see cref="PackLinePools"/> holds the table, read out of the 1.17.3 sources.
    /// </summary>
    public sealed class PackLinePool
    {
        private static readonly int[] NoStandIns = new int[0];

        /// <summary>Creates a pool.</summary>
        /// <param name="id">Stable dotted id, e.g. <c>click.work.morning</c>. Required.</param>
        /// <param name="scenario">The <c>ScenarioType</c> value its groups carry.</param>
        /// <param name="scenarioName">The <c>ScenarioType</c> member name, for logs and docs. Required.</param>
        /// <param name="family">What kind of moment it belongs to.</param>
        /// <param name="shape">What its groups must contain.</param>
        /// <param name="vanillaFirst">First episode the game's own selector draws; 0 for a data-driven pool.</param>
        /// <param name="vanillaLast">Last episode the game's own selector draws; 0 for a data-driven pool.</param>
        /// <param name="selector">Where the game keeps the pool, e.g. <c>ReactionTalkSelector._workClickShortTalkList</c>. Required.</param>
        /// <param name="phase">The timer moment, for timer pools.</param>
        /// <param name="timerMode">The timer mode, for timer pools.</param>
        /// <param name="standIns">
        /// For the once-a-day clock pools: the game episodes a pack line counts as when it plays, so
        /// the game's "already said today" check still holds (<c>CurrentTime7HourSelfTalkSelector</c> and its siblings).
        /// </param>
        /// <exception cref="ArgumentException">A required string is blank.</exception>
        public PackLinePool(
            string id,
            int scenario,
            string scenarioName,
            PackLineFamily family,
            PackLineShape shape,
            int vanillaFirst,
            int vanillaLast,
            string selector,
            PackLineTimerPhase phase = PackLineTimerPhase.None,
            PackLineTimerMode timerMode = PackLineTimerMode.None,
            int[]? standIns = null)
        {
            Id = Require(id, nameof(id));
            Scenario = scenario;
            ScenarioName = Require(scenarioName, nameof(scenarioName));
            Family = family;
            Shape = shape;
            VanillaFirst = vanillaFirst;
            VanillaLast = vanillaLast;
            Selector = Require(selector, nameof(selector));
            Phase = phase;
            TimerMode = timerMode;
            StandIns = standIns ?? NoStandIns;
        }

        /// <summary>Stable dotted id, e.g. <c>click.work.morning</c>; what a group's <c>Pool</c> member names.</summary>
        public string Id { get; }

        /// <summary>The <c>ScenarioType</c> value the pool's groups carry.</summary>
        public int Scenario { get; }

        /// <summary>The <c>ScenarioType</c> member name.</summary>
        public string ScenarioName { get; }

        /// <summary>What kind of game moment it belongs to.</summary>
        public PackLineFamily Family { get; }

        /// <summary>What its groups must contain.</summary>
        public PackLineShape Shape { get; }

        /// <summary>First episode the game's own selector draws (its hard-coded range); 0 when data-driven.</summary>
        public int VanillaFirst { get; }

        /// <summary>Last episode the game's own selector draws; 0 when data-driven.</summary>
        public int VanillaLast { get; }

        /// <summary>True for a pool the game builds from its master data rather than a fixed range (small talk).</summary>
        public bool IsDataDriven => VanillaFirst == 0 && VanillaLast == 0;

        /// <summary>How many lines the game's own range holds; 0 when data-driven.</summary>
        public int VanillaCount => IsDataDriven ? 0 : VanillaLast - VanillaFirst + 1;

        /// <summary>Where the game keeps the pool, for logs and the README.</summary>
        public string Selector { get; }

        /// <summary>The timer moment, for <see cref="PackLineFamily.TimerLine"/> pools.</summary>
        public PackLineTimerPhase Phase { get; }

        /// <summary>The timer mode, for <see cref="PackLineFamily.TimerLine"/> pools.</summary>
        public PackLineTimerMode TimerMode { get; }

        /// <summary>
        /// Game episodes a pack line of this pool counts as once it has played, in preference order;
        /// empty for every pool but the four clock pools.
        /// </summary>
        public IReadOnlyList<int> StandIns { get; }

        /// <summary>
        /// True when the game plays this pool's lines as a click-style reaction, so a test can play one
        /// through <c>FacilityClickHeroine</c>'s own pending-line slot; false for the timer lines, which
        /// only the timer's own moments play, and the startup greetings, which only a launch plays.
        /// </summary>
        public bool IsClickStyle => Family != PackLineFamily.TimerLine && Family != PackLineFamily.GameStart;

        /// <summary>
        /// True when the game keeps this pool in a deck the feature widens (<c>RandomList</c> or the
        /// small-talk cache); false for the startup greetings, whose decks live in the save and are
        /// never widened: every pack line there is drawn by weight (<see cref="PackLineWeights"/>).
        /// </summary>
        public bool HasDeck => Family != PackLineFamily.GameStart;

        /// <summary><c>id (ScenarioName, game 1-23)</c>.</summary>
        public override string ToString() =>
            Id + " (" + ScenarioName + ", " + (IsDataDriven ? "data-driven" : "game " + VanillaFirst + "-" + VanillaLast) + ")";

        private static string Require(string value, string name)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException("must not be null or blank", name);
            }

            return value;
        }
    }
}
