using System;
using System.Collections.Generic;
using System.Globalization;
using CwyLs.ModLoader.Core.Grading;

namespace CwyLs.ModLoader.Core.Textures
{
    /// <summary>What a material property rule writes.</summary>
    public enum MaterialPropertyKind
    {
        /// <summary>A colour, as given (sRGB, as a colour picker shows it; HDR via <c>intensity</c>).</summary>
        Color = 0,

        /// <summary>A colour multiplied onto the material's original colour - a recolour that keeps the game's shading.</summary>
        Tint,

        /// <summary>A float or range property.</summary>
        Float,

        /// <summary>A vector property (x, y, z, w).</summary>
        Vector,
    }

    /// <summary>
    /// One material property change from <c>textures/_rules.json</c>: "set <c>_Color</c> on every
    /// loaded material named <c>mt_desk_1</c> to this colour". The scenery and desk counterpart of a
    /// <see cref="TextureRule"/>: written on the shared material asset in memory, backed up first,
    /// put back by <c>TextureFeature.RestoreAll</c>. No game file is ever written.
    /// </summary>
    public sealed class MaterialPropertyRule
    {
        /// <summary>Creates a rule.</summary>
        /// <param name="packId">The pack the rule came from.</param>
        /// <param name="materialPattern">Material name or wildcard pattern (<see cref="MaterialNamePattern"/>).</param>
        /// <param name="propertyName">Shader property, resolved from any alias (starts with <c>_</c>).</param>
        /// <param name="kind">What is written.</param>
        /// <param name="value">The colour/tint (r, g, b, a), the float (x) or the vector.</param>
        /// <param name="entry">Where it was declared, e.g. <c>textures/_rules.json[2]</c>.</param>
        /// <exception cref="ArgumentException">A string is blank or the property does not start with <c>_</c>.</exception>
        public MaterialPropertyRule(
            string packId, string materialPattern, string propertyName, MaterialPropertyKind kind, Float4 value, string entry)
        {
            if (string.IsNullOrWhiteSpace(packId))
            {
                throw new ArgumentException("a pack id is required", nameof(packId));
            }

            if (string.IsNullOrWhiteSpace(materialPattern))
            {
                throw new ArgumentException("a material name or pattern is required", nameof(materialPattern));
            }

            if (string.IsNullOrWhiteSpace(propertyName) || propertyName[0] != '_')
            {
                throw new ArgumentException("a shader property starting with '_' is required", nameof(propertyName));
            }

            PackId = packId;
            MaterialPattern = materialPattern.Trim();
            PropertyName = propertyName;
            Kind = kind;
            Value = value;
            Entry = string.IsNullOrWhiteSpace(entry) ? TextureRuleParser.RulesPath : entry;
        }

        /// <summary>The pack the rule came from.</summary>
        public string PackId { get; }

        /// <summary>Material name or wildcard pattern.</summary>
        public string MaterialPattern { get; }

        /// <summary>Shader property, e.g. <c>_Color</c>.</summary>
        public string PropertyName { get; }

        /// <summary>What is written.</summary>
        public MaterialPropertyKind Kind { get; }

        /// <summary>The value: colour/tint (r, g, b, a), float (x), vector (x, y, z, w).</summary>
        public Float4 Value { get; }

        /// <summary>Where it was declared, for the log.</summary>
        public string Entry { get; }

        /// <summary>
        /// What makes two rules the same target: pattern and property, case-folded. The later pack's
        /// rule wins (<see cref="MaterialPropertyRuleSet.Merge(IEnumerable{MaterialPropertyRuleSet})"/>).
        /// </summary>
        public string Key => TextureRule.MaterialPrefix + MaterialPattern.ToLowerInvariant() + "." + PropertyName.ToLowerInvariant();

        /// <summary>True when the rule applies to a material with this base name.</summary>
        /// <param name="materialName">A material name without the <c>" (Instance)"</c> suffix.</param>
        public bool Matches(string? materialName) => MaterialNamePattern.Matches(MaterialPattern, materialName);

        /// <summary>
        /// The value to write given what the material holds now: the colour, the vector, the float
        /// in X - or, for a tint, the original multiplied channel by channel (alpha too).
        /// </summary>
        /// <param name="original">The material's current value (colour or vector; a float in X).</param>
        public Float4 Compute(Float4 original) =>
            Kind == MaterialPropertyKind.Tint
                ? new Float4(original.X * Value.X, original.Y * Value.Y, original.Z * Value.Z, original.W * Value.W)
                : Value;

        /// <summary><c>@mt_desk_1._Color tint(0.8,0.6,0.5,1)</c>.</summary>
        public string Slot => TextureRule.MaterialPrefix + MaterialPattern + "." + PropertyName;

        /// <inheritdoc />
        public override string ToString() =>
            Slot + " " + Kind.ToString().ToLowerInvariant() + Format(Value) + " <- " + PackId + "/" + Entry;

        private string Format(Float4 v) =>
            Kind == MaterialPropertyKind.Float
                ? "(" + v.X.ToString("0.###", CultureInfo.InvariantCulture) + ")"
                : v.ToString();
    }

    /// <summary>
    /// An ordered, conflict-free set of <see cref="MaterialPropertyRule"/>s plus the entries that could
    /// not become one - the property-rule twin of <see cref="TextureRuleSet"/>.
    /// </summary>
    public sealed class MaterialPropertyRuleSet
    {
        private static readonly MaterialPropertyRule[] NoRules = new MaterialPropertyRule[0];
        private static readonly TextureRuleProblem[] NoProblems = new TextureRuleProblem[0];

        /// <summary>Creates a set; the rules must already be conflict-free.</summary>
        /// <param name="rules">Rules in apply order; null means none.</param>
        /// <param name="problems">Rejected entries; null means none.</param>
        /// <param name="overridden">Rules a later pack replaced; null means none.</param>
        public MaterialPropertyRuleSet(
            IReadOnlyList<MaterialPropertyRule>? rules,
            IReadOnlyList<TextureRuleProblem>? problems = null,
            IReadOnlyList<MaterialPropertyRule>? overridden = null)
        {
            Rules = rules ?? NoRules;
            Problems = problems ?? NoProblems;
            Overridden = overridden ?? NoRules;
        }

        /// <summary>A set with nothing in it.</summary>
        public static MaterialPropertyRuleSet Empty { get; } = new MaterialPropertyRuleSet(null);

        /// <summary>Rules in apply order. Never null.</summary>
        public IReadOnlyList<MaterialPropertyRule> Rules { get; }

        /// <summary>Entries that could not become a rule. Never null.</summary>
        public IReadOnlyList<TextureRuleProblem> Problems { get; }

        /// <summary>Rules replaced by a later pack's rule with the same key. Never null.</summary>
        public IReadOnlyList<MaterialPropertyRule> Overridden { get; }

        /// <summary>How many rules will be applied.</summary>
        public int Count => Rules.Count;

        /// <summary>
        /// Merges per-pack sets in load order; for a repeated <see cref="MaterialPropertyRule.Key"/>
        /// the later rule takes the earlier one's position and the earlier one moves to
        /// <see cref="Overridden"/>.
        /// </summary>
        /// <param name="orderedPacks">One set per pack in load order; null entries are skipped.</param>
        public static MaterialPropertyRuleSet Merge(IEnumerable<MaterialPropertyRuleSet?>? orderedPacks)
        {
            if (orderedPacks is null)
            {
                return Empty;
            }

            var rules = new List<MaterialPropertyRule>();
            var problems = new List<TextureRuleProblem>();
            var overridden = new List<MaterialPropertyRule>();
            var positions = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (var set in orderedPacks)
            {
                if (set is null)
                {
                    continue;
                }

                problems.AddRange(set.Problems);
                overridden.AddRange(set.Overridden);

                foreach (var rule in set.Rules)
                {
                    if (positions.TryGetValue(rule.Key, out var at))
                    {
                        overridden.Add(rules[at]);
                        rules[at] = rule;
                        continue;
                    }

                    positions.Add(rule.Key, rules.Count);
                    rules.Add(rule);
                }
            }

            return new MaterialPropertyRuleSet(rules, problems, overridden);
        }

        /// <summary>Merges sets given as arguments.</summary>
        /// <param name="orderedPacks">One set per pack, in load order.</param>
        public static MaterialPropertyRuleSet Merge(params MaterialPropertyRuleSet?[]? orderedPacks) =>
            Merge((IEnumerable<MaterialPropertyRuleSet?>?)orderedPacks);

        /// <summary><c>3 property rule(s); 0 overridden; 1 problem(s)</c>.</summary>
        public string Summarize() =>
            string.Format(
                CultureInfo.InvariantCulture,
                "{0} property rule(s); {1} overridden; {2} problem(s)",
                Rules.Count,
                Overridden.Count,
                Problems.Count);
    }
}
