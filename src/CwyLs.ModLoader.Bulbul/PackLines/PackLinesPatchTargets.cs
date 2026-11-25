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
    /// Checked against game 2.0.0; each group names
    /// what it is used for. Strings only - no game
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

        /// <summary>The startup-greeting selector (root namespace; game 1.18.0).</summary>
        public const string GameStartSelectorType = "GameStartTalkSelector";

        /// <summary>A scenario group row of the game's master data.</summary>
        public const string ScenarioGroupDataType = "Bulbul.MasterData.ScenarioGroupData";

        /// <summary>A scenario command row of the game's master data.</summary>
        public const string NovelDataType = "Bulbul.MasterData.NovelData";

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

        /// <summary>The lottery's direction service.</summary>
        public const string SmallTalkDirectionField = "_directionService";

        /// <summary>The lottery's episode and whether it consumes a ready-talk reservation.</summary>
        public const string SmallTalkCurrentEpisodeField = "_currentEpisodeNumber";
        public const string SmallTalkReservedField = "isReservedAsReadyTalk";

        public const string ReactionPoolType = "Bulbul.LocationTalkPool";
        public const string ReactionCategoryType = "Bulbul.LocationTalkCategory";
        public const string ReactionGeneralField = "generalPool";
        public const string ReactionTimePoolsField = "timeOfDayPools";
        public const string AnswerChoiceListField = "_answerChoiceEpisodeList";
        public const string SelfTalkMethod = "TrySelectBreakHeroineSelfShortTalk";
        public const string PlayableEpisodeMethod = "IsPlayableEpisode";
        public const string ReadyTalkReasonMethod = "HasReadyTalkReason";
        private const string LocationType = "Bulbul.LocationType";
        private const string TimeOfDayType = "Bulbul.TimeOfDayProvider+TimeOfDayType";
        private const string GroupByRef = ScenarioGroupDataType + "&";

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

        /// <summary>The base-game reaction pools. A time slot selects an entry in the category's time map.</summary>
        public static IReadOnlyList<ReactionPool> ReactionLists { get; } = new[]
        {
            new ReactionPool("click.normal", "normalClickShortTalk"),
            new ReactionPool("click.work", "workClickShortTalk"),
            new ReactionPool("click.work.morning", "workClickShortTalk", "Morning"),
            new ReactionPool("click.work.noon", "workClickShortTalk", "Noon"),
            new ReactionPool("click.work.evening", "workClickShortTalk", "Evening"),
            new ReactionPool("click.work.night", "workClickShortTalk", "Night"),
            new ReactionPool("click.break", "breakClickShortTalk"),
            new ReactionPool("click.break.morning", "breakClickShortTalk", "Morning"),
            new ReactionPool("click.break.noon", "breakClickShortTalk", "Noon"),
            new ReactionPool("click.break.evening", "breakClickShortTalk", "Evening"),
            new ReactionPool("click.break.night", "breakClickShortTalk", "Night"),
            new ReactionPool("selftalk.break", "breakHeroineSelfShortTalk"),
            new ReactionPool("selftalk.break.morning", "breakHeroineSelfShortTalk", "Morning"),
            new ReactionPool("selftalk.break.noon", "breakHeroineSelfShortTalk", "Noon"),
            new ReactionPool("selftalk.break.evening", "breakHeroineSelfShortTalk", "Evening"),
            new ReactionPool("selftalk.break.night", "breakHeroineSelfShortTalk", "Night"),
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

                // The facility owns the answer-choice selector and the console test's pending reaction.
                Field("packlines.clickheroine.selector", ClickHeroineType, ClickSelectorField, "ReactionTalkSelector"),
                Method("packlines.clickheroine.startreaction", ClickHeroineType, "StartReaction", "void StartReaction(ScenarioType)", "Bulbul.MasterData.ScenarioType"),
                Method("packlines.clickheroine.answerchoice", ClickHeroineType, "AnswerChoiceStory", "void AnswerChoiceStory()"),
                Method("packlines.clickheroine.reactionready", ClickHeroineType, "ReactionReady",
                    "bool ReactionReady(FacilityClickHeroine.ReactionType)", "Bulbul.FacilityClickHeroine+ReactionType"),
                Field("packlines.clickheroine.pending", ClickHeroineType, PendingLineField, "ScenarioInfo"),

                Method("packlines.reaction.setup", ReactionSelectorType, "Setup", "void Setup(IReadOnlyList<ScenarioGroupData>, ScenarioPlayableLocations)",
                    "System.Collections.Generic.IReadOnlyList<Bulbul.MasterData.ScenarioGroupData>", "Bulbul.ScenarioPlayableLocations"),
                Method("packlines.reaction.selftalk", ReactionSelectorType, SelfTalkMethod,
                    "bool TrySelectBreakHeroineSelfShortTalk(LocationType, TimeOfDayProvider.TimeOfDayType, bool, ScenarioGroupData&)",
                    LocationType, TimeOfDayType, "System.Boolean", GroupByRef),
                Method("packlines.reaction.findrow", ReactionSelectorType, "TryFindRow",
                    "bool TryFindRow(ScenarioInfo, ScenarioGroupData&)", "Bulbul.ScenarioInfo", GroupByRef),
                Method("packlines.reaction.commit", ReactionSelectorType, "Commit", "void Commit(ScenarioGroupData)", ScenarioGroupDataType),
                Field("packlines.reaction.currenttime", ReactionSelectorType, "_currentTimeSelfTalkSelector", "CurrentTimeSelfTalkSelector"),
                Field("packlines.reaction.workedtime", ReactionSelectorType, "_workedTimeSelfTalkSelector", "WorkedTimeSelfTalkSelector"),
                Field("packlines.reaction.list.pomodoro.end", ReactionSelectorType, AnswerChoiceListField, "RandomList"),
                Field("packlines.reaction.generalpool", ReactionCategoryType, ReactionGeneralField, "LocationTalkPool"),
                Field("packlines.reaction.timeofdaypools", ReactionCategoryType, ReactionTimePoolsField,
                    "Dictionary<TimeOfDayProvider.TimeOfDayType, LocationTalkPool>"),
                Method("packlines.reaction.pool.peek", ReactionPoolType, "TryPeek",
                    "bool TryPeek(LocationType, bool, ScenarioGroupData&)", LocationType, "System.Boolean", GroupByRef),
                Method("packlines.reaction.pool.remaining", ReactionPoolType, "GetRemaining",
                    "int GetRemaining(LocationType, bool)", LocationType, "System.Boolean"),
                Method("packlines.reaction.pool.refill", ReactionPoolType, "Refill",
                    "void Refill(LocationType, bool)", LocationType, "System.Boolean"),

                Method("packlines.pomodoro.setup", PomodoroSelectorType, "Setup", "void Setup()"),
                TimerPick("workstart", "TakeNextWorkStartVoice"),
                TimerPick("breakstart", "TakeNextBreakStartVoice"),
                TimerPick("finish", "TakeNextFinishVoice"),

                Field("packlines.smalltalk.cache", SmallTalkSelectorType, SmallTalkCacheField, "Dictionary<float, ScenarioGroupData>"),
                Method("packlines.smalltalk.setup", SmallTalkSelectorType, "Setup", "void Setup()"),
                Method("packlines.smalltalk.lottery", SmallTalkSelectorType, "LotterySmallTalk",
                    "float LotterySmallTalk(SmallTalkSelector.PlayTiming, LocationType)", "SmallTalkSelector+PlayTiming", LocationType),
                Method("packlines.smalltalk.canplayready", SmallTalkSelectorType, "CanPlayReadyTalk", "bool CanPlayReadyTalk(LocationType)", LocationType),
                Method("packlines.smalltalk.readyreason", SmallTalkSelectorType, ReadyTalkReasonMethod,
                    "bool HasReadyTalkReason(LocationType)", LocationType),
                Method("packlines.smalltalk.reserveready", SmallTalkSelectorType, "ReserveReadyTalk", "float ReserveReadyTalk(LocationType)", LocationType),
                Method("packlines.smalltalk.playable", SmallTalkSelectorType, PlayableEpisodeMethod,
                    "bool IsPlayableEpisode(ScenarioGroupData, SmallTalkSelector.PlayTiming, LocationType, Nullable<StairsSeat>)",
                    ScenarioGroupDataType, "SmallTalkSelector+PlayTiming", LocationType, "System.Nullable<Bulbul.StairsSeat>"),
                Field("packlines.smalltalk.direction", SmallTalkSelectorType, SmallTalkDirectionField, "DirectionService"),
                Field("packlines.smalltalk.currentepisode", SmallTalkSelectorType, SmallTalkCurrentEpisodeField, "float"),
                Field("packlines.smalltalk.reserved", SmallTalkSelectorType, SmallTalkReservedField, "bool"),
                new PatchTarget("packlines.direction.defect", GameAssembly, "DirectionService", MemberKind.Property, "GamePlayingDefect",
                    expectedSignature: "GamePlayingDefectDirection GamePlayingDefect { get; }"),
                Method("packlines.defect.isusing", "GamePlayingDefectDirection", "IsUsing", "bool IsUsing()"),
                Method("packlines.locations.playable", "Bulbul.ScenarioPlayableLocations", "IsPlayableAt",
                    "bool IsPlayableAt(string, LocationType)", "System.String", LocationType),
                Method("packlines.novel.playable", NovelDataType, "IsPlayableAt", "bool IsPlayableAt(LocationType)", LocationType),
                Field("packlines.group.mypace", ScenarioGroupDataType, "MyPaceOnly", "bool"),
                Property("packlines.location.current", "Bulbul.HeroineLocationService", "CurrentLocation", "LocationType CurrentLocation { get; set; }"),
                Property("packlines.activity.mypace", "Bulbul.HeroineActivityService", "IsMyPace", "bool IsMyPace { get; }"),

                // SaveDataManager.SaveScenarioProgressData (the only writer of PlayedScenarioGroupIDs; master
                // data's save filter patches it and pack lines join that filter) and its ScenarioProgressData;
                // PomodoroData.CurrentTimerMode (which timer list a two-list selector reads).
                Method("packlines.save.scenarioprogress", SaveDataManagerType, "SaveScenarioProgressData", "void SaveScenarioProgressData()"),
                new PatchTarget("packlines.save.progressdata", GameAssembly, SaveDataManagerType, MemberKind.Property, "ScenarioProgressData",
                    expectedSignature: "ScenarioProgressData ScenarioProgressData { get; set; }"),
                new PatchTarget("packlines.save.playedids", GameAssembly, "Bulbul.ScenarioProgressData", MemberKind.Field, "PlayedScenarioGroupIDs",
                    expectedSignature: "List<string> PlayedScenarioGroupIDs"),
                new PatchTarget("packlines.save.pomodorodata", GameAssembly, SaveDataManagerType, MemberKind.Property, "PomodoroData",
                    expectedSignature: "PomodoroData PomodoroData { get; set; }"),
                new PatchTarget("packlines.timer.mode", GameAssembly, "Bulbul.PomodoroData", MemberKind.Field, "CurrentTimerMode",
                    expectedSignature: "TimerMode CurrentTimerMode"),

                // GameStartTalkSelector (game 1.18.0): GetGameStartTalk draws the startup greeting's
                // episode from StartDirectionData's decks (a pack line may replace the pick), and
                // UseGameStartTalk advances that deck and saves it (skipped once when the pick was
                // ours). The group fields and the novel row's group id are what the game then looks
                // the greeting up by (HeroineStateGameStartDirection), checked before a pack line is
                // let in. The master-data lists themselves are the adapter's own seams
                // (masterdata.scenariogroups, masterdata.novels).
                Method("packlines.gamestart.get", GameStartSelectorType, "GetGameStartTalk", "int GetGameStartTalk(ScenarioType)",
                    "Bulbul.MasterData.ScenarioType"),
                Method("packlines.gamestart.use", GameStartSelectorType, "UseGameStartTalk", "void UseGameStartTalk(ScenarioType)",
                    "Bulbul.MasterData.ScenarioType"),
                Field("packlines.group.id", ScenarioGroupDataType, "ID", "string"),
                Field("packlines.group.scenario", ScenarioGroupDataType, "Scenario", "ScenarioType"),
                Field("packlines.group.episode", ScenarioGroupDataType, "EpisodeNumber", "float"),
                Field("packlines.novel.groupid", NovelDataType, "ScenarioGroupID", "string"),
            };

            foreach (var list in ReactionLists)
            {
                all.Add(Field("packlines.reaction.list." + list.PoolId, ReactionSelectorType, list.CategoryField, "LocationTalkCategory"));
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
                all.Add(Field("packlines.pomodoro." + timer.PoolId, PomodoroSelectorType, timer.Field, timer.TypeName.Substring("Bulbul.".Length)));
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

        private static PatchTarget TimerPick(string id, string name) =>
            Method("packlines.pomodoro." + id, PomodoroSelectorType, name,
                "ScenarioInfo " + name + "(bool, LocationType)", "System.Boolean", LocationType);

        private static PatchTarget Property(string id, string type, string property, string signature) =>
            new PatchTarget(id, GameAssembly, type, MemberKind.Property, property, expectedSignature: signature);

        public sealed class ReactionPool
        {
            internal ReactionPool(string poolId, string categoryField, string? timeOfDay = null)
            {
                PoolId = poolId;
                CategoryField = categoryField;
                TimeOfDay = timeOfDay;
            }

            public string PoolId { get; }
            public string CategoryField { get; }
            public string? TimeOfDay { get; }
        }

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
