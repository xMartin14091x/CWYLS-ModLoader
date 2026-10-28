using System.Collections.Generic;
using CwyLs.ModLoader.Core;

namespace CwyLs.ModLoader.Bulbul.PackLines
{
    /// <summary>
    /// Every game member the pack-lines feature patches or reads, as patch-target data:
    /// a game update that moves one is reported at startup, and the feature stays off - the game's
    /// pools stay exactly its own - instead of half-working.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Checked against game 1.17.3; each group names what it is used for. Strings only - no game
    /// type is touched - so <c>tools/seamcheck</c> can evaluate <see cref="All"/> without the game
    /// (registered by <c>PackLinesFeature.Initialize</c>).
    /// </para>
    /// <para>
    /// The list is long because the game keeps each pool in its own private field of its own
    /// selector class; every field the feature reads by reflection is listed, so a renamed one turns
    /// the whole feature off with one warning rather than silently leaving one pool vanilla.
    /// </para>
    /// </remarks>
    public static class PackLinesPatchTargets
    {
        /// <summary>Assembly every target lives in.</summary>
        public const string GameAssembly = "Assembly-CSharp";

        /// <summary>The game's shuffled no-repeat episode list (root namespace).</summary>
        public const string RandomListType = "RandomList";

        /// <summary>The click and break self-talk selector (root namespace).</summary>
        public const string ReactionSelectorType = "ReactionTalkSelector";

        /// <summary>The timer-line selector (root namespace).</summary>
        public const string PomodoroSelectorType = "PomodoroVoiceSelector";

        /// <summary>The small-talk lottery (root namespace).</summary>
        public const string SmallTalkSelectorType = "SmallTalkSelector";

        /// <summary>The click-reaction facility.</summary>
        public const string ClickHeroineType = "Bulbul.FacilityClickHeroine";

        /// <summary>The clock self-talk selector.</summary>
        public const string CurrentTimeSelectorType = "Bulbul.CurrentTimeSelfTalkSelector";

        /// <summary>The worked-time self-talk selector.</summary>
        public const string WorkedTimeSelectorType = "Bulbul.WorkedTimeSelfTalkSelector";

        /// <summary>The save manager.</summary>
        public const string SaveDataManagerType = "Bulbul.SaveDataManager";

        /// <summary>The list field every selector's <c>RandomList</c> keeps its episodes in.</summary>
        public const string RandomListItemsField = "_randomList";

        /// <summary><c>RandomList._beforeLast</c>: the cycle's last element, which tells a saved cycle apart from a new one (PackLineCycles).</summary>
        public const string RandomListBeforeLastField = "_beforeLast";

        /// <summary>The private method that builds a new cycle.</summary>
        public const string InitListMethod = "InitList";

        /// <summary>The click selector field on <see cref="ClickHeroineType"/>.</summary>
        public const string ClickSelectorField = "_clickReactionTalkSelector";

        /// <summary>The pending "play this line on the next click" slot on <see cref="ClickHeroineType"/>.</summary>
        public const string PendingLineField = "_pendingComebackOnClick";

        /// <summary>The small-talk lottery cache.</summary>
        public const string SmallTalkCacheField = "_cache";

        /// <summary>The once-a-day set on the clock selectors.</summary>
        public const string PlayedEpisodesField = "_playedEpisodes";

        /// <summary>The episode list field on the clock and worked-time selectors.</summary>
        public const string EpisodeListField = "_episodeList";

        /// <summary>The pomodoro-mode list field on the two-list timer selectors.</summary>
        public const string PomodoroListField = "_pomodoroVoiceList";

        /// <summary>The count-up-mode list field on the two-list timer selectors.</summary>
        public const string CountupListField = "_countupVoiceList";

        /// <summary>The list field on the one-list timer selectors.</summary>
        public const string VoiceListField = "_voiceList";

        /// <summary>
        /// The <c>ReactionTalkSelector</c> list fields, by pool id.
        /// </summary>
        public static IReadOnlyList<KeyValuePair<string, string>> ReactionLists { get; } = new[]
        {
            Pair("click.normal", "_normalClickShortTalkList"),
            Pair("click.work", "_workClickShortTalkList"),
            Pair("click.work.morning", "_workClickShortTalkMorningList"),
            Pair("click.work.noon", "_workClickShortTalkNoonList"),
            Pair("click.work.evening", "_workClickShortTalkEveningList"),
            Pair("click.work.night", "_workClickShortTalkNightList"),
            Pair("click.break", "_breakClickShortTalkList"),
            Pair("click.break.morning", "_breakClickShortTalkMorningList"),
            Pair("click.break.noon", "_breakClickShortTalkNoonList"),
            Pair("click.break.evening", "_breakClickShortTalkEveningList"),
            Pair("click.break.night", "_breakClickShortTalkNightList"),
            Pair("selftalk.break", "_breakHeroineSelShortTalkfList"),
            Pair("selftalk.break.morning", "_breakHeroineSelfShortTalkMorningList"),
            Pair("selftalk.break.noon", "_breakHeroineSelfShortTalkNoonList"),
            Pair("selftalk.break.evening", "_breakHeroineSelfShortTalkEveningList"),
            Pair("selftalk.break.night", "_breakHeroineSelfShortTalkNightList"),
            Pair("pomodoro.end", "_answerChoiceEpisodeList"),
        };

        /// <summary>
        /// The clock selectors: field on <c>CurrentTimeSelfTalkSelector</c>,
        /// the selector's type, and the pool its <c>_episodeList</c> is.
        /// </summary>
        public static IReadOnlyList<SubSelector> ClockSelectors { get; } = new[]
        {
            new SubSelector("_7HourSelector", "Bulbul.CurrentTime7HourSelfTalkSelector", "selftalk.time.7", EpisodeListField, null),
            new SubSelector("_12HourSelector", "Bulbul.CurrentTime12HourSelfTalkSelector", "selftalk.time.12", EpisodeListField, null),
            new SubSelector("_17HourSelector", "Bulbul.CurrentTime17HourSelfTalkSelector", "selftalk.time.17", EpisodeListField, null),
            new SubSelector("_24HourSelector", "Bulbul.CurrentTime24HourSelfTalkSelector", "selftalk.time.24", EpisodeListField, null),
        };

        /// <summary>The worked-time selectors (fields of <c>WorkedTimeSelfTalkSelector</c>, created in its <c>Setup</c>).</summary>
        public static IReadOnlyList<SubSelector> WorkedSelectors { get; } = new[]
        {
            new SubSelector("_2HourSelector", "Bulbul.WorkedTime2HourSelfTalkSelector", "selftalk.worked.2h", EpisodeListField, null),
            new SubSelector("_5HourSelector", "Bulbul.WorkedTime5HourSelfTalkSelector", "selftalk.worked.5h", EpisodeListField, null),
            new SubSelector("_10HourSelector", "Bulbul.WorkedTime10HourSelfTalkSelector", "selftalk.worked.10h", EpisodeListField, null),
        };

        /// <summary>
        /// The timer selectors held by <c>PomodoroVoiceSelector</c>: field,
        /// type, the pomodoro-mode pool and list, and the count-up pool (null for the one-list selectors).
        /// </summary>
        public static IReadOnlyList<SubSelector> TimerSelectors { get; } = new[]
        {
            new SubSelector("_normalWorkStartVoiceSelector", "Bulbul.NormalWorkStartVoiceSelector", "pomodoro.start", PomodoroListField, "countup.start"),
            new SubSelector("_longWorkStartVoiceSelector", "Bulbul.LongWorkStartVoiceSelector", "pomodoro.start.long", VoiceListField, null),
            new SubSelector("_continuousWorkStartVoiceSelector", "Bulbul.ContinuousWorkStartVoiceSelector", "pomodoro.start.continuous", PomodoroListField, "countup.start.continuous"),
            new SubSelector("_normalBreakStartVoiceSelector", "Bulbul.NormalBreakStartVoiceSelector", "pomodoro.break", PomodoroListField, "countup.break"),
            new SubSelector("_shortWorkedBreakStartVoiceSelector", "Bulbul.ShortWorkedBreakStartVoiceSelector", "pomodoro.break.shortworked", PomodoroListField, "countup.break.shortworked"),
            new SubSelector("_longWorkedBreakStartVoiceSelector", "Bulbul.LongWorkedBreakStartVoiceSelector", "pomodoro.break.longworked", PomodoroListField, "countup.break.longworked"),
            new SubSelector("_normalPomodoroFinishVoiceSelector", "Bulbul.NormalPomodoroFinishVoiceSelector", "pomodoro.finish", PomodoroListField, "countup.finish"),
            new SubSelector("_longWorkFinishVoiceSelector", "Bulbul.LongWorkFinishVoiceSelector", "pomodoro.finish.longwork", PomodoroListField, "countup.finish.longwork"),
            new SubSelector("_shortWorkFinishVoiceSelector", "Bulbul.ShortWorkFinishVoiceSelector", "pomodoro.finish.shortwork", PomodoroListField, "countup.finish.shortwork"),
            new SubSelector("_midwayFinishVoiceSelector", "Bulbul.PomodoroMidwayFinishVoiceSelector", "pomodoro.finish.midway", VoiceListField, null),
        };

        /// <summary>The whole table, registered by <c>PackLinesFeature.Initialize</c>.</summary>
        public static IReadOnlyList<PatchTarget> All { get; } = Build();

        private static List<PatchTarget> Build()
        {
            var all = new List<PatchTarget>
            {
                // RandomList: the list, and InitList (a new cycle).
                Field("packlines.randomlist.items", RandomListType, RandomListItemsField, "List<int>"),
                Field("packlines.randomlist.beforelast", RandomListType, RandomListBeforeLastField, "int"),
                Method("packlines.randomlist.initlist", RandomListType, InitListMethod, "void InitList()"),

                // FacilityClickHeroine: its selector, OneWord (every click/self-talk pick),
                // AnswerChoiceStory (the answer-choice talk), and ReactionReady plus the pending line
                // (the console test plays a line through the comeback slot).
                Field("packlines.clickheroine.selector", ClickHeroineType, ClickSelectorField, "ReactionTalkSelector"),
                Method("packlines.clickheroine.oneword", ClickHeroineType, "OneWord", "void OneWord(ScenarioType)", "Bulbul.MasterData.ScenarioType"),
                Method("packlines.clickheroine.answerchoice", ClickHeroineType, "AnswerChoiceStory", "void AnswerChoiceStory()"),
                Method("packlines.clickheroine.reactionready", ClickHeroineType, "ReactionReady",
                    "bool ReactionReady(FacilityClickHeroine.ReactionType)", "Bulbul.FacilityClickHeroine+ReactionType"),
                Field("packlines.clickheroine.pending", ClickHeroineType, PendingLineField, "ScenarioInfo"),

                // ReactionTalkSelector: Setup (tags the lists), the self-talk pick (clock lines'
                // once-a-day bookkeeping), and the clock and worked-time selectors.
                Method("packlines.reaction.setup", ReactionSelectorType, "Setup", "void Setup()"),
                Method("packlines.reaction.selftalk", ReactionSelectorType, "GetNextBreakHeroineSelfShortTalkSelection",
                    "ValueTuple<ScenarioType, int> GetNextBreakHeroineSelfShortTalkSelection(TimeOfDayProvider.TimeOfDayType)",
                    "Bulbul.TimeOfDayProvider+TimeOfDayType"),
                Field("packlines.reaction.currenttime", ReactionSelectorType, "_currentTimeSelfTalkSelector", "CurrentTimeSelfTalkSelector"),
                Field("packlines.reaction.workedtime", ReactionSelectorType, "_workedTimeSelfTalkSelector", "WorkedTimeSelfTalkSelector"),

                // PomodoroVoiceSelector: Setup (tags the lists) and the three timer picks.
                Method("packlines.pomodoro.setup", PomodoroSelectorType, "Setup", "void Setup()"),
                Method("packlines.pomodoro.workstart", PomodoroSelectorType, "TakeNextWorkStartVoice", "ScenarioInfo TakeNextWorkStartVoice()"),
                Method("packlines.pomodoro.breakstart", PomodoroSelectorType, "TakeNextBreakStartVoice", "ScenarioInfo TakeNextBreakStartVoice()"),
                Method("packlines.pomodoro.finish", PomodoroSelectorType, "TakeNextFinishVoice", "ScenarioInfo TakeNextFinishVoice()"),

                // SmallTalkSelector: the cache, Setup (builds it) and LotterySmallTalk (the lottery).
                Field("packlines.smalltalk.cache", SmallTalkSelectorType, SmallTalkCacheField, "Dictionary<float, ScenarioGroupData>"),
                Method("packlines.smalltalk.setup", SmallTalkSelectorType, "Setup", "void Setup()"),
                Method("packlines.smalltalk.lottery", SmallTalkSelectorType, "LotterySmallTalk",
                    "float LotterySmallTalk(SmallTalkSelector.PlayTiming)", "SmallTalkSelector+PlayTiming"),

                // SaveDataManager.SaveScenarioProgressData (the only writer of PlayedScenarioGroupIDs)
                // and its ScenarioProgressData; PomodoroData.CurrentTimerMode (which timer list a
                // two-list selector reads).
                Method("packlines.save.scenarioprogress", SaveDataManagerType, "SaveScenarioProgressData", "void SaveScenarioProgressData()"),
                new PatchTarget("packlines.save.progressdata", GameAssembly, SaveDataManagerType, MemberKind.Property, "ScenarioProgressData",
                    expectedSignature: "ScenarioProgressData ScenarioProgressData { get; set; }"),
                new PatchTarget("packlines.save.playedids", GameAssembly, "Bulbul.ScenarioProgressData", MemberKind.Field, "PlayedScenarioGroupIDs",
                    expectedSignature: "List<string> PlayedScenarioGroupIDs"),
                new PatchTarget("packlines.save.pomodorodata", GameAssembly, SaveDataManagerType, MemberKind.Property, "PomodoroData",
                    expectedSignature: "PomodoroData PomodoroData { get; set; }"),
                new PatchTarget("packlines.timer.mode", GameAssembly, "Bulbul.PomodoroData", MemberKind.Field, "CurrentTimerMode",
                    expectedSignature: "TimerMode CurrentTimerMode"),
            };

            foreach (var list in ReactionLists)
            {
                all.Add(Field("packlines.reaction.list." + list.Key, ReactionSelectorType, list.Value, "RandomList"));
            }

            foreach (var clock in ClockSelectors)
            {
                all.Add(Field("packlines.currenttime." + clock.PoolId, CurrentTimeSelectorType, clock.Field, "ITalkSelector"));
                all.Add(Field("packlines.list." + clock.PoolId, clock.TypeName, clock.ListField, "RandomList"));
                all.Add(Field("packlines.played." + clock.PoolId, clock.TypeName, PlayedEpisodesField, "HashSet<int>"));
            }

            foreach (var worked in WorkedSelectors)
            {
                all.Add(Field("packlines.workedtime." + worked.PoolId, WorkedTimeSelectorType, worked.Field, "ITalkSelector"));
                all.Add(Field("packlines.list." + worked.PoolId, worked.TypeName, worked.ListField, "RandomList"));
            }

            foreach (var timer in TimerSelectors)
            {
                all.Add(Field("packlines.pomodoro." + timer.PoolId, PomodoroSelectorType, timer.Field, "ITalkSelector"));
                all.Add(Field("packlines.list." + timer.PoolId, timer.TypeName, timer.ListField, "RandomList"));
                if (timer.CountupPoolId != null)
                {
                    all.Add(Field("packlines.list." + timer.CountupPoolId, timer.TypeName, CountupListField, "RandomList"));

                    // The two-list selectors index a per-episode table with the drawn number
                    // (NormalWorkStartVoiceSelector), which a pack episode would overrun; the
                    // feature answers for its own episodes before that line runs.
                    all.Add(Method("packlines.takenext." + timer.PoolId, timer.TypeName, "TakeNextVoice", "int TakeNextVoice()"));
                }
            }

            return all;
        }

        private static KeyValuePair<string, string> Pair(string poolId, string field) =>
            new KeyValuePair<string, string>(poolId, field);

        private static PatchTarget Field(string id, string type, string field, string fieldType) =>
            new PatchTarget(id, GameAssembly, type, MemberKind.Field, field, expectedSignature: fieldType + " " + field);

        private static PatchTarget Method(string id, string type, string method, string signature, params string[] parameters) =>
            new PatchTarget(id, GameAssembly, type, MemberKind.Method, method, parameters, expectedSignature: signature);

        /// <summary>One selector the game keeps inside another: where it is, its type, and its list(s).</summary>
        public sealed class SubSelector
        {
            /// <summary>Creates an entry.</summary>
            /// <param name="field">The field holding it on its parent.</param>
            /// <param name="typeName">Its reflection type name.</param>
            /// <param name="poolId">The pool its first list is.</param>
            /// <param name="listField">The field of its first list.</param>
            /// <param name="countupPoolId">The pool its <see cref="CountupListField"/> is, or null when it has one list.</param>
            public SubSelector(string field, string typeName, string poolId, string listField, string? countupPoolId)
            {
                Field = field;
                TypeName = typeName;
                PoolId = poolId;
                ListField = listField;
                CountupPoolId = countupPoolId;
            }

            /// <summary>The field holding it on its parent.</summary>
            public string Field { get; }

            /// <summary>Its reflection type name.</summary>
            public string TypeName { get; }

            /// <summary>The pool its first list is.</summary>
            public string PoolId { get; }

            /// <summary>The field of its first list.</summary>
            public string ListField { get; }

            /// <summary>The pool its count-up list is, or null for a one-list selector.</summary>
            public string? CountupPoolId { get; }
        }
    }
}
