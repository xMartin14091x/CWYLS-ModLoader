# Core/Audio — the Unity-free half of pack audio

Everything here is plain data and logic, unit-tested in `src/CwyLs.ModLoader.Tests/Audio*Tests.cs`. The game
glue (Harmony, `AudioClip`, the KanKikuchi players) is in `src/CwyLs.ModLoader.Bulbul/Audio/` — read its
README for the behaviour, the pack folders and the console.

| Type | Role |
|---|---|
| `AudioTable`, `AudioTables` | The five replaceable tables, their ids (`ambient`, `ambient_se`, `system_se`, `timer_se`, `direction_music`), pack folders (the frozen `PackContentKind`s), aliases, which are loops |
| `AudioRowDescriptor`, `AudioCandidate` | A game row as data (`ambient/LightRain`, game clip name, length); a pack file claiming a row |
| `AudioReplacementPlan` | Which file replaces which row: by row name, else by game clip name; later pack / later path wins; unknown and paid claims skipped with a reason |
| `AudioSwapBook<TClip>` | Per row: the game's original (never one of our clips), pack and runtime claims (runtime beats pack), the A/B variant (`Pack` default / `Original`), and the clip a row should hold (`Desired`) |
| `AudioPaidContent`, `SoundtrackCatalog`, `SoundtrackTrack` | The paid-content rule: soundtrack DLC app id 3718890; story music that plays a soundtrack track is refused; a pack track re-listing a game track is refused; ambient/SE/timer rows are never paid |
| `MusicTrackList` | Parser for `audio/music/_tracks.json` (schema 1, strict, `x-` notes) |
| `PackMusicPlan`, `PackMusicTrack`, `PackMusicInput`, `PackMusicSkip`, `PackMusicUuid`, `PackMusicPlacement` | Which music files become tracks (title, credit, tag, `cwy-` UUID), and where they go in the list (remembered position, else after the game's tracks) |
| `MusicSaveGuard`, `MusicSaveStash`, `PackMusicState`, `PackMusicOrder` | Strip `cwy-` UUIDs from the three `MusicSettingV2` lists for the write and restore them exactly; remember favourites/exclusions/positions in our own JSON; re-insert at launch |
| `AudioCommandParser`, `AudioRowSpec`, `AudioListFormatter` | The `audio` console grammar, row specs (`all`, table, key, name, clip name), the `audio list` text |
| `AudioRowInfo`, `AudioSwitchResult`/`Outcome`/`Mode`, `MusicTrackInfo`, `AudioPlaying`, `LoopPosition` | What the console and the mod facet report; where a moved loop continues |
