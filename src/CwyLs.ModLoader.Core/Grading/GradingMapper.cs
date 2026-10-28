using System;

namespace CwyLs.ModLoader.Core.Grading
{
    /// <summary>
    /// Maps a flat, blended preset (Lightroom units) onto URP volume overrides - the one place the
    /// numbers that decide how a preset looks are written down. Every constant below is a tuning
    /// choice, documented with its effect, and tabled in <c>src/CwyLs.ModLoader.Bulbul/Grading/README.md</c>.
    /// <para>
    /// Only components the game's own profiles do not use are overridden (the game's
    /// <c>Volume_{day,sunset,night,cloud}</c> hold Beautify + LiftGammaGain only), so the preset sits
    /// on top of the time-of-day look instead of replacing it. The one exception, bloom, is
    /// expressed as a factor on the game's own Beautify bloom for the same reason.
    /// </para>
    /// </summary>
    public static class GradingMapper
    {
        /// <summary>URP contrast per Lightroom contrast: +100 = URP +50 (log-space slope x1.5 around mid grey).</summary>
        public const double ContrastScale = 0.5;

        /// <summary>Luminance offset (URP SMH <c>w</c>) per +100 of a lightening slider: x2 linear (+1 stop) in that range.</summary>
        public const double LiftScale = 0.25;

        /// <summary>Luminance offset per -100 of a darkening slider: x0.5 linear (-1 stop) in that range.</summary>
        public const double DarkenScale = 0.5;

        /// <summary>How strongly a colour-grading wheel at saturation 100 tints its range (0..1 mix toward the pure hue).</summary>
        public const double WheelSaturationScale = 0.35;

        /// <summary>URP's default end of the shadow range.</summary>
        public const double DefaultShadowsEnd = 0.3;

        /// <summary>URP's default start of the highlight range.</summary>
        public const double DefaultHighlightsStart = 0.55;

        /// <summary>URP film grain response (how much grain fades in bright areas); URP's default.</summary>
        public const double GrainResponse = 0.8;

        /// <summary>How many grain textures URP ships (FilmGrainLookup Thin1..Large02).</summary>
        public const int GrainTypes = 10;

        /// <summary>Edge brightness of a vignette at amount +100 (x1.6).</summary>
        public const double VignetteLighten = 0.6;

        /// <summary>Maps a flat preset. Resolve the time of day and apply the blend first.</summary>
        /// <param name="preset">The preset in force (see <see cref="GradingPreset.Resolve"/> and <see cref="GradingPreset.ApplyBlend"/>).</param>
        /// <returns>The plan; empty when the preset changes nothing.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="preset"/> is null.</exception>
        public static GradingPlan Map(GradingPreset preset)
        {
            if (preset is null)
            {
                throw new ArgumentNullException(nameof(preset));
            }

            var exposure = preset.Get("basic.exposure");
            var contrast = preset.Get("basic.contrast");
            var saturation = preset.Get("presence.saturation");
            var temperature = preset.Get("whiteBalance.temperature");
            var tint = preset.Get("whiteBalance.tint");
            var bloom = preset.Get("effects.bloom");
            var lutAmount = preset.Get("lut.amount") / 100.0;

            return new GradingPlan
            {
                PostExposure = NonZero(exposure),
                Contrast = NonZero(contrast * ContrastScale),
                Saturation = NonZero(saturation),
                Temperature = NonZero(temperature),
                Tint = NonZero(tint),
                SplitToning = SplitToning(preset),
                ShadowsMidtonesHighlights = Smh(preset),
                MasterCurve = GradingToneCurves.Master(preset),
                RedCurve = GradingToneCurves.Channel(preset.Curve(GradingCurveChannel.Red)),
                GreenCurve = GradingToneCurves.Channel(preset.Curve(GradingCurveChannel.Green)),
                BlueCurve = GradingToneCurves.Channel(preset.Curve(GradingCurveChannel.Blue)),
                SatVsSatCurve = GradingToneCurves.Vibrance(preset.Get("presence.vibrance")),
                Vignette = Vignette(preset),
                FilmGrain = Grain(preset),
                BloomFactor = bloom == 0 ? (double?)null : Math.Max(0, 1 + (bloom / 100.0)),
                Hsl = HslAdjustment.FromPreset(preset),
                LutFile = preset.LutFile,
                LutAmount = lutAmount,
            };
        }

        /// <summary>
        /// A luminance offset for URP's SMH <c>w</c>: + lightens (x2 linear at +100), - darkens
        /// (x0.5 at -100). URP applies <c>w * (w &lt; 0 ? 1 : 4)</c> to the linear multiplier.
        /// </summary>
        /// <param name="slider">-100..100.</param>
        public static double LuminanceOffset(double slider) =>
            slider >= 0 ? (slider / 100.0) * LiftScale : (slider / 100.0) * DarkenScale;

        /// <summary>
        /// A split-toning tint: HSL(hue, saturation, 0.5) in gamma space, so saturation 0 is the
        /// neutral grey (0.5, 0.5, 0.5) URP's soft-light expects.
        /// </summary>
        /// <param name="hue">0..360.</param>
        /// <param name="saturation">0..100.</param>
        public static Rgb SplitTint(double hue, double saturation) =>
            GradingColor.FromHsl(hue, GradingColor.Clamp01(saturation / 100.0), 0.5);

        /// <summary>
        /// A colour-grading wheel as a linear per-channel multiplier: the pure hue normalised by its
        /// mean, mixed in by <c>saturation / 100 * </c><see cref="WheelSaturationScale"/>. (1, 1, 1) at
        /// saturation 0.
        /// </summary>
        /// <param name="hue">0..360.</param>
        /// <param name="saturation">0..100.</param>
        public static Rgb WheelMultiplier(double hue, double saturation)
        {
            var mix = GradingColor.Clamp01(saturation / 100.0) * WheelSaturationScale;
            if (mix <= 0)
            {
                return Rgb.Grey(1);
            }

            var pure = GradingColor.FromHsv(hue, 1, 1);
            var mean = (pure.R + pure.G + pure.B) / 3.0;
            return Rgb.Lerp(Rgb.Grey(1), pure.Scale(1 / mean), mix);
        }

        /// <summary>The film-grain texture index (Thin1 = 0 .. Large02 = 9) for a Lightroom grain size.</summary>
        /// <param name="size">0..100.</param>
        public static int GrainType(double size)
        {
            var index = (int)Math.Round(GradingColor.Clamp01(size / 100.0) * (GrainTypes - 1), MidpointRounding.AwayFromZero);
            return index < 0 ? 0 : index > GrainTypes - 1 ? GrainTypes - 1 : index;
        }

        private static SplitToningPlan? SplitToning(GradingPreset p)
        {
            var shadowSat = p.Get("splitToning.shadowSaturation");
            var highlightSat = p.Get("splitToning.highlightSaturation");
            var balance = p.Get("splitToning.balance");

            if (shadowSat == 0 && highlightSat == 0)
            {
                return null;
            }

            return new SplitToningPlan(
                SplitTint(p.Get("splitToning.shadowHue"), shadowSat),
                SplitTint(p.Get("splitToning.highlightHue"), highlightSat),
                balance);
        }

        private static SmhPlan? Smh(GradingPreset p)
        {
            var global = WheelMultiplier(p.Get(GradingControls.WheelId("global", "hue")), p.Get(GradingControls.WheelId("global", "saturation")));
            var globalLum = p.Get(GradingControls.WheelId("global", "luminance"));

            var shadows = Wheel(p, "shadows", global, globalLum, p.Get("basic.shadows"));
            var midtones = Wheel(p, "midtones", global, globalLum, 0);
            var highlights = Wheel(p, "highlights", global, globalLum, p.Get("basic.highlights"));
            var balance = p.Get("colorGrading.balance");

            var neutral = new Float4(1, 1, 1, 0);
            if (shadows.Equals(neutral) && midtones.Equals(neutral) && highlights.Equals(neutral))
            {
                return null;
            }

            double? shadowsEnd = null;
            double? highlightsStart = null;
            if (balance != 0)
            {
                // + balance: more of the image counts as highlights and less as shadows.
                var b = balance / 100.0;
                shadowsEnd = Math.Max(0.05, Math.Min(0.6, DefaultShadowsEnd - (0.15 * b)));
                highlightsStart = Math.Max(0.2, Math.Min(0.9, DefaultHighlightsStart - (0.25 * b)));
            }

            return new SmhPlan(shadows, midtones, highlights, shadowsEnd, highlightsStart);
        }

        private static Float4 Wheel(GradingPreset p, string wheel, Rgb global, double globalLum, double toneSlider)
        {
            var own = WheelMultiplier(p.Get(GradingControls.WheelId(wheel, "hue")), p.Get(GradingControls.WheelId(wheel, "saturation")));
            var multiplier = new Rgb(own.R * global.R, own.G * global.G, own.B * global.B);

            var w = LuminanceOffset(p.Get(GradingControls.WheelId(wheel, "luminance"))) +
                    LuminanceOffset(globalLum) +
                    LuminanceOffset(toneSlider);
            w = Math.Max(-1, Math.Min(1, w));

            // URP converts xyz with GammaToLinearSpace before using them as multipliers, so the
            // linear multiplier is handed over gamma-encoded.
            var gamma = GradingColor.ToSrgb(multiplier);
            return Float4.Of(gamma, w);
        }

        private static VignettePlan? Vignette(GradingPreset p)
        {
            var amount = p.Get("effects.vignette.amount");
            if (amount == 0)
            {
                return null;
            }

            var midpoint = p.Get("effects.vignette.midpoint") / 100.0;
            var feather = p.Get("effects.vignette.feather") / 100.0;
            var roundness = p.Get("effects.vignette.roundness");

            var strength = Math.Abs(amount) / 100.0;
            var edge = amount < 0 ? Rgb.Grey(1 - strength) : Rgb.Grey(1 + (VignetteLighten * strength));

            // midpoint 0 reaches far into the frame (URP intensity 0.75), 100 only the corners (0.35);
            // feather 0 is a hard edge (smoothness 0.1), 100 a long falloff (1.0).
            var intensity = 0.75 - (0.40 * midpoint);
            var smoothness = 0.1 + (0.9 * feather);

            return new VignettePlan(edge, intensity, smoothness, roundness >= 50);
        }

        private static FilmGrainPlan? Grain(GradingPreset p)
        {
            var amount = p.Get("effects.grain.amount");
            if (amount <= 0)
            {
                return null;
            }

            return new FilmGrainPlan(GrainType(p.Get("effects.grain.size")), GradingColor.Clamp01(amount / 100.0), GrainResponse);
        }

        private static double? NonZero(double value) => Math.Abs(value) < 1e-12 ? (double?)null : value;
    }
}
