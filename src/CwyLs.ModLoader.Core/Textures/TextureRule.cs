using System;

namespace CwyLs.ModLoader.Core.Textures
{
    /// <summary>
    /// One texture replacement: "in pack <c>P</c>, put the file at <c>RelativePath</c> into
    /// property <c>PropertyName</c> of material <c>MaterialIndex</c> on the renderer called
    /// <c>RendererName</c>".
    /// <para>
    /// Deliberately a plain value with no Unity in it: rules are produced from a file listing or a
    /// <c>_rules.json</c>, merged across packs, and unit-tested, all without an engine. The Unity
    /// side (<c>CwyLs.ModLoader.Bulbul.Textures.TextureFeature</c>) only consumes them.
    /// </para>
    /// </summary>
    public sealed class TextureRule
    {
        /// <summary>The material index a rule uses when nothing says otherwise.</summary>
        public const int DefaultMaterialIndex = 0;

        /// <summary>Creates a rule.</summary>
        /// <param name="packId">Id of the pack the file came from, e.g. <c>cwy.example</c>.</param>
        /// <param name="rendererName">Name of the renderer's GameObject, e.g. <c>Body</c>.</param>
        /// <param name="propertyName">Shader property, e.g. <c>_MainTex</c> - already resolved from any alias.</param>
        /// <param name="relativePath">Path of the PNG relative to the pack root, forward slashes.</param>
        /// <param name="materialIndex">Index into the renderer's materials; 0 unless a rule says otherwise.</param>
        /// <param name="source">Where the rule came from, for the log. Defaults to <see cref="TextureRuleSource.Convention"/>.</param>
        /// <exception cref="ArgumentException">Any of the strings is blank, or <paramref name="materialIndex"/> is negative.</exception>
        public TextureRule(
            string packId,
            string rendererName,
            string propertyName,
            string relativePath,
            int materialIndex = DefaultMaterialIndex,
            TextureRuleSource source = TextureRuleSource.Convention)
        {
            if (string.IsNullOrWhiteSpace(packId))
            {
                throw new ArgumentException("a pack id is required", nameof(packId));
            }

            if (string.IsNullOrWhiteSpace(rendererName))
            {
                throw new ArgumentException("a renderer name is required", nameof(rendererName));
            }

            if (string.IsNullOrWhiteSpace(propertyName))
            {
                throw new ArgumentException("a shader property name is required", nameof(propertyName));
            }

            if (string.IsNullOrWhiteSpace(relativePath))
            {
                throw new ArgumentException("a relative path is required", nameof(relativePath));
            }

            if (materialIndex < 0)
            {
                throw new ArgumentException("a material index cannot be negative", nameof(materialIndex));
            }

            PackId = packId;
            TargetName = rendererName;
            Target = TextureRuleTarget.Renderer;
            PropertyName = propertyName;
            RelativePath = relativePath;
            MaterialIndex = materialIndex;
            Source = source;
        }

        private TextureRule(
            string packId, string materialName, string propertyName, string relativePath, TextureRuleSource source)
        {
            PackId = packId;
            TargetName = materialName;
            Target = TextureRuleTarget.Material;
            PropertyName = propertyName;
            RelativePath = relativePath;

            // A material rule has no index: it names the material directly, wherever it is used.
            MaterialIndex = DefaultMaterialIndex;
            Source = source;
        }

        /// <summary>
        /// A rule that names a material asset rather than a renderer.
        /// </summary>
        /// <param name="packId">Id of the pack the file came from.</param>
        /// <param name="materialName">Material asset name, e.g. <c>mt_Tee_A</c>. No <c>@</c> prefix.</param>
        /// <param name="propertyName">Shader property, already resolved from any alias.</param>
        /// <param name="relativePath">Path of the PNG relative to the pack root, forward slashes.</param>
        /// <param name="source">Where the rule came from.</param>
        /// <exception cref="ArgumentException">Any of the strings is blank.</exception>
        public static TextureRule ForMaterial(
            string packId,
            string materialName,
            string propertyName,
            string relativePath,
            TextureRuleSource source = TextureRuleSource.Convention)
        {
            if (string.IsNullOrWhiteSpace(packId))
            {
                throw new ArgumentException("a pack id is required", nameof(packId));
            }

            if (string.IsNullOrWhiteSpace(materialName))
            {
                throw new ArgumentException("a material name is required", nameof(materialName));
            }

            if (string.IsNullOrWhiteSpace(propertyName))
            {
                throw new ArgumentException("a shader property name is required", nameof(propertyName));
            }

            if (string.IsNullOrWhiteSpace(relativePath))
            {
                throw new ArgumentException("a relative path is required", nameof(relativePath));
            }

            return new TextureRule(packId, materialName, propertyName, relativePath, source);
        }

        /// <summary>The marker that turns a folder name into a material name: <c>textures/@mt_Tee_A/</c>.</summary>
        public const char MaterialPrefix = '@';

        /// <summary>Id of the pack that owns the file.</summary>
        public string PackId { get; }

        /// <summary>Whether this rule names a renderer or a material.</summary>
        public TextureRuleTarget Target { get; }

        /// <summary>The name this rule targets: a renderer's GameObject name, or a material's name.</summary>
        public string TargetName { get; }

        /// <summary>
        /// Name of the GameObject carrying the renderer, e.g. <c>Body</c>. Empty for a material rule.
        /// </summary>
        public string RendererName =>
            Target == TextureRuleTarget.Renderer ? TargetName : string.Empty;

        /// <summary>
        /// Name of the material asset, e.g. <c>mt_Tee_A</c>. Empty for a renderer rule.
        /// </summary>
        public string MaterialName =>
            Target == TextureRuleTarget.Material ? TargetName : string.Empty;

        /// <summary>The shader property to write, e.g. <c>_MainTex</c>. Never an alias.</summary>
        public string PropertyName { get; }

        /// <summary>Path of the PNG relative to the pack root, e.g. <c>textures/Body/_MainTex.png</c>.</summary>
        public string RelativePath { get; }

        /// <summary>Which of the renderer's materials to write. 0 for every folder-convention rule.</summary>
        public int MaterialIndex { get; }

        /// <summary>Whether the rule came from the folder layout or from an explicit <c>_rules.json</c>.</summary>
        public TextureRuleSource Source { get; }

        /// <summary>
        /// What makes two rules the same target: renderer, material index and property. Two rules
        /// with this key cannot both apply - the later pack's wins (see <see cref="TextureRuleSet.Merge"/>).
        /// Renderer and property are folded case-insensitively so <c>body/_maintex.png</c> and
        /// <c>Body/_MainTex.png</c> are one rule, not two that fight.
        /// </summary>
        public string Key =>
            (Target == TextureRuleTarget.Material
                ? MaterialPrefix + TargetName.ToLowerInvariant()
                : TargetName.ToLowerInvariant() + "[" + MaterialIndex + "]") +
            "." + PropertyName.ToLowerInvariant();

        /// <summary>
        /// The slot this rule writes, in the author's own casing:
        /// <c>Body[0]._MainTex</c>, or <c>@mt_Tee_A._MainTex</c>. Unlike <see cref="Key"/> this is
        /// for people to read, so it is never case-folded.
        /// </summary>
        public string Slot =>
            (Target == TextureRuleTarget.Material
                ? MaterialPrefix + TargetName
                : TargetName + "[" + MaterialIndex + "]") +
            "." + PropertyName;

        /// <summary>
        /// The line this rule logs as when it is applied, minus the texture size the engine adds:
        /// <c>Body[0]._MainTex &lt;- cwy.example/textures/Body/_MainTex.png</c>, or
        /// <c>@mt_Tee_A._MainTex &lt;- cwy.example/textures/@mt_Tee_A/_MainTex.png</c>.
        /// </summary>
        public override string ToString() => Slot + " <- " + PackId + "/" + RelativePath;
    }
}
