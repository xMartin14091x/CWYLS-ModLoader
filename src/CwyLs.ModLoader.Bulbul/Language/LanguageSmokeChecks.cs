using System;
using System.Collections.Generic;
using CwyLs.ModLoader.Bulbul.Events;
using CwyLs.ModLoader.Bulbul.MasterData;
using CwyLs.ModLoader.Core.Language;
using CwyLs.ModLoader.Core.Smoke;
using CwyLs.ModLoader.Core.Subtitles;
using HarmonyLib;
using TMPro;
using Game = global::Bulbul;

namespace CwyLs.ModLoader.Bulbul.Language
{
    /// <summary>
    /// Pack-language checks. None of them draws, plays or switches anything: each reads state
    /// the feature already built, or calls a pure game method (<c>LocalizationMasterWrapper.Get</c>,
    /// <c>SettingData.SaveReady</c>, which only copies values between fields) and reads the result.
    /// </summary>
    internal static class LanguageSmokeChecks
    {
        /// <summary>Phase label these checks are reported under.</summary>
        internal const string Phase = "PB";

        private const string RegistryId = "pb.language.registry";
        private const string PatchesId = "pb.language.patches";
        private const string EntryId = "pb.language.entry";
        private const string SaveGuardId = "pb.language.save_guard";
        private const string TextId = "pb.language.text";
        private const string FontsId = "pb.language.fonts";

        /// <summary>Registers every check. Idempotent - the registry replaces by id.</summary>
        internal static void RegisterAll()
        {
            SmokeRegistry.Register(new SmokeCheck(RegistryId, Phase, RegistryCheck));
            SmokeRegistry.Register(new SmokeCheck(PatchesId, Phase, Patches));
            SmokeRegistry.Register(new SmokeCheck(EntryId, Phase, Entry));
            SmokeRegistry.Register(new SmokeCheck(SaveGuardId, Phase, SaveGuard));
            SmokeRegistry.Register(new SmokeCheck(TextId, Phase, Text));
            SmokeRegistry.Register(new SmokeCheck(FontsId, Phase, Fonts));
        }

        /// <summary>Every pack's declarations were read; nothing was refused.</summary>
        private static SmokeCheckResult RegistryCheck()
        {
            var registry = LanguageFeature.Registry;
            var languages = new List<string>();
            foreach (var language in registry.Languages)
            {
                languages.Add(language + " base=" + LanguageCodes.ToCode(language.BaseValue)
                    + " chain=" + string.Join(">", Codes(language.Chain)));
            }

            if (registry.Rejected.Count > 0)
            {
                return SmokeCheckResult.Fail(RegistryId, "refused: " + string.Join(" | ", registry.Rejected)
                    + (languages.Count > 0 ? "; registered: " + string.Join(", ", languages) : string.Empty));
            }

            return registry.Count == 0
                ? SmokeCheckResult.NotYetObserved(RegistryId, "no loaded pack ships a language.json")
                : SmokeCheckResult.Pass(RegistryId, string.Join(", ", languages) + "; " + LanguageFeature.Choice);
        }

        /// <summary>The mandatory seams are patched, so a pack language may be applied.</summary>
        private static SmokeCheckResult Patches()
        {
            if (!LanguageFeature.Enabled)
            {
                return SmokeCheckResult.Pass(PatchesId, LanguageFeature.ConfigSection + "." + LanguageFeature.ConfigKeyEnabled + "=false: nothing patched");
            }

            var detail = LanguagePatches.Detail + "; web rewrites " + LanguagePatches.WebRewrites
                + "; save rewrites " + LanguagePatches.SaveRewrites;

            return LanguageFeature.CanApply
                ? SmokeCheckResult.Pass(PatchesId, detail)
                : SmokeCheckResult.Fail(PatchesId, "pack languages are inactive: " + detail);
        }

        /// <summary>
        /// Each pack language has a wired entry in the game's selector, with its caption, and the
        /// list scrolls inside the game's open height so every entry can be brought whole into view.
        /// </summary>
        private static SmokeCheckResult Entry()
        {
            if (LanguageFeature.Registry.Count == 0)
            {
                return SmokeCheckResult.NotYetObserved(EntryId, "no pack language to add");
            }

            var entries = LanguageSelector.Current;
            if (entries.Count == 0)
            {
                return SmokeCheckResult.NotYetObserved(EntryId, LanguageSelector.Detail);
            }

            var problems = new List<string>();
            var shown = new List<string>();
            foreach (var entry in entries)
            {
                if (entry.Object == null)
                {
                    problems.Add(entry.Language.Code + ": destroyed");
                    continue;
                }

                if (!entry.Wired)
                {
                    problems.Add(entry.Language.Code + ": no click handler");
                }

                if (!string.Equals(entry.Caption, entry.Language.Caption, StringComparison.Ordinal))
                {
                    problems.Add(entry.Language.Code + ": caption '" + entry.Caption + "'");
                }

                shown.Add(entry + " '" + entry.Caption + "'");
            }

            // Every entry must be reachable inside the game's open height.
            var list = LanguageSelectorList.Problem();
            if (list.Length > 0)
            {
                problems.Add(list);
            }

            return problems.Count == 0
                ? SmokeCheckResult.Pass(EntryId, string.Join(", ", shown) + "; " + LanguageSelector.Detail)
                : SmokeCheckResult.Fail(EntryId, string.Join("; ", problems));
        }

        /// <summary>
        /// While a pack language is in memory, <c>SaveReady</c> leaves its base language in the field
        /// the save file is written from. Calls <c>SaveReady</c> only - no file is written.
        /// </summary>
        private static SmokeCheckResult SaveGuard()
        {
            if (!Game.SaveDataManager.HasInstance)
            {
                return SmokeCheckResult.NotYetObserved(SaveGuardId, "the save manager does not exist yet");
            }

            var data = Game.SaveDataManager.Instance.SettingData;
            var inMemory = (int)data.GameLanguage.Value;

            var field = AccessTools.Field(typeof(Game.SettingData), "_gameLanguageForSave");
            if (field is null)
            {
                return SmokeCheckResult.Fail(SaveGuardId, "SettingData._gameLanguageForSave is missing");
            }

            data.SaveReady();
            var saved = field.GetValue(data) is int value ? value : -1;

            if (GameLanguages.IsPackRange(saved))
            {
                return SmokeCheckResult.Fail(
                    SaveGuardId, "SaveReady left " + saved + " in _gameLanguageForSave; a vanilla launch would break");
            }

            var detail = "in memory " + LanguageFeature.Registry.ToName(inMemory) + " (" + inMemory + "), save field "
                + LanguageCodes.ToName(saved) + " (" + saved + ")";

            return LanguageFeature.Registry.IsPackLanguage(inMemory)
                ? SmokeCheckResult.Pass(SaveGuardId, detail)
                : SmokeCheckResult.Pass(SaveGuardId, detail + "; no pack language active, so the guard had nothing to do");
        }

        /// <summary>
        /// The game's own text seam serves each pack language: a row the pack translates comes back
        /// in that language, and a row it does not comes back in the base language, not Japanese.
        /// </summary>
        private static SmokeCheckResult Text()
        {
            if (LanguageFeature.Registry.Count == 0)
            {
                return SmokeCheckResult.NotYetObserved(TextId, "no pack language to read");
            }

            if (!GameScopes.TryProject<Game.LocalizationMasterWrapper>(out var wrapper) || wrapper is null)
            {
                return SmokeCheckResult.NotYetObserved(TextId, "LocalizationMasterWrapper is not resolvable yet");
            }

            var loader = MasterDataFeature.ResolveLoader();
            if (loader?.LocalizationList is null)
            {
                return SmokeCheckResult.NotYetObserved(TextId, "the game's master data is not loaded yet");
            }

            var problems = new List<string>();
            var notes = new List<string>();

            // Reads through the seam without overwriting what the game's own box last received.
            Subtitles.SubtitleNative.ProbeInProgress = true;
            try
            {
                ReadEach(wrapper, loader.LocalizationList, problems, notes);
            }
            finally
            {
                Subtitles.SubtitleNative.ProbeInProgress = false;
            }

            return problems.Count == 0
                ? SmokeCheckResult.Pass(TextId, string.Join("; ", notes))
                : SmokeCheckResult.Fail(TextId, string.Join("; ", problems));
        }

        private static void ReadEach(
            Game.LocalizationMasterWrapper wrapper,
            IReadOnlyDictionary<string, global::Bulbul.MasterData.LocalizationData> rows,
            List<string> problems,
            List<string> notes)
        {
            foreach (var language in LanguageFeature.Registry.Languages)
            {
                string? translatedId = null;
                foreach (var id in MasterDataFeature.Subtitles.IdsFor(language.Code))
                {
                    if (rows.ContainsKey(id))
                    {
                        translatedId = id;
                        break;
                    }
                }

                if (translatedId is null)
                {
                    notes.Add(language.Code + ": no table row matches a game row");
                }
                else
                {
                    var data = rows[translatedId];
                    var shown = LineBreakHelpers.Strip(wrapper.Get(data, (Game.GameLanguageType)language.Value));
                    MasterDataFeature.Subtitles.TryGet(translatedId, language.Code, out var expected);
                    if (string.Equals(shown, expected, StringComparison.Ordinal))
                    {
                        notes.Add(language.Code + "['" + translatedId + "'] = \"" + Clip(shown) + "\"");
                    }
                    else
                    {
                        problems.Add(language.Code + "['" + translatedId + "'] came back as \"" + Clip(shown) + "\", expected \"" + Clip(expected) + "\"");
                    }
                }

                var fallback = FirstUntranslated(rows, language.Code);
                if (fallback != null)
                {
                    var shown = wrapper.Get(fallback, (Game.GameLanguageType)language.Value);
                    var baseText = wrapper.Get(fallback, (Game.GameLanguageType)language.BaseValue);
                    if (string.Equals(shown, baseText, StringComparison.Ordinal))
                    {
                        notes.Add(language.Code + " fallback '" + fallback.ID + "' = " + LanguageCodes.ToCode(language.BaseValue) + " \"" + Clip(shown) + "\"");
                    }
                    else
                    {
                        problems.Add(language.Code + " fallback '" + fallback.ID + "' came back as \"" + Clip(shown) + "\", expected the base language's \"" + Clip(baseText) + "\"");
                    }
                }
            }
        }

        /// <summary>
        /// Each language's named font assets are loaded, reachable from the story box's font, and
        /// hold the mark records; the story label resolves the table's first moving mark to an asset
        /// that has them.
        /// </summary>
        private static SmokeCheckResult Fonts()
        {
            if (LanguageFeature.Registry.Count == 0)
            {
                return SmokeCheckResult.NotYetObserved(FontsId, "no pack language");
            }

            var reports = LanguageFonts.Report;
            if (reports.Count == 0)
            {
                return SmokeCheckResult.NotYetObserved(FontsId, "the room has not loaded yet, so the fonts were not resolved");
            }

            var problems = new List<string>();
            var notes = new List<string>();

            foreach (var language in LanguageFeature.Registry.Languages)
            {
                if (!reports.TryGetValue(language.Code, out var report))
                {
                    problems.Add(language.Code + ": not resolved");
                    continue;
                }

                if (report.Missing.Count > 0)
                {
                    problems.Add(language.Code + ": font asset(s) not loaded: " + string.Join(", ", report.Missing));
                }

                notes.Add(language.Code + ": " + report);

                if (!LanguageFeature.MarkTables.TryGetValue(language.Code, out var table) || table.Pairs.Count == 0
                    || LanguageFeature.MarkPlacement == MarkPlacementMode.Off)
                {
                    continue;
                }

                var mark = table.Pairs[0].Mark;
                foreach (var label in LanguageFonts.StoryLabels())
                {
                    var drawnBy = DrawnBy(label, mark);
                    if (drawnBy == null)
                    {
                        problems.Add(language.Code + ": story '" + label.name + "' cannot draw U+" + mark.ToString("X4"));
                        continue;
                    }

                    var records = LanguageFonts.RecordsIn(drawnBy);
                    if (records == 0)
                    {
                        problems.Add(language.Code + ": story '" + label.name + "' draws U+" + mark.ToString("X4")
                            + " with '" + drawnBy.name + "', which has none of the mark records");
                    }
                    else
                    {
                        notes.Add("story '" + label.name + "' draws U+" + mark.ToString("X4") + " with '" + drawnBy.name + "' (" + records + " record(s))");
                    }

                    break;
                }
            }

            return problems.Count == 0
                ? SmokeCheckResult.Pass(FontsId, string.Join("; ", notes))
                : SmokeCheckResult.Fail(FontsId, string.Join("; ", problems) + " | " + string.Join("; ", notes));
        }

        /// <summary>The asset TMP's own fallback search picks for one character on a label's font.</summary>
        private static TMP_FontAsset? DrawnBy(TextMeshProUGUI label, int codePoint)
        {
            if (label.font == null)
            {
                return null;
            }

            var character = TMP_FontAssetUtilities.GetCharacterFromFontAsset(
                (uint)codePoint, label.font, true, FontStyles.Normal, FontWeight.Regular, out _);
            return character?.textAsset as TMP_FontAsset;
        }

        private static global::Bulbul.MasterData.LocalizationData? FirstUntranslated(
            IReadOnlyDictionary<string, global::Bulbul.MasterData.LocalizationData> rows, string code)
        {
            foreach (var pair in rows)
            {
                if (pair.Value != null && !string.IsNullOrEmpty(pair.Value.En)
                    && !MasterDataFeature.Subtitles.TryGet(pair.Key, code, out _))
                {
                    return pair.Value;
                }
            }

            return null;
        }

        private static IEnumerable<string> Codes(IReadOnlyList<LanguageStep> chain)
        {
            foreach (var step in chain)
            {
                yield return step.Code;
            }
        }

        private static string Clip(string? text) =>
            text is null ? string.Empty : text.Length <= 40 ? text : text.Substring(0, 40) + "…";
    }
}
