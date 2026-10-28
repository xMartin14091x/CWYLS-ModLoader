using System;
using System.Collections.Generic;
using CwyLs.ModLoader.Bulbul.Language;
using CwyLs.ModLoader.Bulbul.MasterData;
using CwyLs.ModLoader.Core.Language;
using CwyLs.ModLoader.Core.Smoke;
using CwyLs.ModLoader.Core.Subtitles;

namespace CwyLs.ModLoader.Bulbul.Subtitles
{
    /// <summary>
    /// The subtitle checks. None of them draws anything (the old on-screen probe is retired):
    /// they read back the seams, the text tables and what the game's own box is showing.
    /// </summary>
    internal static class SubtitleSmokeChecks
    {
        /// <summary>Phase label these checks are reported under.</summary>
        internal const string Phase = "P2";

        private const string NativeId = "p2.subtitles.native";
        private const string TablesId = "p2.subtitles.tables";
        private const string HoldId = "p2.subtitles.hold";

        /// <summary>Registers every subtitle check. Idempotent - the registry replaces by id.</summary>
        internal static void RegisterAll()
        {
            SmokeRegistry.Register(new SmokeCheck(NativeId, Phase, Native));
            SmokeRegistry.Register(new SmokeCheck(TablesId, Phase, Tables));
            SmokeRegistry.Register(new SmokeCheck(HoldId, Phase, Hold));
        }

        /// <summary>
        /// The text seam is patched; while a pack language is active, the last translation it served
        /// is read back off a story label.
        /// </summary>
        private static SmokeCheckResult Native()
        {
            if (!SubtitleNative.Applied)
            {
                return SmokeCheckResult.Fail(NativeId, SubtitleNative.Detail);
            }

            var active = LanguageFeature.Active;
            if (active is null)
            {
                return SmokeCheckResult.Pass(
                    NativeId,
                    "seams in place; the game is in '" + LanguageFeature.CurrentCode + "', one of its own languages, "
                    + "so nothing is translated (" + SubtitleNative.Overrides + " pack row(s) served this session)");
            }

            if (SubtitleNative.Overrides == 0)
            {
                return SmokeCheckResult.NotYetObserved(
                    NativeId,
                    active.Code + " is active but no translated row has been read yet ("
                    + SubtitleNative.Fallbacks + " row(s) fell back to " + LanguageCodes.ToCode(active.BaseValue) + ")");
            }

            var wanted = SubtitleNative.LastOverrideText ?? string.Empty;
            var idle = new List<string>();
            var showing = new List<string>();

            foreach (var label in LanguageFonts.StoryLabels())
            {
                var shown = LineBreakHelpers.Strip(label.text);
                if (string.Equals(shown, wanted, StringComparison.Ordinal))
                {
                    return SmokeCheckResult.Pass(
                        NativeId,
                        $"'{label.name}' shows the pack's {active.Code} text for '{SubtitleNative.LastOverrideId}': "
                        + $"\"{Clip(wanted)}\" ({SubtitleNative.Overrides} served, {SubtitleNative.Fallbacks} fell back)");
                }

                if (SubtitleNative.IsIdle(label))
                {
                    idle.Add($"'{label.name}' idle");
                }
                else
                {
                    showing.Add($"'{label.name}': \"{Clip(shown)}\"");
                }
            }

            var detail =
                $"{SubtitleNative.Overrides} pack row(s) served (last '{SubtitleNative.LastOverrideId}' = \"{Clip(wanted)}\"); "
                + (showing.Count > 0
                    ? "a story box is showing another line: " + string.Join(", ", showing)
                    : idle.Count > 0
                        ? "no story box is showing a line right now (" + string.Join(", ", idle) + ") - run this while she is talking"
                        : "no story label is loaded");

            // A box showing a line from the game's own columns (a row the pack does not translate)
            // is the fallback working, not a failure; only an idle or empty result is "not yet".
            return SmokeCheckResult.NotYetObserved(NativeId, detail);
        }

        /// <summary>The text tables are registered and every pack language has rows.</summary>
        private static SmokeCheckResult Tables()
        {
            var sources = SubtitleSources.Names;
            if (sources.Count == 0)
            {
                return SmokeCheckResult.Fail(TablesId, "no text table is registered");
            }

            var counts = new List<string>();
            var empty = new List<string>();
            foreach (var language in LanguageFeature.Registry.Languages)
            {
                var rows = MasterDataFeature.Subtitles.CountFor(language.Code);
                counts.Add(language.Code + "=" + rows);
                if (rows == 0)
                {
                    empty.Add(language.Code);
                }
            }

            var detail = "sources: " + string.Join(", ", sources)
                + "; rows: " + (counts.Count == 0 ? "no pack language" : string.Join(", ", counts))
                + "; tables: " + MasterDataFeature.Subtitles;

            return empty.Count == 0
                ? SmokeCheckResult.Pass(TablesId, detail)
                : SmokeCheckResult.Fail(TablesId, "no data/subtitles/<code>.json rows for " + string.Join(", ", empty) + "; " + detail);
        }

        /// <summary>The reading-time hold is in place; the detail is its last decision.</summary>
        private static SmokeCheckResult Hold()
        {
            if (!SubtitleHold.Applied)
            {
                return SmokeCheckResult.Fail(HoldId, SubtitleHold.Detail);
            }

            if (!SubtitleFeature.Enabled || !SubtitleFeature.HoldForReading)
            {
                return SmokeCheckResult.Pass(HoldId, "patched but switched off ("
                    + SubtitleFeature.ConfigSection + "." + SubtitleFeature.ConfigKeyHoldForReading + "=false or Enabled=false)");
            }

            return SmokeCheckResult.Pass(
                HoldId,
                SubtitleHold.Holds + " line(s) held so far; last: " + SubtitleHold.LastDecision);
        }

        /// <summary>Shortens a line so a report entry stays readable.</summary>
        private static string Clip(string text) =>
            text.Length <= 40 ? text : text.Substring(0, 40) + "…";
    }
}
