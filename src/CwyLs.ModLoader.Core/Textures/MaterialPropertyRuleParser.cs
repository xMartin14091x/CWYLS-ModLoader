using System;
using System.Collections.Generic;
using System.Globalization;
using CwyLs.ModLoader.Core.Grading;
using CwyLs.ModLoader.Core.Json;
using CwyLs.ModLoader.Core.Packs;

namespace CwyLs.ModLoader.Core.Textures
{
    /// <summary>
    /// Reads the material <em>property</em> entries of <c>textures/_rules.json</c> - the entries that
    /// carry a value instead of a <c>file</c>:
    /// <code>
    /// { "material": "mt_desk_1", "property": "_Color", "tint": "#E8C9A8" }
    /// { "material": "mt_env_*", "property": "_Color", "color": [0.9, 0.95, 1.0] }
    /// { "material": "mt_env_buil", "property": "_EmissionColor", "color": "#FFD9A0", "intensity": 2.5 }
    /// { "material": "mt_desk_1", "property": "_MainTexHSVG", "vector": [0.05, 1.1, 1, 1] }
    /// { "material": "mt_env_aurora", "property": "_Contrast", "float": 0.8 }
    /// </code>
    /// Texture entries in the same file are <see cref="TextureRuleParser"/>'s; each parser skips the
    /// other's entries in silence. A bad entry costs only that entry.
    /// </summary>
    public static class MaterialPropertyRuleParser
    {
        /// <summary>An absolute colour.</summary>
        public const string KeyColor = "color";

        /// <summary>A colour multiplied onto the original.</summary>
        public const string KeyTint = "tint";

        /// <summary>A float.</summary>
        public const string KeyFloat = "float";

        /// <summary>A vector.</summary>
        public const string KeyVector = "vector";

        /// <summary>HDR multiplier for <c>color</c>/<c>tint</c> (rgb only).</summary>
        public const string KeyIntensity = "intensity";

        private const string KeyMaterial = "material";
        private const string KeyRenderer = "renderer";
        private const string KeyProperty = "property";

        private static readonly string[] ValueKeys = { KeyColor, KeyTint, KeyFloat, KeyVector };

        private static readonly string[] KnownKeys = { KeyMaterial, KeyProperty, KeyColor, KeyTint, KeyFloat, KeyVector, KeyIntensity };

        private static readonly Dictionary<string, string> Aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["color"] = "_Color",
            ["maincolor"] = "_Color",
            ["basecolor"] = "_BaseColor",
            ["emission"] = "_EmissionColor",
            ["emissioncolor"] = "_EmissionColor",
            ["shadowcolor"] = "_ShadowColor",
            ["hsvg"] = "_MainTexHSVG",
        };

        private static readonly string[] ColorMirrorsOfColor = { "_BaseColor" };
        private static readonly string[] ColorMirrorsOfBaseColor = { "_Color" };
        private static readonly string[] NoMirrors = new string[0];

        /// <summary>True when a <c>_rules.json</c> entry carries a value member and so is a property rule.</summary>
        /// <param name="entry">One entry.</param>
        public static bool IsPropertyEntry(JsonObject? entry)
        {
            if (entry is null)
            {
                return false;
            }

            foreach (var key in ValueKeys)
            {
                if (entry.ContainsKey(key))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// The other properties that hold the same colour, written too where the material declares
        /// them: lilToon and URP materials mirror <c>_Color</c> into <c>_BaseColor</c> (every scenery
        /// and desk material in game 1.17.3 carries both with the same value).
        /// </summary>
        /// <param name="property">A resolved property.</param>
        public static IReadOnlyList<string> MirrorsOf(string? property)
        {
            if (string.Equals(property, "_Color", StringComparison.Ordinal))
            {
                return ColorMirrorsOfColor;
            }

            return string.Equals(property, "_BaseColor", StringComparison.Ordinal) ? ColorMirrorsOfBaseColor : NoMirrors;
        }

        /// <summary>Resolves a property name or alias (<c>color</c>, <c>emission</c>, <c>hsvg</c> ...).</summary>
        /// <param name="name">As written.</param>
        /// <param name="property">The shader property, or null.</param>
        /// <returns>True when usable.</returns>
        public static bool TryResolveProperty(string? name, out string? property)
        {
            property = null;
            if (string.IsNullOrWhiteSpace(name))
            {
                return false;
            }

            var text = name!.Trim();
            if (text[0] == '_')
            {
                foreach (var c in text)
                {
                    if (!(char.IsLetterOrDigit(c) || c == '_'))
                    {
                        return false;
                    }
                }

                property = text;
                return text.Length > 1;
            }

            return Aliases.TryGetValue(text, out property);
        }

        /// <summary>Reads every property entry of a <c>_rules.json</c>.</summary>
        /// <param name="packId">The pack's id.</param>
        /// <param name="json">The file's text; null or blank is an empty set.</param>
        /// <returns>The rules and a problem per rejected entry. Malformed JSON is reported by <see cref="TextureRuleParser"/>, not twice.</returns>
        /// <exception cref="ArgumentException"><paramref name="packId"/> is blank.</exception>
        public static MaterialPropertyRuleSet FromRulesJson(string packId, string? json)
        {
            if (string.IsNullOrWhiteSpace(packId))
            {
                throw new ArgumentException("a pack id is required", nameof(packId));
            }

            if (string.IsNullOrWhiteSpace(json) || !JsonReader.TryParse(json!, out var root, out _) || !(root is List<object?> entries))
            {
                return MaterialPropertyRuleSet.Empty;
            }

            var rules = new List<MaterialPropertyRule>();
            var problems = new List<TextureRuleProblem>();

            for (var i = 0; i < entries.Count; i++)
            {
                if (!(entries[i] is JsonObject entry) || !IsPropertyEntry(entry))
                {
                    continue;
                }

                var at = TextureRuleParser.RulesPath + "[" + i.ToString(CultureInfo.InvariantCulture) + "]";
                var rule = ReadEntry(packId, entry, at, out var reason);
                if (rule != null)
                {
                    rules.Add(rule);
                }
                else
                {
                    problems.Add(new TextureRuleProblem(packId, at, reason ?? "the entry is not understood"));
                }
            }

            // Within one pack, the later entry for the same target wins and the earlier is reported.
            var merged = MaterialPropertyRuleSet.Merge(new MaterialPropertyRuleSet(rules));
            foreach (var loser in merged.Overridden)
            {
                problems.Add(new TextureRuleProblem(
                    packId, loser.Entry, "a later entry in the same file also sets " + loser.Slot + "; this one is not used"));
            }

            return new MaterialPropertyRuleSet(merged.Rules, problems);
        }

        private static MaterialPropertyRule? ReadEntry(string packId, JsonObject entry, string at, out string? reason)
        {
            reason = null;

            foreach (var member in entry)
            {
                if (member.Key.StartsWith(PackManifestParser.ExtensionPrefix, StringComparison.Ordinal))
                {
                    continue;
                }

                if (member.Key == KeyRenderer)
                {
                    reason = "a property rule names a material (\"material\"), not a renderer: shared material assets " +
                             "are what scenery and desk objects have in common";
                    return null;
                }

                if (Array.IndexOf(KnownKeys, member.Key) < 0)
                {
                    reason = "unknown member \"" + member.Key + "\"; expected one of " + string.Join(", ", KnownKeys) +
                             " (prefix your own fields with \"" + PackManifestParser.ExtensionPrefix + "\")";
                    return null;
                }
            }

            var valueKey = string.Empty;
            foreach (var key in ValueKeys)
            {
                if (!entry.ContainsKey(key))
                {
                    continue;
                }

                if (valueKey.Length > 0)
                {
                    reason = "\"" + valueKey + "\" and \"" + key + "\" cannot both be set; give one value";
                    return null;
                }

                valueKey = key;
            }

            if (!(entry.TryGetValue(KeyMaterial, out var rawMaterial) && rawMaterial is string material && material.Trim().Length > 0))
            {
                reason = "\"material\" is required: a material name or a pattern such as mt_env_*";
                return null;
            }

            if (PaidContentGuard.IsRefused(material, out var paid))
            {
                reason = paid;
                return null;
            }

            if (!(entry.TryGetValue(KeyProperty, out var rawProperty) && rawProperty is string propertyText))
            {
                reason = "\"property\" is required: a shader property such as _Color, or an alias (color, emission, hsvg)";
                return null;
            }

            if (!TryResolveProperty(propertyText, out var property))
            {
                reason = "\"property\": \"" + propertyText + "\" is neither a shader property (starting with '_') nor an alias " +
                         "(" + string.Join(", ", new List<string>(Aliases.Keys)) + ")";
                return null;
            }

            var hasIntensity = entry.TryGetValue(KeyIntensity, out var rawIntensity);
            if (hasIntensity && valueKey != KeyColor && valueKey != KeyTint)
            {
                reason = "\"intensity\" only goes with \"color\" or \"tint\"";
                return null;
            }

            var intensity = 1.0;
            if (hasIntensity && (!TryNumber(rawIntensity, out intensity) || intensity < 0))
            {
                reason = "\"intensity\": expected a number of 0 or more";
                return null;
            }

            Float4 value;
            MaterialPropertyKind kind;
            switch (valueKey)
            {
                case KeyColor:
                case KeyTint:
                    if (!TryColor(entry[valueKey], out var color, out reason))
                    {
                        reason = "\"" + valueKey + "\": " + reason;
                        return null;
                    }

                    value = new Float4(color.X * intensity, color.Y * intensity, color.Z * intensity, color.W);
                    kind = valueKey == KeyColor ? MaterialPropertyKind.Color : MaterialPropertyKind.Tint;
                    break;

                case KeyFloat:
                    if (!TryNumber(entry[valueKey], out var number))
                    {
                        reason = "\"float\": expected a number, not " + JsonReader.TypeName(entry[valueKey]);
                        return null;
                    }

                    value = new Float4(number, 0, 0, 0);
                    kind = MaterialPropertyKind.Float;
                    break;

                default:
                    if (!TryVector(entry[valueKey], out value))
                    {
                        reason = "\"vector\": expected an array of 1 to 4 numbers";
                        return null;
                    }

                    kind = MaterialPropertyKind.Vector;
                    break;
            }

            return new MaterialPropertyRule(packId, material.Trim(), property!, kind, value, at);
        }

        /// <summary>
        /// Reads a colour: <c>"#RGB"</c>, <c>"#RRGGBB"</c>, <c>"#RRGGBBAA"</c>, or an array of three or
        /// four numbers (0 or more; above 1 is HDR). Alpha defaults to 1.
        /// </summary>
        /// <param name="raw">The JSON value.</param>
        /// <param name="color">The colour as (r, g, b, a).</param>
        /// <param name="problem">Why it is not a colour, or null.</param>
        /// <returns>True when read.</returns>
        public static bool TryColor(object? raw, out Float4 color, out string? problem)
        {
            color = default;
            problem = null;

            if (raw is string text)
            {
                var hex = text.Trim();
                if (hex.StartsWith("#", StringComparison.Ordinal))
                {
                    hex = hex.Substring(1);
                }

                if (hex.Length == 3)
                {
                    hex = new string(new[] { hex[0], hex[0], hex[1], hex[1], hex[2], hex[2] });
                }

                if ((hex.Length != 6 && hex.Length != 8) ||
                    !uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var bits))
                {
                    problem = "\"" + text + "\" is not a #RGB, #RRGGBB or #RRGGBBAA colour";
                    return false;
                }

                if (hex.Length == 6)
                {
                    bits = (bits << 8) | 0xFF;
                }

                color = new Float4(
                    ((bits >> 24) & 0xFF) / 255.0,
                    ((bits >> 16) & 0xFF) / 255.0,
                    ((bits >> 8) & 0xFF) / 255.0,
                    (bits & 0xFF) / 255.0);
                return true;
            }

            if (raw is List<object?> list && (list.Count == 3 || list.Count == 4))
            {
                var c = new double[] { 0, 0, 0, 1 };
                for (var i = 0; i < list.Count; i++)
                {
                    if (!TryNumber(list[i], out c[i]) || c[i] < 0)
                    {
                        problem = "colour components are numbers of 0 or more";
                        return false;
                    }
                }

                color = new Float4(c[0], c[1], c[2], c[3]);
                return true;
            }

            problem = "expected \"#RRGGBB\" or [r, g, b(, a)], not " + JsonReader.TypeName(raw);
            return false;
        }

        private static bool TryVector(object? raw, out Float4 vector)
        {
            vector = default;
            if (!(raw is List<object?> list) || list.Count < 1 || list.Count > 4)
            {
                return false;
            }

            var v = new double[4];
            for (var i = 0; i < list.Count; i++)
            {
                if (!TryNumber(list[i], out v[i]))
                {
                    return false;
                }
            }

            vector = new Float4(v[0], v[1], v[2], v[3]);
            return true;
        }

        private static bool TryNumber(object? raw, out double value)
        {
            switch (raw)
            {
                case long whole:
                    value = whole;
                    return true;
                case double real when !double.IsNaN(real) && !double.IsInfinity(real):
                    value = real;
                    return true;
                default:
                    value = 0;
                    return false;
            }
        }
    }
}
