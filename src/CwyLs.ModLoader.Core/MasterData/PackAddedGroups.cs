using System;
using System.Collections.Generic;

namespace CwyLs.ModLoader.Core.MasterData
{
    /// <summary>
    /// The scene groups packs added to the game's master data this session: the ids the game's save
    /// must never hold. Recorded at every injection, from the rows actually injected.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Why: a plain pack group (a <c>data/scenario_groups.json</c> row without <c>Languages</c>) is
    /// injected like a game group, so the game's own small-talk lottery can draw it, and
    /// <c>SmallTalkSelector.OnEndTalk</c> then logs its id in <c>ScenarioProgressData.PlayedScenarioGroupIDs</c>
    /// and saves. The pack-lines save filter (<c>PackLineSaveFilter</c>) uses this set, together with the
    /// pack-line ids, to keep such ids out of the file.
    /// </para>
    /// <para>
    /// Exact provenance, never "the game does not know this id": only an id a pack row injected is ever
    /// recorded. A save synced through Steam Cloud from a newer game version can hold real game ids this
    /// build has never seen; they are not in this set, so the filter leaves them alone.
    /// </para>
    /// <para>
    /// The set only grows within a session (a pack removed by a rescan may still have its id in the
    /// game's in-memory played list), with one exception: an id the game's own data turns out to have is
    /// the game's, and is dropped.
    /// </para>
    /// </remarks>
    public sealed class PackAddedGroups
    {
        private readonly HashSet<string> _ids = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>How many ids are recorded.</summary>
        public int Count => _ids.Count;

        /// <summary>The recorded ids, ordinally sorted (a copy).</summary>
        public IReadOnlyList<string> Ids
        {
            get
            {
                var ids = new List<string>(_ids);
                ids.Sort(StringComparer.Ordinal);
                return ids;
            }
        }

        /// <summary>True when a pack added this group in this session.</summary>
        /// <param name="id">A scene-group id; null is never one.</param>
        public bool Contains(string? id) => id != null && _ids.Contains(id);

        /// <summary>
        /// Records one injection: every injected group whose id the game's own data does not have was
        /// added by a pack. An injected group the game has is an override of the game's group and is
        /// never recorded (and is dropped if an earlier injection recorded it). Without the game's ids
        /// (an empty baseline) nothing is recorded, because an override could not be told from an
        /// addition - a missed id is a leak, a wrong one would cost the player real progress.
        /// </summary>
        /// <param name="injected">The scene-group rows just injected (<c>MasterDataSet.ScenarioGroups</c>).</param>
        /// <param name="baseline">The game's own ids the injection was built on.</param>
        /// <returns>How many ids are newly recorded.</returns>
        public int Record(IEnumerable<ScenarioGroupRow?>? injected, MasterDataBaseline? baseline)
        {
            if (injected is null || baseline is null || baseline.ScenarioGroupCount == 0)
            {
                return 0;
            }

            var added = 0;
            foreach (var row in injected)
            {
                var id = row?.Id;
                if (string.IsNullOrEmpty(id))
                {
                    continue;
                }

                if (baseline.HasScenarioGroup(id))
                {
                    _ids.Remove(id!);
                }
                else if (_ids.Add(id!))
                {
                    added++;
                }
            }

            return added;
        }
    }
}
