using System.Collections.Generic;
using CwyLs.ModLoader.Core;

namespace CwyLs.ModLoader.Bulbul
{
    /// <summary>
    /// The game members this adapter is built on (its verified seams), as data.
    ///
    /// Every entry was checked against game v1.17.2 and nothing here is guessed. The table is
    /// strings only, so it can name members of AudioManager.dll, which this project does not
    /// reference at compile time.
    ///
    /// <para>Parameter lists are exact: an empty list means "takes no parameters", never "any
    /// overload". A parameter is named by its reflection full name, so a nested type uses <c>+</c>.</para>
    ///
    /// <para>Every entry carries a <see cref="PatchTarget.ExpectedSignature"/>, so a silent
    /// return-type or parameter change fails loudly at startup rather than as a Harmony error
    /// mid-session. Each expected string was compared against the shipped v1.17.2 assemblies read
    /// through a metadata-only load, so "expected" is what the runtime actually declares.</para>
    /// </summary>
    public static class BulbulPatchTargets
    {
        private const string GameAssembly = "Assembly-CSharp";
        private const string AudioAssembly = "AudioManager";

        private static readonly PatchTarget[] Table =
        {
            // --- Master data: add/replace dialogue rows ----------------------------------------
            new PatchTarget(
                "masterdata.load", GameAssembly, "Bulbul.MasterDataLoader",
                MemberKind.Method, "Load",
                expectedSignature: "UniTask Load()"),

            // The three collections a pack merges into, in the Load postfix.
            new PatchTarget(
                "masterdata.localizations", GameAssembly, "Bulbul.MasterDataLoader",
                MemberKind.Field, "LocalizationList",
                expectedSignature: "IReadOnlyDictionary<string, LocalizationData> LocalizationList"),

            new PatchTarget(
                "masterdata.novels", GameAssembly, "Bulbul.MasterDataLoader",
                MemberKind.Field, "NovelMasterList",
                expectedSignature: "IReadOnlyList<NovelData> NovelMasterList"),

            new PatchTarget(
                "masterdata.scenariogroups", GameAssembly, "Bulbul.MasterDataLoader",
                MemberKind.Field, "ScenarioGroupMasterList",
                expectedSignature: "IReadOnlyList<ScenarioGroupData> ScenarioGroupMasterList"),

            // --- Satone: voice, expression, motion ---------------------------------------------
            new PatchTarget(
                "heroine.playvoice", GameAssembly, "Bulbul.HeroineService",
                MemberKind.Method, "PlayVoice",
                new[] { "System.String", "System.Boolean", "System.Boolean" },
                expectedSignature: "UniTaskVoid PlayVoice(string, bool, bool)"),

            new PatchTarget(
                "heroine.facial", GameAssembly, "Bulbul.HeroineService",
                MemberKind.Method, "ChangeHeroineFacialAnimation",
                new[] { "System.Int32" },
                expectedSignature: "void ChangeHeroineFacialAnimation(int)"),

            new PatchTarget(
                "heroine.motion", GameAssembly, "Bulbul.HeroineService",
                MemberKind.Method, "ChangeHeroineAnimationForInteger",
                new[] { "System.Int32" },
                expectedSignature: "void ChangeHeroineAnimationForInteger(int)"),

            new PatchTarget(
                "heroine.animation", GameAssembly, "Bulbul.HeroineService",
                MemberKind.Method, "ChangeAnimation",
                new[] { "Bulbul.HeroineService+AnimationType" },
                expectedSignature: "void ChangeAnimation(HeroineService.AnimationType)"),

            // The third parameter is DOTween's DG.Tweening.Ease.
            new PatchTarget(
                "heroine.lookscale", GameAssembly, "Bulbul.HeroineService",
                MemberKind.Method, "ChangeLookScaleAnimation",
                new[] { "System.Single", "System.Single", "DG.Tweening.Ease" },
                expectedSignature: "void ChangeLookScaleAnimation(float, float, Ease)"),

            // --- State events ------------------------------------------------------------------
            // HeroineAI is in the global namespace.
            new PatchTarget(
                "heroineai.changestate", GameAssembly, "HeroineAI",
                MemberKind.Method, "ChangeState",
                new[] { "HeroineAI+ActionStateType" },
                expectedSignature: "void ChangeState(HeroineAI.ActionStateType)"),

            // A representative member of the enum the event bus will dispatch on, so a renamed or
            // renumbered state shows up here rather than as a wrong-looking event.
            new PatchTarget(
                "heroineai.state.wanttalk", GameAssembly, "HeroineAI+ActionStateType",
                MemberKind.Field, "WantTalk",
                isStatic: true,
                expectedSignature: "static HeroineAI.ActionStateType WantTalk"),

            // --- Scenario playback and subtitle text -------------------------------------------
            new PatchTarget(
                "scenario.startready", GameAssembly, "Bulbul.ScenarioReader",
                MemberKind.Method, "StartReady",
                new[]
                {
                    "Bulbul.MasterData.ScenarioType",
                    "System.String",
                    "Bulbul.ScenarioReader+ScenarioReadMode",
                    "System.Boolean",
                    "System.Boolean",
                    "System.Boolean"
                },
                expectedSignature:
                    "void StartReady(ScenarioType, string, ScenarioReader.ScenarioReadMode, bool, bool, bool)"),

            // Private; this is where a line's text reaches the story UI.
            new PatchTarget(
                "scenario.commandvoicetext", GameAssembly, "Bulbul.ScenarioReader",
                MemberKind.Method, "CommandVoiceText",
                new[] { "Bulbul.MasterData.NovelData" },
                expectedSignature: "UniTask CommandVoiceText(NovelData)"),

            // --- Pomodoro events: R3 observables, subscribed not patched -----------------------
            new PatchTarget(
                "pomodoro.onstartwork", GameAssembly, "Bulbul.PomodoroService",
                MemberKind.Property, "OnStartWork",
                expectedSignature: "Observable<Unit> OnStartWork { get; }"),

            new PatchTarget(
                "pomodoro.onendwork", GameAssembly, "Bulbul.PomodoroService",
                MemberKind.Property, "OnEndWork",
                expectedSignature: "Observable<Unit> OnEndWork { get; }"),

            new PatchTarget(
                "pomodoro.onstartbreak", GameAssembly, "Bulbul.PomodoroService",
                MemberKind.Property, "OnStartBreak",
                expectedSignature: "Observable<Unit> OnStartBreak { get; }"),

            new PatchTarget(
                "pomodoro.onendbreak", GameAssembly, "Bulbul.PomodoroService",
                MemberKind.Property, "OnEndBreak",
                expectedSignature: "Observable<Unit> OnEndBreak { get; }"),

            new PatchTarget(
                "pomodoro.oncomplete", GameAssembly, "Bulbul.PomodoroService",
                MemberKind.Property, "OnCompletePomodoro",
                expectedSignature: "Observable<PomodoroService.PomodoroType> OnCompletePomodoro { get; }"),

            // Declared as Observable<(PomodoroType, TimeSpan, TimeSpan)>;
            // the C# tuple is a System.ValueTuple at the metadata level, which is what we assert.
            new PatchTarget(
                "pomodoro.onupdate", GameAssembly, "Bulbul.PomodoroService",
                MemberKind.Property, "OnUpdatePomodoro",
                expectedSignature:
                    "Observable<ValueTuple<PomodoroService.PomodoroType, TimeSpan, TimeSpan>> "
                    + "OnUpdatePomodoro { get; }"),

            // --- Fonts per language ------------------------------------------------------------
            // FontSupplier.GetFontAsset is declared once, by the feature that patches and calls it:
            // "language.font.asset" (Language/LanguagePatchTargets.cs). The old duplicate
            // "font.getfontasset" is gone, together with its former caller, SubtitleFonts.

            // --- Voice clips: third-party AudioManager.dll -------------------------------------
            new PatchTarget(
                "voice.play", AudioAssembly, "KanKikuchi.AudioManager.VoiceManager",
                MemberKind.Method, "Play",
                new[]
                {
                    "System.String",
                    "System.Single",
                    "System.Single",
                    "System.Single",
                    "System.Boolean",
                    "System.Action",
                    "System.String",
                    "UnityEngine.Transform"
                },
                expectedSignature: "AudioPlayer Play(string, float, float, float, bool, Action, string, Transform)"),

            // The clip cache (_audioClipDict) and GetAudioClip live on the open generic base
            // AudioManager`1 and are private. They are declared once, on the closed
            // AudioManager<VoiceManager> that the voice feature actually patches and reads, as
            // "voice.closed.clipcache" and "voice.closed.getaudioclip" (Voice/BulbulVoiceTargets.cs).

            // Protected - the path-to-cache-key rule our clip names must follow.
            new PatchTarget(
                "audio.pathtoname", AudioAssembly, "KanKikuchi.AudioManager.AudioManager`1",
                MemberKind.Method, "PathToName",
                new[] { "System.String" },
                expectedSignature: "string PathToName(string)"),
        };

        /// <summary>Every seam this adapter depends on, grouped by area.</summary>
        public static IReadOnlyList<PatchTarget> All => Table;
    }
}
