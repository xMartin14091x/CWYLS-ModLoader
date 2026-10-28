using System;

namespace CwyLs.ModLoader.Core.Loaders
{
    /// <summary>
    /// What <see cref="FileKindDetector.Detect"/> made of one file. Immutable.
    /// <see cref="Reason"/> is filled in for accepted files too - it says <em>how</em> the
    /// verdict was reached, which is what ends up in the per-file line of a
    /// <see cref="LoadReport"/> and is the difference between "it did not load" and
    /// "it did not load because the .png is really a JPEG".
    /// </summary>
    public sealed class FileKindDetection
    {
        private FileKindDetection(AssetFormat format, bool supported, string reason)
        {
            Format = format;
            Supported = supported;
            Reason = reason;
            Kind = KindOf(format);
        }

        /// <summary>The encoding that was recognised, or <see cref="AssetFormat.Unknown"/>.</summary>
        public AssetFormat Format { get; }

        /// <summary>What the file would become. <see cref="AssetKind.Unknown"/> when nothing was recognised.</summary>
        public AssetKind Kind { get; }

        /// <summary>True when a loader can be handed this file.</summary>
        public bool Supported { get; }

        /// <summary>Why this verdict. Never null, never empty.</summary>
        public string Reason { get; }

        /// <summary>The kind a format belongs to.</summary>
        public static AssetKind KindOf(AssetFormat format)
        {
            switch (format)
            {
                case AssetFormat.Png:
                case AssetFormat.Jpeg:
                    return AssetKind.Texture;
                case AssetFormat.Wav:
                case AssetFormat.OggVorbis:
                    return AssetKind.Audio;
                default:
                    return AssetKind.Unknown;
            }
        }

        /// <summary>A loadable file.</summary>
        /// <param name="format">Recognised format; must be one a loader handles.</param>
        /// <param name="reason">How the format was decided, e.g. <c>PNG header</c>.</param>
        /// <exception cref="ArgumentException">The format is not loadable, or the reason is blank.</exception>
        public static FileKindDetection Accepted(AssetFormat format, string reason)
        {
            if (format != AssetFormat.Png && format != AssetFormat.Wav && format != AssetFormat.OggVorbis)
            {
                throw new ArgumentException("format is not loadable: " + format, nameof(format));
            }

            if (string.IsNullOrEmpty(reason))
            {
                throw new ArgumentException("a reason is required", nameof(reason));
            }

            return new FileKindDetection(format, true, reason);
        }

        /// <summary>A file no loader will touch.</summary>
        /// <param name="format">What it turned out to be, if anything.</param>
        /// <param name="reason">Why it was rejected - written for the person who made the pack.</param>
        /// <exception cref="ArgumentException"><paramref name="reason"/> is blank.</exception>
        public static FileKindDetection Rejected(AssetFormat format, string reason)
        {
            if (string.IsNullOrEmpty(reason))
            {
                throw new ArgumentException("a reason is required", nameof(reason));
            }

            return new FileKindDetection(format, false, reason);
        }

        /// <summary>Format and reason, for a log line.</summary>
        public override string ToString() => Format + ": " + Reason;
    }
}
