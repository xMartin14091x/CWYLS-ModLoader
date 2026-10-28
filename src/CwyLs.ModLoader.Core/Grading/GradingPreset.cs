using System;
using System.Collections.Generic;

namespace CwyLs.ModLoader.Core.Grading
{
    /// <summary>
    /// A grading preset: the Lightroom-style slider values, tone curves and LUT file of one
    /// <c>grading/&lt;name&gt;.json</c>, plus the optional per-time-of-day blocks (<c>day</c>,
    /// <c>sunset</c>, <c>night</c>, <c>cloudy</c>) that override it while the game shows that look.
    /// <para>
    /// Immutable: every change returns a new preset, so the value a console command is editing and
    /// the value being rendered can never be half-updated. Only controls that were set are stored;
    /// an absent control means "leave the game alone", which is different from a control set to
    /// its neutral value only in that it is not written back by <see cref="GradingPresetWriter"/>.
    /// </para>
    /// </summary>
    public sealed class GradingPreset
    {
        /// <summary>The schema this build reads and writes.</summary>
        public const int CurrentSchema = 1;

        private static readonly Dictionary<string, double> NoValues = new Dictionary<string, double>(StringComparer.Ordinal);
        private static readonly Dictionary<GradingCurveChannel, IReadOnlyList<GradingCurvePoint>> NoCurves =
            new Dictionary<GradingCurveChannel, IReadOnlyList<GradingCurvePoint>>();
        private static readonly Dictionary<GradingTimeOfDay, GradingPreset> NoBlocks =
            new Dictionary<GradingTimeOfDay, GradingPreset>();

        private readonly Dictionary<string, double> _values;
        private readonly Dictionary<GradingCurveChannel, IReadOnlyList<GradingCurvePoint>> _curves;
        private readonly Dictionary<GradingTimeOfDay, GradingPreset> _blocks;

        private GradingPreset(
            string name,
            string description,
            Dictionary<string, double> values,
            Dictionary<GradingCurveChannel, IReadOnlyList<GradingCurvePoint>> curves,
            string? lutFile,
            Dictionary<GradingTimeOfDay, GradingPreset> blocks)
        {
            Name = name ?? string.Empty;
            Description = description ?? string.Empty;
            _values = values;
            _curves = curves;
            LutFile = string.IsNullOrWhiteSpace(lutFile) ? null : lutFile;
            _blocks = blocks;
        }

        /// <summary>A preset that sets nothing: the game's own look.</summary>
        public static GradingPreset Neutral { get; } =
            new GradingPreset(string.Empty, string.Empty, NoValues, NoCurves, null, NoBlocks);

        /// <summary>The preset's display name (the JSON <c>name</c>); may be empty.</summary>
        public string Name { get; }

        /// <summary>A sentence for people (the JSON <c>description</c>); may be empty.</summary>
        public string Description { get; }

        /// <summary>The controls that were set, by canonical id. Never null.</summary>
        public IReadOnlyDictionary<string, double> Values => _values;

        /// <summary>The tone curves that were set. Never null.</summary>
        public IReadOnlyDictionary<GradingCurveChannel, IReadOnlyList<GradingCurvePoint>> Curves => _curves;

        /// <summary>
        /// Path of a <c>.cube</c> or strip <c>.png</c> LUT, relative to the preset file's folder; null
        /// when the preset has none.
        /// </summary>
        public string? LutFile { get; }

        /// <summary>The per-time-of-day blocks. Never null; a block never has blocks of its own.</summary>
        public IReadOnlyDictionary<GradingTimeOfDay, GradingPreset> Blocks => _blocks;

        /// <summary>True when the preset sets nothing at all, blocks included.</summary>
        public bool IsEmpty => _values.Count == 0 && _curves.Count == 0 && LutFile is null && _blocks.Count == 0;

        /// <summary>The blend weight, 0..1; 1 when not set.</summary>
        public double BlendWeight => Get(GradingControls.Blend);

        /// <summary>A control's value, or its neutral value when it was not set.</summary>
        /// <param name="id">Canonical id.</param>
        /// <exception cref="KeyNotFoundException">No control has that id.</exception>
        public double Get(string id) =>
            _values.TryGetValue(id, out var value) ? value : GradingControls.Get(id).Neutral;

        /// <summary>True when the control was set (to any value, neutral included).</summary>
        /// <param name="id">Canonical id.</param>
        public bool Has(string id) => _values.ContainsKey(id);

        /// <summary>The points of one curve, or null when it was not set.</summary>
        /// <param name="channel">The channel.</param>
        public IReadOnlyList<GradingCurvePoint>? Curve(GradingCurveChannel channel) =>
            _curves.TryGetValue(channel, out var points) ? points : null;

        /// <summary>A copy with another name and description.</summary>
        /// <param name="name">The name.</param>
        /// <param name="description">The description; null keeps the current one.</param>
        public GradingPreset WithName(string name, string? description = null) =>
            new GradingPreset(name, description ?? Description, _values, _curves, LutFile, _blocks);

        /// <summary>
        /// A copy with one control set, clamped (an angle wrapped) into the control's range.
        /// </summary>
        /// <param name="id">Canonical id.</param>
        /// <param name="value">The value; must be finite.</param>
        /// <exception cref="KeyNotFoundException">No control has that id.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="value"/> is not finite.</exception>
        public GradingPreset WithValue(string id, double value)
        {
            var control = GradingControls.Get(id);
            var values = new Dictionary<string, double>(_values, StringComparer.Ordinal)
            {
                [control.Id] = control.Clamp(value, out _),
            };

            return new GradingPreset(Name, Description, values, _curves, LutFile, _blocks);
        }

        /// <summary>A copy without one control.</summary>
        /// <param name="id">Canonical id.</param>
        public GradingPreset WithoutValue(string id)
        {
            if (!_values.ContainsKey(id))
            {
                return this;
            }

            var values = new Dictionary<string, double>(_values, StringComparer.Ordinal);
            values.Remove(id);
            return new GradingPreset(Name, Description, values, _curves, LutFile, _blocks);
        }

        /// <summary>A copy with one curve set (sorted by input), or removed when null.</summary>
        /// <param name="channel">The channel.</param>
        /// <param name="points">The points; null removes the curve.</param>
        public GradingPreset WithCurve(GradingCurveChannel channel, IReadOnlyList<GradingCurvePoint>? points)
        {
            var curves = new Dictionary<GradingCurveChannel, IReadOnlyList<GradingCurvePoint>>(_curves);
            if (points is null)
            {
                curves.Remove(channel);
            }
            else
            {
                var sorted = new List<GradingCurvePoint>(points);
                sorted.Sort((a, b) => a.X.CompareTo(b.X));
                curves[channel] = sorted;
            }

            return new GradingPreset(Name, Description, _values, curves, LutFile, _blocks);
        }

        /// <summary>A copy with another LUT file (null removes it).</summary>
        /// <param name="lutFile">Path relative to the preset file's folder.</param>
        public GradingPreset WithLutFile(string? lutFile) =>
            new GradingPreset(Name, Description, _values, _curves, lutFile, _blocks);

        /// <summary>A copy with a time-of-day block set, or removed when null.</summary>
        /// <param name="timeOfDay">The look; <see cref="GradingTimeOfDay.Unknown"/> is refused.</param>
        /// <param name="block">The block; its own blocks are dropped. Null removes the block.</param>
        /// <exception cref="ArgumentException"><paramref name="timeOfDay"/> is Unknown.</exception>
        public GradingPreset WithBlock(GradingTimeOfDay timeOfDay, GradingPreset? block)
        {
            if (timeOfDay == GradingTimeOfDay.Unknown)
            {
                throw new ArgumentException("a block needs a known time of day", nameof(timeOfDay));
            }

            var blocks = new Dictionary<GradingTimeOfDay, GradingPreset>(_blocks);
            if (block is null)
            {
                blocks.Remove(timeOfDay);
            }
            else
            {
                blocks[timeOfDay] = block.WithoutBlocks();
            }

            return new GradingPreset(Name, Description, _values, _curves, LutFile, blocks);
        }

        /// <summary>The same preset with no time-of-day blocks.</summary>
        public GradingPreset WithoutBlocks() =>
            _blocks.Count == 0 ? this : new GradingPreset(Name, Description, _values, _curves, LutFile, NoBlocks);

        /// <summary>
        /// The flat preset in force while <paramref name="timeOfDay"/> is the game's look: this
        /// preset with the matching block laid over it (a control, a curve channel or the LUT file the
        /// block sets replaces the base one). Without a matching block, the base alone. The result
        /// has no blocks.
        /// </summary>
        /// <param name="timeOfDay">The game's current look.</param>
        public GradingPreset Resolve(GradingTimeOfDay timeOfDay)
        {
            var flat = WithoutBlocks();
            return _blocks.TryGetValue(timeOfDay, out var block) ? Overlay(flat, block) : flat;
        }

        /// <summary>
        /// <paramref name="top"/> laid over <paramref name="bottom"/>: every control, curve channel and
        /// LUT file <paramref name="top"/> sets wins. Name and blocks come from <paramref name="bottom"/>.
        /// </summary>
        /// <param name="bottom">The base.</param>
        /// <param name="top">What overrides it.</param>
        public static GradingPreset Overlay(GradingPreset bottom, GradingPreset top)
        {
            if (bottom is null)
            {
                throw new ArgumentNullException(nameof(bottom));
            }

            if (top is null)
            {
                throw new ArgumentNullException(nameof(top));
            }

            var values = new Dictionary<string, double>(bottom._values, StringComparer.Ordinal);
            foreach (var pair in top._values)
            {
                values[pair.Key] = pair.Value;
            }

            var curves = new Dictionary<GradingCurveChannel, IReadOnlyList<GradingCurvePoint>>(bottom._curves);
            foreach (var pair in top._curves)
            {
                curves[pair.Key] = pair.Value;
            }

            return new GradingPreset(
                bottom.Name, bottom.Description, values, curves, top.LutFile ?? bottom.LutFile, bottom._blocks);
        }

        /// <summary>
        /// Interpolates two flat presets. A strength control goes from its value in
        /// <paramref name="from"/> (neutral when unset) to its value in <paramref name="to"/>; a shape
        /// control (hue angle, vignette midpoint, grain size) takes <paramref name="to"/>'s value when
        /// <paramref name="to"/> sets it, else <paramref name="from"/>'s; hue angles take the shorter
        /// way round when both set them. Curves are sampled and blended; the LUT amount blends and the
        /// LUT file is <paramref name="to"/>'s when <c>t &gt; 0</c>.
        /// </summary>
        /// <param name="from">Start; blocks are ignored.</param>
        /// <param name="to">End; blocks are ignored.</param>
        /// <param name="t">0 = <paramref name="from"/>, 1 = <paramref name="to"/>; clamped.</param>
        public static GradingPreset Lerp(GradingPreset from, GradingPreset to, double t)
        {
            if (from is null)
            {
                throw new ArgumentNullException(nameof(from));
            }

            if (to is null)
            {
                throw new ArgumentNullException(nameof(to));
            }

            var k = t < 0 ? 0 : t > 1 ? 1 : t;
            var values = new Dictionary<string, double>(StringComparer.Ordinal);
            var ids = new HashSet<string>(from._values.Keys, StringComparer.Ordinal);
            ids.UnionWith(to._values.Keys);

            foreach (var id in ids)
            {
                var control = GradingControls.Get(id);
                var hasFrom = from._values.TryGetValue(id, out var a);
                var hasTo = to._values.TryGetValue(id, out var b);

                if (control.Blend == GradingBlendMode.Strength || control.Blend == GradingBlendMode.Weight)
                {
                    var start = hasFrom ? a : control.Neutral;
                    var end = hasTo ? b : control.Neutral;
                    values[id] = start + (k * (end - start));
                    continue;
                }

                if (control.IsAngle && hasFrom && hasTo)
                {
                    var delta = ((b - a + 540.0) % 360.0) - 180.0;
                    values[id] = control.Clamp(a + (k * delta), out _);
                    continue;
                }

                values[id] = hasTo ? b : a;
            }

            var curves = new Dictionary<GradingCurveChannel, IReadOnlyList<GradingCurvePoint>>();
            foreach (GradingCurveChannel channel in Enum.GetValues(typeof(GradingCurveChannel)))
            {
                var start = from.Curve(channel);
                var end = to.Curve(channel);
                if (start is null && end is null)
                {
                    continue;
                }

                curves[channel] = LerpCurve(start, end, k);
            }

            var lut = k > 0 ? to.LutFile ?? from.LutFile : from.LutFile ?? to.LutFile;
            return new GradingPreset(to.Name, to.Description, values, curves, lut, NoBlocks);
        }

        /// <summary>
        /// This flat preset at its own <c>blend</c> weight: every strength control, curve and the LUT
        /// amount moved that far from the game's look toward the preset. The result has
        /// <c>blend</c> removed (it is now baked in) and no blocks.
        /// </summary>
        /// <remarks>
        /// Done here rather than with the URP volume's own weight because URP does not interpolate
        /// curves, LUT textures, enums or switches: a volume at weight 0.5 would apply the tone curve,
        /// the LUT and the grain type at full strength. Blending the sliders blends everything.
        /// </remarks>
        public GradingPreset ApplyBlend()
        {
            var flat = WithoutBlocks();
            var weight = flat.BlendWeight;
            var blended = Math.Abs(weight - 1.0) < 1e-12 ? flat : Lerp(Neutral, flat, weight);
            return blended.WithoutValue(GradingControls.Blend).WithName(Name, Description);
        }

        /// <summary>True when both presets set the same things to the same values (within 1e-6).</summary>
        /// <param name="other">The other preset.</param>
        public bool SameAs(GradingPreset? other)
        {
            if (other is null || Name != other.Name || Description != other.Description ||
                LutFile != other.LutFile || _values.Count != other._values.Count ||
                _curves.Count != other._curves.Count || _blocks.Count != other._blocks.Count)
            {
                return false;
            }

            foreach (var pair in _values)
            {
                if (!other._values.TryGetValue(pair.Key, out var value) || Math.Abs(value - pair.Value) > 1e-6)
                {
                    return false;
                }
            }

            foreach (var pair in _curves)
            {
                if (!other._curves.TryGetValue(pair.Key, out var points) || !GradingCurves.SamePoints(pair.Value, points))
                {
                    return false;
                }
            }

            foreach (var pair in _blocks)
            {
                if (!other._blocks.TryGetValue(pair.Key, out var block) || !pair.Value.SameAs(block))
                {
                    return false;
                }
            }

            return true;
        }

        /// <inheritdoc />
        public override string ToString() =>
            (Name.Length == 0 ? "(unnamed)" : Name) + ": " + _values.Count + " control(s), " +
            _curves.Count + " curve(s)" + (LutFile is null ? string.Empty : ", lut " + LutFile) +
            (_blocks.Count == 0 ? string.Empty : ", " + _blocks.Count + " time-of-day block(s)");

        private static IReadOnlyList<GradingCurvePoint> LerpCurve(
            IReadOnlyList<GradingCurvePoint>? from, IReadOnlyList<GradingCurvePoint>? to, double t)
        {
            if (from is null)
            {
                return GradingCurves.TowardIdentity(to!, t);
            }

            if (to is null)
            {
                return GradingCurves.TowardIdentity(from, 1 - t);
            }

            // Both set: sample both at the union of their inputs and blend the outputs.
            var xs = new SortedSet<double>();
            foreach (var p in from)
            {
                xs.Add(p.X);
            }

            foreach (var p in to)
            {
                xs.Add(p.X);
            }

            var fa = GradingCurves.AsFunction(from);
            var fb = GradingCurves.AsFunction(to);
            var points = new List<GradingCurvePoint>(xs.Count);
            foreach (var x in xs)
            {
                var v = x / GradingCurvePoint.Max;
                var y = fa(v) + (t * (fb(v) - fa(v)));
                points.Add(new GradingCurvePoint(x, y * GradingCurvePoint.Max));
            }

            return points;
        }

        /// <summary>Creates presets for the parser and the tests; validates nothing.</summary>
        internal static GradingPreset Create(
            string name,
            string description,
            IDictionary<string, double>? values,
            IDictionary<GradingCurveChannel, IReadOnlyList<GradingCurvePoint>>? curves,
            string? lutFile,
            IDictionary<GradingTimeOfDay, GradingPreset>? blocks) =>
            new GradingPreset(
                name,
                description,
                values is null ? new Dictionary<string, double>(StringComparer.Ordinal) : new Dictionary<string, double>(values, StringComparer.Ordinal),
                curves is null
                    ? new Dictionary<GradingCurveChannel, IReadOnlyList<GradingCurvePoint>>()
                    : new Dictionary<GradingCurveChannel, IReadOnlyList<GradingCurvePoint>>(curves),
                lutFile,
                blocks is null
                    ? new Dictionary<GradingTimeOfDay, GradingPreset>()
                    : new Dictionary<GradingTimeOfDay, GradingPreset>(blocks));
    }
}
