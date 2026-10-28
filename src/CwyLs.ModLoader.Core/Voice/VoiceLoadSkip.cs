using System;

namespace CwyLs.ModLoader.Core.Voice
{
    /// <summary>
    /// A file in a pack's <c>audio/voice/</c> folder that the plan will not load, and why. A skip
    /// is never an error: a pack is allowed to contain a file we do not understand, and the reason
    /// is what tells its author how to fix the name.
    /// </summary>
    public sealed class VoiceLoadSkip
    {
        /// <summary>Creates a skip record.</summary>
        /// <param name="packId">Id of the pack the file belongs to.</param>
        /// <param name="relativePath">Path relative to the pack root.</param>
        /// <param name="reason">Why the file is not loaded.</param>
        /// <exception cref="ArgumentException"><paramref name="packId"/> or <paramref name="relativePath"/> is blank.</exception>
        public VoiceLoadSkip(string packId, string relativePath, string reason)
        {
            if (string.IsNullOrWhiteSpace(packId))
            {
                throw new ArgumentException("a pack id is required", nameof(packId));
            }

            if (string.IsNullOrWhiteSpace(relativePath))
            {
                throw new ArgumentException("a relative path is required", nameof(relativePath));
            }

            PackId = packId;
            RelativePath = relativePath;
            Reason = string.IsNullOrWhiteSpace(reason) ? "no reason given" : reason;
        }

        /// <summary>Id of the pack the file belongs to.</summary>
        public string PackId { get; }

        /// <summary>Path relative to the pack root.</summary>
        public string RelativePath { get; }

        /// <summary>Why the file is not loaded. Never null.</summary>
        public string Reason { get; }

        /// <inheritdoc />
        public override string ToString() => PackId + "/" + RelativePath + ": " + Reason;
    }
}
