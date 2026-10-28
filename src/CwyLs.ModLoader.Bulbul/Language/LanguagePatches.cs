using System;
using System.Collections.Generic;
using System.Reflection;
using CwyLs.ModLoader.Core.Language;
using CwyLs.ModLoader.Core.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;
using Game = global::Bulbul;
using Web = global::Bulbul.Web;

namespace CwyLs.ModLoader.Bulbul.Language
{
    /// <summary>
    /// The Harmony patches that make a pack language safe to run: wherever the game only
    /// understands its own seven languages, a pack value is replaced by its base language, and the
    /// save file never sees a pack value.
    /// </summary>
    /// <remarks>
    /// <para>One idea, applied at every site where the game handles a language value:</para>
    /// <list type="bullet">
    /// <item><description>
    /// <b>Prefixes that swap the argument</b> (<c>ref language</c> -> base) on the four
    /// <c>FontSupplier</c> getters, <c>TalkSpeedData.GetSpeed</c>, <c>NewsMaster.GetNewsDatas</c>
    /// and, with <c>Language.SafeWebLanguage</c>, the six <c>Bulbul.Web</c> request constructors.
    /// A prefix and not a postfix: the originals log an error for an unknown value before
    /// returning null, and a postfix would be too late to stop that.
    /// </description></item>
    /// <item><description>
    /// <b>Save guard</b>: a postfix on <c>SettingData.SaveReady</c> rewrites
    /// <c>_gameLanguageForSave</c> to the base language before <c>SaveDataManager.SaveSetting</c>
    /// writes it.
    /// </description></item>
    /// <item><description>
    /// <b>Load re-apply</b>: a postfix on <c>SaveDataManager.LoadSetting</c> puts the selected pack
    /// language back in memory before any UI reads it.
    /// </description></item>
    /// <item><description>
    /// <b>Choice observer</b>: a postfix on <c>LanguageSupplier.Set</c>, where every selector click
    /// lands, keeps <c>Language.Selected</c> in step.
    /// </description></item>
    /// <item><description>
    /// <b>Selector and tutorial</b>: postfixes on <c>SettingUI.Setup</c> (<see cref="LanguageSelector"/>)
    /// and <c>TutorialView.OpenTutorial</c> (fills the page image the game left null).
    /// </description></item>
    /// </list>
    /// </remarks>
    internal static class LanguagePatches
    {
        private static readonly List<string> AppliedIds = new List<string>();
        private static readonly List<string> FailedIds = new List<string>();

        private static AccessTools.FieldRef<Game.SettingData, int>? _saveField;

        /// <summary>What was patched and what failed, for the startup line and the smoke report. Never null.</summary>
        internal static string Detail { get; private set; } = "not attempted";

        /// <summary>The ids of the patches that are in place.</summary>
        internal static IReadOnlyList<string> Applied => AppliedIds;

        /// <summary>The ids of the patches that could not be applied.</summary>
        internal static IReadOnlyList<string> Failed => FailedIds;

        /// <summary>How many times the save guard rewrote a pack value before a save.</summary>
        internal static long SaveRewrites { get; private set; }

        /// <summary>How many web requests had a pack value replaced by its base language.</summary>
        internal static long WebRewrites { get; private set; }

        /// <summary>The last value the save guard wrote, or -1 before it has run.</summary>
        internal static int LastSavedValue { get; private set; } = -1;

        /// <summary>
        /// Applies the patches. The save and load guards go in whenever the feature is enabled, so a
        /// pack value can never reach the save file; the rest only when a pack declares a language.
        /// </summary>
        /// <param name="harmony">This feature's own Harmony instance.</param>
        /// <param name="anyLanguage">True when at least one pack language is registered.</param>
        /// <param name="optionalVerified">True when every optional seam verified.</param>
        /// <returns>True when every mandatory patch is in place.</returns>
        internal static bool Apply(Harmony harmony, bool anyLanguage, bool optionalVerified)
        {
            var mandatoryOk = true;

            try
            {
                _saveField = AccessTools.FieldRefAccess<Game.SettingData, int>("_gameLanguageForSave");
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogError(LanguageFeature.LogPrefix + " _gameLanguageForSave is not reachable: " + ex.GetType().Name + ": " + ex.Message);
                mandatoryOk = false;
            }

            mandatoryOk &= Patch(harmony, "saveready", AccessTools.Method(typeof(Game.SettingData), "SaveReady"), postfix: nameof(SaveReadyPostfix));
            mandatoryOk &= Patch(harmony, "loadsetting", AccessTools.Method(typeof(Game.SaveDataManager), "LoadSetting"), postfix: nameof(LoadSettingPostfix));

            if (anyLanguage)
            {
                var language = new[] { typeof(Game.GameLanguageType) };
                mandatoryOk &= Patch(harmony, "font.asset", AccessTools.Method(typeof(global::FontSupplier), "GetFontAsset", language), prefix: nameof(BaseLanguagePrefix));
                mandatoryOk &= Patch(harmony, "font.normal", AccessTools.Method(typeof(global::FontSupplier), "GetFontNormalMaterial", language), prefix: nameof(BaseLanguagePrefix));
                mandatoryOk &= Patch(harmony, "font.gradation", AccessTools.Method(typeof(global::FontSupplier), "GetFontGradationMaterial", language), prefix: nameof(BaseLanguagePrefix));
                mandatoryOk &= Patch(harmony, "font.underlay", AccessTools.Method(typeof(global::FontSupplier), "GetFontUnderlayMaterial", language), prefix: nameof(BaseLanguagePrefix));
                mandatoryOk &= Patch(harmony, "supplier.set", AccessTools.Method(typeof(Game.LanguageSupplier), "Set", language), postfix: nameof(SupplierSetPostfix));

                // Optional: each degrades one thing when missing, none makes a pack language unsafe.
                Patch(harmony, "talkspeed", AccessTools.Method(typeof(Game.TalkSpeedData), "GetSpeed", language), prefix: nameof(BaseLanguagePrefix));
                Patch(harmony, "news", AccessTools.Method(typeof(Game.NewsMaster), "GetNewsDatas", language), prefix: nameof(NewsPrefix));
                Patch(harmony, "settingui", AccessTools.Method(typeof(Game.SettingUI), "Setup"), postfix: nameof(SettingUiSetupPostfix));
                Patch(harmony, "tutorial", AccessTools.Method(typeof(global::TutorialView), "OpenTutorial"), postfix: nameof(TutorialPostfix));

                if (LanguageFeature.SafeWebLanguage)
                {
                    PatchWeb(harmony, "web.getnews", typeof(Web.GetNews), typeof(string), typeof(Game.GameLanguageType));
                    PatchWeb(harmony, "web.getnewscurrent", typeof(Web.GetNewsCurrent), typeof(Game.GameLanguageType), typeof(ulong));
                    PatchWeb(harmony, "web.login", typeof(Web.Login), typeof(string), typeof(Game.GameLanguageType));
                    PatchWeb(harmony, "web.checklogin", typeof(Web.CheckLogin), typeof(string), typeof(ulong), typeof(Game.GameLanguageType));
                    PatchWeb(harmony, "web.startupcheck", typeof(Web.StartupCheck), typeof(string), typeof(Game.GameLanguageType));
                    PatchWeb(harmony, "web.checkpurchase", typeof(Web.CheckPurchase), typeof(string), typeof(string), typeof(Game.GameLanguageType));
                }
            }

            Detail = AppliedIds.Count + " applied"
                + (FailedIds.Count > 0 ? ", FAILED: " + string.Join(", ", FailedIds) : string.Empty)
                + (optionalVerified ? string.Empty : " (an optional target is missing; see PATCH TARGET MISSING)");

            return mandatoryOk;
        }

        /// <summary>
        /// Prefix for every method whose parameter is <c>language</c>: a pack value becomes its base
        /// language, so the original takes a branch it knows.
        /// </summary>
        /// <param name="language">The argument, replaced in place.</param>
        private static void BaseLanguagePrefix(ref Game.GameLanguageType language)
        {
            var value = (int)language;
            if (value >= GameLanguages.FirstPackValue)
            {
                language = (Game.GameLanguageType)LanguageFeature.Registry.BaseValueOf(value);
            }
        }

        /// <summary>Prefix for <c>NewsMaster.GetNewsDatas(GameLanguageType lang)</c>, whose parameter is <c>lang</c>.</summary>
        /// <param name="lang">The argument, replaced in place.</param>
        private static void NewsPrefix(ref Game.GameLanguageType lang)
        {
            var value = (int)lang;
            if (value >= GameLanguages.FirstPackValue)
            {
                lang = (Game.GameLanguageType)LanguageFeature.Registry.BaseValueOf(value);
            }
        }

        /// <summary>Prefix for the six request constructors: the server never sees a pack value.</summary>
        /// <param name="language">The argument, replaced in place.</param>
        private static void WebPrefix(ref Game.GameLanguageType language)
        {
            var value = (int)language;
            if (value >= GameLanguages.FirstPackValue)
            {
                language = (Game.GameLanguageType)LanguageFeature.Registry.BaseValueOf(value);
                WebRewrites++;
            }
        }

        /// <summary>
        /// Postfix on <c>SettingData.SaveReady()</c>: the int about to be written holds the base
        /// language, never a pack value. Runs before <c>saveDataIO.Save</c>.
        /// </summary>
        /// <param name="__instance">The setting data being saved.</param>
        private static void SaveReadyPostfix(Game.SettingData __instance)
        {
            if (_saveField is null || __instance is null)
            {
                return;
            }

            try
            {
                ref var saved = ref _saveField(__instance);
                var safe = LanguageFeature.Choice.ForSave(saved);
                if (safe != saved)
                {
                    saved = safe;
                    SaveRewrites++;
                    ConsoleEvents.File(
                        LanguageFeature.TraceSys,
                        "saveGuard",
                        ("inMemory", (int)__instance.GameLanguage.Value),
                        ("saved", safe));
                }

                LastSavedValue = saved;
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogError(LanguageFeature.LogPrefix + " the save guard failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>
        /// Postfix on <c>SaveDataManager.LoadSetting()</c>: the selected pack language goes back in
        /// memory; a pack-range value the save somehow held and that will not be applied becomes safe.
        /// </summary>
        /// <param name="__instance">The save manager.</param>
        private static void LoadSettingPostfix(Game.SaveDataManager __instance)
        {
            try
            {
                var property = __instance?.SettingData?.GameLanguage;
                if (property is null)
                {
                    return;
                }

                var saved = (int)property.Value;
                var adopted = LanguageFeature.Choice.Adopt(saved);
                if (adopted == saved)
                {
                    return;
                }

                property.Value = (Game.GameLanguageType)adopted;
                ConsoleEvents.Emit(
                    LanguageFeature.TraceSys,
                    "applied",
                    ConsoleTags.Language,
                    "applied " + LanguageFeature.Registry.ToCode(adopted) + " over the saved "
                        + LanguageFeature.Registry.ToCode(saved),
                    "value,saved",
                    ("value", adopted),
                    ("saved", LanguageFeature.Registry.ToName(saved)),
                    ("config", LanguageFeature.Choice.Selected));
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogError(LanguageFeature.LogPrefix + " could not re-apply the chosen language after LoadSetting: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>Postfix on <c>LanguageSupplier.Set(GameLanguageType)</c>: records the choice.</summary>
        /// <param name="value">The language the game switched to.</param>
        private static void SupplierSetPostfix(Game.GameLanguageType value)
        {
            try
            {
                LanguageFeature.Observe((int)value, "selector");
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogError(LanguageFeature.LogPrefix + " could not record the language change: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>Postfix on <c>SettingUI.Setup()</c>: adds a selector entry per pack language.</summary>
        /// <param name="__instance">The settings window.</param>
        private static void SettingUiSetupPostfix(Game.SettingUI __instance)
        {
            try
            {
                LanguageSelector.OnSetup(__instance);
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogError(LanguageFeature.LogPrefix + " could not add the selector entries: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>
        /// Postfix on <c>TutorialView.OpenTutorial</c>: for a pack language the game's switch left the
        /// page image null; show the base language's screenshot instead.
        /// </summary>
        /// <param name="__instance">The tutorial view.</param>
        private static void TutorialPostfix(global::TutorialView __instance)
        {
            var active = LanguageFeature.Active;
            if (active is null || __instance is null)
            {
                return;
            }

            try
            {
                var suffix = SpriteSuffix(active.BaseValue);
                FillSprite(__instance, "_screenImage", "_screenSprite" + suffix);
                FillSprite(__instance, "_pomodoroTimerImage", "_pomodoroTimerSprite" + suffix);
                FillSprite(__instance, "_levelAndStoryImage", "_levelAndStorySprite" + suffix);
                FillSprite(__instance, "_newEnvironmentImage", "_newEnvironmentSprite" + suffix);
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogWarning(LanguageFeature.LogPrefix + " could not fill the tutorial image: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void FillSprite(global::TutorialView view, string imageField, string spriteField)
        {
            if (!(AccessTools.Field(typeof(global::TutorialView), imageField)?.GetValue(view) is Image image) || image == null)
            {
                return;
            }

            if (image.sprite == null && AccessTools.Field(typeof(global::TutorialView), spriteField)?.GetValue(view) is Sprite sprite)
            {
                image.sprite = sprite;
            }
        }

        /// <summary>The field-name suffix <c>TutorialView</c> uses for each of the game's languages.</summary>
        private static string SpriteSuffix(int builtIn)
        {
            switch (builtIn)
            {
                case GameLanguages.Japanese: return "JP";
                case GameLanguages.ChineseSimplified: return "SC";
                case GameLanguages.ChineseTraditional: return "TC";
                case GameLanguages.Portuguese: return "PR";
                case GameLanguages.Korean: return "KR";
                case GameLanguages.Russian: return "RU";
                default: return "EN";
            }
        }

        private static void PatchWeb(Harmony harmony, string id, Type request, params Type[] parameters)
        {
            MethodBase? constructor = null;
            try
            {
                constructor = AccessTools.Constructor(request, parameters);
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogDebug(LanguageFeature.LogPrefix + " " + request.Name + " constructor lookup threw: " + ex.GetType().Name + ": " + ex.Message);
            }

            Patch(harmony, id, constructor, prefix: nameof(WebPrefix));
        }

        private static bool Patch(Harmony harmony, string id, MethodBase? original, string? prefix = null, string? postfix = null)
        {
            if (original is null)
            {
                FailedIds.Add(id);
                CwyLsModLoader.Log.LogError(LanguageFeature.LogPrefix + " PATCH TARGET MISSING: " + id);
                return false;
            }

            try
            {
                harmony.Patch(
                    original,
                    prefix: prefix is null ? null : new HarmonyMethod(typeof(LanguagePatches), prefix),
                    postfix: postfix is null ? null : new HarmonyMethod(typeof(LanguagePatches), postfix));
                AppliedIds.Add(id);
                return true;
            }
            catch (Exception ex)
            {
                FailedIds.Add(id);
                CwyLsModLoader.Log.LogError(LanguageFeature.LogPrefix + " could not patch " + id + ": " + ex.GetType().Name + ": " + ex.Message);
                return false;
            }
        }
    }
}
