using System.Collections.Generic;
using CwyLs.ModLoader.Core;

namespace CwyLs.ModLoader.Bulbul.Textures
{
    /// <summary>
    /// The decoration seams <see cref="SceneryPatches"/> postfixes so pack textures and material
    /// property rules are re-applied after the game changes a desk, cup or keyboard skin - as
    /// patch-target data, so a game update that renames one is reported at startup
    /// (<c>PATCH TARGET MISSING</c>) instead of the re-apply silently stopping.
    /// </summary>
    /// <remarks>
    /// Observed in game 1.17.3 and checked against the installed <c>Assembly-CSharp.dll</c> with a
    /// metadata-only load: <c>DecorationService.ChangeDecoration</c> (global namespace),
    /// <c>Bulbul.DecorationModelChangerMatTexture.Apply</c> (sets the main texture of a shared
    /// material), <c>Bulbul.DecorationModelChangerCommon.Apply</c> (swaps the renderers'
    /// <c>sharedMaterial</c> and sets <c>mainTexture</c>/<c>_EmissionMap</c>). Static and Unity-free:
    /// seamcheck evaluates <see cref="All"/> offline.
    /// </remarks>
    public static class SceneryPatchTargets
    {
        /// <summary>Assembly every target below lives in.</summary>
        public const string GameAssembly = "Assembly-CSharp";

        /// <summary>The decoration service (global namespace).</summary>
        public const string ServiceTypeName = "DecorationService";

        /// <summary>The changer that swaps a shared material's main texture.</summary>
        public const string MatTextureTypeName = "Bulbul.DecorationModelChangerMatTexture";

        /// <summary>The changer that swaps mesh, shared material and textures.</summary>
        public const string CommonTypeName = "Bulbul.DecorationModelChangerCommon";

        /// <summary>The service method every decoration change goes through.</summary>
        public const string ChangeDecorationMethod = "ChangeDecoration";

        /// <summary>The changers' apply method.</summary>
        public const string ApplyMethod = "Apply";

        private const string SkinType = "DecorationService+DecorationSkinType";
        private const string ModelType = "DecorationService+DecorationModelType";

        /// <summary>The seams, in the order <see cref="SceneryPatches"/> applies them.</summary>
        public static IReadOnlyList<PatchTarget> All { get; } = new[]
        {
            new PatchTarget(
                "scenery.decoration.change", GameAssembly, ServiceTypeName,
                MemberKind.Method, ChangeDecorationMethod,
                new[] { SkinType, "System.Boolean" },
                expectedSignature: "void ChangeDecoration(DecorationService.DecorationSkinType, bool)"),

            new PatchTarget(
                "scenery.decoration.mattexture", GameAssembly, MatTextureTypeName,
                MemberKind.Method, ApplyMethod,
                new[] { ModelType, SkinType, "System.Boolean" },
                expectedSignature: "void Apply(DecorationService.DecorationModelType, DecorationService.DecorationSkinType, bool)"),

            new PatchTarget(
                "scenery.decoration.common", GameAssembly, CommonTypeName,
                MemberKind.Method, ApplyMethod,
                new[] { ModelType, SkinType, "System.Boolean" },
                expectedSignature: "void Apply(DecorationService.DecorationModelType, DecorationService.DecorationSkinType, bool)"),
        };
    }
}
