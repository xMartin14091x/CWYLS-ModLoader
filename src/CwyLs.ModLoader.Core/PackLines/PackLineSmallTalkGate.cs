using System;
using System.Collections.Generic;

namespace CwyLs.ModLoader.Core.PackLines
{
    /// <summary>
    /// The gate for the small-talk pool: keeps language-exclusive episodes out of the
    /// game's lottery cache while their language is not shown. Generic over the cached value so it is
    /// unit-tested without the game's <c>ScenarioGroupData</c>.
    /// </summary>
    /// <remarks>
    /// <c>SmallTalkSelector.Setup</c> builds <c>_cache</c>, a <c>Dictionary&lt;float, ScenarioGroupData&gt;</c>,
    /// from every <c>SmallTalk</c> group in the master data, and
    /// <c>LotterySmallTalk</c> walks it. Taking our entries out right after <c>Setup</c>
    /// leaves the game's own entries in their original enumeration order, so with the gate closed the
    /// lottery sees exactly the game's candidates in the game's order and draws exactly as it would
    /// without the framework. With the gate open, our entries are put back and compete under the
    /// game's own rules: unlock, play timing, content type, interval, and the group's Arg1/Arg2 odds.
    /// </remarks>
    public static class PackLineSmallTalkGate
    {
        /// <summary>
        /// Takes every entry <paramref name="isPackLine"/> accepts out of the game's cache and returns
        /// them in the cache's enumeration order. The game's own entries keep their order.
        /// </summary>
        /// <typeparam name="T">The cached value type.</typeparam>
        /// <param name="cache">The game's cache. Required.</param>
        /// <param name="isPackLine">True for a value that is one of our lines (old or current). Required.</param>
        /// <returns>The withdrawn entries; empty when none.</returns>
        /// <exception cref="ArgumentNullException">An argument is null.</exception>
        public static List<KeyValuePair<float, T>> Withdraw<T>(IDictionary<float, T> cache, Func<T, bool> isPackLine)
        {
            if (cache is null)
            {
                throw new ArgumentNullException(nameof(cache));
            }

            if (isPackLine is null)
            {
                throw new ArgumentNullException(nameof(isPackLine));
            }

            var withdrawn = new List<KeyValuePair<float, T>>();
            foreach (var entry in cache)
            {
                if (isPackLine(entry.Value))
                {
                    withdrawn.Add(entry);
                }
            }

            foreach (var entry in withdrawn)
            {
                cache.Remove(entry.Key);
            }

            return withdrawn;
        }

        /// <summary>
        /// Puts each withdrawn entry whose line is offered into the cache, and takes out each whose
        /// line is not. An episode the cache holds under a different value (not ours) is never touched.
        /// </summary>
        /// <typeparam name="T">The cached value type.</typeparam>
        /// <param name="cache">The game's cache. Required.</param>
        /// <param name="withdrawn">What <see cref="Withdraw{T}"/> returned. Required.</param>
        /// <param name="isOffered">The gate, by episode. Required.</param>
        /// <returns>What changed (<see cref="PackLineReconcile.Consumed"/> is always 0 here).</returns>
        /// <exception cref="ArgumentNullException">An argument is null.</exception>
        public static PackLineReconcile Reconcile<T>(
            IDictionary<float, T> cache,
            IReadOnlyList<KeyValuePair<float, T>> withdrawn,
            Func<float, bool> isOffered)
            where T : class
        {
            if (cache is null)
            {
                throw new ArgumentNullException(nameof(cache));
            }

            if (withdrawn is null)
            {
                throw new ArgumentNullException(nameof(withdrawn));
            }

            if (isOffered is null)
            {
                throw new ArgumentNullException(nameof(isOffered));
            }

            var added = 0;
            var removed = 0;
            foreach (var entry in withdrawn)
            {
                var present = cache.TryGetValue(entry.Key, out var current);
                if (present && !ReferenceEquals(current, entry.Value))
                {
                    continue;
                }

                if (isOffered(entry.Key))
                {
                    if (!present)
                    {
                        cache.Add(entry.Key, entry.Value);
                        added++;
                    }
                }
                else if (present)
                {
                    cache.Remove(entry.Key);
                    removed++;
                }
            }

            return new PackLineReconcile(0, removed, added);
        }
    }
}
