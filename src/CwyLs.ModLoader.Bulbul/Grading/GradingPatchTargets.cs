using System.Collections.Generic;
using CwyLs.ModLoader.Core;

namespace CwyLs.ModLoader.Bulbul.Grading
{
    /// <summary>
    /// Every game, URP and Beautify member the grading layer relies on, as patch-target data: a game
    /// or URP update that renames one shows up as <c>PATCH TARGET MISSING</c> at startup and in
    /// seamcheck, and <see cref="GradingUrp.Bind"/> turns grading off with one warning instead of
    /// failing in a frame. Static and Unity-free: seamcheck evaluates <see cref="All"/> offline.
    /// </summary>
    /// <remarks>
    /// Verified against the installed 1.17.3 <c>Managed/</c> with a metadata-only load (types and
    /// members are listed in <c>src/CwyLs.ModLoader.Bulbul/Grading/README.md</c>). Nothing here is
    /// Harmony-patched: they are read and written by reflection.
    /// </remarks>
    public static class GradingPatchTargets
    {
        private const string Game = "Assembly-CSharp";
        private const string Core = "Unity.RenderPipelines.Core.Runtime";
        private const string Urp = "Unity.RenderPipelines.Universal.Runtime";
        private const string Rendering = "UnityEngine.Rendering.";
        private const string Universal = "UnityEngine.Rendering.Universal.";

        /// <summary>The table.</summary>
        public static IReadOnlyList<PatchTarget> All { get; } = Build();

        private static PatchTarget[] Build()
        {
            var list = new List<PatchTarget>
            {
                // The game's time of day (WindowTimeOfDayType).
                new PatchTarget("grading.timeofday", Game, "Bulbul.WindowViewService", MemberKind.Property, "CurrentTimeOfDayType"),

                // The volume machinery (SRP core).
                new PatchTarget("grading.volume.priority", Core, Rendering + "Volume", MemberKind.Field, "priority"),
                new PatchTarget("grading.volume.weight", Core, Rendering + "Volume", MemberKind.Field, "weight"),
                new PatchTarget("grading.volume.profile", Core, Rendering + "Volume", MemberKind.Field, "sharedProfile"),
                new PatchTarget("grading.volume.global", Core, Rendering + "Volume", MemberKind.Property, "isGlobal"),
                new PatchTarget(
                    "grading.profile.add", Core, Rendering + "VolumeProfile", MemberKind.Method, "Add",
                    new[] { "System.Type", "System.Boolean" }),
                new PatchTarget("grading.profile.components", Core, Rendering + "VolumeProfile", MemberKind.Field, "components"),
                new PatchTarget(
                    "grading.component.overrides", Core, Rendering + "VolumeComponent", MemberKind.Method, "SetAllOverridesTo",
                    new[] { "System.Boolean" }),
                new PatchTarget("grading.parameter.override", Core, Rendering + "VolumeParameter", MemberKind.Property, "overrideState"),
                new PatchTarget("grading.curve.release", Core, Rendering + "TextureCurve", MemberKind.Method, "Release"),
                new PatchTarget("grading.urp.asset", Urp, Universal + "UniversalRenderPipeline", MemberKind.Property, "asset", isStatic: true),

                // Beautify (compiled into the URP runtime here): the game's own bloom, read and scaled.
                new PatchTarget("grading.beautify.bloom", Urp, GradingUrpMembers.BeautifyTypeName, MemberKind.Field, GradingUrpMembers.BeautifyBloomField),
            };

            foreach (var pair in GradingUrpMembers.RequiredFields)
            {
                foreach (var field in pair.Value)
                {
                    list.Add(new PatchTarget(
                        "grading.urp." + pair.Key.ToLowerInvariant() + "." + field.ToLowerInvariant(),
                        Urp,
                        Universal + pair.Key,
                        MemberKind.Field,
                        field));
                }
            }

            return list.ToArray();
        }
    }

    /// <summary>
    /// The names <see cref="GradingUrp"/> resolves, kept apart from it so this table (and seamcheck,
    /// which evaluates <see cref="GradingPatchTargets.All"/> offline) never loads a type with Unity in it.
    /// </summary>
    public static class GradingUrpMembers
    {
        /// <summary>SRP core runtime assembly.</summary>
        public const string CoreAssembly = "Unity.RenderPipelines.Core.Runtime";

        /// <summary>URP runtime assembly (Beautify is compiled into it in this game).</summary>
        public const string UniversalAssembly = "Unity.RenderPipelines.Universal.Runtime";

        /// <summary>Namespace prefix of the URP components.</summary>
        public const string UrpNamespace = "UnityEngine.Rendering.Universal.";

        /// <summary>Beautify's volume component.</summary>
        public const string BeautifyTypeName = "Beautify.Universal.Beautify";

        /// <summary>Beautify's bloom intensity parameter.</summary>
        public const string BeautifyBloomField = "bloomIntensity";

        /// <summary>Every URP component field the plan writes, by component (none is used by the game's own profiles).</summary>
        public static IReadOnlyDictionary<string, string[]> RequiredFields { get; } = new Dictionary<string, string[]>
        {
            ["ColorAdjustments"] = new[] { "postExposure", "contrast", "saturation" },
            ["WhiteBalance"] = new[] { "temperature", "tint" },
            ["SplitToning"] = new[] { "shadows", "highlights", "balance" },
            ["ShadowsMidtonesHighlights"] = new[] { "shadows", "midtones", "highlights", "shadowsEnd", "highlightsStart" },
            ["ColorCurves"] = new[] { "master", "red", "green", "blue", "satVsSat" },
            ["ColorLookup"] = new[] { "texture", "contribution" },
            ["Vignette"] = new[] { "color", "intensity", "smoothness", "rounded" },
            ["FilmGrain"] = new[] { "type", "intensity", "response" },
        };
    }
}
