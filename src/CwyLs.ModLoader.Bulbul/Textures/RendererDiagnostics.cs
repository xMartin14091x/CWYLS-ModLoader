using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace CwyLs.ModLoader.Bulbul.Textures
{
    /// <summary>
    /// Prints everything that decides whether a texture write is actually seen. Written for the
    /// case where every rule applied and verified and the player still saw nothing.
    /// <para>
    /// The questions it answers, in the order they matter:
    /// is this renderer being drawn at all; is it the only one with this name; what material does
    /// it really use; does a <c>MaterialPropertyBlock</c> override that material; and which of
    /// <c>_MainTex</c> / <c>_BaseMap</c> / <c>_BaseColorMap</c> the shader declares and currently
    /// holds, on both the instance and the shared material.
    /// </para>
    /// </summary>
    internal static class RendererDiagnostics
    {
        /// <summary>The three properties a lilToon albedo can be reached through. Order is the log order.</summary>
        internal static readonly string[] AlbedoProperties = { "_MainTex", "_BaseMap", "_BaseColorMap" };

        /// <summary>Line prefix, so a launch log can be grepped for exactly this.</summary>
        internal const string Prefix = "TEXDIAG";

        /// <summary>
        /// A multi-line report for one renderer.
        /// </summary>
        /// <param name="name">The name the rule asked for.</param>
        /// <param name="renderer">The renderer that was chosen, or null when none was found.</param>
        /// <param name="candidates">Every renderer in the scene carrying <paramref name="name"/>.</param>
        /// <param name="lookup">The pass's renderer index, used to name renderers that share a material.</param>
        /// <param name="materialIndex">The material index the rule targets.</param>
        internal static string Describe(
            string name, Renderer? renderer, IReadOnlyList<Renderer> candidates, RendererLookup lookup, int materialIndex)
        {
            var text = new StringBuilder();
            text.Append(Prefix).Append(" '").Append(name).Append("': ")
                .Append(candidates.Count).Append(" renderer(s) with this name");

            foreach (var candidate in candidates)
            {
                text.AppendLine().Append("  - ").Append(RendererLookup.PathOf(candidate))
                    .Append(" active=").Append(Yes(IsActive(candidate)))
                    .Append(" enabled=").Append(Yes(candidate != null && candidate!.enabled))
                    .Append(" visible=").Append(Yes(candidate != null && candidate!.isVisible))
                    .Append(candidate == renderer ? "   <- chosen" : string.Empty);
            }

            if (renderer == null)
            {
                return text.AppendLine().Append("  no renderer chosen").ToString();
            }

            var shared = SafeArray(() => renderer!.sharedMaterials);
            var instances = SafeArray(() => renderer!.materials);

            text.AppendLine().Append("  materials: ").Append(shared.Length)
                .Append(" shared, ").Append(instances.Length).Append(" instance; rule targets index ")
                .Append(materialIndex);

            var sharedMaterial = materialIndex < shared.Length ? shared[materialIndex] : null;
            var instanceMaterial = materialIndex < instances.Length ? instances[materialIndex] : null;

            text.AppendLine().Append("  shared[").Append(materialIndex).Append("] = ")
                .Append(Describe(sharedMaterial));
            text.AppendLine().Append("  instance[").Append(materialIndex).Append("] = ")
                .Append(Describe(instanceMaterial));

            AppendProperties(text, "shared  ", sharedMaterial);
            AppendProperties(text, "instance", instanceMaterial);
            AppendKeywords(text, instanceMaterial ?? sharedMaterial);
            AppendPropertyBlock(text, renderer!);
            AppendSharersIfHidden(text, renderer!, sharedMaterial, lookup);

            return text.ToString();
        }

        /// <summary>
        /// A report for a material rule: which loaded materials carry the name, what each holds in
        /// its albedo slots, and which renderers are using them right now.
        /// </summary>
        /// <param name="name">The material name the rule asked for, without the <c>@</c>.</param>
        /// <param name="matches">Every loaded material with that base name.</param>
        /// <param name="lookup">The pass's renderer index, for naming the users.</param>
        internal static string DescribeMaterial(string name, IReadOnlyList<Material> matches, RendererLookup lookup)
        {
            var text = new StringBuilder();
            text.Append(Prefix).Append(" '@").Append(name).Append("': ")
                .Append(matches.Count).Append(" loaded material(s) with this name");

            foreach (var material in matches)
            {
                text.AppendLine().Append("  - ").Append(Describe(material));

                if (material == null)
                {
                    continue;
                }

                foreach (var property in AlbedoProperties)
                {
                    if (material!.HasProperty(property))
                    {
                        text.Append(' ').Append(property).Append('=').Append(Describe(material.GetTexture(property)));
                    }
                }

                var users = lookup.ActiveUsersOf(material!);
                text.AppendLine().Append("      active renderers: ").Append(users.Count);
                foreach (var user in users)
                {
                    text.AppendLine().Append("        - ").Append(RendererLookup.PathOf(user));
                }
            }

            return text.ToString();
        }

        /// <summary>
        /// The one-line version that rides along on the <c>TEXTURE</c> apply line.
        /// </summary>
        /// <param name="renderer">The renderer written.</param>
        /// <param name="candidates">Every renderer carrying the same name.</param>
        internal static string Brief(Renderer? renderer, IReadOnlyList<Renderer> candidates)
        {
            if (renderer == null)
            {
                return "[no renderer]";
            }

            return "[" + (IsActive(renderer) ? "active" : "INACTIVE") +
                   (renderer!.enabled ? string.Empty : ",disabled") +
                   (renderer.isVisible ? string.Empty : ",offscreen") +
                   (candidates.Count > 1 ? ",1 of " + candidates.Count : string.Empty) +
                   (renderer.HasPropertyBlock() ? ",mpb" : string.Empty) + "]";
        }

        /// <summary>True when the renderer's object is active in the hierarchy.</summary>
        /// <param name="renderer">Any renderer; a destroyed one is not active.</param>
        internal static bool IsActive(Renderer? renderer) =>
            renderer != null && renderer!.gameObject.activeInHierarchy;

        /// <summary><c>'mt_Body' shader 'lilToon' id=12345</c>, or <c>&lt;none&gt;</c>.</summary>
        /// <param name="material">A material, possibly destroyed.</param>
        internal static string Describe(Material? material)
        {
            if (material == null)
            {
                return "<none>";
            }

            var shader = material!.shader;
            return "'" + material.name + "' shader '" + (shader == null ? "<none>" : shader!.name) +
                   "' id=" + material.GetInstanceID();
        }

        /// <summary><c>'_MainTex' 1024x1024</c>, or <c>&lt;empty&gt;</c>.</summary>
        /// <param name="texture">A texture, possibly destroyed or null.</param>
        internal static string Describe(Texture? texture) =>
            texture == null ? "<empty>" : "'" + texture!.name + "' " + texture.width + "x" + texture.height;

        private static void AppendProperties(StringBuilder text, string label, Material? material)
        {
            if (material == null)
            {
                return;
            }

            foreach (var property in AlbedoProperties)
            {
                // HasProperty answers the question the game's files cannot: which of the three the compiled
                // shader actually declares in this build.
                var declared = material!.HasProperty(property);
                text.AppendLine().Append("  ").Append(label).Append(' ').Append(property)
                    .Append(": declared=").Append(Yes(declared))
                    .Append(declared ? " value=" + Describe(material.GetTexture(property)) : string.Empty);
            }
        }

        private static void AppendKeywords(StringBuilder text, Material? material)
        {
            if (material == null)
            {
                return;
            }

            var keywords = material!.shaderKeywords;
            text.AppendLine().Append("  keywords(").Append(keywords.Length).Append("): ")
                .Append(keywords.Length == 0 ? "<none>" : string.Join(" ", keywords));
        }

        private static void AppendPropertyBlock(StringBuilder text, Renderer renderer)
        {
            var has = renderer.HasPropertyBlock();
            text.AppendLine().Append("  propertyBlock: ").Append(Yes(has));

            if (!has)
            {
                return;
            }

            // A block overrides the material for every property it carries, so a block holding the
            // albedo would make a correct material write invisible.
            var block = new MaterialPropertyBlock();
            renderer.GetPropertyBlock(block);

            foreach (var property in AlbedoProperties)
            {
                text.Append(' ').Append(property).Append('=').Append(Describe(block.GetTexture(property)));
            }
        }

        /// <summary>
        /// When the chosen renderer is not being drawn, names the renderers that are and that share
        /// its material - which is how the costume prefab's <c>Body1</c> gets into the log while the
        /// rule only ever said <c>Body</c>.
        /// </summary>
        private static void AppendSharersIfHidden(
            StringBuilder text, Renderer renderer, Material? sharedMaterial, RendererLookup lookup)
        {
            if (IsActive(renderer) || sharedMaterial == null)
            {
                return;
            }

            var sharers = lookup.ActiveUsersOf(sharedMaterial!, renderer);
            text.AppendLine().Append("  CHOSEN RENDERER IS NOT ACTIVE. Active renderers using ")
                .Append(Describe(sharedMaterial)).Append(": ");

            if (sharers.Count == 0)
            {
                text.Append("<none>");
                return;
            }

            for (var i = 0; i < sharers.Count; i++)
            {
                text.AppendLine().Append("    - ").Append(RendererLookup.PathOf(sharers[i]));
            }
        }

        private static Material[] SafeArray(Func<Material[]> read)
        {
            try
            {
                return read() ?? new Material[0];
            }
            catch (Exception ex)
            {
                // Reading materials off a half-destroyed renderer must not abort a diagnostic whose
                // whole job is to explain a broken situation.
                CwyLsModLoader.Log.LogWarning(
                    $"{Prefix}: could not read materials: {ex.GetType().Name}: {ex.Message}");
                return new Material[0];
            }
        }

        private static string Yes(bool value) => value ? "yes" : "no";
    }
}
