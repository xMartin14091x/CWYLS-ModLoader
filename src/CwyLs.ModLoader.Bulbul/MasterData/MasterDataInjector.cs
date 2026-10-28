using System;
using System.Collections.Generic;
using System.Reflection;
using CwyLs.ModLoader.Core.MasterData;
using HarmonyLib;
using Game = global::Bulbul;
using Master = global::Bulbul.MasterData;

namespace CwyLs.ModLoader.Bulbul.MasterData
{
    /// <summary>
    /// Writes the merged pack rows into the game's <c>MasterDataLoader</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The three fields are public (<c>NovelMasterList</c>, <c>LocalizationList</c>,
    /// <c>ScenarioGroupMasterList</c>), so they are assigned directly rather than through
    /// <c>AccessTools.Field</c>; reflection would add nothing but a failure mode.
    /// </para>
    /// <para>
    /// Every injection <em>rebuilds</em> the collections from a baseline captured the first time a
    /// finished load is seen, and the rows themselves are new
    /// <c>LocalizationData</c>/<c>NovelData</c>/<c>ScenarioGroupData</c> instances. That is what
    /// makes re-injection idempotent, and it is also why the game's own ScriptableObject lists are
    /// never touched: <c>NovelMasterList</c> is the novel master asset's own <c>Items</c> list,
    /// and appending to it would survive a
    /// <c>Dispose</c>/<c>Load</c> cycle and double our rows.
    /// </para>
    /// <para>
    /// The element types stay the game's own, so <c>LocalizationMasterWrapper.Get</c>,
    /// <c>ScenarioReader.StartReady</c> and everything else read our rows without knowing they are
    /// ours.
    /// </para>
    /// </remarks>
    internal static class MasterDataInjector
    {
        /// <summary>
        /// <c>NovelData.LookEaseType</c> is a <c>DG.Tweening.Ease</c>, and this assembly does not
        /// reference DOTween, so that one field is set through reflection. Everything else on the
        /// row is a string, a float, an int or an enum declared in Assembly-CSharp.
        /// </summary>
        private const string LookEaseField = "LookEaseType";

        private static readonly Master.LocalizationData[] NoLocalization = new Master.LocalizationData[0];
        private static readonly Master.NovelData[] NoNovel = new Master.NovelData[0];
        private static readonly Master.ScenarioGroupData[] NoGroups = new Master.ScenarioGroupData[0];

        private static FieldInfo? _lookEase;
        private static bool _lookEaseReported;

        private static Master.LocalizationData[] _baseLocalization = NoLocalization;
        private static Master.NovelData[] _baseNovel = NoNovel;
        private static Master.ScenarioGroupData[] _baseGroups = NoGroups;

        // The collection objects the last Inject put on the loader. They are the marker that says
        // "these are ours, not the game's", which is what keeps a re-arm from snapshotting our own
        // rows as if they were the game's.
        private static object? _installedLocalization;
        private static object? _installedNovel;
        private static object? _installedGroups;

        /// <summary>True once the game's own rows have been snapshotted.</summary>
        internal static bool HasBaseline { get; private set; }

        /// <summary>How many localization rows the game itself has.</summary>
        internal static int BaseLocalizationCount => _baseLocalization.Length;

        /// <summary>How many novel rows the game itself has.</summary>
        internal static int BaseNovelCount => _baseNovel.Length;

        /// <summary>How many scenario groups the game itself has.</summary>
        internal static int BaseScenarioGroupCount => _baseGroups.Length;

        /// <summary>
        /// Drops the snapshot, so the next <see cref="Capture"/> takes a fresh one. Called when the
        /// game starts a new load or releases the one we injected into.
        /// </summary>
        /// <remarks>
        /// The installed-collection markers are deliberately kept: they are how the next
        /// <see cref="Capture"/> tells the game's fresh collections from the ones we put there.
        /// </remarks>
        internal static void Forget()
        {
            HasBaseline = false;
            _baseLocalization = NoLocalization;
            _baseNovel = NoNovel;
            _baseGroups = NoGroups;
        }

        /// <summary>
        /// Snapshots the game's own rows, once per load. Must be called while the loader still
        /// holds the game's collections - that is, before the first injection.
        /// </summary>
        /// <param name="loader">The game's master-data loader, with a finished load.</param>
        /// <returns>True when a baseline is available.</returns>
        internal static bool Capture(Game.MasterDataLoader loader)
        {
            if (HasBaseline)
            {
                return true;
            }

            if (loader is null)
            {
                return false;
            }

            var localization = loader.LocalizationList;
            var novel = loader.NovelMasterList;
            var groups = loader.ScenarioGroupMasterList;

            if (localization is null || novel is null || groups is null)
            {
                return false;
            }

            // A re-arm (a new load starting) happens before the loader's fields are re-assigned, so
            // for a few frames they still hold what we installed. Snapshotting that would fold our
            // own rows into the baseline and then lose them when the real load overwrites the
            // fields, so wait for the game's own collections to arrive.
            if (ReferenceEquals(localization, _installedLocalization)
                || ReferenceEquals(novel, _installedNovel)
                || ReferenceEquals(groups, _installedGroups))
            {
                CwyLsModLoader.Log.LogDebug(
                    "MASTERDATA the loader still holds the collections we installed; waiting for the "
                    + "game's own before taking a baseline");
                return false;
            }

            var rows = new List<Master.LocalizationData>(localization.Count);
            foreach (var pair in localization)
            {
                if (pair.Value != null)
                {
                    rows.Add(pair.Value);
                }
            }

            _baseLocalization = rows.ToArray();
            _baseNovel = Copy(novel, NoNovel);
            _baseGroups = Copy(groups, NoGroups);
            HasBaseline = true;

            CwyLsModLoader.Log.LogDebug(
                "MASTERDATA baseline captured: " + _baseLocalization.Length + " localization, "
                + _baseNovel.Length + " novel, " + _baseGroups.Length + " scenario group row(s)");
            return true;
        }

        /// <summary>
        /// The ids the game already has, so the merge can tell an addition from an override and
        /// refuse an ambiguous novel id.
        /// </summary>
        internal static MasterDataBaseline Baseline()
        {
            if (!HasBaseline)
            {
                return MasterDataBaseline.Empty;
            }

            return MasterDataBaseline.Build(
                IdsOf(_baseLocalization, row => row?.ID),
                IdsOf(_baseNovel, row => row?.ID),
                IdsOf(_baseGroups, row => row?.ID));
        }

        /// <summary>
        /// Rebuilds the three collections from the baseline plus <paramref name="set"/> and assigns
        /// them to the loader. Safe to repeat: the result depends only on the baseline and the set.
        /// </summary>
        /// <param name="loader">The game's master-data loader.</param>
        /// <param name="set">The merged pack rows.</param>
        /// <param name="logEachRow">True to log one line per injected row.</param>
        /// <returns>What the loader holds afterwards.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="loader"/> or <paramref name="set"/> is null.</exception>
        internal static InjectionCounts Inject(Game.MasterDataLoader loader, MasterDataSet set, bool logEachRow)
        {
            if (loader is null)
            {
                throw new ArgumentNullException(nameof(loader));
            }

            if (set is null)
            {
                throw new ArgumentNullException(nameof(set));
            }

            var counts = new InjectionCounts();

            var localization = new Dictionary<string, Master.LocalizationData>(
                _baseLocalization.Length + set.Localization.Count, StringComparer.Ordinal);

            foreach (var row in _baseLocalization)
            {
                if (row?.ID != null)
                {
                    localization[row.ID] = row;
                }
            }

            foreach (var row in set.Localization)
            {
                var replaced = localization.ContainsKey(row.Id);
                localization[row.Id] = Build(row);
                counts.Count(MasterDataMerge.LocalizationKind, row.Id, replaced, logEachRow);
            }

            var novel = new List<Master.NovelData>(_baseNovel.Length + set.Novel.Count);
            novel.AddRange(_baseNovel);
            var novelPositions = UniquePositions(novel, row => row?.ID);

            foreach (var row in set.Novel)
            {
                var built = Build(row);
                if (novelPositions.TryGetValue(row.Id, out var position))
                {
                    novel[position] = built;
                    counts.Count(MasterDataMerge.NovelKind, row.Id, true, logEachRow);
                }
                else
                {
                    novelPositions[row.Id] = novel.Count;
                    novel.Add(built);
                    counts.Count(MasterDataMerge.NovelKind, row.Id, false, logEachRow);
                }
            }

            var groups = new List<Master.ScenarioGroupData>(_baseGroups.Length + set.ScenarioGroups.Count);
            groups.AddRange(_baseGroups);
            var groupPositions = UniquePositions(groups, row => row?.ID);

            foreach (var row in set.ScenarioGroups)
            {
                var built = Build(row);
                if (groupPositions.TryGetValue(row.Id, out var position))
                {
                    groups[position] = built;
                    counts.Count(MasterDataMerge.ScenarioGroupsKind, row.Id, true, logEachRow);
                }
                else
                {
                    groupPositions[row.Id] = groups.Count;
                    groups.Add(built);
                    counts.Count(MasterDataMerge.ScenarioGroupsKind, row.Id, false, logEachRow);
                }
            }

            loader.LocalizationList = localization;
            loader.NovelMasterList = novel;
            loader.ScenarioGroupMasterList = groups;

            _installedLocalization = localization;
            _installedNovel = novel;
            _installedGroups = groups;

            counts.LocalizationTotal = localization.Count;
            counts.NovelTotal = novel.Count;
            counts.ScenarioGroupTotal = groups.Count;
            return counts;
        }

        private static Master.LocalizationData Build(LocalizationRow row) =>
            new Master.LocalizationData
            {
                ID = row.Id,
                Ja = row.Ja,
                En = row.En,
                ZhHans = row.ZhHans,
                ZhHant = row.ZhHant,
                Pt = row.Pt,
                Ko = row.Ko,
                Ru = row.Ru,
                Voice = row.Voice
            };

        private static Master.NovelData Build(NovelRow row)
        {
            var data = new Master.NovelData
            {
                ID = row.Id,
                ScenarioGroupID = row.ScenarioGroupId,
                Command = (Master.CommandType)row.Command,
                Arg1 = row.Arg1,
                Arg2 = row.Arg2,
                Arg3 = row.Arg3,
                Arg4 = row.Arg4,
                Talker = (Master.TalkerType)row.Talker,
                LocalizationID = row.LocalizationId,
                BodyMotion = row.BodyMotion,
                FacialMotion = row.FacialMotion,
                LookScale = row.LookScale,
                LookSpeedSeconds = row.LookSpeedSeconds
            };

            SetLookEase(data, row.LookEaseType);
            return data;
        }

        private static Master.ScenarioGroupData Build(ScenarioGroupRow row) =>
            new Master.ScenarioGroupData
            {
                ID = row.Id,
                TitleLocalizationID = row.TitleLocalizationId,
                SubtitleLocalizationID = row.SubtitleLocalizationId,
                Scenario = (Master.ScenarioType)row.Scenario,
                EpisodeNumber = row.EpisodeNumber,
                UnlockLevel = row.UnlockLevel,
                UnlockStoryEpisode = row.UnlockStoryEpisode,
                PlayTiming = row.PlayTiming,
                ContentType = row.ContentType,
                Arg1 = row.Arg1,
                Arg2 = row.Arg2,
                Arg3 = row.Arg3
            };

        /// <summary>
        /// Sets <c>NovelData.LookEaseType</c>. Reflection because its type lives in DOTween, which
        /// this assembly does not reference; a failure leaves the field at its default
        /// (<c>Ease.Unset</c>, which is what every one of the game's own rows stores anyway) and is
        /// reported once.
        /// </summary>
        private static void SetLookEase(Master.NovelData data, int ease)
        {
            try
            {
                _lookEase ??= AccessTools.Field(typeof(Master.NovelData), LookEaseField);
                if (_lookEase is null || !_lookEase.FieldType.IsEnum)
                {
                    ReportLookEase("NovelData." + LookEaseField + " is missing or is not an enum");
                    return;
                }

                _lookEase.SetValue(data, Enum.ToObject(_lookEase.FieldType, ease));
            }
            catch (Exception ex)
            {
                ReportLookEase(ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void ReportLookEase(string problem)
        {
            if (_lookEaseReported)
            {
                return;
            }

            _lookEaseReported = true;
            CwyLsModLoader.Log.LogWarning(
                "MASTERDATA could not set " + LookEaseField + " on an injected novel row, so it keeps "
                + "the default easing: " + problem);
        }

        /// <summary>
        /// Positions of the ids that appear exactly once, so an override can never silently pick
        /// one of several rows sharing an id (26 of the game's novel ids do).
        /// </summary>
        private static Dictionary<string, int> UniquePositions<T>(List<T> rows, Func<T, string?> idOf)
            where T : class
        {
            var positions = new Dictionary<string, int>(rows.Count, StringComparer.Ordinal);
            var duplicates = new HashSet<string>(StringComparer.Ordinal);

            for (var i = 0; i < rows.Count; i++)
            {
                var id = idOf(rows[i]);
                if (string.IsNullOrEmpty(id))
                {
                    continue;
                }

                if (positions.ContainsKey(id!))
                {
                    duplicates.Add(id!);
                    continue;
                }

                positions.Add(id!, i);
            }

            foreach (var duplicate in duplicates)
            {
                positions.Remove(duplicate);
            }

            return positions;
        }

        private static T[] Copy<T>(IReadOnlyList<T> source, T[] empty)
        {
            if (source.Count == 0)
            {
                return empty;
            }

            var copy = new T[source.Count];
            for (var i = 0; i < source.Count; i++)
            {
                copy[i] = source[i];
            }

            return copy;
        }

        private static List<string?> IdsOf<T>(T[] rows, Func<T, string?> idOf)
        {
            var ids = new List<string?>(rows.Length);
            foreach (var row in rows)
            {
                ids.Add(idOf(row));
            }

            return ids;
        }

        /// <summary>What one injection did, counted as it happened rather than predicted.</summary>
        internal sealed class InjectionCounts
        {
            /// <summary>Rows added, per kind.</summary>
            internal Dictionary<string, int> Added { get; } = new Dictionary<string, int>(StringComparer.Ordinal);

            /// <summary>Rows that replaced an existing row, per kind.</summary>
            internal Dictionary<string, int> Overridden { get; } =
                new Dictionary<string, int>(StringComparer.Ordinal);

            /// <summary>Size of the loader's localization dictionary afterwards.</summary>
            internal int LocalizationTotal { get; set; }

            /// <summary>Size of the loader's novel list afterwards.</summary>
            internal int NovelTotal { get; set; }

            /// <summary>Size of the loader's scenario-group list afterwards.</summary>
            internal int ScenarioGroupTotal { get; set; }

            /// <summary>Records one row and, when asked, logs it.</summary>
            /// <param name="kind">Which file the row came from.</param>
            /// <param name="id">The row id.</param>
            /// <param name="replaced">True when it replaced an existing row.</param>
            /// <param name="log">True to write one line per row.</param>
            internal void Count(string kind, string id, bool replaced, bool log)
            {
                var bucket = replaced ? Overridden : Added;
                bucket[kind] = bucket.TryGetValue(kind, out var seen) ? seen + 1 : 1;

                if (log)
                {
                    CwyLsModLoader.Log.LogDebug("MASTERDATA " + (replaced ? "~ " : "+ ") + kind + " " + id);
                }
            }

            /// <summary>How many rows of one kind were added.</summary>
            /// <param name="kind">Which file.</param>
            internal int AddedOf(string kind) => Added.TryGetValue(kind, out var count) ? count : 0;

            /// <summary>How many rows of one kind replaced an existing row.</summary>
            /// <param name="kind">Which file.</param>
            internal int OverriddenOf(string kind) => Overridden.TryGetValue(kind, out var count) ? count : 0;
        }
    }
}
