using System;
using System.Collections.Generic;
using CwyLs.ModLoader.Core.MasterData;

namespace CwyLs.ModLoader.Core.PackLines
{
    /// <summary>
    /// The one save filter for every pack id. Master data owns it and installs it on
    /// <c>SaveDataManager.SaveScenarioProgressData</c>, so it holds whenever packs inject scene groups,
    /// with pack lines on or off. Pack lines join it with their ids and their seen-file callback.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Exactly one filter per save: this object is the only one that strips and restores
    /// <c>PlayedScenarioGroupIDs</c>, and pack lines have no save patch of their own. A second
    /// <see cref="JoinPackLines"/> replaces the first, so the callback runs once per save.
    /// </para>
    /// <para>
    /// When the hook could not be installed (<see cref="Installed"/> false - also the state before the
    /// attempt), nothing can keep an id out of the file. Then <see cref="PlanInjection"/> keeps the
    /// plain small-talk groups packs would add out of the game's data (the game's own lottery is what
    /// logs them), and <see cref="JoinPackLines"/> refuses, so pack lines stay off.
    /// </para>
    /// </remarks>
    public sealed class PackSaveHoldBack
    {
        /// <summary>The game's small-talk scene type (<c>ScenarioType.SmallTalk</c>, game 1.17.3).</summary>
        public const int SmallTalkScenario = 23;

        private static readonly ICollection<string> NoIds = new HashSet<string>(StringComparer.Ordinal);

        private ICollection<string> _packLineIds = NoIds;
        private Action<IReadOnlyList<string>>? _onHeld;
        private Func<string, bool> _rule;
        private PackLineHeldIds? _pending;

        /// <summary>Creates the filter over the session's pack-added groups.</summary>
        /// <param name="addedGroups">Recorded at every injection. Required.</param>
        /// <exception cref="ArgumentNullException"><paramref name="addedGroups"/> is null.</exception>
        public PackSaveHoldBack(PackAddedGroups addedGroups)
        {
            AddedGroups = addedGroups ?? throw new ArgumentNullException(nameof(addedGroups));
            _rule = PackLineSaveFilter.HoldBackRule(_packLineIds, AddedGroups);
        }

        /// <summary>The groups packs added this session.</summary>
        public PackAddedGroups AddedGroups { get; }

        /// <summary>True once the save hook is in place.</summary>
        public bool Installed { get; private set; }

        /// <summary>Why the hook is not in place, or empty when it is.</summary>
        public string Problem { get; private set; } = "the save hook has not been installed yet";

        /// <summary>True while pack lines have joined.</summary>
        public bool PackLinesJoined { get; private set; }

        /// <summary>The hook is in place.</summary>
        public void MarkInstalled()
        {
            Installed = true;
            Problem = string.Empty;
        }

        /// <summary>The hook could not be installed; pack small talk stays out of the game's data.</summary>
        /// <param name="problem">Why, for the log.</param>
        public void MarkMissing(string problem)
        {
            Installed = false;
            Problem = string.IsNullOrEmpty(problem) ? "the save hook is missing" : problem;
        }

        /// <summary>
        /// Pack lines join: their group ids are held back too, and <paramref name="onHeld"/> gets each
        /// save's held ids (all of them, once). Replaces an earlier join. Refused (false, nothing changes)
        /// while the hook is not installed.
        /// </summary>
        /// <param name="packLineIds">The pack lines' group ids seen this session (a live set). Required.</param>
        /// <param name="onHeld">Called once per save that held something back; null for none.</param>
        /// <exception cref="ArgumentNullException"><paramref name="packLineIds"/> is null.</exception>
        public bool JoinPackLines(ICollection<string> packLineIds, Action<IReadOnlyList<string>>? onHeld)
        {
            if (packLineIds is null)
            {
                throw new ArgumentNullException(nameof(packLineIds));
            }

            if (!Installed)
            {
                return false;
            }

            _packLineIds = packLineIds;
            _onHeld = onHeld;
            _rule = PackLineSaveFilter.HoldBackRule(_packLineIds, AddedGroups);
            PackLinesJoined = true;
            return true;
        }

        /// <summary>True for an id the save must not hold (pack line or pack-added group).</summary>
        /// <param name="id">A scene-group id.</param>
        public bool IsHeld(string id) => _rule(id);

        /// <summary>
        /// Prefix of the save: takes every pack id out of <paramref name="ids"/> and keeps it for
        /// <see cref="Restore"/>; tells the pack-lines callback once. Drops anything an earlier save left.
        /// </summary>
        /// <param name="ids">The game's played list.</param>
        /// <returns>What was taken out.</returns>
        public PackLineHeldIds HoldBack(IList<string>? ids)
        {
            _pending = null;
            if (ids is null || (AddedGroups.Count == 0 && _packLineIds.Count == 0))
            {
                return PackLineHeldIds.None;
            }

            var held = PackLineSaveFilter.Strip(ids, _rule);
            if (held.Count == 0)
            {
                return held;
            }

            // Kept before the callback runs, so a throwing callback still gets the ids put back.
            _pending = held;
            _onHeld?.Invoke(new List<string>(held.Ids));
            return held;
        }

        /// <summary>Finalizer of the save: puts back, in place, what <see cref="HoldBack"/> took out. Once.</summary>
        /// <param name="ids">The game's played list.</param>
        public void Restore(IList<string>? ids)
        {
            var held = _pending;
            _pending = null;
            PackLineSaveFilter.Restore(ids, held);
        }

        /// <summary>
        /// What may be injected: <paramref name="set"/> itself while the hook is installed; otherwise the
        /// set without the small-talk groups packs would add (an id the game's data does not have) and
        /// their novel rows, because nothing could keep their ids out of the save. An override of a game
        /// small talk keeps the game's id and stays.
        /// </summary>
        /// <param name="set">The screened merge.</param>
        /// <param name="baseline">The game's own ids; empty or null holds out every pack small talk.</param>
        /// <param name="heldOut">How many groups were kept out.</param>
        public MasterDataSet PlanInjection(MasterDataSet set, MasterDataBaseline? baseline, out int heldOut)
        {
            heldOut = 0;
            if (set is null || Installed)
            {
                return set ?? MasterDataSet.Empty;
            }

            var keptOut = new HashSet<string>(StringComparer.Ordinal);
            var groups = new List<ScenarioGroupRow>(set.ScenarioGroups.Count);
            foreach (var group in set.ScenarioGroups)
            {
                if (group.Scenario == SmallTalkScenario && (baseline is null || !baseline.HasScenarioGroup(group.Id)))
                {
                    keptOut.Add(group.Id);
                    continue;
                }

                groups.Add(group);
            }

            if (keptOut.Count == 0)
            {
                return set;
            }

            var novel = new List<NovelRow>(set.Novel.Count);
            foreach (var row in set.Novel)
            {
                if (!keptOut.Contains(row.ScenarioGroupId))
                {
                    novel.Add(row);
                }
            }

            heldOut = keptOut.Count;
            return new MasterDataSet(set.Localization, novel, groups, set.Subtitles, set.Report);
        }
    }
}
