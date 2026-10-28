using System.Collections.Generic;
using CwyLs.ModLoader.Core;

namespace CwyLs.ModLoader.Bulbul.Language
{
    /// <summary>
    /// The game seams the pack-language feature relies on, as patch-target data, so a game update
    /// that moves one is reported at startup and no pack language is applied (the game stays in
    /// its own languages rather than half-working).
    /// </summary>
    /// <remarks>
    /// Every signature was checked against the shipped v1.17.2 game; each entry says what it is
    /// used for. The six web-request structs are verified through their <c>Language</c> field: the
    /// verifier resolves methods, fields and properties, not constructors, and the constructors
    /// themselves are found by <c>AccessTools.Constructor</c> when they are patched
    /// (<see cref="LanguagePatches"/>).
    /// </remarks>
    public static class LanguagePatchTargets
    {
        /// <summary>Assembly every target below lives in.</summary>
        public const string GameAssembly = "Assembly-CSharp";

        private const string LanguageType = "Bulbul.GameLanguageType";

        /// <summary>
        /// The seams without which no pack language may be applied: fonts, the save guard, the load
        /// re-apply and the choice observer. A missing one here means the feature stays inactive.
        /// </summary>
        public static IReadOnlyList<PatchTarget> Mandatory { get; } = new[]
        {
            // FontSupplier: each of the four returns null (and logs an error) for a
            // value it does not know, and a null material throws in TMP_Text.fontMaterial's setter.
            Method("language.font.asset", "FontSupplier", "GetFontAsset", "TMP_FontAsset GetFontAsset(GameLanguageType)"),
            Method("language.font.normal", "FontSupplier", "GetFontNormalMaterial", "Material GetFontNormalMaterial(GameLanguageType)"),
            Method("language.font.gradation", "FontSupplier", "GetFontGradationMaterial", "Material GetFontGradationMaterial(GameLanguageType)"),
            Method("language.font.underlay", "FontSupplier", "GetFontUnderlayMaterial", "Material GetFontUnderlayMaterial(GameLanguageType)"),

            // SettingData.SaveReady copies GameLanguage into the int the save file holds; the
            // postfix rewrites a pack value to its base language before the write.
            new PatchTarget(
                "language.setting.saveready", GameAssembly, "Bulbul.SettingData",
                MemberKind.Method, "SaveReady", expectedSignature: "void SaveReady()"),
            new PatchTarget(
                "language.setting.field", GameAssembly, "Bulbul.SettingData",
                MemberKind.Field, "_gameLanguageForSave", expectedSignature: "int _gameLanguageForSave"),
            new PatchTarget(
                "language.setting.reactive", GameAssembly, "Bulbul.SettingData",
                MemberKind.Field, "GameLanguage",
                expectedSignature: "ReactiveProperty<GameLanguageType> GameLanguage"),

            // SaveDataManager.LoadSetting replaces SettingData from disk; the postfix
            // re-applies the selected pack language before any UI reads it.
            new PatchTarget(
                "language.save.loadsetting", GameAssembly, "Bulbul.SaveDataManager",
                MemberKind.Method, "LoadSetting", expectedSignature: "void LoadSetting()"),

            // LanguageSupplier.Set is where every selector click lands
            // (through SettingService.ChangeGameLanguage).
            new PatchTarget(
                "language.supplier.set", GameAssembly, "Bulbul.LanguageSupplier",
                MemberKind.Method, "Set", new[] { LanguageType },
                expectedSignature: "void Set(GameLanguageType)"),
        };

        /// <summary>
        /// Seams that degrade one piece when missing - the selector entry, typing speed, news,
        /// tutorial images, web requests - without making a pack language unsafe to apply.
        /// </summary>
        public static IReadOnlyList<PatchTarget> Optional { get; } = new[]
        {
            // SettingUI.Setup wires the seven hand-placed selector buttons.
            new PatchTarget(
                "language.settingui.setup", GameAssembly, "Bulbul.SettingUI",
                MemberKind.Method, "Setup", expectedSignature: "void Setup()"),
            new PatchTarget(
                "language.settingui.en", GameAssembly, "Bulbul.SettingUI",
                MemberKind.Field, "_languageENInteractableUI",
                expectedSignature: "InteractableUI _languageENInteractableUI"),
            new PatchTarget(
                "language.settingui.pulldown", GameAssembly, "Bulbul.SettingUI",
                MemberKind.Field, "_languagePulldownList", expectedSignature: "PulldownListUI _languagePulldownList"),
            new PatchTarget(
                "language.settingui.service", GameAssembly, "Bulbul.SettingUI",
                MemberKind.Field, "_settingService", expectedSignature: "SettingService _settingService"),
            new PatchTarget(
                "language.settingui.se", GameAssembly, "Bulbul.SettingUI",
                MemberKind.Field, "_systemSeService", expectedSignature: "SystemSeService _systemSeService"),
            new PatchTarget(
                "language.pulldown.height", GameAssembly, "PulldownListUI",
                MemberKind.Field, "_openPullDownSizeDeltaY", expectedSignature: "float _openPullDownSizeDeltaY"),
            new PatchTarget(
                "language.pulldown.settarget", GameAssembly, "PulldownListUI",
                MemberKind.Method, "SetTargetPullDownSizeDelta", new[] { "System.Single" },
                expectedSignature: "void SetTargetPullDownSizeDelta(float)"),

            // PulldownListUI._pullDownParentRect: the frame whose height the open/close tween
            // animates; the list's scroll view is built inside it. PulldownListUI.IsOpen: read once
            // a frame while the settings window is open, to scroll the selected language into view
            // when the list opens (LanguageListScroller).
            new PatchTarget(
                "language.pulldown.frame", GameAssembly, "PulldownListUI",
                MemberKind.Field, "_pullDownParentRect", expectedSignature: "RectTransform _pullDownParentRect"),
            new PatchTarget(
                "language.pulldown.isopen", GameAssembly, "PulldownListUI",
                MemberKind.Property, "IsOpen", expectedSignature: "bool IsOpen { get; }"),

            // TalkSpeedData.GetSpeed: an unknown value logs an error on every ScenarioTextMessage.Setup.
            Method("language.talkspeed", "Bulbul.TalkSpeedData", "GetSpeed", "float GetSpeed(GameLanguageType)"),

            // NewsMaster.GetNewsDatas -> News.AsNewsData throws ArgumentOutOfRangeException for an unknown value.
            Method("language.news.datas", "Bulbul.NewsMaster", "GetNewsDatas", "NewsData[] GetNewsDatas(GameLanguageType)"),

            // TutorialView.OpenTutorial: four switch expressions give a null sprite for an unknown value.
            new PatchTarget(
                "language.tutorial.open", GameAssembly, "TutorialView",
                MemberKind.Method, "OpenTutorial", new[] { "TutorialService+TutorialPageType" },
                expectedSignature: "void OpenTutorial(TutorialService.TutorialPageType)"),

            // Bulbul.Web request structs whose Language travels to the server as a plain int
            // (built by the game's WebApi). Verified by field; their constructors are prefixed.
            WebField("language.web.getnews", "Bulbul.Web.GetNews"),
            WebField("language.web.getnewscurrent", "Bulbul.Web.GetNewsCurrent"),
            WebField("language.web.login", "Bulbul.Web.Login"),
            WebField("language.web.checklogin", "Bulbul.Web.CheckLogin"),
            WebField("language.web.startupcheck", "Bulbul.Web.StartupCheck"),
            WebField("language.web.checkpurchase", "Bulbul.Web.CheckPurchase"),
        };

        private static PatchTarget Method(string id, string type, string name, string signature) =>
            new PatchTarget(id, GameAssembly, type, MemberKind.Method, name, new[] { LanguageType }, expectedSignature: signature);

        private static PatchTarget WebField(string id, string type) =>
            new PatchTarget(id, GameAssembly, type, MemberKind.Field, "Language", expectedSignature: "GameLanguageType Language");
    }
}
