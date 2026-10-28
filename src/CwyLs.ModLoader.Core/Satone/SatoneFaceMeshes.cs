using System;
using System.Collections.Generic;

namespace CwyLs.ModLoader.Core.Satone
{
    /// <summary>
    /// The face blendshapes one expression actually drives, grouped by family.
    /// </summary>
    /// <remarks>
    /// Satone's face is a single skinned mesh (<c>Character/Face</c>) with 47 blendshapes whose
    /// names start with <c>Eye_</c>, <c>Eyeblow_</c> (the game's spelling) or <c>Mouth_</c>. Every
    /// <c>anim_face_*</c> clip carries a curve for all of them and leaves most at zero, so the
    /// generator keeps only the shapes a clip pushes above zero. That is what makes two
    /// expressions comparable - <c>smile</c> and <c>fun</c> differ by exactly these lists.
    /// </remarks>
    public sealed class SatoneFaceMeshes
    {
        private static readonly string[] None = new string[0];

        /// <summary>An expression that drives nothing.</summary>
        public static readonly SatoneFaceMeshes Empty = new SatoneFaceMeshes(None, None, None);

        /// <summary>Creates a mesh set. Null lists become empty ones.</summary>
        /// <param name="eye">Blendshapes named <c>Eye_*</c>.</param>
        /// <param name="brow">Blendshapes named <c>Eyeblow_*</c>.</param>
        /// <param name="mouth">Blendshapes named <c>Mouth_*</c>.</param>
        public SatoneFaceMeshes(IReadOnlyList<string>? eye, IReadOnlyList<string>? brow, IReadOnlyList<string>? mouth)
        {
            Eye = eye ?? None;
            Brow = brow ?? None;
            Mouth = mouth ?? None;
        }

        /// <summary>Blendshapes named <c>Eye_*</c>, in the order the generator wrote them (sorted).</summary>
        public IReadOnlyList<string> Eye { get; }

        /// <summary>Blendshapes named <c>Eyeblow_*</c> - the game's spelling of "eyebrow".</summary>
        public IReadOnlyList<string> Brow { get; }

        /// <summary>Blendshapes named <c>Mouth_*</c>.</summary>
        public IReadOnlyList<string> Mouth { get; }

        /// <summary>Total number of blendshapes this expression drives.</summary>
        public int Count => Eye.Count + Brow.Count + Mouth.Count;

        /// <summary>A one-line summary for a log line, e.g. <c>eye 1, brow 2, mouth 3</c>.</summary>
        public override string ToString() =>
            "eye " + Eye.Count + ", brow " + Brow.Count + ", mouth " + Mouth.Count;
    }
}
