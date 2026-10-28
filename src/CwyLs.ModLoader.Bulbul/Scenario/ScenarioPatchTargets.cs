using System.Collections.Generic;
using CwyLs.ModLoader.Core;

namespace CwyLs.ModLoader.Bulbul.Scenario
{
    /// <summary>
    /// Every game member the script runtime patches, calls or reads, as patch-target data:
    /// a game update that moves one is reported at startup and the feature falls back or stays off
    /// instead of half-working.
    /// </summary>
    /// <remarks>
    /// Checked against game 1.17.3; each entry says what it is used for. Strings only - no game type
    /// is touched - so <c>tools/seamcheck</c> can evaluate <see cref="All"/>, <see cref="Seam"/> and
    /// <see cref="Fallback"/> without the game (registered by <c>ScenarioFeature.Initialize</c>).
    /// </remarks>
    public static class ScenarioPatchTargets
    {
        /// <summary>Assembly every target lives in.</summary>
        public const string GameAssembly = "Assembly-CSharp";

        /// <summary>The scene reader.</summary>
        public const string ReaderType = "Bulbul.ScenarioReader";

        /// <summary>The long-story facility.</summary>
        public const string StoryType = "Bulbul.FacilityStory";

        /// <summary>A script row.</summary>
        public const string NovelType = "Bulbul.MasterData.NovelData";

        /// <summary>The reader's rows of the playing group (private).</summary>
        public const string GroupNovelsField = "_currentGroupNovels";

        /// <summary>The reader's current row index (private).</summary>
        public const string MessageIndexField = "_currentMessageIndex";

        /// <summary>The reader's current row (private).</summary>
        public const string CurrentNovelField = "_currentNovel";

        /// <summary>The long-story facility's scene type (private).</summary>
        public const string StoryScenarioTypeField = "scenarioType";

        /// <summary><c>FacilityManagerForPC._facilityStory</c>: reaches the story facility when the room scope does not hand it out.</summary>
        public const string ManagerStoryField = "_facilityStory";

        /// <summary>The PC facility manager (the room's <c>IStoryController</c>).</summary>
        public const string ManagerType = "Bulbul.FacilityManagerForPC";

        /// <summary>The members the primary seam needs: the prefix on <c>ReadSentence</c> and how it goes on.</summary>
        public static IReadOnlyList<PatchTarget> Seam { get; } = new[]
        {
            // The command loop: a private async method whose stub starts the state machine; the prefix
            // runs rows 5000-5999 and returns false.
            Method("script.reader.readsentence", ReaderType, "ReadSentence", "UniTaskVoid ReadSentence(int)", "System.Int32"),

            // Continue = the game's own next-row step (past the last row it ends the story).
            Method("script.reader.readnext", ReaderType, "ReadNext", "void ReadNext()"),

            // JumpTo = the game's own jump by row id, exactly as Jump/IfBetweenDateTime use it.
            Method("script.reader.selectionjump", ReaderType, "SelectionJump", "void SelectionJump(NovelData)", NovelType),

            // Set before running a row, as ReadSentence does itself.
            Field("script.reader.groupnovels", ReaderType, GroupNovelsField, "List<NovelData> " + GroupNovelsField),
            Field("script.reader.messageindex", ReaderType, MessageIndexField, "int " + MessageIndexField),
            Field("script.reader.currentnovel", ReaderType, CurrentNovelField, "NovelData " + CurrentNovelField),
        };

        /// <summary>
        /// The members the fallback needs when <see cref="Seam"/> is missing: script rows become the
        /// game's <c>ReadNextForScript</c> (command 50) and the runtime resumes them with the public
        /// <c>ReadNextForScript()</c>.
        /// </summary>
        public static IReadOnlyList<PatchTarget> Fallback { get; } = new[]
        {
            // IsReadNextForScript (true while the current row is command 50), ReadNextForScript (lets it go on).
            Method("script.fallback.isreadnextforscript", ReaderType, "IsReadNextForScript", "bool IsReadNextForScript()"),
            Method("script.fallback.readnextforscript", ReaderType, "ReadNextForScript", "void ReadNextForScript()"),
            Field("script.fallback.currentnovel", ReaderType, CurrentNovelField, "NovelData " + CurrentNovelField),
        };

        /// <summary>The save-log guard: the prefix and the two members it reads to name the group.</summary>
        public static IReadOnlyList<PatchTarget> SaveGuard { get; } = new[]
        {
            // The long-story played log (main progress, collaboration progress, PlayedScenarioGroupIDs).
            Method("script.story.saveplayedlog", StoryType, "SaveScenarioPlayedLog", "void SaveScenarioPlayedLog()"),

            // The same (type, episode) lookup the method makes.
            Field("script.story.scenariotype", StoryType, StoryScenarioTypeField, "ScenarioType " + StoryScenarioTypeField),

            // scene play replays a seen game episode only when the history screen would offer it
            // (FacilityStory.PlayableScenario; the isTalkLog replay in StorySelectUI).
            Method("script.story.playablescenario", StoryType, "get_PlayableScenario", "IReadOnlyObservableList<ScenarioGroupData> get_PlayableScenario()"),
            Field("script.story.managerfacility", ManagerType, ManagerStoryField, "FacilityStory " + ManagerStoryField),
            new PatchTarget("script.story.episodenumber", GameAssembly, StoryType, MemberKind.Property, "EpisodeNumber",
                expectedSignature: "float EpisodeNumber { get; }"),
        };

        /// <summary>What <c>scene play</c>, the waits and the deferred actions call or read.</summary>
        public static IReadOnlyList<PatchTarget> Room { get; } = new[]
        {
            Method("script.reader.isplaying", ReaderType, "IsPlayingScenario", "bool IsPlayingScenario()"),
            Method("script.reader.startready", ReaderType, "StartReady",
                "void StartReady(ScenarioType, string, ScenarioReader.ScenarioReadMode, bool, bool, bool)",
                "Bulbul.MasterData.ScenarioType", "System.String", "Bulbul.ScenarioReader+ScenarioReadMode",
                "System.Boolean", "System.Boolean", "System.Boolean"),
            Method("script.reader.startclickreaction", ReaderType, "StartClickHeroineReaction", "void StartClickHeroineReaction()"),
            Method("script.reader.endclickreaction", ReaderType, "EndClickHeroineReaction", "void EndClickHeroineReaction()"),
            Method("script.reader.addendcallback", ReaderType, "AddEndCallback", "void AddEndCallback(Action)", "System.Action"),

            // The long-story entry the game's own WantTalk click uses (through FacilityManagerForPC).
            Method("script.story.startstory", "Bulbul.IStoryController", "StartStory", "void StartStory(ScenarioType, float, bool)",
                "Bulbul.MasterData.ScenarioType", "System.Single", "System.Boolean"),

            // The room's state machine: deferred actions and scene play wait for Idle.
            new PatchTarget("script.room.mainstate", GameAssembly, "Bulbul.RoomGameManager", MemberKind.Property, "CurrentMainState",
                expectedSignature: "RoomGameManager.MainState CurrentMainState { get; }"),

            Method("script.heroine.ispossibletalk", "Bulbul.HeroineService", "IsPossibleTalk", "bool IsPossibleTalk()"),
            Method("script.heroine.lookinitslowly", "Bulbul.HeroineService", "LookInitSlowly", "void LookInitSlowly()"),
            Method("script.heroine.initfacialafterdelay", "Bulbul.HeroineService", "InitHeroineFacialAfterDelay",
                "void InitHeroineFacialAfterDelay(float)", "System.Single"),

            // The row columns the runtime reads (Bulbul.MasterData.NovelData).
            Field("script.novel.id", NovelType, "ID", "string ID"),
            Field("script.novel.command", NovelType, "Command", "CommandType Command"),
            Field("script.novel.arg1", NovelType, "Arg1", "string Arg1"),
            Field("script.novel.arg2", NovelType, "Arg2", "float Arg2"),
            Field("script.novel.arg3", NovelType, "Arg3", "string Arg3"),
            Field("script.novel.arg4", NovelType, "Arg4", "string Arg4"),
        };

        /// <summary>Every target, for the registry and <c>tools/seamcheck</c>.</summary>
        public static IReadOnlyList<PatchTarget> All { get; } = Concat(Seam, Fallback, SaveGuard, Room);

        private static IReadOnlyList<PatchTarget> Concat(params IReadOnlyList<PatchTarget>[] parts)
        {
            var all = new List<PatchTarget>();
            foreach (var part in parts)
            {
                all.AddRange(part);
            }

            return all;
        }

        private static PatchTarget Field(string id, string type, string field, string signature) =>
            new PatchTarget(id, GameAssembly, type, MemberKind.Field, field, expectedSignature: signature);

        private static PatchTarget Method(string id, string type, string method, string signature, params string[] parameters) =>
            new PatchTarget(id, GameAssembly, type, MemberKind.Method, method, parameters, expectedSignature: signature);
    }
}
