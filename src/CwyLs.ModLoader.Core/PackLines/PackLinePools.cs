using System;
using System.Collections.Generic;

namespace CwyLs.ModLoader.Core.PackLines
{
    /// <summary>
    /// Every pool a pack line can join, as observed in game 1.17.3. The hard-coded
    /// ranges are the game's own <c>RandomList.Setup(min, max)</c> calls; a pack line's episode
    /// (1000 or more) is added to exactly one of them while its language is shown.
    /// </summary>
    /// <remarks>
    /// Where each range comes from (game 1.17.3):
    /// <list type="bullet">
    ///   <item><description>Click and self-talk ranges and the answer-choice talk:
    ///   <c>ReactionTalkSelector</c>.</description></item>
    ///   <item><description>Clock self-talk: <c>Bulbul.CurrentTime{7,12,17,24}HourSelfTalkSelector</c>
    ///   (episodes 1, 2, 3, 4-5); worked-time self-talk:
    ///   <c>Bulbul.WorkedTime{2,5,10}HourSelfTalkSelector</c> (1, 2, 3-4).</description></item>
    ///   <item><description>Timer lines: <c>Bulbul.NormalWorkStartVoiceSelector</c>,
    ///   <c>LongWorkStartVoiceSelector</c>, <c>ContinuousWorkStartVoiceSelector</c>,
    ///   <c>NormalBreakStartVoiceSelector</c>, <c>ShortWorkedBreakStartVoiceSelector</c>,
    ///   <c>LongWorkedBreakStartVoiceSelector</c>, <c>NormalPomodoroFinishVoiceSelector</c>,
    ///   <c>LongWorkFinishVoiceSelector</c>, <c>ShortWorkFinishVoiceSelector</c>,
    ///   <c>PomodoroMidwayFinishVoiceSelector</c>.</description></item>
    ///   <item><description>Small talk: built from the master data in <c>SmallTalkSelector.Setup</c>.</description></item>
    /// </list>
    /// Every range matches the 1.17.3 master data exactly (episodes 1..N, contiguous, in row order).
    /// </remarks>
    public static class PackLinePools
    {
        /// <summary>The lowest episode number a pack line may use, so it can never collide with a game episode.</summary>
        public const int MinimumEpisode = 1000;

        /// <summary>
        /// The highest episode number a pack line may use: episodes are stored as <c>float</c> in the
        /// game's row and compared as <c>(float)int</c> (<c>FacilityClickHeroine</c>), so they must stay
        /// exactly representable.
        /// </summary>
        public const int MaximumEpisode = 1 << 24;

        private const string Reaction = "ReactionTalkSelector.";

        private static readonly Dictionary<string, PackLinePool> ById =
            new Dictionary<string, PackLinePool>(StringComparer.OrdinalIgnoreCase);

        private static readonly Dictionary<int, List<PackLinePool>> ByScenario = new Dictionary<int, List<PackLinePool>>();

        static PackLinePools()
        {
            var all = new List<PackLinePool>
            {
                // Clicks on Satone (ReactionTalkSelector).
                Click("click.normal", 8, "HeroineClickNormal", 1, 23, "_normalClickShortTalkList"),
                Click("click.work", 9, "HeroineClickWork", 1, 23, "_workClickShortTalkList"),
                Click("click.work.morning", 500, "HeroineClickWork_Morning", 1, 5, "_workClickShortTalkMorningList"),
                Click("click.work.noon", 501, "HeroineClickWork_Noon", 1, 5, "_workClickShortTalkNoonList"),
                Click("click.work.evening", 502, "HeroineClickWork_Evening", 1, 5, "_workClickShortTalkEveningList"),
                Click("click.work.night", 503, "HeroineClickWork_Night", 1, 5, "_workClickShortTalkNightList"),
                Click("click.break", 10, "HeroineClickBreak", 1, 20, "_breakClickShortTalkList"),
                Click("click.break.morning", 550, "HeroineClickBreak_Morning", 1, 10, "_breakClickShortTalkMorningList"),
                Click("click.break.noon", 551, "HeroineClickBreak_Noon", 1, 5, "_breakClickShortTalkNoonList"),
                Click("click.break.evening", 552, "HeroineClickBreak_Evening", 1, 5, "_breakClickShortTalkEveningList"),
                Click("click.break.night", 553, "HeroineClickBreak_Night", 1, 11, "_breakClickShortTalkNightList"),

                // Self-talk during a break (ReactionTalkSelector).
                Self("selftalk.break", 21, "HeroineSelfShortTalkBreak", 1, 22, Reaction + "_breakHeroineSelShortTalkfList"),
                Self("selftalk.break.morning", 2000, "HeroineSelfShortTalkBreak_Morning", 1, 7, Reaction + "_breakHeroineSelfShortTalkMorningList"),
                Self("selftalk.break.noon", 2001, "HeroineSelfShortTalkBreak_Noon", 1, 9, Reaction + "_breakHeroineSelfShortTalkNoonList"),
                Self("selftalk.break.evening", 2002, "HeroineSelfShortTalkBreak_Evening", 1, 7, Reaction + "_breakHeroineSelfShortTalkEveningList"),
                Self("selftalk.break.night", 2003, "HeroineSelfShortTalkBreak_Night", 1, 7, Reaction + "_breakHeroineSelfShortTalkNightList"),

                // Clock lines: 07:00-07:29, 12:00-12:29, 17:00-17:29, 00:xx; once a day each, 20 %.
                Self("selftalk.time.7", 2050, "HeroineSelfShortTalk_CurrentTime", 1, 1, "CurrentTime7HourSelfTalkSelector._episodeList", new[] { 1 }),
                Self("selftalk.time.12", 2050, "HeroineSelfShortTalk_CurrentTime", 2, 2, "CurrentTime12HourSelfTalkSelector._episodeList", new[] { 2 }),
                Self("selftalk.time.17", 2050, "HeroineSelfShortTalk_CurrentTime", 3, 3, "CurrentTime17HourSelfTalkSelector._episodeList", new[] { 3 }),
                Self("selftalk.time.24", 2050, "HeroineSelfShortTalk_CurrentTime", 4, 5, "CurrentTime24HourSelfTalkSelector._episodeList", new[] { 4, 5 }),

                // Worked-time lines: after 2 h, 5 h, 10 h of work in the session; once a day each.
                Self("selftalk.worked.2h", 2100, "HeroineSelfShortTalk_WorkedTime", 1, 1, "WorkedTime2HourSelfTalkSelector._episodeList"),
                Self("selftalk.worked.5h", 2100, "HeroineSelfShortTalk_WorkedTime", 2, 2, "WorkedTime5HourSelfTalkSelector._episodeList"),
                Self("selftalk.worked.10h", 2100, "HeroineSelfShortTalk_WorkedTime", 3, 4, "WorkedTime10HourSelfTalkSelector._episodeList"),

                // Pomodoro timer lines (PomodoroVoiceSelector).
                Timer("pomodoro.start", 4, "SpeakWord_PomodoroStart", 1, 15, "NormalWorkStartVoiceSelector._pomodoroVoiceList", PackLineTimerPhase.WorkStart, PackLineTimerMode.Pomodoro),
                Timer("pomodoro.start.continuous", 1000, "SpeakWord_Pomodoro_WorkContinuousStart", 1, 10, "ContinuousWorkStartVoiceSelector._pomodoroVoiceList", PackLineTimerPhase.WorkStart, PackLineTimerMode.Pomodoro),
                Timer("pomodoro.start.long", 1001, "SpeakWord_Pomodoro_WorkLongStart", 1, 10, "LongWorkStartVoiceSelector._voiceList", PackLineTimerPhase.WorkStart, PackLineTimerMode.Pomodoro),
                Timer("pomodoro.break", 5, "SpeakWord_PomodoroBreak", 1, 15, "NormalBreakStartVoiceSelector._pomodoroVoiceList", PackLineTimerPhase.BreakStart, PackLineTimerMode.Pomodoro),
                Timer("pomodoro.break.shortworked", 1100, "SpeakWord_Pomodoro_ShortWorkedBreakStart", 1, 10, "ShortWorkedBreakStartVoiceSelector._pomodoroVoiceList", PackLineTimerPhase.BreakStart, PackLineTimerMode.Pomodoro),
                Timer("pomodoro.break.longworked", 1101, "SpeakWord_Pomodoro_LongWorkedBreakStart", 1, 6, "LongWorkedBreakStartVoiceSelector._pomodoroVoiceList", PackLineTimerPhase.BreakStart, PackLineTimerMode.Pomodoro),
                Timer("pomodoro.finish", 6, "SpeakWord_PomodoroFinish", 1, 20, "NormalPomodoroFinishVoiceSelector._pomodoroVoiceList", PackLineTimerPhase.Finish, PackLineTimerMode.Pomodoro),
                Timer("pomodoro.finish.longwork", 1200, "SpeakWord_Pomodoro_LongWorkFinish", 1, 7, "LongWorkFinishVoiceSelector._pomodoroVoiceList", PackLineTimerPhase.Finish, PackLineTimerMode.Pomodoro),
                Timer("pomodoro.finish.shortwork", 1201, "SpeakWord_Pomodoro_ShortWorkFinish", 1, 10, "ShortWorkFinishVoiceSelector._pomodoroVoiceList", PackLineTimerPhase.Finish, PackLineTimerMode.Pomodoro),
                Timer("pomodoro.finish.midway", 1202, "SpeakWord_Pomodoro_MidwayFinish", 1, 10, "PomodoroMidwayFinishVoiceSelector._voiceList", PackLineTimerPhase.Finish, PackLineTimerMode.Pomodoro),

                // Count-up timer lines: the same selectors' second list.
                Timer("countup.start", 3000, "SpeakWord_CountupStart", 1, 15, "NormalWorkStartVoiceSelector._countupVoiceList", PackLineTimerPhase.WorkStart, PackLineTimerMode.Countup),
                Timer("countup.start.continuous", 3001, "SpeakWord_Countup_WorkContinuousStart", 1, 8, "ContinuousWorkStartVoiceSelector._countupVoiceList", PackLineTimerPhase.WorkStart, PackLineTimerMode.Countup),
                Timer("countup.break", 3100, "SpeakWord_CountupBreak", 1, 13, "NormalBreakStartVoiceSelector._countupVoiceList", PackLineTimerPhase.BreakStart, PackLineTimerMode.Countup),
                Timer("countup.break.shortworked", 3101, "SpeakWord_Countup_ShortWorkedBreakStart", 1, 7, "ShortWorkedBreakStartVoiceSelector._countupVoiceList", PackLineTimerPhase.BreakStart, PackLineTimerMode.Countup),
                Timer("countup.break.longworked", 3102, "SpeakWord_Countup_LongWorkedBreakStart", 1, 6, "LongWorkedBreakStartVoiceSelector._countupVoiceList", PackLineTimerPhase.BreakStart, PackLineTimerMode.Countup),
                Timer("countup.finish", 3200, "SpeakWord_CountupFinish", 1, 15, "NormalPomodoroFinishVoiceSelector._countupVoiceList", PackLineTimerPhase.Finish, PackLineTimerMode.Countup),
                Timer("countup.finish.shortwork", 3201, "SpeakWord_Countup_ShortWorkFinish", 1, 6, "ShortWorkFinishVoiceSelector._countupVoiceList", PackLineTimerPhase.Finish, PackLineTimerMode.Countup),
                Timer("countup.finish.longwork", 3202, "SpeakWord_Countup_LongWorkFinish", 1, 7, "LongWorkFinishVoiceSelector._countupVoiceList", PackLineTimerPhase.Finish, PackLineTimerMode.Countup),

                // The answer-choice talk after a completed pomodoro (pomodoroend_*; ReactionTalkSelector).
                new PackLinePool(
                    "pomodoro.end", 7, "ShortConversation", PackLineFamily.TimerConversation, PackLineShape.Conversation,
                    1, 20, Reaction + "_answerChoiceEpisodeList"),

                // Small talk: data-driven (SmallTalkSelector.Setup), weighted by the group's Arg1/Arg2.
                new PackLinePool(
                    "smalltalk", 23, "SmallTalk", PackLineFamily.SmallTalk, PackLineShape.SmallTalk,
                    0, 0, "SmallTalkSelector._cache"),

                // Startup greetings (HeroineStateGameStartDirection.ReadyHeroineSpeak, game 1.18.0): the kind
                // follows the time since the last launch; GameStartTalkSelector draws the episode from
                // StartDirectionData's decks (in the save; ranges from StartDirectionData.Setup) or uses 1.
                // GameStart_First_CameraTouch (the very first launch, the tutorial's) is not a pool.
                Start("gamestart.sameday", 13, "GameStart_LessTowDays", 1, 17, "StartDirectionData._lessTowDaysTalkList"),
                Start("gamestart.newday", 12, "GameStart_LessTowDays_CameraTouch", 1, 12, "StartDirectionData._lessTowDaysCameraTouchTalkList"),
                Start("gamestart.newday.morning", 1400, "GameStart_LessTowDays_CameraTouch_Morning", 1, 9, "StartDirectionData._lessTowDaysCameraTouchMorningTalkList"),
                Start("gamestart.newday.noon", 1402, "GameStart_LessTowDays_CameraTouch_Noon", 1, 4, "StartDirectionData._lessTowDaysCameraTouchNoonTalkList"),
                Start("gamestart.newday.evening", 1404, "GameStart_LessTowDays_CameraTouch_Evening", 1, 5, "StartDirectionData._lessTowDaysCameraTouchEveningTalkList"),
                Start("gamestart.newday.night", 1406, "GameStart_LessTowDays_CameraTouch_Night", 1, 7, "StartDirectionData._lessTowDaysCameraTouchNightTalkList"),
                Start("gamestart.days3to14", 14, "GameStart_LessHarfMonth_CameraTouch", 1, 8, "StartDirectionData._lessHarfMonthTalkList"),
                Start("gamestart.days15to30", 15, "GameStart_GreaterHarfMonth_CameraTouch", 1, 2, "StartDirectionData._greaterMonthTalkList"),
                Start("gamestart.days31plus", 16, "GameStart_GreaterMonth_CameraTouch", 1, 1, "GameStartTalkSelector (always 1)"),
                Start("gamestart.login100", 17, "GameStart_GreaterYear_AND_PlayOneHundred_CameraTouch", 1, 1, "GameStartTalkSelector (always 1)"),
            };

            foreach (var pool in all)
            {
                ById.Add(pool.Id, pool);
                if (!ByScenario.TryGetValue(pool.Scenario, out var list))
                {
                    list = new List<PackLinePool>(1);
                    ByScenario.Add(pool.Scenario, list);
                }

                list.Add(pool);
            }

            All = all;
        }

        /// <summary>Every pool, in the order above (53: 43 as in game 1.17.3, plus 10 startup-greeting pools from 1.18.0).</summary>
        public static IReadOnlyList<PackLinePool> All { get; }

        /// <summary>Looks a pool up by id, case-insensitively.</summary>
        /// <param name="id">A pool id, e.g. <c>click.work</c>.</param>
        /// <param name="pool">The pool, or null.</param>
        /// <returns>True when the id names a pool.</returns>
        public static bool TryGet(string? id, out PackLinePool? pool)
        {
            pool = null;
            return !string.IsNullOrWhiteSpace(id) && ById.TryGetValue(id!.Trim(), out pool);
        }

        /// <summary>The pools whose groups carry this <c>ScenarioType</c> value; empty when none does.</summary>
        /// <param name="scenario">A <c>ScenarioType</c> value.</param>
        public static IReadOnlyList<PackLinePool> ForScenario(int scenario) =>
            ByScenario.TryGetValue(scenario, out var list) ? (IReadOnlyList<PackLinePool>)list : Array.Empty<PackLinePool>();

        /// <summary>
        /// Picks the pool a language-exclusive group joins: the one its scenario implies, or the one
        /// its <c>Pool</c> member names, which must carry the same scenario.
        /// </summary>
        /// <param name="scenario">The group's <c>Scenario</c> value.</param>
        /// <param name="scenarioName">Its name, for the message.</param>
        /// <param name="poolId">The group's <c>Pool</c> member; empty or null when absent.</param>
        /// <param name="pool">The pool on success.</param>
        /// <param name="problem">Why not, on failure; empty on success.</param>
        /// <returns>True when exactly one pool fits.</returns>
        public static bool TryResolve(int scenario, string scenarioName, string? poolId, out PackLinePool? pool, out string problem)
        {
            pool = null;
            var candidates = ForScenario(scenario);
            if (candidates.Count == 0)
            {
                problem = "Scenario " + scenarioName + " is not a pool pack lines can join; use one of "
                          + DescribeScenarios();
                return false;
            }

            if (string.IsNullOrWhiteSpace(poolId))
            {
                if (candidates.Count == 1)
                {
                    pool = candidates[0];
                    problem = string.Empty;
                    return true;
                }

                problem = "Scenario " + scenarioName + " feeds " + candidates.Count + " pools, so \"Pool\" is required: one of "
                          + Join(candidates);
                return false;
            }

            if (!TryGet(poolId, out var named) || named is null)
            {
                problem = "Pool \"" + poolId + "\" is not a pack-lines pool; for Scenario " + scenarioName + " use "
                          + Join(candidates);
                return false;
            }

            if (named.Scenario != scenario)
            {
                problem = "Pool \"" + named.Id + "\" draws " + named.ScenarioName + " groups, not " + scenarioName
                          + "; for Scenario " + scenarioName + " use " + Join(candidates);
                return false;
            }

            pool = named;
            problem = string.Empty;
            return true;
        }

        private static string Join(IReadOnlyList<PackLinePool> pools)
        {
            var ids = new string[pools.Count];
            for (var i = 0; i < pools.Count; i++)
            {
                ids[i] = pools[i].Id;
            }

            return string.Join(", ", ids);
        }

        private static string DescribeScenarios()
        {
            var names = new List<string>();
            foreach (var pool in All)
            {
                if (!names.Contains(pool.ScenarioName))
                {
                    names.Add(pool.ScenarioName);
                }
            }

            return string.Join(", ", names);
        }

        /// <summary>The <c>Pool</c> value that offers one line in every startup-greeting pool.</summary>
        public const string AnyGameStart = "gamestart.any";

        /// <summary>The <c>Pool</c> value that offers one line in every pool a single line can fill (a global line).</summary>
        public const string Global = "*";

        /// <summary>
        /// The pools a spread <c>Pool</c> value stands for: <see cref="AnyGameStart"/> = every
        /// <see cref="PackLineFamily.GameStart"/> pool; <see cref="Global"/> = every pool except the
        /// answer-choice talk (<see cref="PackLineFamily.TimerConversation"/>, which needs its choices).
        /// Some targets share a <c>ScenarioType</c> (the clock slots <c>selftalk.time.*</c>, the
        /// worked-time slots <c>selftalk.worked.*</c>); the game finds a group by
        /// <c>(Scenario, EpisodeNumber)</c>, so <see cref="PackLineScreen"/> gives each of their copies
        /// after the first its own episode number.
        /// </summary>
        /// <param name="id">A group's <c>Pool</c> member.</param>
        /// <param name="targets">The pools, in catalogue order, or empty.</param>
        /// <returns>True when <paramref name="id"/> is a spread.</returns>
        public static bool TryGetSpread(string? id, out IReadOnlyList<PackLinePool> targets)
        {
            var trimmed = id?.Trim();
            if (string.Equals(trimmed, AnyGameStart, StringComparison.OrdinalIgnoreCase))
            {
                targets = AnyGameStartTargets;
                return true;
            }

            if (string.Equals(trimmed, Global, StringComparison.Ordinal))
            {
                targets = GlobalTargets;
                return true;
            }

            targets = Array.Empty<PackLinePool>();
            return false;
        }

        private static IReadOnlyList<PackLinePool> AnyGameStartTargets =>
            _anyGameStart ??= Where(p => p.Family == PackLineFamily.GameStart);

        private static IReadOnlyList<PackLinePool> GlobalTargets =>
            _global ??= Where(p => p.Family != PackLineFamily.TimerConversation);

        private static IReadOnlyList<PackLinePool>? _anyGameStart;
        private static IReadOnlyList<PackLinePool>? _global;

        private static IReadOnlyList<PackLinePool> Where(Func<PackLinePool, bool> keep)
        {
            var list = new List<PackLinePool>();
            foreach (var pool in All)
            {
                if (keep(pool))
                {
                    list.Add(pool);
                }
            }

            return list;
        }

        private static PackLinePool Start(string id, int scenario, string name, int first, int last, string selector) =>
            new PackLinePool(id, scenario, name, PackLineFamily.GameStart, PackLineShape.SpokenLine, first, last, selector);

        private static PackLinePool Click(string id, int scenario, string name, int first, int last, string field) =>
            new PackLinePool(id, scenario, name, PackLineFamily.ClickReaction, PackLineShape.SpokenLine, first, last, Reaction + field);

        private static PackLinePool Self(string id, int scenario, string name, int first, int last, string selector, int[]? standIns = null) =>
            new PackLinePool(id, scenario, name, PackLineFamily.SelfTalk, PackLineShape.SpokenLine, first, last, selector, standIns: standIns);

        private static PackLinePool Timer(
            string id, int scenario, string name, int first, int last, string selector, PackLineTimerPhase phase, PackLineTimerMode mode) =>
            new PackLinePool(id, scenario, name, PackLineFamily.TimerLine, PackLineShape.TimerVoice, first, last, selector, phase, mode);
    }
}
