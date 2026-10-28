using System;
using CwyLs.ModLoader.Core.Textures;
using UnityEngine;

namespace CwyLs.ModLoader.Bulbul.Textures
{
    /// <summary>
    /// One rule that actually reached a live material this session: the rule, where the renderer
    /// was found, and what was put there.
    /// <para>
    /// Kept so the smoke checks and any mod can ask "is my texture on Satone right now?" without
    /// re-walking the scene, and so a failed apply is distinguishable from one that never ran.
    /// </para>
    /// </summary>
    public sealed class AppliedTexture
    {
        /// <summary>Records an applied rule.</summary>
        /// <param name="rule">The rule that was applied.</param>
        /// <param name="rendererPath">Scene path of the renderer, e.g. <c>Character/Character/Body</c>.</param>
        /// <param name="texture">The texture that was assigned; must be alive.</param>
        /// <param name="detail">
        /// What the renderer looked like and which slots were written, e.g.
        /// <c>[active,mpb] -&gt; instance:_MainTex+shared:_MainTex+shared:_BaseMap</c>. Null becomes empty.
        /// </param>
        /// <exception cref="ArgumentNullException"><paramref name="rule"/> or <paramref name="texture"/> is null.</exception>
        /// <exception cref="ArgumentException"><paramref name="rendererPath"/> is blank.</exception>
        public AppliedTexture(TextureRule rule, string rendererPath, Texture texture, string? detail = null)
        {
            if (string.IsNullOrWhiteSpace(rendererPath))
            {
                throw new ArgumentException("a renderer path is required", nameof(rendererPath));
            }

            if (texture == null)
            {
                throw new ArgumentNullException(nameof(texture));
            }

            Rule = rule ?? throw new ArgumentNullException(nameof(rule));
            RendererPath = rendererPath;
            TextureName = texture.name;
            Width = texture.width;
            Height = texture.height;
            Detail = detail ?? string.Empty;
        }

        /// <summary>
        /// The renderer's state and the slots that were written, for the log line and the smoke
        /// detail. Never null; this is what distinguishes "applied to the renderer the player sees"
        /// from "applied to an inactive one".
        /// </summary>
        public string Detail { get; }

        /// <summary>The rule that was applied.</summary>
        public TextureRule Rule { get; }

        /// <summary>Scene path of the renderer the rule landed on.</summary>
        public string RendererPath { get; }

        /// <summary>
        /// <c>Texture2D.name</c> of what was assigned. The loader names a texture after its file, so
        /// this is the file stem - <c>_MainTex</c> for <c>textures/Body/_MainTex.png</c> - and it is
        /// how a scene inspector tells our texture from the game's.
        /// </summary>
        public string TextureName { get; }

        /// <summary>Width in pixels of the assigned texture, as it was at apply time.</summary>
        public int Width { get; }

        /// <summary>Height in pixels of the assigned texture, as it was at apply time.</summary>
        public int Height { get; }

        /// <summary>
        /// The <c>TEXTURE</c> log line for this apply:
        /// <c>Body[0]._MainTex &lt;- cwy.example/textures/Body/_MainTex.png (1024x1024)</c>.
        /// </summary>
        public override string ToString() =>
            Rule + " (" + Width + "x" + Height + ")" +
            (Detail.Length == 0 ? string.Empty : " " + Detail);
    }
}
