using System.Collections.Generic;
using CwyLs.ModLoader.Core;

namespace CwyLs.ModLoader.Bulbul.Textures
{
    /// <summary>
    /// The four costume seams <see cref="CostumePatches"/> postfixes to re-apply pack textures after
    /// a costume change, as patch-target data, so a game update that renames one is reported at
    /// startup (<c>PATCH TARGET MISSING</c>) instead of the re-apply silently stopping.
    /// </summary>
    /// <remarks>
    /// Signatures match <c>Bulbul.CostumeChangeService</c> and <c>Bulbul.CostumeSkinChanger</c> in
    /// game 1.17.3 and were checked against the shipped assembly through a metadata-only load.
    /// </remarks>
    public static class CostumePatchTargets
    {
        /// <summary>Assembly every target below lives in.</summary>
        public const string GameAssembly = "Assembly-CSharp";

        /// <summary>The service that swaps Satone's costume prefab and skin.</summary>
        public const string ServiceTypeName = "Bulbul.CostumeChangeService";

        /// <summary>The component that points the shirt renderer at another material.</summary>
        public const string SkinChangerTypeName = "Bulbul.CostumeSkinChanger";

        /// <summary>Last step of a non-default model change: the new renderers exist from here.</summary>
        public const string AttachBonesMethod = "AttachCostumeBonesToCharacter";

        /// <summary>The default-costume branch: the five default objects come back on.</summary>
        public const string ActivateDefaultMethod = "ActivateDefaultCostume";

        /// <summary>The last thing a costume change does: the "finished" signal.</summary>
        public const string TryChangeSkinMethod = "TryChangeSkin";

        /// <summary>The shirt renderer's material swap.</summary>
        public const string ChangeSkinMethod = "ChangeSkin";

        /// <summary>The seams, in the order <see cref="CostumePatches"/> applies them.</summary>
        public static IReadOnlyList<PatchTarget> All { get; } = new[]
        {
            // Private.
            new PatchTarget(
                "textures.costume.attachbones", GameAssembly, ServiceTypeName,
                MemberKind.Method, AttachBonesMethod,
                new[] { "UnityEngine.Transform" },
                expectedSignature: "void AttachCostumeBonesToCharacter(Transform)"),

            // Private.
            new PatchTarget(
                "textures.costume.activatedefault", GameAssembly, ServiceTypeName,
                MemberKind.Method, ActivateDefaultMethod,
                expectedSignature: "void ActivateDefaultCostume()"),

            // Private.
            new PatchTarget(
                "textures.costume.trychangeskin", GameAssembly, ServiceTypeName,
                MemberKind.Method, TryChangeSkinMethod,
                new[] { "Bulbul.CostumeChangeService+CostumeSkinType" },
                expectedSignature: "bool TryChangeSkin(CostumeChangeService.CostumeSkinType)"),

            new PatchTarget(
                "textures.costume.changeskin", GameAssembly, SkinChangerTypeName,
                MemberKind.Method, ChangeSkinMethod,
                new[] { "UnityEngine.Material" },
                expectedSignature: "void ChangeSkin(Material)"),
        };
    }
}
