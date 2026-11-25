using System;
using System.Collections.Generic;
using CwyLs.ModLoader.Core.PackLines;

namespace CwyLs.ModLoader.Bulbul.PackLines
{
    /// <summary>A pack deck interleaved with the remaining vanilla Desk picks of a reaction pool.</summary>
    internal sealed class PackLinesReactionDeck
    {
        private readonly List<int> _items = new List<int>();
        private int _vanillaRemaining;

        internal PackLinesReactionDeck(PackLinePool pool, IEnumerable<int> episodes, IEnumerable<int> weighted)
        {
            Pool = pool;
            State = new PackLinePoolState(episodes, weighted);
        }

        internal PackLinePool Pool { get; }
        internal PackLinePoolState State { get; }

        internal void Rebind(IEnumerable<int> episodes, IEnumerable<int> weighted) => State.Rebind(episodes, weighted, _items);

        internal void Refill()
        {
            _items.Clear();
            _vanillaRemaining = 0;
            State.StartCycle();
        }

        internal int? Peek(int vanillaRemaining, Func<int, bool> offered, Func<int, int> pickIndex)
        {
            // Positive episodes belong to packs. A negative entry is an uninspected vanilla pick.
            // The game's category chooses general/time-of-day first and owns the vanilla shuffle.
            if (vanillaRemaining > _vanillaRemaining)
            {
                Refill();
                for (var i = vanillaRemaining; i > 0; i--)
                {
                    _items.Add(-i);
                }

                _vanillaRemaining = vanillaRemaining;
            }

            while (_vanillaRemaining > vanillaRemaining)
            {
                _items.Remove(-_vanillaRemaining--);
            }

            if (vanillaRemaining == 0)
            {
                return null;
            }

            State.Reconcile(_items, offered, pickIndex);
            return _items.Count > 0 && State.IsPackEpisode(_items[0]) ? _items[0] : (int?)null;
        }

        internal void Consume(int episode) => _items.Remove(episode);
    }
}
