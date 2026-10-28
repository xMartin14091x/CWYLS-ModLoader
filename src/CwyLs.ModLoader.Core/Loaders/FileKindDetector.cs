using System;
using System.Text;

namespace CwyLs.ModLoader.Core.Loaders
{
    /// <summary>
    /// Decides what a pack file is, from its extension and the first few bytes of its content.
    /// <para>
    /// Content wins over extension whenever both are available. A pack is hand-made: a file is far
    /// more often renamed than re-encoded, so the bytes are the truth and a mismatched extension is
    /// a note on an otherwise successful detection, not a failure. With no bytes to look at the
    /// extension is all there is, and the reason string says so.
    /// </para>
    /// </summary>
    public static class FileKindDetector
    {
        /// <summary>
        /// How many leading bytes are enough for every signature here. A caller that streams a
        /// file only needs this much of it (12 are actually used; 16 is a round number with room).
        /// </summary>
        public const int HeaderBytes = 16;

        private static readonly byte[] PngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

        /// <summary>
        /// Detects the format of <paramref name="path"/>.
        /// </summary>
        /// <param name="path">
        /// Path to the file. Only its spelling is used - nothing is read from disk, which is what
        /// keeps this type Unity-free and unit-testable.
        /// </param>
        /// <param name="firstBytes">
        /// The first <see cref="HeaderBytes"/> bytes of the file, or null/empty when the caller has
        /// not read the file yet. More bytes than that are fine; fewer are used as far as they go.
        /// </param>
        /// <returns>Never null. Check <see cref="FileKindDetection.Supported"/> before loading.</returns>
        /// <exception cref="ArgumentException"><paramref name="path"/> is blank or has no file name.</exception>
        public static FileKindDetection Detect(string path, byte[]? firstBytes = null)
        {
            var key = AssetKey.FromPath(path);
            var fromExtension = FormatOfExtension(key.Extension);

            if (firstBytes is null || firstBytes.Length == 0)
            {
                return FromExtensionAlone(key, fromExtension);
            }

            var fromHeader = Sniff(firstBytes);

            switch (fromHeader)
            {
                case AssetFormat.Jpeg:
                    return FileKindDetection.Rejected(
                        AssetFormat.Jpeg,
                        "JPEG is not supported; re-save '" + key.Name + "' as PNG");

                case AssetFormat.Unknown:
                    return FileKindDetection.Rejected(
                        AssetFormat.Unknown,
                        "unrecognised file header " + Hex(firstBytes) + RiffNote(firstBytes) +
                        "; expected PNG, WAV or Ogg");

                default:
                    // A recognised, loadable header. Say so, and flag a lying extension.
                    return FileKindDetection.Accepted(
                        fromHeader,
                        fromExtension == fromHeader
                            ? Label(fromHeader) + " header"
                            : Label(fromHeader) + " header (extension '" +
                              (key.Extension.Length == 0 ? "none" : key.Extension) + "' says otherwise)");
            }
        }

        private static FileKindDetection FromExtensionAlone(AssetKey key, AssetFormat fromExtension)
        {
            switch (fromExtension)
            {
                case AssetFormat.Png:
                case AssetFormat.Wav:
                case AssetFormat.OggVorbis:
                    return FileKindDetection.Accepted(
                        fromExtension, "extension '" + key.Extension + "' (no header bytes were read)");

                case AssetFormat.Jpeg:
                    return FileKindDetection.Rejected(
                        AssetFormat.Jpeg,
                        "JPEG is not supported; re-save '" + key.Name + "' as PNG");

                default:
                    return FileKindDetection.Rejected(
                        AssetFormat.Unknown,
                        key.Extension.Length == 0
                            ? "no extension; expected .png, .wav or .ogg"
                            : "unsupported extension '" + key.Extension + "'; expected .png, .wav or .ogg");
            }
        }

        private static AssetFormat FormatOfExtension(string extension)
        {
            switch (extension)
            {
                case ".png": return AssetFormat.Png;
                case ".wav": return AssetFormat.Wav;
                case ".ogg": return AssetFormat.OggVorbis;
                case ".jpg":
                case ".jpeg": return AssetFormat.Jpeg;
                default: return AssetFormat.Unknown;
            }
        }

        /// <summary>
        /// Reads the magic bytes. A RIFF container that is not WAVE (an AVI, say) returns
        /// <see cref="AssetFormat.Unknown"/> on purpose - <see cref="RiffNote"/> then explains it.
        /// </summary>
        private static AssetFormat Sniff(byte[] bytes)
        {
            if (StartsWith(bytes, PngSignature))
            {
                return AssetFormat.Png;
            }

            if (Ascii(bytes, 0, "OggS"))
            {
                return AssetFormat.OggVorbis;
            }

            if (Ascii(bytes, 0, "RIFF") && Ascii(bytes, 8, "WAVE"))
            {
                return AssetFormat.Wav;
            }

            // FF D8 FF covers JFIF, Exif and raw JPEG alike.
            if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
            {
                return AssetFormat.Jpeg;
            }

            return AssetFormat.Unknown;
        }

        private static string RiffNote(byte[] bytes) =>
            Ascii(bytes, 0, "RIFF") ? " (a RIFF container, but not WAVE)" : string.Empty;

        private static string Label(AssetFormat format) => format == AssetFormat.OggVorbis ? "Ogg" : format.ToString().ToUpperInvariant();

        private static bool StartsWith(byte[] bytes, byte[] signature)
        {
            if (bytes.Length < signature.Length)
            {
                return false;
            }

            for (var i = 0; i < signature.Length; i++)
            {
                if (bytes[i] != signature[i])
                {
                    return false;
                }
            }

            return true;
        }

        private static bool Ascii(byte[] bytes, int offset, string expected)
        {
            if (bytes.Length < offset + expected.Length)
            {
                return false;
            }

            for (var i = 0; i < expected.Length; i++)
            {
                if (bytes[offset + i] != (byte)expected[i])
                {
                    return false;
                }
            }

            return true;
        }

        private static string Hex(byte[] bytes)
        {
            var count = Math.Min(4, bytes.Length);
            var builder = new StringBuilder(count * 3);
            for (var i = 0; i < count; i++)
            {
                if (i > 0)
                {
                    builder.Append(' ');
                }

                builder.Append(bytes[i].ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
            }

            return builder.ToString();
        }
    }
}
