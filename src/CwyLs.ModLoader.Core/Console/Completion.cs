using System;
using System.Collections.Generic;

namespace CwyLs.ModLoader.Core.Console
{
    /// <summary>What a Tab press found.</summary>
    public sealed class CompletionResult
    {
        /// <summary>An empty result: nothing matched, so the input is left alone.</summary>
        public static readonly CompletionResult None =
            new CompletionResult(Array.Empty<string>(), string.Empty, false);

        /// <summary>Creates a result.</summary>
        /// <param name="matches">What matched, in rank order. Null becomes empty.</param>
        /// <param name="completion">What the input should become. Null becomes empty.</param>
        /// <param name="isFuzzy">True when nothing matched by prefix and these are fuzzy matches.</param>
        public CompletionResult(IReadOnlyList<string>? matches, string? completion, bool isFuzzy)
        {
            Matches = matches ?? Array.Empty<string>();
            Text = completion ?? string.Empty;
            IsFuzzy = isFuzzy;
        }

        /// <summary>What matched, best first. Never null.</summary>
        public IReadOnlyList<string> Matches { get; }

        /// <summary>
        /// What the input line should become: the single match, or the longest prefix every match
        /// shares (never shorter than what was typed). Empty when nothing matched.
        /// </summary>
        public string Text { get; }

        /// <summary>
        /// True when no candidate started with what was typed and these matches are fuzzy - the
        /// caller should list them rather than silently rewriting the input.
        /// </summary>
        public bool IsFuzzy { get; }

        /// <summary>True when exactly one candidate matched, so Tab can complete it outright.</summary>
        public bool IsUnique => Matches.Count == 1;

        /// <summary>True when nothing matched.</summary>
        public bool IsEmpty => Matches.Count == 0;
    }

    /// <summary>
    /// Tab completion: prefix first, and only when nothing starts with what was typed, a fuzzy
    /// pass that accepts the letters in order with gaps (<c>pl</c> finds <c>packs list</c>'s
    /// <c>packs</c>, <c>mdt</c> finds <c>masterdata</c>). Unity-free, so every rule here is a
    /// unit test rather than a surprise in-game.
    /// </summary>
    public static class Completion
    {
        /// <summary>
        /// Completes what has been typed against a set of candidates.
        /// </summary>
        /// <param name="typed">
        /// The word being completed. Null or empty offers every candidate, sorted, and completes
        /// to their common prefix.
        /// </param>
        /// <param name="candidates">The possible words; null entries and duplicates are ignored.</param>
        public static CompletionResult Complete(string? typed, IEnumerable<string>? candidates)
        {
            if (candidates is null)
            {
                return CompletionResult.None;
            }

            var word = typed ?? string.Empty;
            var prefixMatches = new List<string>();
            var fuzzyMatches = new List<ScoredMatch>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var candidate in candidates)
            {
                if (string.IsNullOrEmpty(candidate) || !seen.Add(candidate))
                {
                    continue;
                }

                if (word.Length == 0 || candidate.StartsWith(word, StringComparison.OrdinalIgnoreCase))
                {
                    prefixMatches.Add(candidate);
                }
                else if (TryScoreFuzzy(word, candidate, out var score))
                {
                    fuzzyMatches.Add(new ScoredMatch(candidate, score));
                }
            }

            if (prefixMatches.Count > 0)
            {
                prefixMatches.Sort(StringComparer.OrdinalIgnoreCase);
                return new CompletionResult(prefixMatches, CommonPrefix(prefixMatches, word), false);
            }

            if (fuzzyMatches.Count == 0)
            {
                return CompletionResult.None;
            }

            // Tighter matches first (fewer skipped characters), then alphabetically so the order
            // is stable between presses.
            fuzzyMatches.Sort(static (a, b) =>
            {
                var byScore = a.Score.CompareTo(b.Score);
                return byScore != 0 ? byScore : StringComparer.OrdinalIgnoreCase.Compare(a.Text, b.Text);
            });

            var ranked = new List<string>(fuzzyMatches.Count);
            foreach (var match in fuzzyMatches)
            {
                ranked.Add(match.Text);
            }

            // A single fuzzy hit is unambiguous enough to insert; several are only a suggestion,
            // so the typed word survives and the caller lists them.
            return new CompletionResult(ranked, ranked.Count == 1 ? ranked[0] : word, true);
        }

        /// <summary>
        /// The longest prefix every match shares, never shorter than what was already typed.
        /// </summary>
        /// <param name="matches">The matching candidates.</param>
        /// <param name="typed">What was typed; returned when the matches share nothing more.</param>
        public static string CommonPrefix(IReadOnlyList<string>? matches, string? typed = null)
        {
            var fallback = typed ?? string.Empty;
            if (matches is null || matches.Count == 0)
            {
                return fallback;
            }

            var shared = matches[0];
            for (var i = 1; i < matches.Count && shared.Length > 0; i++)
            {
                var other = matches[i];
                var length = Math.Min(shared.Length, other.Length);
                var common = 0;
                while (common < length &&
                       char.ToLowerInvariant(shared[common]) == char.ToLowerInvariant(other[common]))
                {
                    common++;
                }

                shared = shared.Substring(0, common);
            }

            return shared.Length >= fallback.Length ? shared : fallback;
        }

        /// <summary>
        /// True when every character of <paramref name="typed"/> appears in
        /// <paramref name="candidate"/> in order, ignoring case.
        /// </summary>
        /// <param name="typed">The letters typed.</param>
        /// <param name="candidate">The candidate word.</param>
        public static bool IsFuzzyMatch(string? typed, string? candidate) =>
            TryScoreFuzzy(typed ?? string.Empty, candidate ?? string.Empty, out _);

        private static bool TryScoreFuzzy(string typed, string candidate, out int score)
        {
            score = int.MaxValue;
            if (typed.Length == 0 || candidate.Length < typed.Length)
            {
                return false;
            }

            var at = 0;
            var first = -1;
            var gaps = 0;

            foreach (var wanted in typed)
            {
                var found = -1;
                for (var i = at; i < candidate.Length; i++)
                {
                    if (char.ToLowerInvariant(candidate[i]) == char.ToLowerInvariant(wanted))
                    {
                        found = i;
                        break;
                    }
                }

                if (found < 0)
                {
                    return false;
                }

                if (first < 0)
                {
                    first = found;
                }
                else
                {
                    gaps += found - at;
                }

                at = found + 1;
            }

            // Lower is better: how late the match starts, plus how spread out it is, plus the
            // unmatched tail, so "mdt" prefers "masterdata" over a long unrelated word.
            score = first + gaps + (candidate.Length - typed.Length);
            return true;
        }

        private readonly struct ScoredMatch
        {
            public ScoredMatch(string text, int score)
            {
                Text = text;
                Score = score;
            }

            public string Text { get; }

            public int Score { get; }
        }
    }
}
