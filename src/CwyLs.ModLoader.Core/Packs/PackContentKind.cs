namespace CwyLs.ModLoader.Core.Packs
{
    /// <summary>
    /// What a file inside a pack is, decided purely from its path by the folder conventions in
    /// <see cref="PackContentIndex"/>. The kind says which loader will be asked for the file;
    /// it is not a claim that the file's contents are valid.
    /// </summary>
    /// <remarks>
    /// Members are appended, never renumbered: the value is written into trace records and a
    /// reordered enum would make an old trace misleading. The list is frozen; a later feature
    /// that needs a convention nobody declared reads its files from <see cref="Other"/>, as the
    /// subtitle tables (<c>data/subtitles/&lt;lang&gt;.json</c>) already do.
    /// </remarks>
    public enum PackContentKind
    {
        /// <summary>Anything the conventions do not recognise. Carried, never loaded.</summary>
        Other = 0,

        /// <summary>The pack's own <c>manifest.json</c>.</summary>
        Manifest,

        /// <summary>A texture under <c>textures/</c>.</summary>
        Texture,

        /// <summary>A voice clip under <c>audio/voice/</c>.</summary>
        Voice,

        /// <summary>
        /// A music track under <c>audio/music/</c> (direct child). The track list
        /// <c>audio/music/_tracks.json</c> is <see cref="MusicTracks"/>, not a track.
        /// </summary>
        Music,

        /// <summary><c>data/localization.json</c> - dialogue and UI strings.</summary>
        Localization,

        /// <summary><c>data/novel.json</c> - novel/story rows.</summary>
        Novel,

        /// <summary><c>data/scenario_groups.json</c> - scenario group rows.</summary>
        ScenarioGroups,

        /// <summary>
        /// <c>language.json</c> at the pack root - languages the pack declares
        /// (<c>CwyLs.ModLoader.Core.Language.LanguageDeclarationParser</c>).
        /// </summary>
        Language,

        /// <summary>
        /// An EditData file, <c>data/edit/*.json</c> (direct children): edits to any master table,
        /// Content-Patcher style. Applied in ordinal path order within a pack.
        /// </summary>
        EditData,

        /// <summary>A replacement clip for an ambient loop, <c>audio/ambient/*.ogg|wav</c>.</summary>
        AmbientAudio,

        /// <summary>A replacement clip for an ambient one-shot, <c>audio/ambient_se/*.ogg|wav</c>.</summary>
        AmbientSeAudio,

        /// <summary>A replacement clip for a system sound, <c>audio/se/system/*.ogg|wav</c>.</summary>
        SystemSeAudio,

        /// <summary>A replacement clip for a timer sound, <c>audio/se/timer/*.ogg|wav</c>.</summary>
        TimerSeAudio,

        /// <summary>
        /// A replacement clip for story ("direction") music, <c>audio/direction_music/*.ogg|wav</c>
        /// (the game's <c>DirectionMusicMaster</c> table). Reserved in advance so the frozen list
        /// does not block it.
        /// </summary>
        DirectionMusicAudio,

        /// <summary>The pack's music track list, <c>audio/music/_tracks.json</c>.</summary>
        MusicTracks,

        /// <summary>A replacement UI sprite, <c>ui/**.png</c> at any depth (D-UI).</summary>
        UiSprite,

        /// <summary>The UI sprite rules, <c>ui/_sprites.json</c> (D-UI).</summary>
        UiSprites,

        /// <summary>The pack's cutscene triggers, <c>data/triggers.json</c>.</summary>
        Triggers,
    }
}
