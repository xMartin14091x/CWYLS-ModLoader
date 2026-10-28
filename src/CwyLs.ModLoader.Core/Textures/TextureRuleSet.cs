using System;
using System.Collections.Generic;
using System.Globalization;

namespace CwyLs.ModLoader.Core.Textures
{
    /// <summary>
    /// An ordered, conflict-free set of <see cref="TextureRule"/>s, plus the files that could not
    /// become one.
    /// <para>
    /// "Conflict-free" is the whole point: two packs may both ship <c>textures/Body/_MainTex.png</c>,
    /// and exactly one of them can win. <see cref="Merge"/> settles that by load order - the later
    /// pack overrides - and the loser is still visible in <see cref="Overridden"/> so a log line can
    /// say which pack lost and to whom, instead of a modder wondering why their texture does nothing.
    /// </para>
    /// <para>
    /// The winner keeps the loser's <em>position</em> rather than moving to the end, so adding a
    /// pack that overrides one texture does not reshuffle every <c>TEXTURE</c> line in the log.
    /// </para>
    /// </summary>
    public sealed class TextureRuleSet
    {
        private static readonly TextureRule[] NoRules = new TextureRule[0];
        private static readonly TextureRuleProblem[] NoProblems = new TextureRuleProblem[0];

        /// <summary>Creates a set. The rules must already be conflict-free; use <see cref="Merge"/> otherwise.</summary>
        /// <param name="rules">The rules, in apply order. Null means none.</param>
        /// <param name="problems">Files that were rejected. Null means none.</param>
        /// <param name="overridden">Rules that lost to a later pack. Null means none.</param>
        public TextureRuleSet(
            IReadOnlyList<TextureRule>? rules,
            IReadOnlyList<TextureRuleProblem>? problems = null,
            IReadOnlyList<TextureRule>? overridden = null)
        {
            Rules = rules ?? NoRules;
            Problems = problems ?? NoProblems;
            Overridden = overridden ?? NoRules;
        }

        /// <summary>A set with nothing in it.</summary>
        public static TextureRuleSet Empty { get; } = new TextureRuleSet(NoRules, NoProblems, NoRules);

        /// <summary>The rules, in the order they should be applied. Never null.</summary>
        public IReadOnlyList<TextureRule> Rules { get; }

        /// <summary>Files that could not become a rule, in the order they were read. Never null.</summary>
        public IReadOnlyList<TextureRuleProblem> Problems { get; }

        /// <summary>
        /// Rules that were replaced by a later pack's rule for the same <see cref="TextureRule.Key"/>.
        /// Never null; always empty for a set that came straight out of one pack.
        /// </summary>
        public IReadOnlyList<TextureRule> Overridden { get; }

        /// <summary>How many rules will be applied.</summary>
        public int Count => Rules.Count;

        /// <summary>True when there is nothing to apply and nothing to complain about.</summary>
        public bool IsEmpty => Rules.Count == 0 && Problems.Count == 0;

        /// <summary>
        /// Merges per-pack rule sets into one, in load order: for a repeated
        /// <see cref="TextureRule.Key"/> the <em>last</em> set wins and the earlier rule moves to
        /// <see cref="Overridden"/>. Problems from every set are kept, in order.
        /// </summary>
        /// <param name="orderedPacks">
        /// One set per pack, in dependency/load order - the order <c>PackManager.Loaded</c> gives.
        /// Null entries are skipped; null itself yields <see cref="Empty"/>.
        /// </param>
        public static TextureRuleSet Merge(IEnumerable<TextureRuleSet?>? orderedPacks)
        {
            if (orderedPacks is null)
            {
                return Empty;
            }

            var rules = new List<TextureRule>();
            var problems = new List<TextureRuleProblem>();
            var overridden = new List<TextureRule>();
            var positions = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (var set in orderedPacks)
            {
                if (set is null)
                {
                    continue;
                }

                foreach (var problem in set.Problems)
                {
                    problems.Add(problem);
                }

                foreach (var loser in set.Overridden)
                {
                    overridden.Add(loser);
                }

                foreach (var rule in set.Rules)
                {
                    if (positions.TryGetValue(rule.Key, out var at))
                    {
                        // Keep the position, swap the rule: a later pack overriding one texture
                        // must not reorder the rest of the apply log.
                        overridden.Add(rules[at]);
                        rules[at] = rule;
                        continue;
                    }

                    positions.Add(rule.Key, rules.Count);
                    rules.Add(rule);
                }
            }

            return new TextureRuleSet(rules, problems, overridden);
        }

        /// <summary>Merges sets given as arguments; see <see cref="Merge(IEnumerable{TextureRuleSet})"/>.</summary>
        /// <param name="orderedPacks">One set per pack, in load order.</param>
        public static TextureRuleSet Merge(params TextureRuleSet?[]? orderedPacks) =>
            Merge((IEnumerable<TextureRuleSet?>?)orderedPacks);

        /// <summary>Every rule targeting one renderer, in apply order. Never null.</summary>
        /// <param name="rendererName">A renderer GameObject name; matched case-insensitively.</param>
        public IReadOnlyList<TextureRule> ForRenderer(string? rendererName)
        {
            if (string.IsNullOrWhiteSpace(rendererName))
            {
                return NoRules;
            }

            var matches = new List<TextureRule>();
            foreach (var rule in Rules)
            {
                if (rule.Target == TextureRuleTarget.Renderer &&
                    string.Equals(rule.TargetName, rendererName, StringComparison.OrdinalIgnoreCase))
                {
                    matches.Add(rule);
                }
            }

            return matches;
        }

        /// <summary>The distinct renderer names the rules mention, in first-seen order. Never null.</summary>
        public IReadOnlyList<string> RendererNames => NamesOf(TextureRuleTarget.Renderer);

        /// <summary>The distinct material names the rules mention, in first-seen order. Never null.</summary>
        public IReadOnlyList<string> MaterialNames => NamesOf(TextureRuleTarget.Material);

        private IReadOnlyList<string> NamesOf(TextureRuleTarget target)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var names = new List<string>();

            foreach (var rule in Rules)
            {
                if (rule.Target == target && seen.Add(rule.TargetName))
                {
                    names.Add(rule.TargetName);
                }
            }

            return names;
        }

        /// <summary>
        /// One line for LogOutput.log, e.g.
        /// <c>7 rule(s) over 3 renderer(s); 1 overridden; 2 problem(s)</c>.
        /// </summary>
        /// <remarks>Invariant culture, so the string is the same on every machine and in every test.</remarks>
        public string Summarize() =>
            string.Format(
                CultureInfo.InvariantCulture,
                "{0} rule(s) over {1} renderer(s) and {4} material(s); {2} overridden; {3} problem(s)",
                Rules.Count,
                RendererNames.Count,
                Overridden.Count,
                Problems.Count,
                MaterialNames.Count);

        /// <inheritdoc />
        public override string ToString() => Summarize();
    }
}
