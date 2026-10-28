using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace CwyLs.ModLoader.Core.Actions
{
    /// <summary>The JSON type an action parameter takes (it maps onto the MCP tool's input schema).</summary>
    public enum ActionValueKind
    {
        /// <summary>Text.</summary>
        String = 0,

        /// <summary>A whole number (64-bit).</summary>
        Integer = 1,

        /// <summary>A number.</summary>
        Number = 2,

        /// <summary><c>true</c> or <c>false</c>.</summary>
        Boolean = 3,
    }

    /// <summary>
    /// One named argument of an action: its type, whether it is required, allowed values, numeric
    /// bounds and whether its value is private. Immutable. Everything here maps onto a JSON-schema
    /// property (<see cref="ActionToolSchema"/>), and the runner checks arguments against it before
    /// anything is gated or run (<see cref="ActionArgsValidation"/>).
    /// </summary>
    public sealed class ActionParameter
    {
        /// <summary>The pattern of a parameter name: a letter, then letters, digits or <c>_</c>, up to 32.</summary>
        public const string NamePattern = "^[A-Za-z][A-Za-z0-9_]{0,31}$";

        private static readonly Regex NameRegex =
            new Regex(@"\A[A-Za-z][A-Za-z0-9_]{0,31}\z", RegexOptions.CultureInvariant);

        /// <summary>Creates a parameter.</summary>
        /// <param name="name">The argument name, e.g. <c>title</c>. Compared case-insensitively.</param>
        /// <param name="kind">Its type.</param>
        /// <param name="required">True when the action cannot run without it.</param>
        /// <param name="description">One sentence for <c>act list</c>, the API catalog and MCP.</param>
        /// <param name="allowedValues">For a <see cref="ActionValueKind.String"/>: the only accepted values (case-insensitive); null for any.</param>
        /// <param name="isPrivate">
        /// True when the value is the player's own text (a todo title, a note body): the <c>ACTION</c>
        /// audit line and every log show only its length (privacy rule). String arguments of
        /// <see cref="ActionTier.Write"/> actions are treated as private even without this flag.
        /// </param>
        /// <param name="minimum">For <see cref="ActionValueKind.Integer"/> or <see cref="ActionValueKind.Number"/>: the smallest accepted value (inclusive); null for none.</param>
        /// <param name="maximum">For <see cref="ActionValueKind.Integer"/> or <see cref="ActionValueKind.Number"/>: the largest accepted value (inclusive); null for none.</param>
        /// <exception cref="ArgumentException">A name, description, allowed-value list or bound is unusable.</exception>
        public ActionParameter(
            string name,
            ActionValueKind kind,
            bool required,
            string description,
            IEnumerable<string>? allowedValues = null,
            bool isPrivate = false,
            double? minimum = null,
            double? maximum = null)
        {
            if (name is null || !NameRegex.IsMatch(name))
            {
                throw new ArgumentException("\"" + name + "\" is not a parameter name (" + NamePattern + ")", nameof(name));
            }

            if (!Enum.IsDefined(typeof(ActionValueKind), kind))
            {
                throw new ArgumentException("unknown value kind " + (int)kind, nameof(kind));
            }

            if (string.IsNullOrWhiteSpace(description))
            {
                throw new ArgumentException("a parameter needs a one-sentence description", nameof(description));
            }

            var allowed = new List<string>();
            if (allowedValues != null)
            {
                if (kind != ActionValueKind.String)
                {
                    throw new ArgumentException("only a String parameter has allowed values", nameof(allowedValues));
                }

                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var value in allowedValues)
                {
                    if (string.IsNullOrWhiteSpace(value) || !seen.Add(value))
                    {
                        throw new ArgumentException("allowed values must be distinct and not blank", nameof(allowedValues));
                    }

                    allowed.Add(value);
                }

                if (allowed.Count == 0)
                {
                    throw new ArgumentException("an allowed-value list cannot be empty; pass null for any value", nameof(allowedValues));
                }
            }

            if (minimum.HasValue || maximum.HasValue)
            {
                if (kind != ActionValueKind.Integer && kind != ActionValueKind.Number)
                {
                    throw new ArgumentException("only an Integer or Number parameter has bounds", nameof(minimum));
                }

                if ((minimum.HasValue && !IsFinite(minimum.Value)) || (maximum.HasValue && !IsFinite(maximum.Value)))
                {
                    throw new ArgumentException("bounds must be finite numbers", nameof(minimum));
                }

                if (minimum.HasValue && maximum.HasValue && minimum.Value > maximum.Value)
                {
                    throw new ArgumentException("the minimum is above the maximum", nameof(minimum));
                }
            }

            Name = name;
            Kind = kind;
            Required = required;
            Description = description.Trim();
            AllowedValues = allowed;
            IsPrivate = isPrivate;
            Minimum = minimum;
            Maximum = maximum;
        }

        /// <summary>The argument name.</summary>
        public string Name { get; }

        /// <summary>Its type.</summary>
        public ActionValueKind Kind { get; }

        /// <summary>True when the action cannot run without it.</summary>
        public bool Required { get; }

        /// <summary>One sentence for people and for MCP.</summary>
        public string Description { get; }

        /// <summary>The only accepted values; empty means any.</summary>
        public IReadOnlyList<string> AllowedValues { get; }

        /// <summary>True when the value is the player's own text and is never written to a log (only its length).</summary>
        public bool IsPrivate { get; }

        /// <summary>The smallest accepted value (inclusive), or null.</summary>
        public double? Minimum { get; }

        /// <summary>The largest accepted value (inclusive), or null.</summary>
        public double? Maximum { get; }

        /// <summary><c>title:String (required)</c>.</summary>
        public override string ToString() => Name + ":" + Kind + (Required ? " (required)" : string.Empty);

        private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
