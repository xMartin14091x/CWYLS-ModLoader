using System;

namespace CwyLs.ModLoader.Core.Packs
{
    /// <summary>
    /// One file inside a pack, as classified by <see cref="PackContentIndex"/>. The path is always
    /// relative to the pack root and always uses forward slashes, so an index built on Windows and
    /// one built on macOS are identical.
    /// </summary>
    public sealed class PackFile
    {
        /// <summary>Creates an entry.</summary>
        /// <param name="path">Normalised relative path, forward slashes, no leading separator.</param>
        /// <param name="kind">What the conventions say this file is.</param>
        /// <exception cref="ArgumentException"><paramref name="path"/> is null or blank.</exception>
        public PackFile(string path, PackContentKind kind)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("a relative path is required", nameof(path));
            }

            Path = path;
            Kind = kind;

            var slash = path.LastIndexOf('/');
            var fileName = slash < 0 ? path : path.Substring(slash + 1);
            var dot = fileName.LastIndexOf('.');

            FileName = fileName;
            Name = dot <= 0 ? fileName : fileName.Substring(0, dot);
            Extension = dot < 0 ? string.Empty : fileName.Substring(dot).ToLowerInvariant();
        }

        /// <summary>Path relative to the pack root, with forward slashes, e.g. <c>audio/voice/x.ogg</c>.</summary>
        public string Path { get; }

        /// <summary>What the folder conventions say this file is.</summary>
        public PackContentKind Kind { get; }

        /// <summary>File name with extension, e.g. <c>x.ogg</c>.</summary>
        public string FileName { get; }

        /// <summary>
        /// File name without extension, e.g. <c>x</c>. For a voice clip this is the name the game's
        /// audio cache keys on, which is why it is precomputed here.
        /// </summary>
        public string Name { get; }

        /// <summary>Lower-cased extension including the dot, e.g. <c>.ogg</c>; empty when there is none.</summary>
        public string Extension { get; }

        /// <inheritdoc />
        public override string ToString() => Path + " (" + Kind + ")";
    }
}
