using System;
using System.Collections.Generic;

namespace CwyLs.ModLoader.Core.Grading
{
    /// <summary>
    /// Every slider a grading preset can set, in Lightroom's panel order. A control's id is its
    /// JSON path in <c>grading/&lt;name&gt;.json</c> (<c>basic.exposure</c>,
    /// <c>hsl.blues.luminance</c>, <c>effects.vignette.amount</c>), and the name
    /// <c>grade set</c> accepts besides a short alias (<c>exposure</c>, <c>blues.luminance</c>).
    /// <para>
    /// Ranges follow Lightroom where the mapping allows: -100..100 for most sliders, -5..5 EV for
    /// exposure, 0..360 degrees for hues, 0..100 for saturations of tints. What each one maps to on
    /// the URP side is <see cref="GradingMapper"/>'s business and is tabled in
    /// <c>src/CwyLs.ModLoader.Bulbul/Grading/README.md</c>.
    /// </para>
    /// </summary>
    public static class GradingControls
    {
        /// <summary>Unit string of a hue angle; such a control wraps instead of clamping.</summary>
        public const string DegreesUnit = "deg";

        /// <summary>Unit string of an exposure value.</summary>
        public const string EvUnit = "EV";

        /// <summary>Id of the blend weight (0..1).</summary>
        public const string Blend = "blend";

        /// <summary>The eight HSL colour bands, in Lightroom's order.</summary>
        public static IReadOnlyList<string> HslBands { get; } =
            new[] { "reds", "oranges", "yellows", "greens", "aquas", "blues", "purples", "magentas" };

        /// <summary>The three properties of an HSL band.</summary>
        public static IReadOnlyList<string> HslProperties { get; } = new[] { "hue", "saturation", "luminance" };

        /// <summary>The four colour-grading wheels, in Lightroom's order.</summary>
        public static IReadOnlyList<string> Wheels { get; } = new[] { "shadows", "midtones", "highlights", "global" };

        private static readonly List<GradingControl> Ordered = Build();

        private static readonly Dictionary<string, GradingControl> ById = Index(Ordered, aliases: false);

        private static readonly Dictionary<string, GradingControl> ByAlias = Index(Ordered, aliases: true);

        private static readonly Dictionary<string, string> UnsupportedReasons =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["presence.clarity"] =
                    "clarity is local contrast (a large-radius unsharp mask); URP and Beautify have no faithful equivalent",
                ["presence.dehaze"] =
                    "dehaze is a local, haze-estimating operator; a global contrast/saturation guess would not be faithful",
                ["presence.texture"] =
                    "texture is mid-frequency local contrast; there is no faithful screen-wide equivalent",
                ["effects.grain.roughness"] =
                    "URP film grain has fixed grain textures; roughness has no equivalent (size picks the texture)",
                ["effects.vignette.highlights"] =
                    "URP's vignette multiplies the image; it cannot protect highlights",
                ["effects.vignette.style"] =
                    "URP's vignette has one style (colour priority)",
                ["colorGrading.blending"] =
                    "URP's shadows/midtones/highlights ranges have fixed smoothstep blending; use colorGrading.balance",
                ["toneCurve.parametric"] =
                    "the parametric curve is not mapped; use the point curves or basic.highlights/shadows/whites/blacks",
            };

        /// <summary>Every control, in Lightroom's panel order. Never null.</summary>
        public static IReadOnlyList<GradingControl> All => Ordered;

        /// <summary>
        /// Members Lightroom has that this format recognises and deliberately does not map, with the
        /// reason. A preset that sets one gets a warning, not an error.
        /// </summary>
        public static IReadOnlyDictionary<string, string> Unsupported => UnsupportedReasons;

        /// <summary>
        /// Every group path a control id passes through (<c>basic</c>, <c>hsl</c>, <c>hsl.reds</c>,
        /// <c>effects.vignette</c> ...). The parser recurses into exactly these objects.
        /// </summary>
        public static IReadOnlyCollection<string> GroupPaths { get; } = Groups(Ordered);

        /// <summary>Looks a control up by id or alias, case-insensitively.</summary>
        /// <param name="nameOrAlias">E.g. <c>basic.exposure</c>, <c>exposure</c>, <c>Blues.Luminance</c>.</param>
        /// <param name="control">The control, or null.</param>
        /// <returns>True when found.</returns>
        public static bool TryFind(string? nameOrAlias, out GradingControl? control)
        {
            control = null;
            if (string.IsNullOrWhiteSpace(nameOrAlias))
            {
                return false;
            }

            var key = nameOrAlias!.Trim();
            return ById.TryGetValue(key, out control) || ByAlias.TryGetValue(key, out control);
        }

        /// <summary>
        /// Looks a control up by canonical id only (case-insensitively) - what a preset file may
        /// use. Aliases are for the console.
        /// </summary>
        /// <param name="id">E.g. <c>basic.exposure</c>.</param>
        /// <param name="control">The control, or null.</param>
        /// <returns>True when found.</returns>
        public static bool TryGetById(string? id, out GradingControl? control)
        {
            control = null;
            return !string.IsNullOrWhiteSpace(id) && ById.TryGetValue(id!.Trim(), out control);
        }

        /// <summary>The control with exactly this canonical id.</summary>
        /// <param name="id">A canonical id.</param>
        /// <exception cref="KeyNotFoundException">No control has that id.</exception>
        public static GradingControl Get(string id) =>
            ById.TryGetValue(id, out var control)
                ? control
                : throw new KeyNotFoundException("no grading control '" + id + "'");

        /// <summary>Canonical id of an HSL control: <c>hsl.&lt;band&gt;.&lt;property&gt;</c>.</summary>
        /// <param name="band">One of <see cref="HslBands"/>.</param>
        /// <param name="property">One of <see cref="HslProperties"/>.</param>
        public static string HslId(string band, string property) => "hsl." + band + "." + property;

        /// <summary>Canonical id of a colour-grading wheel control: <c>colorGrading.&lt;wheel&gt;.&lt;property&gt;</c>.</summary>
        /// <param name="wheel">One of <see cref="Wheels"/>.</param>
        /// <param name="property"><c>hue</c>, <c>saturation</c> or <c>luminance</c>.</param>
        public static string WheelId(string wheel, string property) => "colorGrading." + wheel + "." + property;

        private static List<GradingControl> Build()
        {
            const GradingBlendMode S = GradingBlendMode.Strength;
            const GradingBlendMode H = GradingBlendMode.Shape;

            var list = new List<GradingControl>
            {
                // Basic.
                new GradingControl("basic.exposure", -5, 5, 0, EvUnit, S, "brightness in stops (URP post exposure)", "exposure"),
                new GradingControl("basic.contrast", -100, 100, 0, "", S, "contrast around mid grey", "contrast"),
                new GradingControl("basic.highlights", -100, 100, 0, "", S, "brightness of the bright range", "highlights"),
                new GradingControl("basic.shadows", -100, 100, 0, "", S, "brightness of the dark range", "shadows"),
                new GradingControl("basic.whites", -100, 100, 0, "", S, "white point: + clips sooner, - dims white", "whites"),
                new GradingControl("basic.blacks", -100, 100, 0, "", S, "black point: + fades/lifts black, - crushes", "blacks"),

                // White balance.
                new GradingControl("whiteBalance.temperature", -100, 100, 0, "", S, "+ warmer (yellow), - cooler (blue)", "temperature", "temp"),
                new GradingControl("whiteBalance.tint", -100, 100, 0, "", S, "+ magenta, - green", "tint"),

                // Presence.
                new GradingControl("presence.vibrance", -100, 100, 0, "", S, "saturation of the less saturated colours", "vibrance"),
                new GradingControl("presence.saturation", -100, 100, 0, "", S, "saturation of every colour", "saturation"),
            };

            // HSL / colour mixer.
            foreach (var band in HslBands)
            {
                list.Add(new GradingControl(HslId(band, "hue"), -100, 100, 0, "", S, band + ": hue shift toward the neighbouring colour", band + ".hue"));
                list.Add(new GradingControl(HslId(band, "saturation"), -100, 100, 0, "", S, band + ": saturation", band + ".saturation", band + ".sat"));
                list.Add(new GradingControl(HslId(band, "luminance"), -100, 100, 0, "", S, band + ": brightness", band + ".luminance", band + ".lum"));
            }

            // Split toning.
            list.Add(new GradingControl("splitToning.shadowHue", 0, 360, 0, DegreesUnit, H, "split toning: hue of the shadow tint", "split.shadowHue"));
            list.Add(new GradingControl("splitToning.shadowSaturation", 0, 100, 0, "", S, "split toning: strength of the shadow tint", "split.shadowSaturation"));
            list.Add(new GradingControl("splitToning.highlightHue", 0, 360, 0, DegreesUnit, H, "split toning: hue of the highlight tint", "split.highlightHue"));
            list.Add(new GradingControl("splitToning.highlightSaturation", 0, 100, 0, "", S, "split toning: strength of the highlight tint", "split.highlightSaturation"));
            list.Add(new GradingControl("splitToning.balance", -100, 100, 0, "", S, "split toning: + favours the highlight tint", "split.balance"));

            // Colour grading wheels.
            foreach (var wheel in Wheels)
            {
                list.Add(new GradingControl(WheelId(wheel, "hue"), 0, 360, 0, DegreesUnit, H, wheel + " wheel: hue", wheel + ".hue"));
                list.Add(new GradingControl(WheelId(wheel, "saturation"), 0, 100, 0, "", S, wheel + " wheel: saturation", wheel + ".saturation"));
                list.Add(new GradingControl(WheelId(wheel, "luminance"), -100, 100, 0, "", S, wheel + " wheel: luminance", wheel + ".luminance"));
            }

            list.Add(new GradingControl("colorGrading.balance", -100, 100, 0, "", H, "wheels: + widens the highlight range, - the shadow range", "grading.balance"));

            // Effects.
            list.Add(new GradingControl("effects.vignette.amount", -100, 100, 0, "", S, "vignette: - darkens the edges, + lightens them", "vignette", "vignette.amount"));
            list.Add(new GradingControl("effects.vignette.midpoint", 0, 100, 50, "", H, "vignette: how far it reaches into the centre (low = further)", "vignette.midpoint"));
            list.Add(new GradingControl("effects.vignette.roundness", -100, 100, 0, "", H, "vignette: 50 or more = circular, below = fitted to the screen", "vignette.roundness"));
            list.Add(new GradingControl("effects.vignette.feather", 0, 100, 50, "", H, "vignette: softness of the edge", "vignette.feather"));
            list.Add(new GradingControl("effects.grain.amount", 0, 100, 0, "", S, "film grain strength", "grain", "grain.amount"));
            list.Add(new GradingControl("effects.grain.size", 0, 100, 25, "", H, "film grain size (picks one of URP's ten grain textures)", "grain.size"));
            list.Add(new GradingControl("effects.bloom", -100, 100, 0, "", S, "the game's own bloom: -100 off, +100 doubled", "bloom"));

            // LUT and blend.
            list.Add(new GradingControl("lut.amount", 0, 100, 100, "", S, "strength of the LUT file (lut.file)", "lut"));
            list.Add(new GradingControl(Blend, 0, 1, 1, "", GradingBlendMode.Weight, "how much of the whole preset is applied (0 = the game's own look)"));

            return list;
        }

        private static Dictionary<string, GradingControl> Index(List<GradingControl> controls, bool aliases)
        {
            var map = new Dictionary<string, GradingControl>(StringComparer.OrdinalIgnoreCase);
            foreach (var control in controls)
            {
                if (!aliases)
                {
                    map.Add(control.Id, control);
                    continue;
                }

                foreach (var alias in control.Aliases)
                {
                    map.Add(alias, control);
                }
            }

            return map;
        }

        private static IReadOnlyCollection<string> Groups(List<GradingControl> controls)
        {
            var groups = new HashSet<string>(StringComparer.Ordinal);
            foreach (var control in controls)
            {
                var id = control.Id;
                for (var dot = id.IndexOf('.'); dot > 0; dot = id.IndexOf('.', dot + 1))
                {
                    groups.Add(id.Substring(0, dot));
                }
            }

            return groups;
        }
    }
}
