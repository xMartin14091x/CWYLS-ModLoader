using System;

namespace CwyLs.ModLoader.Core.Packs
{
    /// <summary>
    /// One pack that passed every gate, with its manifest and its classified contents. The
    /// Unity-side <c>LoadedPack</c> adds the absolute root path; this type stays filesystem-free
    /// so the whole resolution is testable off-engine.
    /// </summary>
    public sealed class ResolvedPack
    {
        /// <summary>Creates a resolved pack.</summary>
        /// <param name="source">The source it came from.</param>
        /// <param name="manifest">Its parsed manifest.</param>
        /// <param name="index">Its classified contents.</param>
        /// <param name="gameVersionCheck">
        /// What its game-version fields said about the running game, or null when nothing was
        /// checked (a pack built by hand). <see cref="PackResolver"/> always sets it.
        /// </param>
        /// <exception cref="ArgumentNullException"><paramref name="source"/>, <paramref name="manifest"/> or <paramref name="index"/> is null.</exception>
        public ResolvedPack(
            IPackSource source,
            PackManifest manifest,
            PackContentIndex index,
            PackGameVersionCheck? gameVersionCheck = null)
        {
            Source = source ?? throw new ArgumentNullException(nameof(source));
            Manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
            Index = index ?? throw new ArgumentNullException(nameof(index));
            GameVersionCheck = gameVersionCheck;
        }

        /// <summary>The source this pack was read from.</summary>
        public IPackSource Source { get; }

        /// <summary>The parsed manifest.</summary>
        public PackManifest Manifest { get; }

        /// <summary>The classified contents.</summary>
        public PackContentIndex Index { get; }

        /// <summary>
        /// What the manifest's <c>gameVersion</c>/<c>minGameVersion</c>/<c>maxGameVersion</c> said
        /// about the running game, including the warning to log, or null when nothing was checked.
        /// </summary>
        public PackGameVersionCheck? GameVersionCheck { get; }

        /// <summary>Shorthand for <c>Manifest.Id</c>.</summary>
        public string Id => Manifest.Id;

        /// <summary><c>id@version</c>.</summary>
        public override string ToString() => Manifest.ToString();
    }
}
