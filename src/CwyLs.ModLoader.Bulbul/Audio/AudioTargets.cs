using System.Collections.Generic;
using CwyLs.ModLoader.Core;

namespace CwyLs.ModLoader.Bulbul.Audio
{
    /// <summary>
    /// The game members the audio feature reads, writes, calls or patches, as patch-target data
    /// (strings only, so seamcheck can evaluate the table without the game). Registered once from
    /// <see cref="AudioFeature.Initialize"/>; checked against game 1.17.3.
    /// </summary>
    /// <remarks>
    /// Groups, and what a missing member switches off (one WARN each, the rest keeps working):
    /// row replacement (the five master lists and their row clip fields), the soundtrack check
    /// (<c>MusicDataList</c> - without it story music is refused), pack music (the two
    /// <c>MusicService</c> members, <c>GameAudioInfo.CreateNormal</c> and the save guard - pack music
    /// is never added without the guard), live A/B (the KanKikuchi players - without them a loop row
    /// is not replaced at all, see <c>AudioPlayers</c>), and <c>audio play</c> (the three services).
    /// </remarks>
    public static class AudioTargets
    {
        /// <summary>The game assembly.</summary>
        public const string GameAssembly = "Assembly-CSharp";

        /// <summary>The third-party audio assembly (not referenced at compile time).</summary>
        public const string AudioAssembly = "AudioManager";

        /// <summary>The KanKikuchi namespace, with the trailing dot.</summary>
        public const string AudioNamespace = "KanKikuchi.AudioManager.";

        /// <summary>The managers whose players can hold a replaceable clip, by table (see <c>AudioPlayers</c>).</summary>
        public static readonly string[] ManagerTypeNames =
        {
            "AmbientBGMManager", "ScenarioAmbientBGMManager", "AmbientSEManager", "AmbientSEDuckingManager",
            "SEManager", "TimerSEManager", "ScenarioMusicManager",
        };

        private const string Loader = "Bulbul.MasterDataLoader";

        /// <summary>Every target, built once.</summary>
        public static IReadOnlyList<PatchTarget> All { get; } = Build();

        private static IReadOnlyList<PatchTarget> Build()
        {
            var targets = new List<PatchTarget>
            {
                // --- Row replacement: the five lists (Bulbul.MasterDataLoader) ---------------------
                new PatchTarget("audio.masterdata.ambient", GameAssembly, Loader, MemberKind.Field, "AmbientMasterList",
                    expectedSignature: "IReadOnlyList<AmbientSoundMasterData> AmbientMasterList"),
                new PatchTarget("audio.masterdata.ambientse", GameAssembly, Loader, MemberKind.Field, "AmbientSeMasterList",
                    expectedSignature: "IReadOnlyList<AmbientSeSoundMasterData> AmbientSeMasterList"),
                new PatchTarget("audio.masterdata.systemse", GameAssembly, Loader, MemberKind.Field, "SystemSeMasterList",
                    expectedSignature: "IReadOnlyList<SystemSeMasterData> SystemSeMasterList"),
                new PatchTarget("audio.masterdata.timerse", GameAssembly, Loader, MemberKind.Field, "TimerSeMasterList",
                    expectedSignature: "IReadOnlyList<TimerSeMasterData> TimerSeMasterList"),
                new PatchTarget("audio.masterdata.directionmusic", GameAssembly, Loader, MemberKind.Field, "DirectionMusicDataList",
                    expectedSignature: "IReadOnlyList<DirectionMusicData> DirectionMusicDataList"),

                // The game's music list: the soundtrack catalog for the paid check (Bulbul.MasterDataLoader).
                new PatchTarget("audio.masterdata.music", GameAssembly, Loader, MemberKind.Field, "MusicDataList",
                    expectedSignature: "IReadOnlyList<MusicData> MusicDataList"),

                // --- The row clip fields written by a swap (AmbientSoundMasterData etc.) -------------
                new PatchTarget("audio.row.ambient.clip", GameAssembly, "Bulbul.AmbientSoundMasterData", MemberKind.Field, "AudioClip",
                    expectedSignature: "AudioClip AudioClip"),
                new PatchTarget("audio.row.ambientse.clip", GameAssembly, "Bulbul.AmbientSeSoundMasterData", MemberKind.Field, "AudioClip",
                    expectedSignature: "AudioClip AudioClip"),
                new PatchTarget("audio.row.systemse.clip", GameAssembly, "Bulbul.SystemSeMasterData", MemberKind.Field, "AudioClip",
                    expectedSignature: "AudioClip AudioClip"),
                new PatchTarget("audio.row.timerse.clip", GameAssembly, "Bulbul.TimerSeMasterData", MemberKind.Field, "AudioClip",
                    expectedSignature: "AudioClip AudioClip"),
                new PatchTarget("audio.row.directionmusic.clip", GameAssembly, "Bulbul.DirectionMusicData", MemberKind.Field, "AudioClip",
                    expectedSignature: "AudioClip AudioClip"),

                // --- Pack music ----------------------------------------------------------------------
                // Prefixed: pack tracks join the list the game is about to load (MusicService).
                new PatchTarget("audio.music.load", GameAssembly, "MusicService", MemberKind.Method, "Load",
                    new[] { "System.Collections.Generic.IReadOnlyCollection<Bulbul.GameAudioInfo>" },
                    expectedSignature: "void Load(IReadOnlyCollection<GameAudioInfo>)"),

                // Called: a track whose clip decodes after the load is added late (MusicService).
                new PatchTarget("audio.music.additem", GameAssembly, "MusicService", MemberKind.Method, "AddMusicItem",
                    new[] { "Bulbul.GameAudioInfo" },
                    expectedSignature: "bool AddMusicItem(GameAudioInfo)"),

                // Read: which pack tracks are listed (MusicService).
                new PatchTarget("audio.music.allmusiclist", GameAssembly, "MusicService", MemberKind.Property, "AllMusicList",
                    expectedSignature: "IReadOnlyList<GameAudioInfo> AllMusicList { get; }"),

                // Called: builds a pack track the way the game builds its own (Bulbul.GameAudioInfo).
                new PatchTarget("audio.gameaudioinfo.createnormal", GameAssembly, "Bulbul.GameAudioInfo", MemberKind.Method, "CreateNormal",
                    new[]
                    {
                        "UnityEngine.AudioClip", "Bulbul.AudioTag", "System.String", "System.String", "System.String",
                        "System.Boolean", "System.String", "System.String",
                    },
                    isStatic: true,
                    expectedSignature: "static GameAudioInfo CreateNormal(AudioClip, AudioTag, string, string, string, bool, string, string)"),

                // --- The save guard (prefix + finalizer, never a postfix: SaveDataHook owns those) ---
                // Bulbul.SaveDataManager: readies the music setting, then saves it.
                new PatchTarget("audio.save.savemusicsetting", GameAssembly, "Bulbul.SaveDataManager", MemberKind.Method, "SaveMusicSetting",
                    expectedSignature: "void SaveMusicSetting()"),
                new PatchTarget("audio.save.musicsetting", GameAssembly, "Bulbul.SaveDataManager", MemberKind.Property, "MusicSetting",
                    expectedSignature: "MusicSettingV2 MusicSetting { get; set; }"),

                // MusicSettingV2: the three UUID lists the guard strips.
                new PatchTarget("audio.save.favorites", GameAssembly, "Bulbul.MusicSettingV2", MemberKind.Field, "FavoriteAudioUUIDs",
                    expectedSignature: "List<string> FavoriteAudioUUIDs"),
                new PatchTarget("audio.save.playlistorder", GameAssembly, "Bulbul.MusicSettingV2", MemberKind.Field, "PlaylistOrder",
                    expectedSignature: "List<string> PlaylistOrder"),
                new PatchTarget("audio.save.excluded", GameAssembly, "Bulbul.MusicSettingV2", MemberKind.Field, "ExcludedFromPlaylistUUIDs",
                    expectedSignature: "List<string> ExcludedFromPlaylistUUIDs"),

                // --- 'audio play': the game's own services, so the row's current clip plays ----------
                // SystemSeService, AmbientSeService, TimerSeService (registered in the room scope by RoomRegister).
                new PatchTarget("audio.service.systemse.play", GameAssembly, "Bulbul.SystemSeService", MemberKind.Method, "Play",
                    new[] { "Bulbul.SystemSeParam" },
                    expectedSignature: "void Play(SystemSeParam)"),
                new PatchTarget("audio.service.ambientse.play", GameAssembly, "Bulbul.AmbientSeService", MemberKind.Method, "Play",
                    new[] { "Bulbul.AmbientSeParam" },
                    expectedSignature: "void Play(AmbientSeParam)"),
                new PatchTarget("audio.service.timerse.playpreview", GameAssembly, "Bulbul.TimerSeService", MemberKind.Method, "PlayPreview",
                    new[] { "Bulbul.TimerSeType", "System.Single" },
                    expectedSignature: "void PlayPreview(TimerSeType, float)"),

                // --- Live A/B: the KanKikuchi players (AudioManager.dll, by reflection) --------------
                // AudioManager: every player of a manager.
                new PatchTarget("audio.manager.playerlist", AudioAssembly, AudioNamespace + "AudioManager`1", MemberKind.Property, "AudioPlayerList",
                    expectedSignature: "IReadOnlyList<AudioPlayer> AudioPlayerList { get; }"),

                // SingletonMonoBehaviour: read instead of Instance, whose getter logs an error when absent.
                new PatchTarget("audio.singleton.instancefield", AudioAssembly, AudioNamespace + "SingletonMonoBehaviour`1", MemberKind.Field, "_instance",
                    isStatic: true,
                    expectedSignature: "static T _instance"),

                // AudioPlayer: the members live A/B reads.
                new PatchTarget("audio.player.audiosource", AudioAssembly, AudioNamespace + "AudioPlayer", MemberKind.Property, "AudioSource",
                    expectedSignature: "AudioSource AudioSource { get; }"),
                new PatchTarget("audio.player.state", AudioAssembly, AudioNamespace + "AudioPlayer", MemberKind.Property, "CurrentState",
                    expectedSignature: "AudioPlayer.State CurrentState { get; }"),
                new PatchTarget("audio.player.tryreplace", AudioAssembly, AudioNamespace + "AudioPlayer", MemberKind.Method, "TryReplaceAudioClip",
                    new[] { "UnityEngine.AudioClip", "UnityEngine.AudioClip" },
                    expectedSignature: "bool TryReplaceAudioClip(AudioClip, AudioClip)"),
            };

            // One per manager type, so a renamed manager is reported by name (e.g. AmbientBGMManager).
            foreach (var manager in ManagerTypeNames)
            {
                targets.Add(new PatchTarget(
                    "audio.manager." + manager.ToLowerInvariant(), AudioAssembly, AudioNamespace + manager,
                    MemberKind.Field, "AUDIO_DIRECTORY_PATH",
                    isStatic: true,
                    expectedSignature: "static string AUDIO_DIRECTORY_PATH"));
            }

            return targets;
        }
    }
}
