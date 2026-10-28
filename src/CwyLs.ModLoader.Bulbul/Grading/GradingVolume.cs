using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using CwyLs.ModLoader.Core.Grading;
using CwyLs.ModLoader.Threading;
using UnityEngine;

namespace CwyLs.ModLoader.Bulbul.Grading
{
    /// <summary>
    /// Our one global URP <c>Volume</c>: a hidden, scene-surviving <c>CWY.Grading</c> GameObject on
    /// layer 0 (inside the room cameras' volume mask, which covers layers 0 and 1 in the game's
    /// RoomScene), at
    /// a priority above the game's time-of-day volumes (priority 0; the start-scene volume 1), so its
    /// overrides are blended last - on top of the game's look, which keeps changing underneath.
    /// <para>
    /// Every <see cref="Apply"/> builds a fresh in-memory profile holding only the components the
    /// plan touches, with only the planned parameters overridden, swaps it in, and frees the old one
    /// a second later (URP's volume stack may still hold the old curves and LUT for a frame). The
    /// A/B switch is the volume's weight: 0 shows the game's own look in the very next frame, 1 the
    /// graded one. <see cref="Destroy"/> removes everything; nothing of the game's is ever edited.
    /// </para>
    /// </summary>
    internal sealed class GradingVolume
    {
        /// <summary>Name of the hidden GameObject.</summary>
        internal const string ObjectName = "CWY.Grading";

        /// <summary>The layer the volume lives on (in every room camera's volume mask).</summary>
        internal const int Layer = 0;

        /// <summary>Seconds before a replaced profile, its curves and LUT are freed.</summary>
        internal const double ReleaseDelaySeconds = 1.0;

        private readonly GradingUrp _urp;
        private readonly GameObject _host;
        private readonly Component _volume;
        private ScriptableObject? _profile;
        private List<object> _curves = new List<object>();

        private GradingVolume(GradingUrp urp, GameObject host, Component volume)
        {
            _urp = urp;
            _host = host;
            _volume = volume;
        }

        /// <summary>True while the GameObject and the Volume exist.</summary>
        internal bool Alive => _host != null && _volume != null;

        /// <summary>The volume component (for the smoke checks and the bloom read).</summary>
        internal Component Volume => _volume;

        /// <summary>The components the current profile holds, e.g. <c>ColorAdjustments+Vignette</c>.</summary>
        internal string Components { get; private set; } = "none";

        /// <summary>The volume's weight: 1 graded, 0 the game's own look.</summary>
        internal float Weight
        {
            get => Alive ? _urp.GetWeight(_volume) : 0f;
            set
            {
                if (Alive)
                {
                    _urp.SetWeight(_volume, value);
                }
            }
        }

        /// <summary>The volume's priority.</summary>
        internal float Priority => Alive ? _urp.GetPriority(_volume) : 0f;

        /// <summary>Creates the GameObject and the volume (no profile yet). Main thread.</summary>
        /// <param name="urp">The bindings.</param>
        /// <param name="priority">Volume priority; above the game's.</param>
        internal static GradingVolume Create(GradingUrp urp, float priority)
        {
            var host = new GameObject(ObjectName) { layer = Layer };
            UnityEngine.Object.DontDestroyOnLoad(host);
            host.hideFlags = HideFlags.DontSave;

            try
            {
                var volume = urp.AddVolume(host, priority);
                return new GradingVolume(urp, host, volume);
            }
            catch
            {
                UnityEngine.Object.Destroy(host);
                throw;
            }
        }

        /// <summary>Builds a profile for <paramref name="plan"/> and swaps it in. Main thread.</summary>
        /// <param name="plan">What to override.</param>
        /// <param name="lut">The baked ColorLookup LUT, or null.</param>
        /// <param name="gameBloom">The game's current Beautify bloom, when the plan scales it and it could be read.</param>
        internal void Apply(GradingPlan plan, Texture? lut, float? gameBloom)
        {
            var profile = _urp.CreateProfile();
            var curves = new List<object>();
            var names = new List<string>();

            try
            {
                Fill(profile, plan, lut, gameBloom, curves, names);
            }
            catch
            {
                Free(profile, curves);
                throw;
            }

            var oldProfile = _profile;
            var oldCurves = _curves;

            _urp.SetProfile(_volume, profile);
            _profile = profile;
            _curves = curves;
            Components = names.Count == 0 ? "none" : string.Join("+", names.ToArray());

            DeferFree(oldProfile, oldCurves);
        }

        /// <summary>Removes the volume and frees its profile now. Main thread.</summary>
        internal void Destroy()
        {
            if (_host != null)
            {
                UnityEngine.Object.Destroy(_host);
            }

            Free(_profile, _curves);
            _profile = null;
            _curves = new List<object>();
            Components = "none";
        }

        private void Fill(ScriptableObject profile, GradingPlan plan, Texture? lut, float? gameBloom, List<object> curves, List<string> names)
        {
            if (plan.PostExposure.HasValue || plan.Contrast.HasValue || plan.Saturation.HasValue)
            {
                var c = Add(profile, "ColorAdjustments", names);
                SetFloat(c, "ColorAdjustments.postExposure", plan.PostExposure);
                SetFloat(c, "ColorAdjustments.contrast", plan.Contrast);
                SetFloat(c, "ColorAdjustments.saturation", plan.Saturation);
            }

            if (plan.Temperature.HasValue || plan.Tint.HasValue)
            {
                var c = Add(profile, "WhiteBalance", names);
                SetFloat(c, "WhiteBalance.temperature", plan.Temperature);
                SetFloat(c, "WhiteBalance.tint", plan.Tint);
            }

            if (plan.SplitToning != null)
            {
                var c = Add(profile, "SplitToning", names);
                _urp.Set(c, "SplitToning.shadows", ToColor(plan.SplitToning.Shadows));
                _urp.Set(c, "SplitToning.highlights", ToColor(plan.SplitToning.Highlights));
                _urp.Set(c, "SplitToning.balance", (float)plan.SplitToning.Balance);
            }

            if (plan.ShadowsMidtonesHighlights != null)
            {
                var smh = plan.ShadowsMidtonesHighlights;
                var c = Add(profile, "ShadowsMidtonesHighlights", names);
                _urp.Set(c, "ShadowsMidtonesHighlights.shadows", ToVector(smh.Shadows));
                _urp.Set(c, "ShadowsMidtonesHighlights.midtones", ToVector(smh.Midtones));
                _urp.Set(c, "ShadowsMidtonesHighlights.highlights", ToVector(smh.Highlights));
                SetFloat(c, "ShadowsMidtonesHighlights.shadowsEnd", smh.ShadowsEnd);
                SetFloat(c, "ShadowsMidtonesHighlights.highlightsStart", smh.HighlightsStart);
            }

            if (plan.MasterCurve != null || plan.RedCurve != null || plan.GreenCurve != null ||
                plan.BlueCurve != null || plan.SatVsSatCurve != null)
            {
                var c = Add(profile, "ColorCurves", names);
                SetCurve(c, "ColorCurves.master", plan.MasterCurve, 0f, curves);
                SetCurve(c, "ColorCurves.red", plan.RedCurve, 0f, curves);
                SetCurve(c, "ColorCurves.green", plan.GreenCurve, 0f, curves);
                SetCurve(c, "ColorCurves.blue", plan.BlueCurve, 0f, curves);
                SetCurve(c, "ColorCurves.satVsSat", plan.SatVsSatCurve, 0.5f, curves);
            }

            if (lut != null)
            {
                var c = Add(profile, "ColorLookup", names);
                _urp.Set(c, "ColorLookup.texture", lut);
                _urp.Set(c, "ColorLookup.contribution", 1f);
            }

            if (plan.Vignette != null)
            {
                var c = Add(profile, "Vignette", names);
                _urp.Set(c, "Vignette.color", ToColor(plan.Vignette.Color));
                _urp.Set(c, "Vignette.intensity", (float)plan.Vignette.Intensity);
                _urp.Set(c, "Vignette.smoothness", (float)plan.Vignette.Smoothness);
                _urp.Set(c, "Vignette.rounded", plan.Vignette.Rounded);
            }

            if (plan.FilmGrain != null)
            {
                var c = Add(profile, "FilmGrain", names);
                var lookup = _urp.ValueTypeOf(c, "FilmGrain.type");
                _urp.Set(c, "FilmGrain.type", Enum.ToObject(lookup, plan.FilmGrain.TypeIndex));
                _urp.Set(c, "FilmGrain.intensity", (float)plan.FilmGrain.Intensity);
                _urp.Set(c, "FilmGrain.response", (float)plan.FilmGrain.Response);
            }

            if (plan.BloomFactor.HasValue && gameBloom.HasValue && _urp.BeautifyType != null)
            {
                var c = Add(profile, "Beautify", names);
                _urp.Set(c, "Beautify." + GradingUrp.BeautifyBloomField, (float)(gameBloom.Value * plan.BloomFactor.Value));
            }
        }

        private ScriptableObject Add(ScriptableObject profile, string component, List<string> names)
        {
            names.Add(component);
            return _urp.AddComponent(profile, component);
        }

        private void SetFloat(ScriptableObject component, string key, double? value)
        {
            if (value.HasValue)
            {
                _urp.Set(component, key, (float)value.Value);
            }
        }

        private void SetCurve(ScriptableObject component, string key, IReadOnlyList<CurveKey>? keys, float zeroValue, List<object> curves)
        {
            if (keys is null || keys.Count == 0)
            {
                return;
            }

            var curve = _urp.CreateCurve(keys, zeroValue, loop: false);
            curves.Add(curve);
            _urp.Set(component, key, curve);
        }

        private void DeferFree(ScriptableObject? profile, List<object> curves)
        {
            if (profile is null && curves.Count == 0)
            {
                return;
            }

            var work = MainThread.PostAfter(ReleaseDelaySeconds, "grading.free", () => Free(profile, curves));
            if (!work.IsAccepted)
            {
                // The game is quitting or the queue is full: free now rather than leak.
                Free(profile, curves);
            }
        }

        private void Free(ScriptableObject? profile, List<object> curves)
        {
            foreach (var curve in curves)
            {
                _urp.ReleaseCurve(curve);
            }

            if (profile == null)
            {
                return;
            }

            try
            {
                if (_urp.ComponentsOf(profile!) is IList components)
                {
                    foreach (var component in components)
                    {
                        if (component is UnityEngine.Object unityObject && unityObject != null)
                        {
                            UnityEngine.Object.Destroy(unityObject);
                        }
                    }
                }

                UnityEngine.Object.Destroy(profile);
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogWarning("GRADING: could not free a replaced profile: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static Color ToColor(Rgb c) => new Color((float)c.R, (float)c.G, (float)c.B, 1f);

        private static Vector4 ToVector(Float4 v) => new Vector4((float)v.X, (float)v.Y, (float)v.Z, (float)v.W);

        /// <summary>One line for the console and the smoke report.</summary>
        internal string Describe()
        {
            if (!Alive)
            {
                return "no volume";
            }

            var sb = new StringBuilder(ObjectName)
                .Append(" priority=").Append(Priority.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture))
                .Append(" weight=").Append(Weight.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture))
                .Append(" layer=").Append(_host.layer)
                .Append(" components=").Append(Components);
            return sb.ToString();
        }
    }
}
