using System;
using System.Text;

namespace CwyLs.ModLoader.Core.Loaders
{
    /// <summary>
    /// A pack file addressed the way the rest of the framework wants it: a normalised
    /// forward-slash path, plus the bare <see cref="Name"/> the game will see.
    /// <para>
    /// <see cref="Name"/> is load-bearing, not cosmetic. Texture replacement assigns the loaded
    /// <c>Texture2D.name</c> to a lilToon material slot and voice replacement registers an
    /// <c>AudioClip</c> into KanKikuchi's <c>VoiceManager</c> cache <em>by clip name</em>,
    /// where the game's own keys are plain names such as <c>Voice_Tutorial_1_001</c>.
    /// So the file <c>audio/voice/Voice_Tutorial_1_001.ogg</c> must yield exactly
    /// <c>Voice_Tutorial_1_001</c>, with no folder, no extension and no case change.
    /// </para>
    /// </summary>
    public readonly struct AssetKey : IEquatable<AssetKey>
    {
        private AssetKey(string path, string name, string extension)
        {
            Path = path;
            Name = name;
            Extension = extension;
        }

        /// <summary>
        /// The normalised path: backslashes turned into <c>/</c>, repeated separators collapsed,
        /// <c>.</c> segments dropped. A leading <c>/</c> is preserved, and <c>..</c> segments are
        /// deliberately <em>not</em> resolved - this type normalises spelling, it does not walk a
        /// file system.
        /// </summary>
        public string Path { get; }

        /// <summary>
        /// File name without its extension - the name the game will address the asset by.
        /// A leading dot is not an extension, so <c>.keep</c> has the name <c>.keep</c>, and only
        /// the last extension is removed, so <c>Voice.v2.ogg</c> has the name <c>Voice.v2</c>.
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// Extension including the dot, lower-cased (<c>.png</c>), or empty when there is none.
        /// Lower-cased because it is only ever compared against a fixed set of known extensions;
        /// <see cref="Name"/> and <see cref="Path"/> keep the author's casing.
        /// </summary>
        public string Extension { get; }

        /// <summary>
        /// Builds a key from a pack-relative (or absolute) path.
        /// </summary>
        /// <param name="path">Path to a file. Either separator style is accepted.</param>
        /// <exception cref="ArgumentException">
        /// The path is null, blank, or does not end in a file name (e.g. <c>textures/</c>).
        /// </exception>
        public static AssetKey FromPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("a path is required", nameof(path));
            }

            var last = path![path.Length - 1];
            if (last == '/' || last == '\\')
            {
                // "textures/" would otherwise normalise to the file name "textures".
                throw new ArgumentException("path has no file name: " + path, nameof(path));
            }

            var normalised = Normalise(path);
            if (normalised.Length == 0)
            {
                throw new ArgumentException("path has no file name: " + path, nameof(path));
            }

            var lastSlash = normalised.LastIndexOf('/');
            var fileName = lastSlash < 0 ? normalised : normalised.Substring(lastSlash + 1);
            if (fileName.Length == 0)
            {
                throw new ArgumentException("path has no file name: " + path, nameof(path));
            }

            // IndexOf > 0: a leading dot is part of the name (".keep"), never an extension.
            var dot = fileName.LastIndexOf('.');
            var hasExtension = dot > 0;

            return new AssetKey(
                normalised,
                hasExtension ? fileName.Substring(0, dot) : fileName,
                hasExtension ? fileName.Substring(dot).ToLowerInvariant() : string.Empty);
        }

        private static string Normalise(string path)
        {
            var rooted = path[0] == '/' || path[0] == '\\';
            var builder = new StringBuilder(path.Length);
            if (rooted)
            {
                builder.Append('/');
            }

            var segmentStart = 0;
            var wrote = false;
            for (var i = 0; i <= path.Length; i++)
            {
                var atEnd = i == path.Length;
                if (!atEnd && path[i] != '/' && path[i] != '\\')
                {
                    continue;
                }

                var length = i - segmentStart;
                segmentStart = i + 1;

                // Drops "" (a repeated separator) and "." (a no-op segment).
                if (length == 0 || (length == 1 && path[i - 1] == '.'))
                {
                    continue;
                }

                if (wrote)
                {
                    builder.Append('/');
                }

                builder.Append(path, i - length, length);
                wrote = true;
            }

            return builder.ToString();
        }

        /// <summary>Two keys are equal when their normalised paths are, byte for byte.</summary>
        /// <remarks>
        /// Ordinal on purpose: macOS is case-insensitive and Windows is too, but the game's clip
        /// and texture names are case-sensitive, so a key that only differs in case addresses a
        /// different asset as far as the framework is concerned.
        /// </remarks>
        public bool Equals(AssetKey other) => string.Equals(Path, other.Path, StringComparison.Ordinal);

        /// <inheritdoc />
        public override bool Equals(object? obj) => obj is AssetKey other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode() => Path is null ? 0 : StringComparer.Ordinal.GetHashCode(Path);

        /// <summary>Equality operator; see <see cref="Equals(AssetKey)"/>.</summary>
        public static bool operator ==(AssetKey left, AssetKey right) => left.Equals(right);

        /// <summary>Inequality operator; see <see cref="Equals(AssetKey)"/>.</summary>
        public static bool operator !=(AssetKey left, AssetKey right) => !left.Equals(right);

        /// <summary>The normalised path.</summary>
        public override string ToString() => Path ?? string.Empty;
    }
}
