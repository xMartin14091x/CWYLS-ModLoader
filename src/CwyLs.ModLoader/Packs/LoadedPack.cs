using System;
using System.IO;
using CwyLs.ModLoader.Core.Packs;

namespace CwyLs.ModLoader.Packs
{
    /// <summary>
    /// A pack that passed every gate and is installed on this machine: the Core-side
    /// <see cref="ResolvedPack"/> plus the absolute folder its files live in.
    /// <para>
    /// This is the type the features consume - they open <see cref="Index"/> entries through
    /// <see cref="FullPath(PackFile)"/> to read textures, audio and data files - so it
    /// is deliberately the only place where a pack's contents and its location meet.
    /// </para>
    /// </summary>
    public sealed class LoadedPack
    {
        /// <summary>Creates a loaded pack.</summary>
        /// <param name="manifest">The parsed manifest.</param>
        /// <param name="rootPath">Absolute path of the pack folder.</param>
        /// <param name="index">The classified contents of that folder.</param>
        /// <exception cref="ArgumentNullException"><paramref name="manifest"/> or <paramref name="index"/> is null.</exception>
        /// <exception cref="ArgumentException"><paramref name="rootPath"/> is null or blank.</exception>
        public LoadedPack(PackManifest manifest, string rootPath, PackContentIndex index)
        {
            if (string.IsNullOrWhiteSpace(rootPath))
            {
                throw new ArgumentException("a pack root path is required", nameof(rootPath));
            }

            Manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
            RootPath = rootPath;
            Index = index ?? throw new ArgumentNullException(nameof(index));
        }

        /// <summary>The pack's manifest.</summary>
        public PackManifest Manifest { get; }

        /// <summary>Absolute path of the pack folder, e.g. <c>&lt;game root&gt;/CWYLS Mods/cwy.example</c>.</summary>
        public string RootPath { get; }

        /// <summary>The pack's classified contents.</summary>
        public PackContentIndex Index { get; }

        /// <summary>Shorthand for <c>Manifest.Id</c>.</summary>
        public string Id => Manifest.Id;

        /// <summary>The absolute path of one indexed file.</summary>
        /// <param name="file">An entry of <see cref="Index"/>.</param>
        /// <exception cref="ArgumentNullException"><paramref name="file"/> is null.</exception>
        public string FullPath(PackFile file)
        {
            if (file is null)
            {
                throw new ArgumentNullException(nameof(file));
            }

            return FullPath(file.Path);
        }

        /// <summary>The absolute path of a file given relative to the pack root.</summary>
        /// <param name="relativePath">A forward-slash relative path, as the index stores them.</param>
        /// <exception cref="ArgumentException"><paramref name="relativePath"/> is blank or escapes the pack.</exception>
        public string FullPath(string relativePath)
        {
            var normalised = PackContentIndex.Normalise(relativePath);
            if (normalised is null)
            {
                throw new ArgumentException(
                    "\"" + relativePath + "\" is not a path inside the pack", nameof(relativePath));
            }

            return Path.Combine(RootPath, normalised.Replace('/', Path.DirectorySeparatorChar));
        }

        /// <summary><c>id@version</c>.</summary>
        public override string ToString() => Manifest.ToString();
    }
}
