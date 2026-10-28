using System;
using System.Collections.Generic;

namespace CwyLs.ModLoader.Core.PackLines
{
    /// <summary>
    /// What <see cref="PackLineSaveFilter.Strip"/> took out of a list, with each item's original
    /// position, so <see cref="PackLineSaveFilter.Restore"/> can put the list back exactly.
    /// </summary>
    public sealed class PackLineHeldIds
    {
        private readonly List<KeyValuePair<int, string>> _items;

        internal PackLineHeldIds(List<KeyValuePair<int, string>> items)
        {
            _items = items;
        }

        /// <summary>Nothing held back.</summary>
        public static PackLineHeldIds None { get; } = new PackLineHeldIds(new List<KeyValuePair<int, string>>());

        /// <summary>How many ids were taken out.</summary>
        public int Count => _items.Count;

        /// <summary>The ids taken out, in their original order.</summary>
        public IEnumerable<string> Ids
        {
            get
            {
                foreach (var item in _items)
                {
                    yield return item.Value;
                }
            }
        }

        /// <summary>(original index, id) pairs in ascending index order.</summary>
        internal IReadOnlyList<KeyValuePair<int, string>> Items => _items;
    }

    /// <summary>
    /// Keeps pack-line ids out of the game's save.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The only save list a pack line can reach is <c>ScenarioProgressData.PlayedScenarioGroupIDs</c>:
    /// a small talk adds its group id when it ends (<c>SmallTalkSelector</c>), and the save
    /// file is synced by Steam Cloud. Every write of that list goes through
    /// <c>Bulbul.SaveDataManager.SaveScenarioProgressData</c>, which serialises synchronously
    /// (<c>SaveDataIO.Save</c> -&gt; <c>ES3.Save</c>).
    /// The adapter strips the pack ids in a prefix of that method and restores them, in place, in a
    /// finalizer: the file never holds them, and in-session behaviour (a small talk counts as seen and
    /// switches to its "seen" odds) is unchanged.
    /// </para>
    /// <para>
    /// Click, self-talk, timer and answer-choice lines are never logged anywhere: <c>FacilityClickHeroine</c>
    /// and <c>HeroineAI</c> play them without <c>FacilityStory.SaveScenarioPlayedLog</c>, and the game's
    /// selectors that hold them are not part of any save (only <c>StartDirectionData</c>'s game-start
    /// lists are, and no pool here is one of them).
    /// </para>
    /// </remarks>
    public static class PackLineSaveFilter
    {
        /// <summary>
        /// Takes every id <paramref name="isPackId"/> accepts out of <paramref name="ids"/>, remembering
        /// where each was. The other ids keep their order. Nothing is allocated when nothing matches.
        /// </summary>
        /// <param name="ids">The game's list. Null is treated as empty.</param>
        /// <param name="isPackId">True for an id the save must not hold. Required.</param>
        /// <returns>What was taken out; <see cref="PackLineHeldIds.None"/> when nothing was.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="isPackId"/> is null.</exception>
        public static PackLineHeldIds Strip(IList<string>? ids, Func<string, bool> isPackId)
        {
            if (isPackId is null)
            {
                throw new ArgumentNullException(nameof(isPackId));
            }

            if (ids is null || ids.Count == 0)
            {
                return PackLineHeldIds.None;
            }

            List<KeyValuePair<int, string>>? held = null;
            for (var i = 0; i < ids.Count; i++)
            {
                var id = ids[i];
                if (id != null && isPackId(id))
                {
                    held ??= new List<KeyValuePair<int, string>>();
                    held.Add(new KeyValuePair<int, string>(i, id));
                }
            }

            if (held is null)
            {
                return PackLineHeldIds.None;
            }

            for (var i = held.Count - 1; i >= 0; i--)
            {
                ids.RemoveAt(held[i].Key);
            }

            return new PackLineHeldIds(held);
        }

        /// <summary>
        /// Puts back what <see cref="Strip"/> took out, each at its original position, so the list is
        /// exactly what it was before the save. Safe to call with <see cref="PackLineHeldIds.None"/>.
        /// </summary>
        /// <param name="ids">The game's list, as <see cref="Strip"/> left it (the save does not change it).</param>
        /// <param name="held">What was taken out; null does nothing.</param>
        public static void Restore(IList<string>? ids, PackLineHeldIds? held)
        {
            if (ids is null || held is null || held.Count == 0)
            {
                return;
            }

            foreach (var item in held.Items)
            {
                // Ascending original indices: every earlier item is already back, so each index is
                // exact again. A list shortened behind our back gets the rest appended.
                var index = item.Key <= ids.Count ? item.Key : ids.Count;
                ids.Insert(index, item.Value);
            }
        }
    }
}
