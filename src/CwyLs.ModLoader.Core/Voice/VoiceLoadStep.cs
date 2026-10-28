using System;

namespace CwyLs.ModLoader.Core.Voice
{
    /// <summary>One file a <see cref="VoiceLoadPlan"/> says to load, and how to load it.</summary>
    public sealed class VoiceLoadStep
    {
        /// <summary>Creates a step.</summary>
        /// <param name="packId">Id of the pack the file belongs to.</param>
        /// <param name="relativePath">Path relative to the pack root, forward slashes.</param>
        /// <param name="name">The voice name derived from the file name.</param>
        /// <param name="loader">Which loader the extension calls for.</param>
        /// <exception cref="ArgumentException">Any of the strings is blank.</exception>
        public VoiceLoadStep(string packId, string relativePath, string name, VoiceLoaderKind loader)
        {
            if (string.IsNullOrWhiteSpace(packId))
            {
                throw new ArgumentException("a pack id is required", nameof(packId));
            }

            if (string.IsNullOrWhiteSpace(relativePath))
            {
                throw new ArgumentException("a relative path is required", nameof(relativePath));
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("a voice name is required", nameof(name));
            }

            PackId = packId;
            RelativePath = relativePath;
            Name = name;
            Loader = loader;
        }

        /// <summary>Id of the pack the file belongs to; becomes <see cref="VoiceEntry.Source"/>.</summary>
        public string PackId { get; }

        /// <summary>Path relative to the pack root, e.g. <c>audio/voice/Voice_CWY_Test.wav</c>.</summary>
        public string RelativePath { get; }

        /// <summary>The voice name the clip will be registered under.</summary>
        public string Name { get; }

        /// <summary>Which loader to use.</summary>
        public VoiceLoaderKind Loader { get; }

        /// <inheritdoc />
        public override string ToString() => PackId + "/" + RelativePath + " -> " + Name + " (" + Loader + ")";
    }
}
