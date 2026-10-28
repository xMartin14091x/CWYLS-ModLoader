using System;
using System.Collections.Generic;

namespace CwyLs.ModLoader.Core.PackLines
{
    /// <summary>What one <see cref="PackLinePoolState.Reconcile"/> call changed.</summary>
    public readonly struct PackLineReconcile
    {
        /// <summary>Creates a result.</summary>
        /// <param name="consumed">Pack episodes the game used since the last call.</param>
        /// <param name="withdrawn">Pack episodes taken out because their language is not shown.</param>
        /// <param name="placed">Pack episodes put in because their language is shown.</param>
        public PackLineReconcile(int consumed, int withdrawn, int placed)
        {
            Consumed = consumed;
            Withdrawn = withdrawn;
            Placed = placed;
        }

        /// <summary>Pack episodes the game used since the last call (no longer in its list).</summary>
        public int Consumed { get; }

        /// <summary>Pack episodes taken out of the list because their language is not shown.</summary>
        public int Withdrawn { get; }

        /// <summary>Pack episodes put into the list because their language is shown.</summary>
        public int Placed { get; }

        /// <summary>True when the list was changed.</summary>
        public bool Changed => Withdrawn > 0 || Placed > 0;

        /// <summary><c>consumed=0 withdrawn=0 placed=2</c>.</summary>
        public override string ToString() => "consumed=" + Consumed + " withdrawn=" + Withdrawn + " placed=" + Placed;
    }

    /// <summary>
    /// Our bookkeeping for one of the game's <c>RandomList</c> pools: which of the pool's
    /// pack episodes are still owed to the current cycle and which are sitting in the game's list.
    /// Pure logic over the list's <c>List&lt;int&gt;</c>, so the gate is unit-tested without the game.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The game's <c>RandomList</c> holds a shuffled cycle
    /// of the episodes min..max: <c>GetNext</c> reads the front, <c>UseNext</c> or
    /// <c>RemoveFromList</c> drops one, and when the list is empty <c>InitList</c> builds the next
    /// cycle, moves its first element to the back if it equals the previous cycle's last (no repeat
    /// across cycles) and remembers the new last element in <c>_beforeLast</c>.
    /// </para>
    /// <para>
    /// The gate keeps that behaviour: each pack episode an open gate allows is inserted at a random
    /// position in front of the last element, so it plays exactly once per cycle like a game line,
    /// the cycle's last element (and with it <c>_beforeLast</c>) stays a game episode, and a pack line
    /// can never repeat across a cycle boundary. A closed gate takes our episodes out again, so the
    /// list is the game's own; they stay owed, and come back if the language returns within the
    /// same cycle. While no language of a line is ever shown, nothing is inserted and nothing random
    /// is drawn: the game's list, odds and random-number stream are untouched.
    /// </para>
    /// </remarks>
    public sealed class PackLinePoolState
    {
        private readonly List<int> _episodes = new List<int>();
        private readonly HashSet<int> _known = new HashSet<int>();
        private readonly HashSet<int> _owed = new HashSet<int>();
        private readonly HashSet<int> _placed = new HashSet<int>();

        /// <summary>Creates the state for a pool whose game list was just set up.</summary>
        /// <param name="episodes">The pool's pack episodes; null means none.</param>
        public PackLinePoolState(IEnumerable<int>? episodes)
        {
            Load(episodes);
        }

        /// <summary>The pool's pack episodes, in registry order.</summary>
        public IReadOnlyList<int> Episodes => _episodes;

        /// <summary>How many pack episodes are still owed to the current cycle.</summary>
        public int OwedCount => _owed.Count;

        /// <summary>How many pack episodes are in the game's list right now.</summary>
        public int PlacedCount => _placed.Count;

        /// <summary>True when the episode is one of this pool's pack episodes.</summary>
        /// <param name="episode">An episode number.</param>
        public bool IsPackEpisode(int episode) => _known.Contains(episode);

        /// <summary>True when the episode is in the game's list because of us.</summary>
        /// <param name="episode">An episode number.</param>
        public bool IsPlaced(int episode) => _placed.Contains(episode);

        /// <summary>True when the episode has not played yet in this cycle.</summary>
        /// <param name="episode">An episode number.</param>
        public bool IsOwed(int episode) => _owed.Contains(episode);

        /// <summary>
        /// A copy with the same episodes and the same owed and placed sets, for a dry run that must
        /// not disturb the live bookkeeping (the quiet smoke check).
        /// </summary>
        public PackLinePoolState Clone()
        {
            var copy = new PackLinePoolState(_episodes);
            copy._owed.Clear();
            copy._owed.UnionWith(_owed);
            copy._placed.UnionWith(_placed);
            return copy;
        }

        /// <summary>Pack episodes this cycle already dealt: neither owed nor in the list.</summary>
        public IReadOnlyList<int> PlayedThisCycle
        {
            get
            {
                var played = new List<int>();
                foreach (var episode in _episodes)
                {
                    if (!_owed.Contains(episode) && !_placed.Contains(episode))
                    {
                        played.Add(episode);
                    }
                }

                return played;
            }
        }

        /// <summary>
        /// A previous session already dealt these episodes in the cycle the game's list is still in
        /// (<see cref="PackLineCycles"/>): they are no longer owed, so they wait for the next cycle.
        /// Call before the first <see cref="Reconcile"/>. Returns how many were taken off the owed set.
        /// </summary>
        /// <param name="played">The episodes; unknown or already placed ones are ignored.</param>
        public int RestorePlayed(IEnumerable<int>? played)
        {
            var restored = 0;
            if (played is null)
            {
                return restored;
            }

            foreach (var episode in played)
            {
                if (_known.Contains(episode) && !_placed.Contains(episode) && _owed.Remove(episode))
                {
                    restored++;
                }
            }

            return restored;
        }

        /// <summary>
        /// The game just built a fresh cycle from its own range (<c>RandomList.InitList</c>): none of
        /// our episodes is in it and every one is owed again. Call <see cref="Reconcile"/> next.
        /// </summary>
        public void StartCycle()
        {
            _owed.Clear();
            foreach (var episode in _episodes)
            {
                _owed.Add(episode);
            }

            _placed.Clear();
        }

        /// <summary>
        /// Brings the game's list in line with the gate: records what the game used, takes out the
        /// episodes whose language is not shown, and inserts the owed ones whose language is.
        /// </summary>
        /// <param name="list">The game's list (<c>RandomList._randomList</c>). Required.</param>
        /// <param name="isOffered">The gate: true when an episode's line may be offered right now. Required.</param>
        /// <param name="pickIndex">
        /// Given the list's length n (at least 1), a position in [0, n) to insert in front of. Values
        /// outside the range are clamped. Called once per insertion, never when nothing is inserted.
        /// </param>
        /// <returns>What changed.</returns>
        /// <exception cref="ArgumentNullException">An argument is null.</exception>
        public PackLineReconcile Reconcile(IList<int> list, Func<int, bool> isOffered, Func<int, int> pickIndex)
        {
            if (list is null)
            {
                throw new ArgumentNullException(nameof(list));
            }

            if (isOffered is null)
            {
                throw new ArgumentNullException(nameof(isOffered));
            }

            if (pickIndex is null)
            {
                throw new ArgumentNullException(nameof(pickIndex));
            }

            if (_episodes.Count == 0)
            {
                return default;
            }

            // 1. Placed episodes the game removed were played (UseNext / RemoveFromList).
            var consumed = 0;
            foreach (var episode in Snapshot(_placed))
            {
                if (!list.Contains(episode))
                {
                    _placed.Remove(episode);
                    _owed.Remove(episode);
                    consumed++;
                }
            }

            // 2. One of our episodes in the list that we did not place (a re-run Setup, a state we
            //    lost): adopt it, so step 3 can take it out when its language is not shown.
            for (var i = 0; i < list.Count; i++)
            {
                var value = list[i];
                if (_known.Contains(value) && _placed.Add(value))
                {
                    _owed.Add(value);
                }
            }

            // 3. Language not shown: out of the list, still owed to this cycle.
            var withdrawn = 0;
            foreach (var episode in Snapshot(_placed))
            {
                if (!isOffered(episode))
                {
                    while (list.Remove(episode))
                    {
                    }

                    _placed.Remove(episode);
                    withdrawn++;
                }
            }

            // 4. Language shown: owed episodes go in, in front of the last element. An empty list is
            //    left alone - the game is about to start a new cycle, which re-owes everything.
            var placed = 0;
            if (list.Count > 0)
            {
                foreach (var episode in _episodes)
                {
                    if (!_owed.Contains(episode) || _placed.Contains(episode) || !isOffered(episode))
                    {
                        continue;
                    }

                    var index = pickIndex(list.Count);
                    if (index < 0)
                    {
                        index = 0;
                    }
                    else if (index >= list.Count)
                    {
                        index = list.Count - 1;
                    }

                    list.Insert(index, episode);
                    _placed.Add(episode);
                    placed++;
                }
            }

            return new PackLineReconcile(consumed, withdrawn, placed);
        }

        /// <summary>
        /// The pool's lines changed (a pack rescan or a new injection): every episode of the old set is
        /// taken out of the game's list, and the new set starts owed and unplaced.
        /// </summary>
        /// <param name="episodes">The pool's new pack episodes; null means none.</param>
        /// <param name="list">The game's list; null when it is not reachable (nothing is removed then).</param>
        public void Rebind(IEnumerable<int>? episodes, IList<int>? list)
        {
            if (list != null)
            {
                for (var i = list.Count - 1; i >= 0; i--)
                {
                    if (_known.Contains(list[i]))
                    {
                        list.RemoveAt(i);
                    }
                }
            }

            Load(episodes);
        }

        private void Load(IEnumerable<int>? episodes)
        {
            // Copied first: the caller may pass Episodes itself, which the next line clears.
            var incoming = episodes is null ? new List<int>() : new List<int>(episodes);
            _episodes.Clear();
            _known.Clear();
            foreach (var episode in incoming)
            {
                if (_known.Add(episode))
                {
                    _episodes.Add(episode);
                }
            }

            StartCycle();
        }

        private static int[] Snapshot(HashSet<int> set)
        {
            var copy = new int[set.Count];
            set.CopyTo(copy);
            return copy;
        }
    }
}
