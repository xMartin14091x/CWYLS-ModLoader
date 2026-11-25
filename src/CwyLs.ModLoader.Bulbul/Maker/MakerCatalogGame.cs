using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using CwyLs.ModLoader.Bulbul.MasterData;
using CwyLs.ModLoader.Core.Api.Maker.Catalog;
using CwyLs.ModLoader.Core.Logging;
using UnityEngine;
using Master = global::Bulbul.MasterData;

namespace CwyLs.ModLoader.Bulbul.Maker
{
    /// <summary>
    /// The main-thread half of the Pack Maker catalog: one read of the game's lines, the scenario
    /// group behind each, the lengths of the game's own voice clips and the voice names registered
    /// at runtime, into a Unity-free <see cref="MakerGameData"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Main thread only (<see cref="MakerCatalog"/> hops here with the dispatcher). Everything is
    /// read, nothing is changed: the loader's collections are the ones the master-data feature
    /// injected (pack rows included), and the voice cache is read without the patched lookup, so
    /// no override answers. <c>AudioClip.length</c> is the clip's header (samples over frequency);
    /// reading it does not load the clip's audio data.
    /// </para>
    /// <para>
    /// Rows <c>Satone.Say</c> adds at runtime (<c>cwy_say_*</c>) are not game lines and are left out.
    /// </para>
    /// </remarks>
    internal static class MakerCatalogGame
    {
        /// <summary>Reads the game's lines and clips. Main thread only.</summary>
        /// <returns>The snapshot; one with a <see cref="MakerGameData.Problem"/> when the game's data is not loaded yet.</returns>
        internal static MakerGameData Capture()
        {
            var loader = MasterDataFeature.ResolveLoader();
            var localization = loader?.LocalizationList;
            if (loader is null || localization is null || loader.NovelMasterList is null)
            {
                return new MakerGameData(null, null, null, "The game's lines are not loaded yet; try again in a moment.");
            }

            var novelRows = NovelRows(loader.NovelMasterList);
            var storyIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var novel in novelRows)
                if (novel.LineId != null) storyIds.Add(novel.LineId);
            var groups = GroupsByLine(loader.NovelMasterList);
            var rows = new List<MakerGameRow>(localization.Count);
            var gateOnly = new List<MakerGameRow>();
            foreach (var pair in localization)
            {
                var data = pair.Value;
                var id = data?.ID;
                if (data is null || string.IsNullOrWhiteSpace(id) || MakerLineRules.IsRuntimeRow(id))
                {
                    continue;
                }

                if (!storyIds.Contains(id!))
                {
                    // Not listed, but the DLC gate decides over it (lines the game picks at playback, titles), so Play and
                    // export checks keyed by its id are refused when it is paid content.
                    gateOnly.Add(new MakerGameRow(id!, data.Voice, null, null, null));
                    continue;
                }

                rows.Add(new MakerGameRow(id!, data.Voice, groups.TryGetValue(id!, out var group) ? group : null, data.Ja, data.En));
            }

            var snapshot = new MakerGameData(rows, ClipLengths(), RegisteredVoices(), novelRows,
                ScenarioGroups(loader.ScenarioGroupMasterList, title => localization.TryGetValue(title, out var localized) ? localized : null),
                null, Gate, gateOnly);
            ReportGate(snapshot);
            return snapshot;
        }

        /// <summary>
        /// The DLC gate's verdict over the given game tables (normally the game's untouched rows, before any pack merges), without
        /// the Pack Maker extras (no titles, texts or clip lengths), for the in-game paths (<see cref="PaidDlcContent"/>).
        /// </summary>
        /// <param name="localization">The localisation rows.</param>
        /// <param name="novel">The novel rows.</param>
        /// <param name="groups">The scenario groups.</param>
        internal static MakerDlcHidden DecideFrom(IEnumerable<Master.LocalizationData?> localization, IReadOnlyList<Master.NovelData>? novel,
            IReadOnlyList<Master.ScenarioGroupData>? groups)
        {
            var groupsByLine = GroupsByLine(novel);
            var rows = new List<MakerGameRow>();
            foreach (var data in localization)
            {
                var id = data?.ID;
                if (data is null || string.IsNullOrWhiteSpace(id) || MakerLineRules.IsRuntimeRow(id))
                {
                    continue;
                }

                rows.Add(new MakerGameRow(id!, data.Voice, groupsByLine.TryGetValue(id!, out var group) ? group : null, null, null));
            }

            return Gate.Decide(rows, NovelRows(novel), ScenarioGroups(groups, _ => null));
        }

        /// <summary>
        /// Pack Maker's DLC gate: the loader's shipped markers and no ownership read, so every paid DLC line is hidden from
        /// everyone (owners included) until an ownership read is approved and added. It reads the running game's version: on a
        /// build newer than the markers were made for, lines no classified group speaks are hidden too. No Steam or network call.
        /// </summary>
        internal static MakerDlcGate Gate { get; } = MakerDlcGate.ShippedFor(() => Application.version);

        private static string? _lastGateSummary;

        /// <summary>
        /// The game's own paid-DLC flag field (<see cref="MakerDlcMarkers.DlcFlagMember"/>, game 2.0.0), read by reflection because the
        /// loader also runs on 1.18.1, which has no such field. Null when absent.
        /// </summary>
        private static readonly Lazy<FieldInfo?> DlcFlagField = new Lazy<FieldInfo?>(() =>
        {
            try
            {
                var field = typeof(Master.ScenarioGroupData).GetField(MakerDlcMarkers.DlcFlagMember, BindingFlags.Public | BindingFlags.Instance);
                return field != null && field.FieldType == typeof(bool) ? field : null;
            }
            catch (Exception)
            {
                return null;
            }
        });

        /// <summary>True when the running game has the DLC flag field.</summary>
        internal static bool DlcFlagReadable => DlcFlagField.Value != null;

        /// <summary>
        /// The game's DLC flag for a scenario group: false for a group a content pack added (never paid content), the field's value
        /// when the game has it, null when it has none or the read failed (the gate then decides by version, fail closed).
        /// </summary>
        /// <param name="group">The game's scenario group row.</param>
        internal static bool? DlcFlagOf(Master.ScenarioGroupData? group)
        {
            if (group is null)
            {
                return null;
            }

            if (MasterDataFeature.AddedGroups.Contains(group.ID))
            {
                return false;
            }

            var field = DlcFlagField.Value;
            if (field is null)
            {
                return null;
            }

            try
            {
                return field.GetValue(group) is bool flag ? flag : (bool?)null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>One trace event per capture; one INFO line when what is hidden changes. Never line text.</summary>
        private static void ReportGate(MakerGameData data)
        {
            var hidden = data.Hidden;
            ConsoleEvents.File(MakerCatalog.TraceSys, "makerDlcGate", ("markers", Gate.Markers.Count), ("known", hidden.IsKnown),
                ("lines", hidden.LineCount), ("groups", hidden.GroupCount), ("voices", hidden.VoiceCount),
                ("unresolvedGroups", hidden.UnresolvedGroups), ("summary", hidden.Summary));
            var summary = hidden.IsKnown && hidden.LineCount == 0 && hidden.GroupCount == 0 ? null : hidden.Summary;
            if (string.Equals(summary, _lastGateSummary, StringComparison.Ordinal))
            {
                return;
            }

            _lastGateSummary = summary;
            CwyLsModLoader.Log.LogInfo(MakerCatalog.LogPrefix + " DLC gate: " + (summary ?? "no DLC lines hidden")
                + (summary != null ? "; no ownership read in this build, so paid DLC lines stay hidden in Pack Maker" : string.Empty));
        }

        /// <summary>The novel rows that speak a line, with the raw motion ids Studio context shows (reference metadata only).</summary>
        private static List<MakerGameNovelRow> NovelRows(IReadOnlyList<Master.NovelData>? novel)
        {
            var rows = new List<MakerGameNovelRow>();
            if (novel is null)
            {
                return rows;
            }

            foreach (var row in novel)
            {
                var line = row?.LocalizationID;

                // "0" is the game's own "no line" value for a command row.
                if (row is null || string.IsNullOrEmpty(line) || line == "0")
                {
                    continue;
                }

                rows.Add(new MakerGameNovelRow(row.ID, row.ScenarioGroupID, line, row.Talker.ToString(), Motion(row.BodyMotion), Motion(row.FacialMotion)));
            }

            return rows;
        }

        /// <summary>A motion id as text; the game's "unset" value is negative.</summary>
        private static string? Motion(long id) => id < 0 ? null : id.ToString(System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>The scenario groups in the game's order, each with a title read from the game's own localisation.</summary>
        private static List<MakerGameGroup> ScenarioGroups(IReadOnlyList<Master.ScenarioGroupData>? list, Func<string, Master.LocalizationData?> localize)
        {
            var groups = new List<MakerGameGroup>();
            if (list is null)
            {
                return groups;
            }

            foreach (var group in list)
            {
                if (group is null || string.IsNullOrWhiteSpace(group.ID))
                {
                    continue;
                }

                string? title = null;
                var titleId = group.TitleLocalizationID;
                if (!string.IsNullOrEmpty(titleId) && titleId != "0" && localize(titleId!) is Master.LocalizationData text)
                {
                    title = string.IsNullOrWhiteSpace(text.En) ? text.Ja : text.En;
                }

                groups.Add(new MakerGameGroup(group.ID, title, (int)group.Scenario, DlcFlagOf(group), group.TitleLocalizationID, group.SubtitleLocalizationID));
            }

            return groups;
        }

        /// <summary>Localisation id to the scenario group of the first novel row that names it.</summary>
        private static Dictionary<string, string> GroupsByLine(IReadOnlyList<Master.NovelData>? novel)
        {
            var groups = new Dictionary<string, string>(StringComparer.Ordinal);
            if (novel is null)
            {
                return groups;
            }

            foreach (var row in novel)
            {
                var id = row?.LocalizationID;
                var group = row?.ScenarioGroupID;

                // "0" is the game's own "no line" value for a command row.
                if (string.IsNullOrEmpty(id) || id == "0" || string.IsNullOrEmpty(group) || groups.ContainsKey(id!))
                {
                    continue;
                }

                groups.Add(id!, group!);
            }

            return groups;
        }

        /// <summary>The game's own clips and their lengths, or null when the voice manager's cache is not readable.</summary>
        private static Dictionary<string, double>? ClipLengths()
        {
            try
            {
                var cache = VoiceSeam.TryGetGameClipCache(VoiceSeam.TryGetInstance(out _));
                if (cache is null)
                {
                    return null;
                }

                var lengths = new Dictionary<string, double>(cache.Count, StringComparer.Ordinal);
                foreach (DictionaryEntry entry in cache)
                {
                    // Unity's == also catches a clip whose asset was unloaded.
                    if (entry.Key is string name && entry.Value is AudioClip clip && clip != null)
                    {
                        lengths[name] = clip.length;
                    }
                }

                return lengths;
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogDebug(MakerCatalog.LogPrefix + " the game's voice clips could not be read: " + ex.GetType().Name + ": " + ex.Message);
                return null;
            }
        }

        /// <summary>Names the voice feature has a clip for (packs' global clips, mods' runtime clips).</summary>
        private static IReadOnlyList<string>? RegisteredVoices()
        {
            try
            {
                return VoiceFeature.Names;
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogDebug(MakerCatalog.LogPrefix + " the registered voices could not be read: " + ex.GetType().Name + ": " + ex.Message);
                return null;
            }
        }
    }
}
