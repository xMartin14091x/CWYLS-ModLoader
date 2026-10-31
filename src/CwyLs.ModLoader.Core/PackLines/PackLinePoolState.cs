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

    /// <summary>What one pick did to a list, as <see cref="PackLinePoolState.EndPick"/> saw it.</summary>
    public readonly struct PackLinePickResult
    {
        /// <summary>Creates a result.</summary>
        /// <param name="read">True when the game took the list's first episode during the pick.</param>
        /// <param name="episode">The episode it took; 0 when <paramref name="read"/> is false.</param>
        /// <param name="weighted">True when that episode was the weighted line put in front for this pick.</param>
        /// <param name="takenBack">How many weighted episodes the pick left in the list and were taken out again.</param>
        public PackLinePickResult(bool read, int episode, bool weighted, int takenBack)
        {
            Read = read;
            Episode = read ? episode : 0;
            Weighted = read && weighted;
            TakenBack = takenBack;
        }

        /// <summary>True when the game took the list's first episode during the pick (the pool was read).</summary>
        public bool Read { get; }

        /// <summary>The episode the game took; 0 when the list was not read.</summary>
        public int Episode { get; }

        /// <summary>True when the game took the weighted line put in front for this pick.</summary>
        public bool Weighted { get; }

        /// <summary>Weighted episodes the game did not take, taken out of its list again.</summary>
        public int TakenBack { get; }

        /// <summary><c>read=True episode=1001 weighted=True takenBack=0</c>.</summary>
        public override string ToString() =>
            "read=" + Read + " episode=" + Episode + " weighted=" + Weighted + " takenBack=" + TakenBack;
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
    /// <para>
    /// <b>Weighted lines</b> (a <c>Weight</c> member, <see cref="PackLineWeights"/>) are never dealt:
    /// they are known (<see cref="IsPackEpisode"/>) but never owed or placed. At a pick, the caller
    /// rolls them and hands the winner to <see cref="BeginPick"/>, which puts it in front of the list
    /// so the game's next read returns it; <see cref="EndPick"/> takes it out again if the game read
    /// another list, and tells which episode the game took. A non-empty list stays non-empty and an
    /// empty one stays empty (the winner waits for the game's own refill,
    /// <see cref="OnCycleDuringPick"/>), so every emptiness check the game makes - and with it every
    /// one of its own random draws - is what it would be without us. The game's own episodes, their
    /// order and <c>_beforeLast</c> are never touched.
    /// </para>
    /// </remarks>
    public sealed class PackLinePoolState
    {
        private readonly List<int> _episodes = new List<int>();
        private readonly HashSet<int> _known = new HashSet<int>();
        private readonly HashSet<int> _owed = new HashSet<int>();
        private readonly HashSet<int> _placed = new HashSet<int>();
        private readonly List<int> _weighted = new List<int>();
        private readonly HashSet<int> _weightedKnown = new HashSet<int>();

        private bool _inPick;
        private int? _front;
        private int? _pending;
        private int? _pickHead;

        /// <summary>Creates the state for a pool whose game list was just set up.</summary>
        /// <param name="episodes">The pool's pack episodes dealt into its deck; null means none.</param>
        public PackLinePoolState(IEnumerable<int>? episodes)
            : this(episodes, null)
        {
        }

        /// <summary>Creates the state for a pool whose game list was just set up.</summary>
        /// <param name="episodes">The pool's unweighted pack episodes, dealt into its deck; null means none.</param>
        /// <param name="weighted">
        /// The pool's weighted pack episodes, never dealt, only put in front for a pick they won; null
        /// means none. An episode also in <paramref name="episodes"/> counts as dealt.
        /// </param>
        public PackLinePoolState(IEnumerable<int>? episodes, IEnumerable<int>? weighted)
        {
            Load(episodes, weighted);
        }

        /// <summary>The pool's pack episodes dealt into its deck (the unweighted lines), in registry order.</summary>
        public IReadOnlyList<int> Episodes => _episodes;

        /// <summary>The pool's weighted pack episodes, in registry order: never dealt, rolled at each pick.</summary>
        public IReadOnlyList<int> WeightedEpisodes => _weighted;

        /// <summary>True when the pool has any pack episode, dealt or weighted.</summary>
        public bool HasLines => _episodes.Count > 0 || _weighted.Count > 0;

        /// <summary>True between <see cref="BeginPick"/> and <see cref="EndPick"/>.</summary>
        public bool InPick => _inPick;

        /// <summary>The weighted episode in front of the list for the current pick, or null.</summary>
        public int? Front => _front;

        /// <summary>How many pack episodes are still owed to the current cycle.</summary>
        public int OwedCount => _owed.Count;

        /// <summary>How many pack episodes are in the game's list right now.</summary>
        public int PlacedCount => _placed.Count;

        /// <summary>True when the episode is one of this pool's pack episodes, dealt or weighted.</summary>
        /// <param name="episode">An episode number.</param>
        public bool IsPackEpisode(int episode) => _known.Contains(episode) || _weightedKnown.Contains(episode);

        /// <summary>True when the episode is one of this pool's weighted (never dealt) pack episodes.</summary>
        /// <param name="episode">An episode number.</param>
        public bool IsWeighted(int episode) => _weightedKnown.Contains(episode);

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
            var copy = new PackLinePoolState(_episodes, _weighted);
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

            if (_episodes.Count == 0 && _weighted.Count == 0)
            {
                return default;
            }

            // 0. A weighted episode is only ever in the list for the pick it won (BeginPick..EndPick);
            //    one found outside a pick was left by a pick that never ended: out it goes.
            var withdrawn = 0;
            if (!_inPick && _weighted.Count > 0)
            {
                withdrawn += RemoveWeighted(list);
            }

            if (_episodes.Count == 0)
            {
                return new PackLineReconcile(0, withdrawn, 0);
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
        /// taken out of the game's list, and the new set starts owed and unplaced. The pool keeps no
        /// weighted episode (see the three-argument overload).
        /// </summary>
        /// <param name="episodes">The pool's new pack episodes; null means none.</param>
        /// <param name="list">The game's list; null when it is not reachable (nothing is removed then).</param>
        public void Rebind(IEnumerable<int>? episodes, IList<int>? list) => Rebind(episodes, null, list);

        /// <summary>
        /// The pool's lines changed (a pack rescan or a new injection): every episode of the old sets,
        /// dealt or weighted, is taken out of the game's list; the new dealt set starts owed and
        /// unplaced, and any pick in progress is dropped.
        /// </summary>
        /// <param name="episodes">The pool's new unweighted (dealt) pack episodes; null means none.</param>
        /// <param name="weighted">The pool's new weighted pack episodes; null means none.</param>
        /// <param name="list">The game's list; null when it is not reachable (nothing is removed then).</param>
        public void Rebind(IEnumerable<int>? episodes, IEnumerable<int>? weighted, IList<int>? list)
        {
            if (list != null)
            {
                for (var i = list.Count - 1; i >= 0; i--)
                {
                    if (IsPackEpisode(list[i]))
                    {
                        list.RemoveAt(i);
                    }
                }
            }

            Load(episodes, weighted);
        }

        // ---- weighted lines: one pick at a time -------------------------------------------------

        /// <summary>
        /// The game is about to pick (the gate ran): puts the weighted episode that won this pick's
        /// roll in front of the list, so the game's next read returns it, and notes the list's first
        /// episode so <see cref="EndPick"/> can tell whether the game read this list. An empty list is
        /// left empty (its emptiness steers the game's own choices); the winner then waits for the
        /// game's refill, <see cref="OnCycleDuringPick"/>. A pick that never ended is closed first.
        /// </summary>
        /// <param name="list">The game's list. Required.</param>
        /// <param name="winner">The weighted episode that won the roll, or null for none (nothing is inserted).</param>
        /// <exception cref="ArgumentNullException"><paramref name="list"/> is null.</exception>
        public void BeginPick(IList<int> list, int? winner)
        {
            if (list is null)
            {
                throw new ArgumentNullException(nameof(list));
            }

            if (_inPick)
            {
                EndPick(list);
            }

            _inPick = true;
            _front = null;
            _pending = null;
            if (winner.HasValue && _weightedKnown.Contains(winner.Value) && !list.Contains(winner.Value))
            {
                if (list.Count > 0)
                {
                    list.Insert(0, winner.Value);
                    _front = winner.Value;
                }
                else
                {
                    _pending = winner.Value;
                }
            }

            _pickHead = list.Count > 0 ? list[0] : (int?)null;
        }

        /// <summary>
        /// The game refilled the list during a pick (<c>InitList</c>, after <see cref="StartCycle"/> and
        /// <see cref="Reconcile"/>): a winner that waited for it goes in front now, and the list's new
        /// first episode is what a read would take. Does nothing outside a pick.
        /// </summary>
        /// <param name="list">The game's list. Required.</param>
        /// <exception cref="ArgumentNullException"><paramref name="list"/> is null.</exception>
        public void OnCycleDuringPick(IList<int> list)
        {
            if (list is null)
            {
                throw new ArgumentNullException(nameof(list));
            }

            if (!_inPick)
            {
                return;
            }

            if (_pending.HasValue && list.Count > 0 && !list.Contains(_pending.Value))
            {
                list.Insert(0, _pending.Value);
                _front = _pending;
                _pending = null;
            }

            _pickHead = list.Count > 0 ? list[0] : (int?)null;
        }

        /// <summary>
        /// The game's pick is over: whether it read this list (the first episode at the pick is gone),
        /// which episode it took, and whether that was the weighted winner. A weighted episode still in
        /// the list (the game read another list) is taken out again, so outside a pick the list holds
        /// only the game's episodes and the dealt ones. Does nothing outside a pick.
        /// </summary>
        /// <param name="list">The game's list. Required.</param>
        /// <exception cref="ArgumentNullException"><paramref name="list"/> is null.</exception>
        public PackLinePickResult EndPick(IList<int> list)
        {
            if (list is null)
            {
                throw new ArgumentNullException(nameof(list));
            }

            if (!_inPick)
            {
                return default;
            }

            var head = _pickHead;
            var front = _front;
            _inPick = false;
            _front = null;
            _pending = null;
            _pickHead = null;

            var read = head.HasValue && !list.Contains(head.Value);
            var takenBack = RemoveWeighted(list);
            return new PackLinePickResult(read, head ?? 0, read && front.HasValue && head == front, takenBack);
        }

        private int RemoveWeighted(IList<int> list)
        {
            var removed = 0;
            for (var i = list.Count - 1; i >= 0; i--)
            {
                if (_weightedKnown.Contains(list[i]))
                {
                    list.RemoveAt(i);
                    removed++;
                }
            }

            return removed;
        }

        private void Load(IEnumerable<int>? episodes, IEnumerable<int>? weighted)
        {
            // Copied first: the caller may pass Episodes itself, which the next line clears.
            var incoming = episodes is null ? new List<int>() : new List<int>(episodes);
            var incomingWeighted = weighted is null ? new List<int>() : new List<int>(weighted);
            _episodes.Clear();
            _known.Clear();
            foreach (var episode in incoming)
            {
                if (_known.Add(episode))
                {
                    _episodes.Add(episode);
                }
            }

            _weighted.Clear();
            _weightedKnown.Clear();
            foreach (var episode in incomingWeighted)
            {
                if (!_known.Contains(episode) && _weightedKnown.Add(episode))
                {
                    _weighted.Add(episode);
                }
            }

            _inPick = false;
            _front = null;
            _pending = null;
            _pickHead = null;
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
