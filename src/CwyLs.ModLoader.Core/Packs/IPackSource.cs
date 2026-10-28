using System.Collections.Generic;

namespace CwyLs.ModLoader.Core.Packs
{
    /// <summary>
    /// Everything <see cref="PackResolver"/> needs to know about one candidate pack, with no
    /// filesystem behind it. A real pack folder implements this by reading the directory; a test
    /// implements it with literals. Nothing here may touch Unity, BepInEx or <c>System.IO</c>.
    /// </summary>
    public interface IPackSource
    {
        /// <summary>
        /// How this candidate is named in reports <em>before</em> its manifest has been read -
        /// normally the folder name. Once the manifest parses, its own <c>id</c> takes over.
        /// </summary>
        string Id { get; }

        /// <summary>The pack's folder name, for a message that has to point at a directory.</summary>
        string FolderName { get; }

        /// <summary>
        /// The whole text of the pack's <c>manifest.json</c>, or null when the folder has none or
        /// it could not be read. Null is <see cref="PackSkipReason.MissingManifest"/>.
        /// </summary>
        string? ManifestText { get; }

        /// <summary>
        /// Every file in the pack, relative to the pack root, in any separator style;
        /// <see cref="PackContentIndex.Build"/> normalises them. Never null.
        /// </summary>
        IReadOnlyList<string> Files { get; }
    }
}
