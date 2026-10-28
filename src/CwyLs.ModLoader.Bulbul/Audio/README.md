# Audio — game sounds and music from packs

Replaces the clips of the game's sound rows from content packs and mods, adds pack music to the game's
music list, and offers a live **A/B switch** between the game's clip and the replacement. Entry point:
`AudioFeature.Initialize(Config)`, called once from `BulbulPlugin.Awake` after `MasterDataFeature.Initialize`.
Pure logic (plans, paid-content rule, save guard, console parsing) is in `src/CwyLs.ModLoader.Core/Audio/`.

## Pack files

| Folder (direct children, `.ogg`/`.wav`) | Replaces | Game list (`MasterDataLoader`) | Played by |
|---|---|---|---|
| `audio/ambient/<Name>` | ambient loops (rain, wind, radio…) | `AmbientMasterList` | `AmbientBGMManager` (room), `ScenarioAmbientBGMManager` (story) |
| `audio/ambient_se/<Name>` | ambient one-shots (keyboard, cup, pages…) | `AmbientSeMasterList` | `AmbientSEManager`, `AmbientSEDuckingManager` |
| `audio/se/system/<Name>` | system sounds (click, level up…) | `SystemSeMasterList` | `SEManager` |
| `audio/se/timer/<Name>` | timer sounds | `TimerSeMasterList` | `TimerSEManager` |
| `audio/direction_music/<Title>` | story music (refused while it plays a soundtrack track, see below) | `DirectionMusicDataList` | `ScenarioMusicManager` |
| `audio/music/*.ogg\|wav` + optional `_tracks.json` | adds tracks to the music list | (new rows) | `MusicManager` |

`<Name>` is the row's name (the enum value) **or** the game clip's name, case-insensitive:
`audio/ambient/ThunderRain.ogg` and `audio/ambient/Rainthunder.ogg` both replace the thunderstorm.
Later packs win; a mod's runtime clip beats every pack. The rows of game 1.17.3 (`row (game clip)` where
they differ; `audio list` prints the live table):

- **ambient (30):** RadioNoise, PinkNoise, RecordNoise, DeepSea (Underwater), Wind, Fireworks_First (bulbul_se_fireworks_short_0219), Fireworks_Second (bulbul_se_fireworks_parapara_0221), WindBell, LightRain, HeavyRain (menu: "Rain"), ThunderRain (Rainthunder), CookSimmer, Crickets, Frog1 (Flog1), Frog2 (Flog2), Chicada, Higurashi, TurtleDove, BirdChorus, Whale, HotSpring (Onsen), Locomotive (Train), Sea, RoomNoise, CookTypeB, City, Jellyfish, Fireplace, Brook, Horizon (SeaBirdShriek)
- **ambient_se (56):** KeyboardTyping, MoveKeyboard, PutKeyboard, ChairCreaking, ChairCaster, MoveNote, PutNote, WritePen, StickPenIntoNote, PlaceCup, RubbingClothes_1–4, PageFlipSoundTextbook, PageFlipSoundNovel, HandClap, OpenWindow, CloseWindow, CoffeeMakerAll, GlitchNoise, MonochoromeNoise, ScreenMove, BlackOut, PlaceCupOnSaucer, MechanicalKeyboard_01–03, PantographKeyboard_01–03, SitSofa_1, DropPen, Walk_1, KitchenWaterOnly_1, KitchenWashingCup_1, FridgeDoorOpen, PlayTakeOutFromFridge_1, FridgeDoorClose, EatenCupcakeCrushCup, EatenCupcakeRubHands, MusicboxWinding, MusicboxSound, Ice_Maker_Open, Ice_In_Glass, Ice_Maker_Close, Icewater_In_Glass, Icewater_Swirl_Glass_A–G, PlaceCupOnCorkCoaster, Motion_Cup_Ice_Shak_all
- **system_se (12):** GameStartCameraNoise, Cancel, Click, LevelUp, TaskComplete, ScenarioStart, SpecialOpening, PulldownOpen, PulldownClose, BuyItem, Select, CancelSelect (clips `bulbul_systemse_*`)
- **timer_se (7):** Start_A–C, Finish_A–C (clips `bulbul_timerse_timerstart_*`/`timerend_*`), Pause (`bulbul_systemse_timerpause`)
- **direction_music (1):** main32_Music (Without You_main32_full) — **refused: soundtrack**

`_tracks.json` (schema 1): `{"schema":1,"tracks":[{"file":"x.ogg","title":"…","credit":"…","tag":"other|special|original"}]}`
or a bare array; `x-` members are notes; unknown members, `uuid`, folders in `file` are errors. A file the list
does not name is still added, titled by its file name. `.mp3`/`.flac`/… are skipped with a hint to
convert them to `.ogg` or `.wav`.

## How a swap works (and why)

- The swap writes the row's `AudioClip` field. Every game service reads the lists live on each play
  (`RoomAmbientSoundService.GetAmbientSoundData`, `SystemSeService.Play`, `TimerSeService.Play`,
  `AmbientSeService.Play`, `ScenarioReader.CommandPlayBGM`) — nothing caches a clip.
- Our clip gets the **game clip's name**: the KanKikuchi managers find, fade and stop players by name
  (`AudioManager<T>.GetAudioPlayerByName`, `Stop(string)`, the SE volume rules in `AmbientSEManager`).
- Every swap **moves the players** holding the old clip (`AudioPlayers.Move`): ambient loops are started
  once and kept alive at volume 0 (`EnvironmentApplicationService.ApplySound`), and
  `RoomAmbientSoundService.Stop` stops by clip **reference** (in `AudioManager<T>`) — a row swapped under a
  playing loop would leave it unstoppable. The move uses the game's own `AudioPlayer.TryReplaceAudioClip`
  (it keeps state, volume, pitch, loop and the sample position), then seeks to the same
  position (wrapped) when the clips differ in length; a refused move restarts the player cleanly. Without
  the players (seam missing) loop rows are not replaced at all.
- **When:** after every master-data injection (`MasterDataFeature.Injected`; EditData may rebuild the
  lists), when the clips finish decoding, at `RoomReady` (fallback when `[MasterData] Enabled=false`), and
  on console use. **Restored** at `Application.quitting` (rows) and by `audio off` (rows and players).
  Clips one of ours never become "the original" (`AudioSwapBook`), so rebuilt rows cannot trap a replacement.

## Pack music and the save

`PackMusic`: a prefix on `MusicService.Load` (called from `EntryBehavior.LoadAsync`)
adds the decoded pack tracks (`GameAudioInfo.CreateNormal`, unlocked, UUID
`cwy-<32 hex of SHA-256(pack|path)>`), after the game's tracks or at the position remembered from last
session — never first (the first track is the one that starts at launch). Late clips go through
`MusicService.AddMusicItem`. A removed pack track stays listed until the next launch.

The music save (`MusicSettingV2`: favourites, playlist order, exclusions) is Steam-Cloud-synced. A prefix +
finalizer on `SaveDataManager.SaveMusicSetting` (synchronous) strip every `cwy-` UUID for the write and
put it back for the live game; if a pack UUID would still reach the file, that one save is skipped (ERROR
line). The stripped state goes to `BepInEx/data/audio/pack-music.json` (UUIDs and indices only) and is
re-inserted at the next load. Pack music is never added unless the guard is patched. Prefix + finalizer,
not a postfix: `SaveDataHook` owns the one postfix per save method.

## Paid content

The game's only DLC is the original soundtrack (`BulbulSteamDLC.DLC_1 = 3718890`, never queried in play —
`SteamManager.IsInstalledDLC` has no caller). Its tracks are the music list's `AudioTag.Original` rows (31 of
43). So: game music rows are never replaceable (packs only add tracks); a story-music row whose clip or
title starts with a game track's title is refused (`main32_Music` → *Without You*); a pack track whose title
or file name equals a game track's title or clip name is refused. No ambient/SE/timer row has a purchase or
DLC flag (point-purchase environments are bought with in-game points, so they count as free). One
`AUDIO ! <row> refused (paid content)` WARN per refused file. No override switch.

## Config, console, smoke, trace

`[Audio]` in the adapter's .cfg: `Enabled` (true), `ReplaceClips` (true), `PackMusic` (true), `LogEachClip` (true).

Console `audio` (group `game`): `audio` / `status` · `list [all|replaced|<table>]` · `ab <name|table|all> [original|pack]`
(no word = toggle; `all` flips every replaced row together) · `play <name>` (one-shot rows through the game's own
service: `SystemSeService.Play`, `AmbientSeService.Play`, `TimerSeService.PlayPreview`) · `music` · `on` / `off` · `reload`.

Lines: `SETUP sound replacement and pack music ready…`, `SETUP AUDIO: n row clip(s) and m music file(s) from packs`,
`AUDIO ~ ambient/LightRain <- pack/path` (per row, `LogEachClip`), `AUDIO - <row> (game clip)` (restore),
`AUDIO ab <row> -> original|pack mode=moved|restarted|rowonly|unchanged|failed players= pos= note=` (one per row
switched), `AUDIO + music '<title>' <- <pack>`, `AUDIO on|off: …`, WARN `AUDIO ! <row> refused (paid content)`.
Trace family `sys=audio`: `preload`, `applied`, `restore`, `refused`, `ab`, `music`, `musicState`, `saveguard`, `play`, `on`, `off`.

Smoke: quiet `d.audio.seams`, `d.audio.rows`, `d.audio.paid`, `d.audio.music`, `d.audio.saveguard`;
loud `d.audio.ab` (flips a replaced loop to original and back; restore = all rows to pack).

Seams: `AudioTargets.All` (35, registered as `CWYLS Game Adapter (audio)`). Mod facet: `helper.Audio`
(`Rows`, `MusicTracks`, `ReplaceClip`, `Select`, `Reload`; `src/CwyLs.ModLoader/Mods/Facets/CwyModAudio.cs`).

## Not covered

- Sounds the game plays **by path**, not through a row: scenario SE (`ScenarioReader.CommandPlaySE` →
  `SEManager.Play(string)`), BGM paths, voices (the voice feature owns those).
- Game music tracks are never replaced (paid content), only added to.
- The A/B choice is not saved; every launch starts with the replacements.
