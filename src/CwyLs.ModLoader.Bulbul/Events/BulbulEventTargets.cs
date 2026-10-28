using System.Collections.Generic;
using CwyLs.ModLoader.Core;

namespace CwyLs.ModLoader.Bulbul.Events
{
    /// <summary>
    /// The game members the event bus adds on top of <see cref="BulbulPatchTargets"/>: the seams
    /// the bus patches, subscribes to or reads that the main table does not already declare.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Same rules as the main table: strings only, exact parameter lists, and an
    /// <see cref="PatchTarget.ExpectedSignature"/> on every entry. Each expected string was read
    /// back out of the shipped v1.17.2 assemblies through a metadata-only load, so "expected" is
    /// what the runtime declares.
    /// </para>
    /// <para>
    /// Ids are prefixed <c>events.</c> so they never collide with the main table, which the
    /// registry would otherwise reject as duplicates. Members already declared there -
    /// <c>heroine.playvoice</c>, <c>heroineai.changestate</c>, <c>scenario.startready</c>,
    /// <c>scenario.commandvoicetext</c>, <c>masterdata.localizations</c>, <c>voice.play</c> and the
    /// six pomodoro observables - are deliberately not repeated.
    /// </para>
    /// <para>
    /// One id per game member across every registry. <c>LocalizationMasterWrapper.Get</c>, which
    /// the speech line reads text through, is <c>subtitles.localization.get</c>
    /// (<c>Subtitles/SubtitlePatchTargets</c>, the feature that patches it); the voice clip cache is
    /// <c>voice.closed.clipcache</c> (<c>Voice/BulbulVoiceTargets</c>). <c>GetBeforeState</c> is gone:
    /// the state prefix reads <c>GetCurrentState</c> instead.
    /// </para>
    /// </remarks>
    public static class BulbulEventTargets
    {
        private const string GameAssembly = "Assembly-CSharp";

        private static readonly PatchTarget[] Table =
        {
            // --- Satone's state: the prefix reads the current state so the event can report a
            // truthful "previous", rather than trusting the state machine's BeforeStateKey, which
            // is only updated when a transition actually matches (MyUtil.StateMachine, game 1.17.2).
            // HeroineAI is in the global namespace.
            new PatchTarget(
                "events.heroineai.getcurrentstate", GameAssembly, "HeroineAI",
                MemberKind.Method, "GetCurrentState",
                expectedSignature: "HeroineAI.ActionStateType GetCurrentState()"),

            // --- Voice: the whole path is HeroineService.PlayVoice -> HeroineAI.PlayVoice ->
            // HeroineVoiceController.PlayVoice -> VoiceManager.Play. The last one is the only point
            // a clip actually starts, so LineSpoken is published there; the two above it are
            // patched to stash the caller's intent (mouth motion, story priority).
            new PatchTarget(
                "events.heroinevoice.playvoice", GameAssembly, "HeroineVoiceController",
                MemberKind.Method, "PlayVoice",
                new[] { "System.String", "System.Boolean", "System.Boolean" },
                expectedSignature: "UniTaskVoid PlayVoice(string, bool, bool)"),

            // Polled once a frame to close a line with LineEnded.
            new PatchTarget(
                "events.heroine.isfinishedvoice", GameAssembly, "Bulbul.HeroineService",
                MemberKind.Property, "IsFinishedVoice",
                expectedSignature: "bool IsFinishedVoice { get; }"),

            // --- Scenario: StartReady is already declared by the main table; these close the pair
            // and give the line's context.
            new PatchTarget(
                "events.scenario.endstory", GameAssembly, "Bulbul.ScenarioReader",
                MemberKind.Method, "EndStory",
                expectedSignature: "void EndStory()"),

            new PatchTarget(
                "events.scenario.playingtype", GameAssembly, "Bulbul.ScenarioReader",
                MemberKind.Property, "PlayingScenarioType",
                expectedSignature: "ScenarioType PlayingScenarioType { get; }"),

            new PatchTarget(
                "events.scenario.playinggroupid", GameAssembly, "Bulbul.ScenarioReader",
                MemberKind.Property, "PlayingScenarioGroupID",
                expectedSignature: "string PlayingScenarioGroupID { get; }"),

            // --- Say attribution: the four private commands that read a master-data row and
            // then call HeroineService.PlayVoice. A prefix on each records the row, so the clip it
            // starts is named by the row the reader is reading rather than by a reverse index on
            // the clip name - which is ambiguous whenever two rows name the same clip. The fourth,
            // CommandVoiceText, is already declared in the main table as
            // "scenario.commandvoicetext" (BulbulPatchTargets) and is not repeated here: the
            // registry rejects a duplicate id, and it is verified either way.
            new PatchTarget(
                "events.scenario.commandtext", GameAssembly, "Bulbul.ScenarioReader",
                MemberKind.Method, "CommandText",
                new[] { "Bulbul.MasterData.NovelData" },
                expectedSignature: "UniTask CommandText(NovelData)"),

            new PatchTarget(
                "events.scenario.commandpomodorovoicetext", GameAssembly, "Bulbul.ScenarioReader",
                MemberKind.Method, "CommandPomodoroVoiceText",
                new[] { "Bulbul.MasterData.NovelData" },
                expectedSignature: "UniTask CommandPomodoroVoiceText(NovelData)"),

            new PatchTarget(
                "events.scenario.commandtextloopplaytext", GameAssembly, "Bulbul.ScenarioReader",
                MemberKind.Method, "CommandTextLoopPlayText",
                new[] { "Bulbul.MasterData.NovelData" },
                expectedSignature: "UniTask CommandTextLoopPlayText(NovelData)"),

            // The two columns the row prefix reads off the captured row.
            new PatchTarget(
                "events.novel.data.localizationid", GameAssembly, "Bulbul.MasterData.NovelData",
                MemberKind.Field, "LocalizationID",
                expectedSignature: "string LocalizationID"),

            new PatchTarget(
                "events.novel.data.scenariogroupid", GameAssembly, "Bulbul.MasterData.NovelData",
                MemberKind.Field, "ScenarioGroupID",
                expectedSignature: "string ScenarioGroupID"),

            // --- Localization: how a voice name becomes the text the player reads. The text itself
            // comes through LocalizationMasterWrapper.Get, declared once as
            // "subtitles.localization.get" (Subtitles/SubtitlePatchTargets.cs).
            // The game's own answer to "which language is the player reading?" - it reads
            // SaveDataManager.Instance.SettingData.GameLanguage and falls back to the platform
            // default.
            new PatchTarget(
                "events.language.get", GameAssembly, "Bulbul.LanguageSupplier",
                MemberKind.Method, "Get",
                expectedSignature: "GameLanguageType Get()"),

            // The three columns the reverse voice index is built from.
            new PatchTarget(
                "events.localization.data.voice", GameAssembly, "Bulbul.MasterData.LocalizationData",
                MemberKind.Field, "Voice",
                expectedSignature: "string Voice"),

            new PatchTarget(
                "events.localization.data.id", GameAssembly, "Bulbul.MasterData.LocalizationData",
                MemberKind.Field, "ID",
                expectedSignature: "string ID"),

            new PatchTarget(
                "events.localization.data.ja", GameAssembly, "Bulbul.MasterData.LocalizationData",
                MemberKind.Field, "Ja",
                expectedSignature: "string Ja"),

            // --- Pomodoro: the three observables and the state property the main table does not declare.
            new PatchTarget(
                "events.pomodoro.onstartpomodoro", GameAssembly, "Bulbul.PomodoroService",
                MemberKind.Property, "OnStartPomodoro",
                expectedSignature: "Observable<Unit> OnStartPomodoro { get; }"),

            new PatchTarget(
                "events.pomodoro.onpause", GameAssembly, "Bulbul.PomodoroService",
                MemberKind.Property, "OnPause",
                expectedSignature: "Observable<Unit> OnPause { get; }"),

            new PatchTarget(
                "events.pomodoro.onunpause", GameAssembly, "Bulbul.PomodoroService",
                MemberKind.Property, "OnUnpause",
                expectedSignature: "Observable<PomodoroService.PomodoroType> OnUnpause { get; }"),

            new PatchTarget(
                "events.pomodoro.currenttype", GameAssembly, "Bulbul.PomodoroService",
                MemberKind.Property, "CurrentPomodoroType",
                expectedSignature: "PomodoroService.PomodoroType CurrentPomodoroType { get; }"),

            // The fallback route to the instance: Bulbul.FacilityPomodoro calls this once, so a
            // postfix on it captures the service even if the room scope refuses to resolve.
            new PatchTarget(
                "events.pomodoro.setup", GameAssembly, "Bulbul.PomodoroService",
                MemberKind.Method, "Setup",
                expectedSignature: "void Setup()"),

            // --- Window view: the four methods that turn a window object on or off.
            new PatchTarget(
                "events.window.changewindowview", GameAssembly, "Bulbul.WindowViewService",
                MemberKind.Method, "ChangeWindowView",
                new[] { "Bulbul.WindowViewType" },
                expectedSignature: "void ChangeWindowView(WindowViewType)"),

            new PatchTarget(
                "events.window.changeweatherandtime", GameAssembly, "Bulbul.WindowViewService",
                MemberKind.Method, "ChangeWeatherAndTime",
                new[] { "Bulbul.WindowViewType" },
                expectedSignature: "void ChangeWeatherAndTime(WindowViewType)"),

            new PatchTarget(
                "events.window.activatewindow", GameAssembly, "Bulbul.WindowViewService",
                MemberKind.Method, "ActivateWindow",
                new[] { "Bulbul.WindowViewType" },
                expectedSignature: "void ActivateWindow(WindowViewType)"),

            new PatchTarget(
                "events.window.deactivatewindow", GameAssembly, "Bulbul.WindowViewService",
                MemberKind.Method, "DeactivateWindow",
                new[] { "Bulbul.WindowViewType" },
                expectedSignature: "void DeactivateWindow(WindowViewType)"),

            // --- Music: every play path in MusicService ends with onPlayMusic.OnNext, so the bus
            // subscribes instead of patching four methods, two of which are async and would report
            // before the track actually changed. MusicService is in the global namespace.
            new PatchTarget(
                "events.music.onplaymusic", GameAssembly, "MusicService",
                MemberKind.Property, "OnPlayMusic",
                expectedSignature: "Observable<GameAudioInfo> OnPlayMusic { get; }"),

            new PatchTarget(
                "events.music.onenableshuffle", GameAssembly, "MusicService",
                MemberKind.Property, "OnEnableShuffle",
                expectedSignature: "Observable<Unit> OnEnableShuffle { get; }"),

            new PatchTarget(
                "events.music.ondisableshuffle", GameAssembly, "MusicService",
                MemberKind.Property, "OnDisableShuffle",
                expectedSignature: "Observable<Unit> OnDisableShuffle { get; }"),

            // Subscribed here (it used to be the data bus's own subscription) and handed on as a
            // GameSignal; the data bus reports it as "event:OnChangeMusic".
            new PatchTarget(
                "events.music.onchangemusic", GameAssembly, "MusicService",
                MemberKind.Property, "OnChangeMusic",
                expectedSignature: "Observable<MusicChangeKind> OnChangeMusic { get; }"),

            new PatchTarget(
                "events.music.playingmusic", GameAssembly, "MusicService",
                MemberKind.Property, "PlayingMusic",
                expectedSignature: "GameAudioInfo PlayingMusic { get; set; }"),

            new PatchTarget(
                "events.music.isshuffle", GameAssembly, "MusicService",
                MemberKind.Property, "IsShuffle",
                expectedSignature: "bool IsShuffle { get; set; }"),

            // Read reflectively for the track's playlist index: its type lives in
            // ObservableCollections.dll, which this assembly deliberately does not reference.
            new PatchTarget(
                "events.music.currentplaylist", GameAssembly, "MusicService",
                MemberKind.Property, "CurrentPlayList",
                expectedSignature: "ObservableList<GameAudioInfo> CurrentPlayList { get; set; }"),

            new PatchTarget(
                "events.music.title", GameAssembly, "Bulbul.GameAudioInfo",
                MemberKind.Field, "Title",
                expectedSignature: "string Title"),

            // The fallback route to the instance, called once by Bulbul.FacilityMusic.
            new PatchTarget(
                "events.music.setup", GameAssembly, "MusicService",
                MemberKind.Method, "Setup",
                expectedSignature: "void Setup()"),

            // --- Saves: every Save*/Delete* method carries the one shared postfix (SaveDataHook,
            // found by reflection); this entry stands for the whole family, so a renamed save API is
            // reported once rather than as 46 misses. The data bus's registry adds the load seams.
            new PatchTarget(
                "events.savedata.saveall", GameAssembly, "Bulbul.SaveDataManager",
                MemberKind.Method, "SaveAll",
                expectedSignature: "void SaveAll()"),

            // --- How the bus gets hold of the game's service instances.
            new PatchTarget(
                "events.room.resolve", GameAssembly, "Bulbul.RoomLifetimeScope",
                MemberKind.Method, "Resolve",
                isStatic: true,
                expectedSignature: "static T Resolve()"),

            new PatchTarget(
                "events.project.tryresolve", GameAssembly, "NestopiSystem.DIContainers.ProjectLifetimeScope",
                MemberKind.Method, "TryResolve",
                new[] { "T&" },
                isStatic: true,
                expectedSignature: "static bool TryResolve(T&)")
        };

        /// <summary>Every seam the event bus adds, in the order the bus uses them.</summary>
        public static IReadOnlyList<PatchTarget> All => Table;
    }
}
