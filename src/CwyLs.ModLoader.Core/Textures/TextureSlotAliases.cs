using System;
using System.Collections.Generic;

namespace CwyLs.ModLoader.Core.Textures
{
    /// <summary>
    /// Translates the friendly <em>slot</em> name a pack author writes into the shader property a
    /// material is actually keyed by.
    /// <para>
    /// A slot that starts with <c>_</c> is taken to be a shader property already and is passed
    /// through verbatim (after a spelling check), so a pack can reach any property this or a future
    /// shader has without waiting for an alias. Everything else must be in the table below.
    /// </para>
    /// <para>
    /// The table is built from the shader and materials this game actually ships (game 1.17.3),
    /// not from lilToon's documentation:
    /// </para>
    /// <list type="table">
    ///   <listheader><term>alias</term><description>property (lilToon.shader line) and the evidence</description></listheader>
    ///   <item><term>base, main</term><description><c>_MainTex</c> (32) - <c>mt_Body._MainTex</c> = <c>t_body_base.png</c></description></item>
    ///   <item><term>basemap</term><description><c>_BaseMap</c> (500) - hidden SRP mirror of <c>_MainTex</c></description></item>
    ///   <item><term>basecolor</term><description><c>_BaseColorMap</c> (501) - hidden SRP mirror of <c>_MainTex</c></description></item>
    ///   <item><term>main2, main3</term><description><c>_Main2ndTex</c> (40), <c>_Main3rdTex</c> (68)</description></item>
    ///   <item><term>shadow</term><description><c>_ShadowColorTex</c> (148) - lilToon's shadow colour slot; <b>unused</b> by this game's materials</description></item>
    ///   <item><term>shadow2, shadow3</term><description><c>_Shadow2ndColorTex</c> (154), <c>_Shadow3rdColorTex</c> (160)</description></item>
    ///   <item><term>shadowborder</term><description><c>_ShadowBorderMask</c> (139) - where <c>mt_Face</c>/<c>mt_Hair</c> put <c>*_shadow.png</c></description></item>
    ///   <item><term>shadowstrength, shadowblur</term><description><c>_ShadowStrengthMask</c> (137), <c>_ShadowBlurMask</c> (141)</description></item>
    ///   <item><term>rim</term><description><c>_RimColorTex</c> (244) - <c>mt_Body._RimColorTex</c> = <c>t_body_shadow.png</c></description></item>
    ///   <item><term>rimshade</term><description><c>_RimShadeMask</c> (174) - <c>mt_Hair._RimShadeMask</c> = <c>Hair_reflect.png</c></description></item>
    ///   <item><term>backlight</term><description><c>_BacklightColorTex</c> (126) - <c>mt_Body._BacklightColorTex</c> = <c>t_body_shadow.png</c></description></item>
    ///   <item><term>reflect, reflection</term><description><c>_ReflectionColorTex</c> (195) - <c>mt_Hair._ReflectionColorTex</c> = <c>Hair_reflect.png</c></description></item>
    ///   <item><term>emission, emission2</term><description><c>_EmissionMap</c> (285), <c>_Emission2ndMap</c> (319)</description></item>
    ///   <item><term>normal, bump</term><description><c>_BumpMap</c> (99)</description></item>
    ///   <item><term>matcap, matcap2</term><description><c>_MatCapTex</c> (204), <c>_MatCap2ndTex</c> (224) - <c>mt_Hairpin</c> uses the 2nd</description></item>
    ///   <item><term>matcapmask, matcap2mask</term><description><c>_MatCapBlendMask</c> (211), <c>_MatCap2ndBlendMask</c> (231)</description></item>
    ///   <item><term>smoothness</term><description><c>_SmoothnessTex</c> (181) - <c>mt_Hairpin._SmoothnessTex</c></description></item>
    ///   <item><term>metallic</term><description><c>_MetallicGlossMap</c> (183)</description></item>
    ///   <item><term>alphamask</term><description><c>_AlphaMask</c> (95)</description></item>
    ///   <item><term>outline</term><description><c>_OutlineTex</c> (472)</description></item>
    /// </list>
    /// <para>
    /// Note the one that surprises people: this game never fills <c>_ShadowColorTex</c>. The PNGs
    /// it calls "shadow" (<c>t_body_shadow.png</c>, <c>Face_2_shadow.png</c>, <c>Hair_shadow.png</c>)
    /// are bound to <c>_BacklightColorTex</c>, <c>_RimColorTex</c> and - on the face and hair -
    /// <c>_ShadowBorderMask</c>. Use <c>shadowborder</c>/<c>rim</c>/<c>backlight</c> to replace what
    /// the game shipped; <c>shadow</c> writes into a slot that was previously empty.
    /// </para>
    /// </summary>
    public static class TextureSlotAliases
    {
        /// <summary>The character a shader property name must start with.</summary>
        public const char PropertyPrefix = '_';

        private static readonly Dictionary<string, string> Table =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "base", "_MainTex" },
                { "main", "_MainTex" },
                { "basemap", "_BaseMap" },
                { "basecolor", "_BaseColorMap" },
                { "main2", "_Main2ndTex" },
                { "main3", "_Main3rdTex" },
                { "shadow", "_ShadowColorTex" },
                { "shadow2", "_Shadow2ndColorTex" },
                { "shadow3", "_Shadow3rdColorTex" },
                { "shadowborder", "_ShadowBorderMask" },
                { "shadowstrength", "_ShadowStrengthMask" },
                { "shadowblur", "_ShadowBlurMask" },
                { "rim", "_RimColorTex" },
                { "rimshade", "_RimShadeMask" },
                { "backlight", "_BacklightColorTex" },
                { "reflect", "_ReflectionColorTex" },
                { "reflection", "_ReflectionColorTex" },
                { "emission", "_EmissionMap" },
                { "emission2", "_Emission2ndMap" },
                { "normal", "_BumpMap" },
                { "bump", "_BumpMap" },
                { "matcap", "_MatCapTex" },
                { "matcap2", "_MatCap2ndTex" },
                { "matcapmask", "_MatCapBlendMask" },
                { "matcap2mask", "_MatCap2ndBlendMask" },
                { "smoothness", "_SmoothnessTex" },
                { "metallic", "_MetallicGlossMap" },
                { "alphamask", "_AlphaMask" },
                { "outline", "_OutlineTex" },
            };

        /// <summary>lilToon's hidden SRP mirrors of <c>_MainTex</c> (lilToon.shader lines 500-501).</summary>
        /// <remarks>
        /// This game runs URP (its graphics settings name a
        /// <c>UniversalRenderPipelineAsset</c>, and the Managed folder ships the URP runtime), and
        /// every <c>mt_*</c> material binds the same texture to all three of <c>_MainTex</c>,
        /// <c>_BaseMap</c> and <c>_BaseColorMap</c>. Which one the URP pass samples cannot be told
        /// from outside - the game ships its 104 shaders compiled only - so a texture written to <c>_MainTex</c> alone may be sampled from a mirror that
        /// still holds the game's own texture. Writing all three costs nothing and removes the
        /// question.
        /// </remarks>
        public static IReadOnlyList<string> MainTexMirrors { get; } = new[] { "_BaseMap", "_BaseColorMap" };

        /// <summary>
        /// The other properties that should carry the same texture as <paramref name="property"/>,
        /// or an empty list when it has none.
        /// </summary>
        /// <param name="property">A resolved shader property, e.g. <c>_MainTex</c>.</param>
        public static IReadOnlyList<string> MirrorsOf(string? property) =>
            string.Equals(property, "_MainTex", StringComparison.Ordinal)
                ? MainTexMirrors
                : (IReadOnlyList<string>)NoMirrors;

        private static readonly string[] NoMirrors = new string[0];

        /// <summary>Every alias, ordinally sorted. For the log line that lists what a pack may use.</summary>
        public static IReadOnlyList<string> Aliases
        {
            get
            {
                var names = new List<string>(Table.Count);
                foreach (var pair in Table)
                {
                    names.Add(pair.Key);
                }

                names.Sort(StringComparer.Ordinal);
                return names;
            }
        }

        /// <summary>
        /// Resolves a slot to a shader property.
        /// </summary>
        /// <param name="slot">
        /// A file stem such as <c>base</c> or <c>_MainTex</c>. Aliases are case-insensitive; an
        /// explicit property keeps the author's casing, because Unity's material property lookup
        /// is case-sensitive.
        /// </param>
        /// <param name="property">The shader property, or null when the slot is not understood.</param>
        /// <param name="reason">Why it was not understood, or null on success.</param>
        /// <returns>True when <paramref name="slot"/> named a usable property.</returns>
        public static bool TryResolve(string? slot, out string? property, out string? reason)
        {
            property = null;
            reason = null;

            if (string.IsNullOrWhiteSpace(slot))
            {
                reason = "the slot name is empty";
                return false;
            }

            var name = slot!.Trim();

            if (name[0] == PropertyPrefix)
            {
                if (!IsWellFormedProperty(name))
                {
                    reason = "\"" + name + "\" is not a shader property name (expected _ then letters, digits or _)";
                    return false;
                }

                property = name;
                return true;
            }

            if (Table.TryGetValue(name, out var resolved))
            {
                property = resolved;
                return true;
            }

            reason = "\"" + name + "\" is not a known slot alias; use a shader property such as _MainTex, " +
                     "or one of: " + string.Join(", ", ToArray(Aliases));
            return false;
        }

        /// <summary>The shader property for a slot, or null when the slot is not understood.</summary>
        /// <param name="slot">A file stem such as <c>base</c> or <c>_MainTex</c>.</param>
        public static string? Resolve(string? slot) =>
            TryResolve(slot, out var property, out _) ? property : null;

        /// <summary>True when <paramref name="name"/> spells a shader property: <c>_</c> then word characters.</summary>
        /// <param name="name">A candidate property name.</param>
        public static bool IsWellFormedProperty(string? name)
        {
            if (string.IsNullOrEmpty(name) || name![0] != PropertyPrefix || name.Length < 2)
            {
                return false;
            }

            for (var i = 1; i < name.Length; i++)
            {
                var c = name[i];
                if (!char.IsLetterOrDigit(c) && c != '_')
                {
                    return false;
                }
            }

            return true;
        }

        // netstandard2.1's string.Join has an IEnumerable<string> overload, but spelling the array
        // out keeps the message allocation-free of LINQ and identical on every runtime.
        private static string[] ToArray(IReadOnlyList<string> names)
        {
            var array = new string[names.Count];
            for (var i = 0; i < names.Count; i++)
            {
                array[i] = names[i];
            }

            return array;
        }
    }
}
