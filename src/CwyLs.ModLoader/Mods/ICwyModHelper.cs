using System.Collections.Generic;
using BepInEx.Configuration;
using CwyLs.ModLoader.Core.Mods;
using CwyLs.ModLoader.Core.Packs;

namespace CwyLs.ModLoader.Mods
{
    /// <summary>
    /// Everything a code mod may use, in one per-mod object (the SMAPI <c>IModHelper</c> model).
    /// Only the framework implements this interface, so members can be added without breaking
    /// mods. Each facet wraps a capability that already exists; game-specific
    /// facets are supplied by the game adapter and report <c>IsAvailable = false</c> without it.
    /// </summary>
    public interface ICwyModHelper
    {
        /// <summary>This mod's id, e.g. <c>cwy.sample.hello</c>.</summary>
        string ModId { get; }

        /// <summary>This mod's parsed <c>manifest.json</c>.</summary>
        ICwyModManifest Manifest { get; }

        /// <summary>Absolute path of the mod's folder, <c>&lt;game root&gt;/CWYLS Mods/&lt;id&gt;</c>.</summary>
        string DirectoryPath { get; }

        /// <summary>Log lines tagged with this mod's id (console tag <c>MOD</c>).</summary>
        ICwyModLog Log { get; }

        /// <summary>
        /// This mod's own BepInEx config file, <c>BepInEx/config/cwy.mod.&lt;id&gt;.cfg</c>, created on
        /// first use. Bind entries with <c>Config.Bind(section, key, default, description)</c>.
        /// Hotkeys registered through <see cref="Input"/> live here too. This member is bound to
        /// BepInEx 5's <c>ConfigFile</c> type (the one API type from BepInEx a mod sees); a move to
        /// another BepInEx major version would change it in a major CWY:LS Mod Loader version.
        /// </summary>
        ConfigFile Config { get; }

        /// <summary>This mod's own persistent files under <c>BepInEx/data/mods/&lt;id&gt;/</c>, written atomically.</summary>
        ICwyModData Data { get; }

        /// <summary>Game events, per-frame ticks and the mods-ready signal; every subscription is removed when the mod stops.</summary>
        ICwyModEvents Events { get; }

        /// <summary>Satone: expression, motion, gaze, restore and new spoken lines.</summary>
        ICwyModSatone Satone { get; }

        /// <summary>The player's data: a read-only snapshot and change notifications.</summary>
        ICwyModGameData GameData { get; }

        /// <summary>The language the game is displaying, including pack-declared languages.</summary>
        ICwyModLanguage Language { get; }

        /// <summary>Files in this mod's own folder: paths, text, JSON, PNG, WAV and OGG.</summary>
        ICwyModContent Content { get; }

        /// <summary>The loaded content packs whose <c>contentPackFor</c> names this mod, in load order.</summary>
        IReadOnlyList<ICwyModInfo> ContentPacks { get; }

        /// <summary>Hotkeys registered under this mod's id and bound in its config file.</summary>
        ICwyModInput Input { get; }

        /// <summary>Console commands, listed under this mod's id in <c>help</c>.</summary>
        ICwyModConsole Console { get; }

        /// <summary>Windows in the game's own style.</summary>
        ICwyModUi Ui { get; }

        /// <summary>The shared main-thread dispatcher, with this mod as the owner of its work.</summary>
        ICwyModMainThread MainThread { get; }

        /// <summary>Other mods and packs: who is loaded, and their APIs.</summary>
        ICwyModRegistry ModRegistry { get; }

        /// <summary>
        /// A Harmony instance with this mod's id. Its patches, and any patch whose method is declared
        /// in this mod's assemblies, are removed when the mod fails in <c>Entry</c> and when the game quits.
        /// </summary>
        ICwyModHarmony Harmony { get; }

        /// <summary>Content-Patcher-style edits to master tables. A slot until its backend is installed.</summary>
        ICwyModEditData EditData { get; }

        /// <summary>Pack clips for ambient, sound effects, story and pack music. A slot until its backend is installed.</summary>
        ICwyModAudio Audio { get; }

        /// <summary>Lightroom-style photo grading over the room camera.</summary>
        ICwyModGrading Grading { get; }

        /// <summary>Pack sprites for the game's own UI. A slot until its backend is installed.</summary>
        ICwyModUiSkin UiSkin { get; }

        /// <summary>The <c>when</c> condition grammar, evaluation, and this mod's own tokens.</summary>
        ICwyModConditions Conditions { get; }

        /// <summary>Semantic game actions: list, run as this mod, and publish this mod's own.</summary>
        ICwyModActions Actions { get; }

        /// <summary>Custom script commands for pack scenes, numbered 5000-5999.</summary>
        ICwyModScenes Scenes { get; }

        /// <summary>The room camera's presets and story cameras. A slot until its backend is installed.</summary>
        ICwyModCamera Camera { get; }

        /// <summary>Satone speaking audio that arrives in chunks. A slot until its backend is installed.</summary>
        ICwyModVoiceStream VoiceStream { get; }

        /// <summary>Push-to-talk microphone utterances; off by default, never in-game on macOS. A slot until its backend is installed.</summary>
        ICwyModMic Mic { get; }

        /// <summary>The local game API on 127.0.0.1:7870, off by default. A slot until its backend is installed.</summary>
        ICwyModApi Api { get; }
    }

    /// <summary>A parsed manifest, as mods see it. Only the framework implements this interface.</summary>
    public interface ICwyModManifest
    {
        /// <summary>Unique, stable id.</summary>
        string Id { get; }

        /// <summary>Human-readable name.</summary>
        string Name { get; }

        /// <summary>The pack's or mod's own version.</summary>
        SemVer Version { get; }

        /// <summary>Author, or null.</summary>
        string? Author { get; }

        /// <summary>One-line description, or null.</summary>
        string? Description { get; }

        /// <summary>Game build it was made and tested with, or null.</summary>
        string? GameVersion { get; }

        /// <summary>Oldest CWY:LS Mod Loader API version it needs, or null.</summary>
        SemVer? LoaderVersion { get; }

        /// <summary>Relative path of the entry DLL, or null for a content pack.</summary>
        string? EntryDll { get; }

        /// <summary>The code mod this pack is data for, or null.</summary>
        PackDependency? ContentPackFor { get; }

        /// <summary>Declared dependencies (<c>dependsOn</c>).</summary>
        IReadOnlyList<PackDependency> DependsOn { get; }

        /// <summary>The manifest's <c>x-</c> members, as parsed JSON values. Never null.</summary>
        IReadOnlyDictionary<string, object?> Extensions { get; }
    }

    /// <summary>One loaded pack or code mod, as <see cref="ICwyModRegistry"/> reports it.</summary>
    public interface ICwyModInfo
    {
        /// <summary>Its id.</summary>
        string Id { get; }

        /// <summary>Its manifest.</summary>
        ICwyModManifest Manifest { get; }

        /// <summary>Absolute path of its folder.</summary>
        string DirectoryPath { get; }

        /// <summary>True for a code mod (the manifest has an <c>entryDll</c>).</summary>
        bool IsCodeMod { get; }

        /// <summary>
        /// Its state: <see cref="ModState.Loaded"/> for every content pack that resolved, and the
        /// loader's verdict for a code mod.
        /// </summary>
        ModState State { get; }

        /// <summary>Why it was skipped or failed, or empty.</summary>
        string Detail { get; }
    }

    /// <summary>Other mods and packs. Only the framework implements this interface.</summary>
    public interface ICwyModRegistry
    {
        /// <summary>True when a content pack with this id resolved, or a code mod with this id is loaded.</summary>
        /// <param name="id">A pack or mod id.</param>
        bool IsLoaded(string id);

        /// <summary>The pack or mod with this id, or null.</summary>
        /// <param name="id">A pack or mod id.</param>
        ICwyModInfo? Get(string id);

        /// <summary>Every resolved pack and code mod, in load order.</summary>
        IReadOnlyList<ICwyModInfo> GetAll();

        /// <summary>
        /// Another mod's API object (its <c>GetApi()</c>) as <typeparamref name="T"/>, or null when
        /// the mod is not loaded, has no API, or its API does not implement <typeparamref name="T"/>
        /// (logged once). Available from the mods-ready signal on; earlier calls return null.
        /// </summary>
        /// <typeparam name="T">An interface the other mod's API implements (reference its contract DLL).</typeparam>
        /// <param name="modId">The other mod's id.</param>
        T? GetApi<T>(string modId)
            where T : class;
    }
}
