using System;
using System.Collections.Generic;
using System.Diagnostics;
using CwyLs.ModLoader.Bulbul.Language;
using CwyLs.ModLoader.Core.Bus;
using CwyLs.ModLoader.Core.Language;
using CwyLs.ModLoader.Core.Logging;
using CwyLs.ModLoader.Core.Subtitles;
using HarmonyLib;
using TMPro;
using Game = global::Bulbul;
using Master = global::Bulbul.MasterData;

namespace CwyLs.ModLoader.Bulbul.Subtitles
{
    /// <summary>
    /// The game's own text box is the only place a line is shown: this class serves a pack
    /// language's text through the game's text seam and watches what the box displays.
    /// </summary>
    /// <remarks>
    /// <para>Two postfixes, both owned by the subtitle feature's Harmony instance:</para>
    /// <list type="bullet">
    /// <item><description>
    /// <c>LocalizationMasterWrapper.Get(LocalizationData, GameLanguageType)</c> - the one overload
    /// every localized string funnels through (<c>Get(data)</c>, both <c>TryGet</c> and both
    /// <c>Bind</c> overloads call it). When the language
    /// asked for is a pack language, the result is the pack's row, or the first fallback along the
    /// language's declared chain (<see cref="LanguageText"/>) - never the game's default branch,
    /// which is Japanese (<c>:51</c>). For the game's own languages it returns at once.
    /// </description></item>
    /// <item><description>
    /// <c>ScenarioTextMessage.StartText(string)</c> - not to change anything, but to know what the
    /// box shows and since when, for the reading-time hold (<see cref="SubtitleHold"/>) and for the
    /// one console line per displayed translation.
    /// </description></item>
    /// </list>
    /// </remarks>
    internal static class SubtitleNative
    {
        private static readonly Stopwatch Clock = Stopwatch.StartNew();
        private static readonly object Gate = new object();

        /// <summary>
        /// What each story label said when the room came up - the designer's placeholder, which is
        /// what an idle box shows. Keyed by instance id.
        /// </summary>
        private static readonly Dictionary<int, string> Placeholders = new Dictionary<int, string>();

        private static string? _lastDisplayText;

        /// <summary>Seconds since this class was first used; the one clock the hold and the log share.</summary>
        internal static double Now => Clock.Elapsed.TotalSeconds;

        /// <summary>True once both postfixes are in place.</summary>
        internal static bool Applied { get; private set; }

        /// <summary>What was patched, or why it could not be. Never null.</summary>
        internal static string Detail { get; private set; } = "not attempted";

        /// <summary>How many localized strings have been served from a pack table.</summary>
        internal static long Overrides { get; private set; }

        /// <summary>How many localized strings in a pack language fell back to a game column.</summary>
        internal static long Fallbacks { get; private set; }

        /// <summary>The last row id served from a pack table, or null.</summary>
        internal static string? LastOverrideId { get; private set; }

        /// <summary>The last pack text served, as stored (without soft breaks), or null.</summary>
        internal static string? LastOverrideText { get; private set; }

        /// <summary>
        /// True while a smoke check reads through the seam itself: the text is served as usual but
        /// the counters and the "last served" record are left alone, so the check does not overwrite
        /// what the game's own box last received. Main thread only.
        /// </summary>
        internal static bool ProbeInProgress { get; set; }

        /// <summary>When the game's text box was last given a line, on <see cref="Now"/>'s clock; negative before.</summary>
        internal static double LastStartTextAt { get; private set; } = double.NegativeInfinity;

        /// <summary>The last text the game's box was given, or null.</summary>
        internal static string? LastStartText { get; private set; }

        /// <summary>Applies both postfixes. Idempotent.</summary>
        /// <param name="harmony">The subtitle feature's Harmony instance.</param>
        /// <returns>True when both patches are in place.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="harmony"/> is null.</exception>
        internal static bool Apply(Harmony harmony)
        {
            if (harmony is null)
            {
                throw new ArgumentNullException(nameof(harmony));
            }

            if (Applied)
            {
                return true;
            }

            try
            {
                var get = AccessTools.Method(
                    typeof(Game.LocalizationMasterWrapper),
                    SubtitlePatchTargets.GetMethodName,
                    new[] { typeof(Master.LocalizationData), typeof(Game.GameLanguageType) });

                var startText = AccessTools.Method(
                    typeof(Game.ScenarioTextMessage),
                    SubtitlePatchTargets.StartTextMethodName,
                    new[] { typeof(string) });

                if (get is null || startText is null)
                {
                    Detail = "PATCH TARGET MISSING: "
                        + (get is null ? SubtitlePatchTargets.LocalizationWrapperTypeName + ".Get " : string.Empty)
                        + (startText is null ? SubtitlePatchTargets.TextMessageTypeName + ".StartText" : string.Empty);
                    CwyLsModLoader.Log.LogError(SubtitleFeature.LogPrefix + " " + Detail);
                    return false;
                }

                harmony.Patch(get, postfix: new HarmonyMethod(typeof(SubtitleNative), nameof(GetPostfix)));
                harmony.Patch(startText, postfix: new HarmonyMethod(typeof(SubtitleNative), nameof(StartTextPostfix)));

                Applied = true;
                Detail = "postfixed LocalizationMasterWrapper.Get(LocalizationData, GameLanguageType) "
                    + "and ScenarioTextMessage.StartText(string)";
                CwyLsModLoader.Log.LogDebug(SubtitleFeature.LogPrefix + " " + Detail);
                return true;
            }
            catch (Exception ex)
            {
                Detail = $"could not patch the story text seams: {ex.GetType().Name}: {ex.Message}";
                CwyLsModLoader.Log.LogError(SubtitleFeature.LogPrefix + " " + Detail);
                return false;
            }
        }

        /// <summary>
        /// Records each story label's idle text. Called at <c>RoomReady</c>, when no line has been
        /// spoken into the boxes yet.
        /// </summary>
        internal static void RememberPlaceholders()
        {
            foreach (var label in LanguageFonts.StoryLabels())
            {
                lock (Gate)
                {
                    Placeholders[label.GetInstanceID()] = label.text ?? string.Empty;
                }
            }
        }

        /// <summary>
        /// True when a story label is not showing a spoken line: its object is off, its text is
        /// blank, it still says what it said at <c>RoomReady</c>, or it does not hold the last line
        /// the game started.
        /// </summary>
        /// <param name="label">A story label.</param>
        internal static bool IsIdle(TextMeshProUGUI? label)
        {
            if (label == null || !label.gameObject.activeInHierarchy)
            {
                return true;
            }

            var text = label.text;
            if (string.IsNullOrWhiteSpace(text))
            {
                return true;
            }

            lock (Gate)
            {
                if (Placeholders.TryGetValue(label.GetInstanceID(), out var placeholder)
                    && string.Equals(placeholder, text, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return !string.Equals(text, LastStartText, StringComparison.Ordinal);
        }

        /// <summary>
        /// Harmony postfix on <c>LocalizationMasterWrapper.Get(LocalizationData, GameLanguageType)</c>.
        /// Parameter names match the original, which is how Harmony binds them.
        /// </summary>
        /// <param name="__instance">The wrapper, used to read a fallback language's column.</param>
        /// <param name="data">The master-data row the game asked about.</param>
        /// <param name="language">The language the game asked for.</param>
        /// <param name="__result">The game's answer, replaced for a pack language.</param>
        private static void GetPostfix(
            Game.LocalizationMasterWrapper __instance,
            Master.LocalizationData data,
            Game.GameLanguageType language,
            ref string __result)
        {
            // Cheapest possible exit: this runs for every localized string in the game.
            var value = (int)language;
            if (value < GameLanguages.FirstPackValue)
            {
                return;
            }

            try
            {
                if (!LanguageFeature.CanApply || !LanguageFeature.Registry.TryGet(value, out var pack) || pack is null)
                {
                    return;
                }

                var id = data?.ID;
                if (string.IsNullOrEmpty(id))
                {
                    return;
                }

                // A fallback to a game language reads the game's own column through the same method;
                // that call is a built-in value, so this postfix returns at its first line.
                var result = LanguageText.Resolve(
                    pack,
                    id,
                    SubtitleSources.All,
                    builtIn => __instance.Get(data, (Game.GameLanguageType)builtIn));

                if (!result.Found)
                {
                    return;
                }

                if (!result.FromPack)
                {
                    __result = result.Text!;
                    if (!ProbeInProgress)
                    {
                        Fallbacks++;
                    }

                    return;
                }

                var display = SubtitleFeature.LineBreaks
                    ? LineBreakHelpers.Apply(HelperFor(result.Code), result.Text)
                    : LineBreakHelpers.Shape(HelperFor(result.Code), result.Text);
                __result = display;
                if (ProbeInProgress)
                {
                    return;
                }

                Overrides++;

                // The game re-reads a string whenever it rebinds a label; one trace line per row, not per read.
                var repeat = string.Equals(id, LastOverrideId, StringComparison.Ordinal);
                LastOverrideId = id;
                LastOverrideText = result.Text;
                _lastDisplayText = display;

                if (!repeat)
                {
                    ConsoleEvents.File(
                        SubtitleFeature.TraceSys,
                        "textOverride",
                        ("localizationId", id),
                        ("lang", result.Code),
                        ("text", result.Text));
                }
            }
            catch (Exception ex)
            {
                // Never let a translation break the game's own text: log and leave the original.
                CwyLsModLoader.Log.LogWarning(
                    $"{SubtitleFeature.LogPrefix} text seam failed for '{data?.ID}' [{value}]: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// Harmony postfix on <c>ScenarioTextMessage.StartText(string)</c>: records what the box shows
        /// and when; a pack translation that reached the box gets its one console line here.
        /// </summary>
        /// <param name="text">The text the box was given.</param>
        private static void StartTextPostfix(string text)
        {
            LastStartText = text;
            LastStartTextAt = Now;

            try
            {
                var display = _lastDisplayText;
                if (display is null || !string.Equals(display, text, StringComparison.Ordinal))
                {
                    return;
                }

                _lastDisplayText = null;
                var stored = LastOverrideText ?? LineBreakHelpers.Strip(text);
                ConsoleEvents.Emit(
                    SubtitleFeature.TraceSys,
                    SubtitleFeature.TraceName,
                    ConsoleTags.Subtitle,
                    "\"" + stored + "\"",
                    "lang,via,id:localizationId,min:minSeconds",
                    (ConsolePayload.MirrorKey, SubtitleFeature.MirrorToConsole),
                    ("text", stored),
                    ("lang", LanguageFeature.CurrentCode),
                    ("via", "textbox"),
                    ("localizationId", LastOverrideId),
                    ("minSeconds", Math.Round(ReadingTime.MinimumFor(stored), 1)),
                    ("shown", true));
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogWarning(SubtitleFeature.LogPrefix + " could not log the displayed line: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static string HelperFor(string? code) =>
            LanguageFeature.Registry.TryGet(code, out var language) && language != null ? language.LineBreakHelper : string.Empty;
    }
}
