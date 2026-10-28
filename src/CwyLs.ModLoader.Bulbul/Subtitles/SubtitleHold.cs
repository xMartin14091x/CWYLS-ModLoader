using System;
using CwyLs.ModLoader.Bulbul.Language;
using CwyLs.ModLoader.Core.Language;
using CwyLs.ModLoader.Core.Logging;
using CwyLs.ModLoader.Core.Satone.Say;
using CwyLs.ModLoader.Core.Subtitles;
using HarmonyLib;
using Game = global::Bulbul;
using Master = global::Bulbul.MasterData;

namespace CwyLs.ModLoader.Bulbul.Subtitles
{
    /// <summary>
    /// Every line the framework causes to be shown stays in the game's text box for at least
    /// <see cref="ReadingTime.MinimumFor"/> - <c>max(2.5 s, characters / 15 per second)</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Where the time goes.</b> A voiced line leaves the box when <c>ScenarioReader</c> moves on:
    /// <c>CommandVoiceText</c> waits for the typewriter and the clip, then calls
    /// <c>AutoNext()</c>, which waits <c>waitSeconds</c> and
    /// reads the next row - for a <c>Satone.Say</c> group that row is
    /// <c>EndScenario</c>, which closes the box. So a Harmony prefix on
    /// <c>AutoNext(float waitSeconds)</c> raises <c>waitSeconds</c> to whatever the line still needs;
    /// the prefix runs in the async method's stub, before the argument is copied into its state
    /// machine. This is the game's own pause - the same delay a <c>Text</c> row's <c>Arg2</c> uses
    /// - so nothing new is drawn and a click or skip still cancels it.
    /// </para>
    /// <para>
    /// <b>Which lines.</b> "Lines we cause to be shown": every <c>Satone.Say</c> line (its group id
    /// starts <c>cwy_say_</c>), and every story line while a pack language is active (its text is
    /// ours, translated or fallen back). A game line in one of the game's own languages keeps the
    /// game's timing.
    /// </para>
    /// <para>
    /// <b>Voiced lines (language voice sets).</b> While a pack language is active and the line has its own clip
    /// - the clip the voice manager was handed within 2 s after the text (<see cref="VoicePlayback"/>,
    /// <see cref="ReadingTime.ClipBelongsToLine"/>) - the box also stays up until that clip has
    /// played plus 0.3 s: <c>max(2.5 s, characters / 15, clip + 0.3 s)</c>. The game's own
    /// wait for "text finished and <c>IsFinishedVoice</c>" (in <c>ScenarioReader</c>'s text
    /// commands) already outlasts the clip, so this adds at most the tail. In the game's own
    /// languages nothing changes.
    /// </para>
    /// </remarks>
    internal static class SubtitleHold
    {
        /// <summary>A box line older than this is not the line <c>AutoNext</c> is finishing.</summary>
        internal const double StaleSeconds = 120d;

        private static AccessTools.FieldRef<Game.ScenarioReader, Master.NovelData>? _currentNovel;

        /// <summary>True once the prefix is in place.</summary>
        internal static bool Applied { get; private set; }

        /// <summary>What was patched, or why not. Never null.</summary>
        internal static string Detail { get; private set; } = "not attempted";

        /// <summary>How many lines had their time in the box extended.</summary>
        internal static long Holds { get; private set; }

        /// <summary>The last decision, for the smoke report. Never null.</summary>
        internal static string LastDecision { get; private set; } = "no line has finished yet";

        /// <summary>Applies the <c>AutoNext</c> prefix. Idempotent.</summary>
        /// <param name="harmony">The subtitle feature's Harmony instance.</param>
        /// <returns>True when the prefix is in place.</returns>
        internal static bool Apply(Harmony harmony)
        {
            if (Applied)
            {
                return true;
            }

            try
            {
                var autoNext = AccessTools.Method(typeof(Game.ScenarioReader), SubtitlePatchTargets.AutoNextMethodName, new[] { typeof(float) });
                _currentNovel = AccessTools.FieldRefAccess<Game.ScenarioReader, Master.NovelData>(SubtitlePatchTargets.CurrentNovelFieldName);
                if (autoNext is null)
                {
                    Detail = "PATCH TARGET MISSING: " + SubtitlePatchTargets.ScenarioReaderTypeName + "." + SubtitlePatchTargets.AutoNextMethodName + "(float)";
                    CwyLsModLoader.Log.LogError(SubtitleFeature.LogPrefix + " " + Detail);
                    return false;
                }

                harmony.Patch(autoNext, prefix: new HarmonyMethod(typeof(SubtitleHold), nameof(AutoNextPrefix)));
                Applied = true;
                Detail = "prefixed ScenarioReader.AutoNext(float)";
                return true;
            }
            catch (Exception ex)
            {
                Detail = "could not patch ScenarioReader.AutoNext: " + ex.GetType().Name + ": " + ex.Message;
                CwyLsModLoader.Log.LogError(SubtitleFeature.LogPrefix + " " + Detail);
                return false;
            }
        }

        /// <summary>
        /// Harmony prefix on <c>ScenarioReader.AutoNext(float waitSeconds)</c>: raises the wait to
        /// what the line in the box still needs. Parameter names match the original.
        /// </summary>
        /// <param name="__instance">The scenario reader.</param>
        /// <param name="waitSeconds">The game's wait, raised in place.</param>
        private static void AutoNextPrefix(Game.ScenarioReader __instance, ref float waitSeconds)
        {
            if (!SubtitleFeature.Enabled || !SubtitleFeature.HoldForReading || _currentNovel is null || __instance is null)
            {
                return;
            }

            try
            {
                var novel = _currentNovel(__instance);
                if (novel is null || !ShowsText(novel.Command))
                {
                    return;
                }

                var isSay = SayIdGenerator.IsSayGroup(novel.ScenarioGroupID);
                var packLanguage = LanguageFeature.Active;
                if (!isSay && packLanguage is null)
                {
                    return;
                }

                var text = SubtitleNative.LastStartText;
                var shownAt = SubtitleNative.LastStartTextAt;
                var now = SubtitleNative.Now;
                if (string.IsNullOrEmpty(text) || now - shownAt > StaleSeconds)
                {
                    return;
                }

                var visible = LineBreakHelpers.Strip(text);

                // Language voice sets: while a pack language is active, the line's own voice clip (the one the
                // voice manager was handed a frame after the text) holds the box until it has played
                // + 0.3 s, measured from its start. The game already waited for the clip before
                // AutoNext, so in practice this adds the tail; it is the guarantee that a translated
                // take is never cut by our own hold (and it outlasts the game's 30 s VoiceText
                // timeout in ScenarioReader). In the game's own languages a Say line keeps the
                // reading-time rule unchanged.
                var clipSeconds = 0d;
                var clipStartedAt = double.NegativeInfinity;
                var clipName = string.Empty;
                if (packLanguage != null
                    && VoicePlayback.TryLast(out var playedName, out var playedLength, out var playedAgo, out _))
                {
                    var startedAt = now - playedAgo;
                    if (ReadingTime.ClipBelongsToLine(shownAt, startedAt))
                    {
                        clipSeconds = playedLength;
                        clipStartedAt = startedAt;
                        clipName = playedName;
                    }
                }

                var remaining = ReadingTime.RemainingFor(visible, shownAt, now, clipStartedAt, clipSeconds);
                var minimum = ReadingTime.MinimumFor(visible, clipSeconds);

                if (remaining <= waitSeconds)
                {
                    LastDecision = Describe(novel, visible, minimum, now - shownAt, waitSeconds, 0d, clipName, clipSeconds);
                    return;
                }

                var added = remaining - waitSeconds;
                LastDecision = Describe(novel, visible, minimum, now - shownAt, waitSeconds, added, clipName, clipSeconds);
                waitSeconds = (float)remaining;
                Holds++;

                ConsoleEvents.File(
                    SubtitleFeature.TraceSys,
                    "hold",
                    ("localizationId", novel.LocalizationID),
                    ("group", novel.ScenarioGroupID),
                    ("reason", isSay ? "say" : "packLanguage:" + packLanguage!.Code),
                    ("characters", ReadingTime.CountVisible(visible)),
                    ("minSeconds", Math.Round(minimum, 2)),
                    ("shownFor", Math.Round(now - shownAt, 2)),
                    ("clip", clipName.Length == 0 ? null : clipName),
                    ("clipSeconds", Math.Round(clipSeconds, 2)),
                    ("addedSeconds", Math.Round(added, 2)));
            }
            catch (Exception ex)
            {
                // Never let the hold break the game's own scenario flow: log and leave its wait.
                CwyLsModLoader.Log.LogWarning(SubtitleFeature.LogPrefix + " reading-time hold failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static bool ShowsText(Master.CommandType command) =>
            command == Master.CommandType.Text
            || command == Master.CommandType.VoiceText
            || command == Master.CommandType.PomodoroVoiceText;

        private static string Describe(
            Master.NovelData novel, string text, double minimum, double shownFor, float wait, double added,
            string clipName, double clipSeconds) =>
            "'" + novel.LocalizationID + "' (" + ReadingTime.CountVisible(text) + " chars"
            + (clipName.Length == 0
                ? string.Empty
                : ", clip " + clipName + " " + clipSeconds.ToString("0.0#", System.Globalization.CultureInfo.InvariantCulture) + " s")
            + ") needs "
            + minimum.ToString("0.0#", System.Globalization.CultureInfo.InvariantCulture) + " s, was up "
            + shownFor.ToString("0.0#", System.Globalization.CultureInfo.InvariantCulture) + " s, game wait "
            + wait.ToString("0.0#", System.Globalization.CultureInfo.InvariantCulture) + " s, added "
            + added.ToString("0.0#", System.Globalization.CultureInfo.InvariantCulture) + " s";
    }
}
