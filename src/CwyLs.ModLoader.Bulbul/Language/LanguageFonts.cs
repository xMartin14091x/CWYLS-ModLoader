using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using CwyLs.ModLoader.Bulbul.Events;
using CwyLs.ModLoader.Core.Language;
using CwyLs.ModLoader.Core.Logging;
using CwyLs.ModLoader.Core.Subtitles;
using HarmonyLib;
using TMPro;
using UnityEngine;
using Game = global::Bulbul;

namespace CwyLs.ModLoader.Bulbul.Language
{
    /// <summary>
    /// The font side of a pack language - the font assets it names, reachable from the fonts
    /// the game draws with, and its mark-positioning table installed into those assets.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Which asset draws the language.</b> A pack names font assets the game already ships
    /// (<c>"font": { "gameAssets": ["Base_TH", "Dynamic_TH"] }</c>). The game's UI and story labels
    /// draw with <c>FontSupplier.GetFontAsset(base language)</c> - <c>Base_JP</c> for English - whose
    /// fallback table the game already fills with every language's assets; for Thai, TMP's
    /// depth-first fallback search reaches the static <c>Base_TH</c> before any dynamic asset, so
    /// <c>Base_TH</c> is what draws Thai in the story box (checked by simulating
    /// <c>TMP_FontAssetUtilities.GetCharacterFromFontAsset_Internal</c> over the exported assets).
    /// When a named asset is <em>not</em> reachable from a label's font, it is appended to that
    /// font's fallback table - appended, never inserted, and taken back out on quit.
    /// </para>
    /// <para>
    /// <b>The mark table</b> becomes pair-adjustment records (<see cref="MarkPlacement"/>) inserted at
    /// index 0 of each named asset's <c>glyphPairAdjustmentRecords</c> <em>and</em> written into its
    /// lookup dictionary: the renderer (<c>TextMeshProUGUI</c>) reads the dictionary, and a later
    /// <c>TMP_FontAsset.ReadFontAssetDefinition</c> rebuilds that dictionary from the list keeping the
    /// first record per key, so ours survive the rebuild. Records are
    /// tracked and removed on quit; a key the asset already had is restored.
    /// </para>
    /// <para>
    /// All in memory; no game file is touched. Applied at <c>RoomReady</c> (the assets are loaded
    /// with the first scene) and idempotent, so a second room load adds nothing.
    /// </para>
    /// </remarks>
    internal static class LanguageFonts
    {
        private const int MaxFallbackDepth = 8;

        private static readonly object Gate = new object();
        private static readonly List<AddedFallback> Fallbacks = new List<AddedFallback>();
        private static readonly List<InstalledRecord> Records = new List<InstalledRecord>();
        private static readonly Dictionary<string, LanguageFontReport> Reports =
            new Dictionary<string, LanguageFontReport>(StringComparer.Ordinal);

        private static FieldInfo? _lookupField;

        /// <summary>The last report per language code, for the smoke check.</summary>
        internal static IReadOnlyDictionary<string, LanguageFontReport> Report
        {
            get
            {
                lock (Gate)
                {
                    return new Dictionary<string, LanguageFontReport>(Reports, StringComparer.Ordinal);
                }
            }
        }

        /// <summary>How many of our pair-adjustment records are installed right now.</summary>
        internal static int RecordCount
        {
            get
            {
                lock (Gate)
                {
                    return Records.Count;
                }
            }
        }

        /// <summary>
        /// Resolves every language's named assets, makes them reachable and installs its mark table.
        /// Safe to call repeatedly: anything already done is skipped.
        /// </summary>
        /// <param name="trigger">What asked, for the log.</param>
        internal static void Apply(string trigger)
        {
            if (!LanguageFeature.Enabled)
            {
                return;
            }

            var loaded = LoadedAssets();
            foreach (var language in LanguageFeature.Registry.Languages)
            {
                try
                {
                    ApplyOne(language, loaded, trigger);
                }
                catch (Exception ex)
                {
                    CwyLsModLoader.Log.LogError(
                        LanguageFeature.LogPrefix + " font work for " + language.Code + " failed: " + ex.GetType().Name + ": " + ex.Message);
                }
            }

            // After the packs, so a pack's own table for the same assets wins (an installed pair is never replaced).
            try
            {
                ApplyBuiltIn(loaded, trigger);
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogError(LanguageFeature.LogPrefix + " built-in Thai marks failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>What the built-in Thai table did at the last room load, for the console and smoke checks.</summary>
        internal static string BuiltInReport
        {
            get
            {
                lock (Gate)
                {
                    return _builtInReport;
                }
            }
        }

        private static string _builtInReport = "not applied yet";

        /// <summary>
        /// Installs the loader's own Thai mark table (<see cref="BuiltInMarkTables"/>) into the game's Thai font
        /// assets, with or without a language pack, so Thai tone marks sit above their vowels anywhere the
        /// game's Thai font draws: pack lines, subtitles, mod windows.
        /// </summary>
        private static void ApplyBuiltIn(TMP_FontAsset[] loaded, string trigger)
        {
            var table = BuiltInMarkTables.Thai;
            if (table is null)
            {
                SetBuiltInReport("unavailable: " + BuiltInMarkTables.LoadError);
                CwyLsModLoader.Log.LogWarning(LanguageFeature.LogPrefix + " built-in Thai marks unavailable: " + BuiltInMarkTables.LoadError);
                return;
            }

            var notes = new List<string>();
            var installedNow = 0;
            foreach (var name in BuiltInMarkTables.ThaiFontAssets)
            {
                TMP_FontAsset? asset = null;
                foreach (var candidate in loaded)
                {
                    if (candidate != null && string.Equals(candidate.name, name, StringComparison.Ordinal))
                    {
                        asset = candidate;
                        break;
                    }
                }

                if (asset == null)
                {
                    notes.Add(name + ": not loaded");
                    continue;
                }

                installedNow += Install(asset, table, notes);
            }

            var report = string.Join("; ", notes);
            SetBuiltInReport(report);
            ConsoleEvents.Emit(
                LanguageFeature.TraceSys,
                "fonts",
                ConsoleTags.Language,
                "built-in Thai marks: " + report,
                "added",
                ("added", installedNow),
                ("trigger", trigger));
        }

        private static void SetBuiltInReport(string report)
        {
            lock (Gate)
            {
                _builtInReport = report;
            }
        }

        /// <summary>Takes out every fallback entry and pair record this class added. Called on quit.</summary>
        internal static void Remove()
        {
            lock (Gate)
            {
                foreach (var added in Fallbacks)
                {
                    try
                    {
                        added.Target?.fallbackFontAssetTable?.Remove(added.Asset);
                    }
                    catch (Exception ex)
                    {
                        CwyLsModLoader.Log.LogWarning(LanguageFeature.LogPrefix + " could not remove a fallback entry: " + ex.GetType().Name + ": " + ex.Message);
                    }
                }

                foreach (var installed in Records)
                {
                    try
                    {
                        installed.Uninstall();
                    }
                    catch (Exception ex)
                    {
                        CwyLsModLoader.Log.LogWarning(LanguageFeature.LogPrefix + " could not remove a mark record: " + ex.GetType().Name + ": " + ex.Message);
                    }
                }

                Fallbacks.Clear();
                Records.Clear();
                Reports.Clear();
                _builtInReport = "removed";
            }
        }

        /// <summary>The loaded font asset with this name, or null.</summary>
        /// <param name="name">An asset name, e.g. <c>Base_TH</c>.</param>
        internal static TMP_FontAsset? Find(string name)
        {
            foreach (var asset in LoadedAssets())
            {
                if (asset != null && string.Equals(asset.name, name, StringComparison.Ordinal))
                {
                    return asset;
                }
            }

            return null;
        }

        /// <summary>True when <paramref name="wanted"/> is <paramref name="from"/> or somewhere in its fallback tree.</summary>
        /// <param name="from">The font a label draws with.</param>
        /// <param name="wanted">The asset that must be reachable.</param>
        internal static bool Reaches(TMP_FontAsset? from, TMP_FontAsset? wanted)
        {
            if (from == null || wanted == null)
            {
                return false;
            }

            var seen = new HashSet<int>();
            return Reaches(from, wanted, seen, 0);
        }

        /// <summary>How many of our records one asset holds.</summary>
        /// <param name="asset">A font asset.</param>
        internal static int RecordsIn(TMP_FontAsset? asset)
        {
            if (asset == null)
            {
                return 0;
            }

            lock (Gate)
            {
                var count = 0;
                foreach (var installed in Records)
                {
                    if (ReferenceEquals(installed.Asset, asset))
                    {
                        count++;
                    }
                }

                return count;
            }
        }

        private static void ApplyOne(PackLanguage language, TMP_FontAsset[] loaded, string trigger)
        {
            var declaration = language.Declaration;
            var resolved = new List<TMP_FontAsset>();
            var missing = new List<string>();

            foreach (var name in declaration.FontAssets)
            {
                TMP_FontAsset? found = null;
                foreach (var asset in loaded)
                {
                    if (asset != null && string.Equals(asset.name, name, StringComparison.Ordinal))
                    {
                        found = asset;
                        break;
                    }
                }

                if (found != null)
                {
                    resolved.Add(found);
                }
                else
                {
                    missing.Add(name);
                }
            }

            var fallbackNotes = resolved.Count > 0 ? EnsureReachable(language, resolved[0]) : "no font asset named";

            var markNotes = new List<string>();
            var installedNow = 0;
            if (LanguageFeature.MarkTables.TryGetValue(language.Code, out var table))
            {
                foreach (var asset in resolved)
                {
                    installedNow += Install(asset, table, markNotes);
                }
            }

            var report = new LanguageFontReport(
                resolved.ConvertAll(a => a.name),
                missing,
                fallbackNotes,
                table is null ? "no mark table" : string.Join("; ", markNotes));

            lock (Gate)
            {
                Reports[language.Code] = report;
            }

            if (missing.Count > 0)
            {
                CwyLsModLoader.Log.LogWarning(
                    LanguageFeature.LogPrefix + " " + language + ": font asset(s) not loaded: " + string.Join(", ", missing));
            }

            ConsoleEvents.Emit(
                LanguageFeature.TraceSys,
                "fonts",
                ConsoleTags.Language,
                language.Code + " fonts: " + (resolved.Count == 0 ? "none resolved" : string.Join(", ", report.Resolved)),
                "marks,fallback",
                ("marks", report.Marks),
                ("fallback", fallbackNotes),
                ("added", installedNow),
                ("missing", missing.ToArray()),
                ("trigger", trigger));
        }

        /// <summary>
        /// Makes <paramref name="asset"/> reachable from the base language's supplier font and from
        /// every story label's font, appending it where it is not.
        /// </summary>
        private static string EnsureReachable(PackLanguage language, TMP_FontAsset asset)
        {
            var targets = new List<(TMP_FontAsset Font, string What)>();

            if (GameScopes.TryProject<global::FontSupplier>(out var supplier) && supplier != null)
            {
                try
                {
                    var baseFont = supplier.GetFontAsset((Game.GameLanguageType)language.BaseValue);
                    if (baseFont != null)
                    {
                        targets.Add((baseFont, "FontSupplier(" + LanguageCodes.ToName(language.BaseValue) + ")"));
                    }
                }
                catch (Exception ex)
                {
                    CwyLsModLoader.Log.LogWarning(LanguageFeature.LogPrefix + " FontSupplier.GetFontAsset failed: " + ex.GetType().Name + ": " + ex.Message);
                }
            }

            foreach (var label in StoryLabels())
            {
                if (label.font != null)
                {
                    targets.Add((label.font, "story '" + label.name + "'"));
                }
            }

            if (targets.Count == 0)
            {
                return "no label font to check yet";
            }

            var reached = 0;
            var appended = new List<string>();
            foreach (var (font, what) in targets)
            {
                if (Reaches(font, asset))
                {
                    reached++;
                    continue;
                }

                var table = font.fallbackFontAssetTable;
                if (table == null)
                {
                    table = new List<TMP_FontAsset>();
                    font.fallbackFontAssetTable = table;
                }

                table.Add(asset);
                lock (Gate)
                {
                    Fallbacks.Add(new AddedFallback(font, asset));
                }

                appended.Add(what + " -> " + font.name);
            }

            return reached + " of " + targets.Count + " label font(s) already reach " + asset.name
                + (appended.Count > 0 ? "; appended to " + string.Join(", ", appended) : string.Empty);
        }

        /// <summary>Installs a table into one asset. Returns how many records were added now.</summary>
        private static int Install(TMP_FontAsset asset, MarkTable table, List<string> notes)
        {
            if (!FontFacts.TryRead(asset, out var family, out var pointSize, out var factsProblem))
            {
                notes.Add(asset.name + ": " + factsProblem);
                return 0;
            }

            var characters = asset.characterLookupTable;
            var plan = MarkPlacement.Plan(
                table,
                family,
                pointSize,
                code => characters != null && characters.TryGetValue((uint)code, out var character) && character != null
                    ? character.glyphIndex
                    : (uint?)null,
                LanguageFeature.MarkPlacement);

            if (plan.Records.Count == 0)
            {
                notes.Add(asset.name + ": " + plan.Detail);
                return 0;
            }

            var feature = asset.fontFeatureTable;
            if (feature == null)
            {
                notes.Add(asset.name + ": no font feature table");
                return 0;
            }

            if (feature.glyphPairAdjustmentRecords == null)
            {
                feature.glyphPairAdjustmentRecords = new List<TMP_GlyphPairAdjustmentRecord>();
            }

            var lookup = Lookup(feature);
            if (lookup == null)
            {
                asset.ReadFontAssetDefinition();
                lookup = Lookup(feature);
            }

            if (lookup == null)
            {
                notes.Add(asset.name + ": the pair lookup dictionary is not reachable");
                return 0;
            }

            var added = 0;
            var zero = new TMP_GlyphValueRecord(0f, 0f, 0f, 0f);
            lock (Gate)
            {
                foreach (var planned in plan.Records)
                {
                    if (lookup.TryGetValue(planned.Key, out var existing) && IsOurs(existing))
                    {
                        continue;
                    }

                    var record = new TMP_GlyphPairAdjustmentRecord(
                        new TMP_GlyphAdjustmentRecord(planned.FirstGlyph, zero),
                        new TMP_GlyphAdjustmentRecord(
                            planned.SecondGlyph,
                            new TMP_GlyphValueRecord(planned.XPlacement, planned.YPlacement, 0f, 0f)));

                    feature.glyphPairAdjustmentRecords.Insert(0, record);
                    lookup[planned.Key] = record;
                    Records.Add(new InstalledRecord(asset, feature, lookup, planned.Key, record, existing));
                    added++;
                }
            }

            notes.Add(asset.name + ": " + plan.Detail + " (" + RecordsIn(asset) + " installed)");
            return added;
        }

        private static bool IsOurs(TMP_GlyphPairAdjustmentRecord? record)
        {
            if (record is null)
            {
                return false;
            }

            foreach (var installed in Records)
            {
                if (ReferenceEquals(installed.Record, record))
                {
                    return true;
                }
            }

            return false;
        }

        private static Dictionary<uint, TMP_GlyphPairAdjustmentRecord>? Lookup(TMP_FontFeatureTable feature)
        {
            try
            {
                _lookupField ??= AccessTools.Field(typeof(TMP_FontFeatureTable), "m_GlyphPairAdjustmentRecordLookupDictionary");
                return _lookupField?.GetValue(feature) as Dictionary<uint, TMP_GlyphPairAdjustmentRecord>;
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogWarning(LanguageFeature.LogPrefix + " the pair lookup dictionary is not readable: " + ex.GetType().Name + ": " + ex.Message);
                return null;
            }
        }

        private static bool Reaches(TMP_FontAsset from, TMP_FontAsset wanted, HashSet<int> seen, int depth)
        {
            if (ReferenceEquals(from, wanted))
            {
                return true;
            }

            if (depth >= MaxFallbackDepth || !seen.Add(from.GetInstanceID()) || from.fallbackFontAssetTable == null)
            {
                return false;
            }

            foreach (var next in from.fallbackFontAssetTable)
            {
                if (next != null && Reaches(next, wanted, seen, depth + 1))
                {
                    return true;
                }
            }

            return false;
        }

        private static TMP_FontAsset[] LoadedAssets()
        {
            try
            {
                // FindObjectsOfTypeAll reaches assets no live GameObject points at - exactly what a
                // font asset sitting in another font's fallback table is.
                return Resources.FindObjectsOfTypeAll<TMP_FontAsset>();
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogWarning(LanguageFeature.LogPrefix + " could not list the loaded font assets: " + ex.GetType().Name + ": " + ex.Message);
                return new TMP_FontAsset[0];
            }
        }

        /// <summary>The labels of the game's story text boxes (<c>ScenarioTextMessage</c>, held by <c>StorySystemUI</c>).</summary>
        internal static IEnumerable<TextMeshProUGUI> StoryLabels()
        {
            Game.ScenarioTextMessage[] messages;
            try
            {
                messages = Resources.FindObjectsOfTypeAll<Game.ScenarioTextMessage>();
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogWarning(LanguageFeature.LogPrefix + " could not look for the story text boxes: " + ex.GetType().Name + ": " + ex.Message);
                yield break;
            }

            foreach (var message in messages)
            {
                var label = message == null ? null : message.GetComponent<TextMeshProUGUI>();
                if (label != null)
                {
                    yield return label;
                }
            }
        }

        /// <summary>One fallback entry we appended.</summary>
        private sealed class AddedFallback
        {
            internal AddedFallback(TMP_FontAsset target, TMP_FontAsset asset)
            {
                Target = target;
                Asset = asset;
            }

            internal TMP_FontAsset Target { get; }

            internal TMP_FontAsset Asset { get; }
        }

        /// <summary>One pair record we installed, with what it replaced.</summary>
        private sealed class InstalledRecord
        {
            internal InstalledRecord(
                TMP_FontAsset asset,
                TMP_FontFeatureTable feature,
                Dictionary<uint, TMP_GlyphPairAdjustmentRecord> lookup,
                uint key,
                TMP_GlyphPairAdjustmentRecord record,
                TMP_GlyphPairAdjustmentRecord? previous)
            {
                Asset = asset;
                Feature = feature;
                LookupTable = lookup;
                Key = key;
                Record = record;
                Previous = previous;
            }

            internal TMP_FontAsset Asset { get; }

            internal TMP_FontFeatureTable Feature { get; }

            internal Dictionary<uint, TMP_GlyphPairAdjustmentRecord> LookupTable { get; }

            internal uint Key { get; }

            internal TMP_GlyphPairAdjustmentRecord Record { get; }

            internal TMP_GlyphPairAdjustmentRecord? Previous { get; }

            internal void Uninstall()
            {
                Feature.glyphPairAdjustmentRecords?.Remove(Record);
                if (LookupTable.TryGetValue(Key, out var current) && ReferenceEquals(current, Record))
                {
                    if (Previous != null)
                    {
                        LookupTable[Key] = Previous;
                    }
                    else
                    {
                        LookupTable.Remove(Key);
                    }
                }
            }
        }
    }

    /// <summary>What <see cref="LanguageFonts"/> did for one language, for the smoke report.</summary>
    internal sealed class LanguageFontReport
    {
        internal LanguageFontReport(List<string> resolved, List<string> missing, string fallback, string marks)
        {
            Resolved = resolved;
            Missing = missing;
            Fallback = fallback;
            Marks = marks;
        }

        /// <summary>The named assets that were found.</summary>
        internal IReadOnlyList<string> Resolved { get; }

        /// <summary>The named assets that were not loaded.</summary>
        internal IReadOnlyList<string> Missing { get; }

        /// <summary>What the reachability check did.</summary>
        internal string Fallback { get; }

        /// <summary>What the mark installation did, per asset.</summary>
        internal string Marks { get; }

        /// <summary>One line.</summary>
        public override string ToString() =>
            "fonts " + (Resolved.Count == 0 ? "none" : string.Join(", ", Resolved))
            + (Missing.Count > 0 ? " (missing " + string.Join(", ", Missing) + ")" : string.Empty)
            + "; " + Fallback + "; marks: " + Marks;
    }

    /// <summary>
    /// Reads a font asset's family name and point size. <c>TMP_FontAsset.faceInfo</c> returns
    /// <c>UnityEngine.TextCore.FaceInfo</c>, whose module this adapter does not reference, so both
    /// are read by reflection; a failure is reported, never guessed.
    /// </summary>
    internal static class FontFacts
    {
        private static PropertyInfo? _faceInfo;

        /// <summary>Reads the facts.</summary>
        /// <param name="asset">The font asset.</param>
        /// <param name="family">Its family name.</param>
        /// <param name="pointSize">Its sampling point size.</param>
        /// <param name="problem">Why they could not be read.</param>
        /// <returns>True when both were read.</returns>
        internal static bool TryRead(TMP_FontAsset asset, out string family, out float pointSize, out string problem)
        {
            family = string.Empty;
            pointSize = 0f;
            try
            {
                _faceInfo ??= typeof(TMP_FontAsset).GetProperty("faceInfo", BindingFlags.Public | BindingFlags.Instance);
                var face = _faceInfo?.GetValue(asset);
                if (face is null)
                {
                    problem = "faceInfo is not readable";
                    return false;
                }

                var type = face.GetType();
                family = type.GetProperty("familyName")?.GetValue(face) as string ?? string.Empty;
                var size = type.GetProperty("pointSize")?.GetValue(face);
                pointSize = size is null ? 0f : Convert.ToSingle(size, CultureInfo.InvariantCulture);
                problem = string.Empty;
                return true;
            }
            catch (Exception ex)
            {
                problem = "faceInfo could not be read: " + ex.GetType().Name + ": " + ex.Message;
                CwyLsModLoader.Log.LogWarning(LanguageFeature.LogPrefix + " " + asset.name + " " + problem);
                return false;
            }
        }
    }
}
