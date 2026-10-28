using System;

namespace CwyLs.ModLoader.Core.Voice
{
    /// <summary>
    /// One registered voice line: the name the game will ask for, where it came from, and the
    /// loaded clip.
    /// </summary>
    /// <remarks>
    /// <see cref="Clip"/> is typed <see cref="object"/> on purpose. This assembly is the
    /// Unity-free half of the framework (see CwyLs.ModLoader.Core.csproj), so it cannot name
    /// <c>UnityEngine.AudioClip</c>; at runtime the clip is exactly that, and the plugin side
    /// casts. Keeping it on the entry rather than in a second dictionary means the registry's
    /// override rules and the clip table can never disagree about who owns a name.
    /// </remarks>
    public sealed class VoiceEntry
    {
        /// <summary>Creates an entry.</summary>
        /// <param name="name">
        /// The voice name, as the game looks it up - e.g. <c>Voice_Tutorial_1_001</c>. Checked for
        /// blankness here; the character rules are <see cref="VoiceNameRules"/>'s job and are
        /// applied by <see cref="VoiceRegistry.Register"/>, so an invalid name can be built and
        /// then refused with a reason.
        /// </param>
        /// <param name="source">Pack id, or <c>runtime</c> for a mod's registration. Required.</param>
        /// <param name="priority">Which registration wins a conflict.</param>
        /// <param name="path">Absolute or pack-relative path the clip was loaded from; null for a runtime clip.</param>
        /// <param name="clip">The loaded <c>UnityEngine.AudioClip</c>, or null in off-engine tests.</param>
        /// <exception cref="ArgumentException"><paramref name="name"/> or <paramref name="source"/> is blank.</exception>
        public VoiceEntry(
            string name,
            string source,
            VoicePriority priority,
            string? path = null,
            object? clip = null)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("a voice name is required", nameof(name));
            }

            if (string.IsNullOrWhiteSpace(source))
            {
                throw new ArgumentException("a source is required", nameof(source));
            }

            Name = name;
            Source = source;
            Priority = priority;
            Path = string.IsNullOrWhiteSpace(path) ? null : path;
            Clip = clip;
        }

        /// <summary>The name the game asks for. Case-sensitive, exactly as the game's own keys are.</summary>
        public string Name { get; }

        /// <summary>Pack id the clip came from, or <see cref="VoiceRegistry.RuntimeSource"/>.</summary>
        public string Source { get; }

        /// <summary>Which registration wins a conflict on <see cref="Name"/>.</summary>
        public VoicePriority Priority { get; }

        /// <summary>Where the clip was loaded from, or null when it was made in memory.</summary>
        public string? Path { get; }

        /// <summary>The loaded clip (a <c>UnityEngine.AudioClip</c> at runtime), or null.</summary>
        public object? Clip { get; }

        /// <summary>True when this came from a mod rather than a pack.</summary>
        public bool IsRuntime => Priority == VoicePriority.Runtime;

        /// <summary>This entry with a different clip, everything else kept.</summary>
        /// <param name="clip">The replacement clip.</param>
        public VoiceEntry WithClip(object? clip) => new VoiceEntry(Name, Source, Priority, Path, clip);

        /// <inheritdoc />
        public override string ToString() => Name + " <- " + Source + (Path is null ? string.Empty : " (" + Path + ")");
    }
}
