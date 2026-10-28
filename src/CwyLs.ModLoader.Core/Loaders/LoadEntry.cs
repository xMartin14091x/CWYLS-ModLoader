using System;
using System.Globalization;

namespace CwyLs.ModLoader.Core.Loaders
{
    /// <summary>
    /// One attempt to load one file. Immutable, and produced whether the attempt succeeded or not:
    /// a pack that silently loads 41 of its 42 textures is the failure mode this type exists to
    /// prevent.
    /// </summary>
    public sealed class LoadEntry
    {
        /// <summary>Creates an entry. Prefer <see cref="Success"/> / <see cref="Failure"/>.</summary>
        /// <param name="path">The file this entry is about. Required.</param>
        /// <param name="kind">What it was being loaded as.</param>
        /// <param name="ok">Whether an asset came out.</param>
        /// <param name="detail">Evidence or reason; null becomes empty.</param>
        /// <param name="ms">Wall-clock duration in milliseconds.</param>
        /// <param name="bytes">Size of the file in bytes; 0 when it was never read.</param>
        /// <exception cref="ArgumentException"><paramref name="path"/> is null or empty.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="ms"/> or <paramref name="bytes"/> is negative.</exception>
        public LoadEntry(string path, AssetKind kind, bool ok, string? detail = null, long ms = 0, long bytes = 0)
        {
            if (string.IsNullOrEmpty(path))
            {
                throw new ArgumentException("a path is required", nameof(path));
            }

            if (ms < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(ms), ms, "elapsed time cannot be negative");
            }

            if (bytes < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(bytes), bytes, "size cannot be negative");
            }

            Path = path;
            Kind = kind;
            Ok = ok;
            Detail = detail ?? string.Empty;
            Ms = ms;
            Bytes = bytes;
        }

        /// <summary>The file this entry is about, as the caller spelled it.</summary>
        public string Path { get; }

        /// <summary>What the file was being loaded as.</summary>
        public AssetKind Kind { get; }

        /// <summary>True when an asset was produced.</summary>
        public bool Ok { get; }

        /// <summary>Evidence on success ("512x512 RGBA32"), the reason on failure. Never null.</summary>
        public string Detail { get; }

        /// <summary>Wall-clock duration in milliseconds.</summary>
        public long Ms { get; }

        /// <summary>Size of the file in bytes, or 0 when it was never read.</summary>
        public long Bytes { get; }

        /// <summary>A loaded file.</summary>
        public static LoadEntry Success(string path, AssetKind kind, string? detail = null, long ms = 0, long bytes = 0) =>
            new LoadEntry(path, kind, true, detail, ms, bytes);

        /// <summary>A file that did not load. <paramref name="detail"/> is what the log line will say.</summary>
        public static LoadEntry Failure(string path, AssetKind kind, string detail, long ms = 0, long bytes = 0) =>
            new LoadEntry(path, kind, false, detail, ms, bytes);

        /// <summary>One report line: <c>ok texture 12ms path - detail</c>.</summary>
        public override string ToString() =>
            string.Format(
                CultureInfo.InvariantCulture,
                "{0} {1} {2}ms {3}{4}",
                Ok ? "ok" : "FAILED",
                Kind.ToString().ToLowerInvariant(),
                Ms,
                Path,
                Detail.Length == 0 ? string.Empty : " - " + Detail);
    }
}
