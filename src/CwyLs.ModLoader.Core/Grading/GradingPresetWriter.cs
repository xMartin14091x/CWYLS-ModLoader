using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace CwyLs.ModLoader.Core.Grading
{
    /// <summary>
    /// Writes a preset as the JSON <see cref="GradingPresetParser"/> reads: two-space indented,
    /// groups in Lightroom's panel order, only the controls that were set, numbers with at most four
    /// decimals. The output is a fixed point: parsing it and writing again gives the same text.
    /// </summary>
    public static class GradingPresetWriter
    {
        /// <summary>Writes the preset, blocks included.</summary>
        /// <param name="preset">The preset.</param>
        /// <exception cref="ArgumentNullException"><paramref name="preset"/> is null.</exception>
        public static string Write(GradingPreset preset)
        {
            if (preset is null)
            {
                throw new ArgumentNullException(nameof(preset));
            }

            var root = new Node();
            root.Scalars.Add(new KeyValuePair<string, string>(
                GradingPresetParser.SchemaKey, GradingPreset.CurrentSchema.ToString(CultureInfo.InvariantCulture)));

            if (preset.Name.Length > 0)
            {
                root.Scalars.Add(new KeyValuePair<string, string>(GradingPresetParser.NameKey, MiniJson.String(preset.Name)));
            }

            if (preset.Description.Length > 0)
            {
                root.Scalars.Add(new KeyValuePair<string, string>(
                    GradingPresetParser.DescriptionKey, MiniJson.String(preset.Description)));
            }

            Fill(root, preset);

            foreach (var timeOfDay in GradingTimesOfDay.Known)
            {
                if (preset.Blocks.TryGetValue(timeOfDay, out var block))
                {
                    var node = new Node();
                    Fill(node, block);
                    root.Children.Add(new KeyValuePair<string, Node>(GradingTimesOfDay.BlockName(timeOfDay), node));
                }
            }

            var sb = new StringBuilder(1024);
            root.WriteTo(sb, 0);
            sb.Append('\n');
            return sb.ToString();
        }

        /// <summary>A number as written to a preset: invariant, at most four decimals, no exponent.</summary>
        /// <param name="value">The value.</param>
        public static string Number(double value)
        {
            var rounded = Math.Round(value, 4);
            if (rounded == 0)
            {
                rounded = 0; // no "-0"
            }

            return rounded.ToString("0.####", CultureInfo.InvariantCulture);
        }

        private static void Fill(Node root, GradingPreset preset)
        {
            // Controls in panel order; the control table's order is the panel order.
            foreach (var control in GradingControls.All)
            {
                if (!preset.Values.TryGetValue(control.Id, out var value))
                {
                    continue;
                }

                root.Set(control.Id.Split('.'), Number(value));
            }

            if (preset.Curves.Count > 0)
            {
                var curves = root.Child(GradingPresetParser.ToneCurveKey);
                foreach (GradingCurveChannel channel in Enum.GetValues(typeof(GradingCurveChannel)))
                {
                    var points = preset.Curve(channel);
                    if (points is null)
                    {
                        continue;
                    }

                    var sb = new StringBuilder("[");
                    for (var i = 0; i < points.Count; i++)
                    {
                        if (i > 0)
                        {
                            sb.Append(", ");
                        }

                        sb.Append('[').Append(Number(points[i].X)).Append(", ").Append(Number(points[i].Y)).Append(']');
                    }

                    sb.Append(']');
                    curves.Scalars.Add(new KeyValuePair<string, string>(GradingCurves.NameOf(channel), sb.ToString()));
                }
            }

            if (preset.LutFile != null)
            {
                var lut = root.Child(GradingPresetParser.LutKey);
                lut.Scalars.Insert(0, new KeyValuePair<string, string>(GradingPresetParser.LutFileKey, MiniJson.String(preset.LutFile)));
            }
        }

        /// <summary>An ordered JSON object: scalar members first (in insertion order), then child objects.</summary>
        private sealed class Node
        {
            internal List<KeyValuePair<string, string>> Scalars { get; } = new List<KeyValuePair<string, string>>();

            internal List<KeyValuePair<string, Node>> Children { get; } = new List<KeyValuePair<string, Node>>();

            internal Node Child(string key)
            {
                foreach (var pair in Children)
                {
                    if (pair.Key == key)
                    {
                        return pair.Value;
                    }
                }

                var node = new Node();
                Children.Add(new KeyValuePair<string, Node>(key, node));
                return node;
            }

            internal void Set(string[] path, string json)
            {
                var node = this;
                for (var i = 0; i < path.Length - 1; i++)
                {
                    node = node.Child(path[i]);
                }

                node.Scalars.Add(new KeyValuePair<string, string>(path[path.Length - 1], json));
            }

            internal void WriteTo(StringBuilder sb, int depth)
            {
                var pad = new string(' ', (depth + 1) * 2);
                sb.Append('{');
                var first = true;

                foreach (var pair in Scalars)
                {
                    sb.Append(first ? "\n" : ",\n").Append(pad).Append(MiniJson.String(pair.Key)).Append(": ").Append(pair.Value);
                    first = false;
                }

                foreach (var pair in Children)
                {
                    sb.Append(first ? "\n" : ",\n").Append(pad).Append(MiniJson.String(pair.Key)).Append(": ");
                    pair.Value.WriteTo(sb, depth + 1);
                    first = false;
                }

                sb.Append(first ? string.Empty : "\n").Append(first ? string.Empty : new string(' ', depth * 2)).Append('}');
            }
        }
    }
}
