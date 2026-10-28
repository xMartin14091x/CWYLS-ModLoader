using System.Collections.Generic;
using CwyLs.ModLoader.Core;

namespace CwyLs.ModLoader.Bulbul
{
    /// <summary>
    /// The voice seam as patch-target data, so the startup report covers the member the voice
    /// feature actually patches: the <em>closed</em> generic <c>AudioManager&lt;VoiceManager&gt;</c>.
    /// These are the only ids for <c>GetAudioClip</c> and <c>_audioClipDict</c>; the open-definition
    /// duplicates (<c>audio.getaudioclip</c>, <c>audio.clipcache</c>) were removed from
    /// <c>BulbulPatchTargets</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The table is built rather than declared because a closed generic's reflection name cannot
    /// be written by hand with any confidence - it carries the argument's full assembly identity.
    /// <see cref="VoiceSeam.ClosedAudioManagerTypeName"/> is the runtime's own
    /// <c>Type.FullName</c>, which round-trips through <c>Assembly.GetType</c> by construction; if
    /// the type cannot be resolved at all, the constant fallback is used so the report still says
    /// what was looked for instead of nothing.
    /// </para>
    /// <para>
    /// Every expected signature below was read off the shipped v1.17.2 <c>AudioManager.dll</c>
    /// through a metadata-only load of the closed type.
    /// </para>
    /// </remarks>
    public static class BulbulVoiceTargets
    {
        private static IReadOnlyList<PatchTarget>? _table;

        /// <summary>The voice seams, resolved against the running game the first time they are read.</summary>
        public static IReadOnlyList<PatchTarget> All => _table ??= Build();

        private static IReadOnlyList<PatchTarget> Build()
        {
            var audioManager = VoiceSeam.ClosedAudioManagerTypeName;
            var singleton = VoiceSeam.ClosedSingletonTypeName;

            return new[]
            {
                // The method the voice feature prefixes. A prefix that returns our clip covers an
                // override and a brand-new name alike, and never touches the game's own cache.
                // Private, declared on the open generic base.
                new PatchTarget(
                    "voice.closed.getaudioclip", VoiceSeam.AudioAssemblyName, audioManager,
                    MemberKind.Method, VoiceSeam.GetAudioClipMethodName,
                    new[] { "System.String" },
                    expectedSignature: "AudioClip GetAudioClip(string)"),

                // The cache the prefix deliberately does not write to; listed because a change to
                // it would change what "not registered" falls through to.
                new PatchTarget(
                    "voice.closed.clipcache", VoiceSeam.AudioAssemblyName, audioManager,
                    MemberKind.Field, "_audioClipDict",
                    expectedSignature: "Dictionary<string, AudioClip> _audioClipDict"),

                // How everything here reaches the live manager.
                // SingletonMonoBehaviour, closed over VoiceManager.
                new PatchTarget(
                    "voice.singleton.instance", VoiceSeam.AudioAssemblyName, singleton,
                    MemberKind.Property, "Instance",
                    isStatic: true,
                    expectedSignature: "static VoiceManager Instance { get; }"),

                // Where the game's voice clips come from, and therefore what a pack file name has
                // to look like.
                new PatchTarget(
                    "voice.audio_directory_path", VoiceSeam.AudioAssemblyName, VoiceSeam.VoiceManagerTypeName,
                    MemberKind.Field, "AUDIO_DIRECTORY_PATH",
                    isStatic: true,
                    expectedSignature: "static string AUDIO_DIRECTORY_PATH"),

                // Language voice set prefetch: the rows StartReady just readied, read by the postfix
                // on StartReady (declared in the main table) to load a scene's language clips before
                // its first line.
                new PatchTarget(
                    "voice.lang.grouplocalizations", "Assembly-CSharp", "Bulbul.ScenarioReader",
                    MemberKind.Field, LanguageVoicePatches.GroupLocalizationsFieldName,
                    expectedSignature: "List<LocalizationData> _currentGroupLocalizations"),
            };
        }
    }
}
