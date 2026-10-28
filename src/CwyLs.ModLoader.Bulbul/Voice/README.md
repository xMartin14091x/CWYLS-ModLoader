# Voice — replacing and adding Satone's lines

`VoiceFeature` makes the game play audio we supply, under any name — one of the game's own 1381
`Voice_*` lines, or a name the game has never heard of. Content packs feed it from disk; a mod feeds
it at runtime. That second half is what a mod that generates speech builds on.

Wire-up is one call from the adapter plugin's `Awake`:

```csharp
VoiceFeature.Initialize(Config);   // binds Voice.*, patches, registers the smoke checks, preloads
```

## The seam

The game's voice cache and the method that reads it are **private members of the open generic base**
`KanKikuchi.AudioManager.AudioManager<T>` — `_audioClipDict` and
`AudioClip GetAudioClip(string)` — so neither is visible on `VoiceManager`
itself. `VoiceSeam` takes the generic definition, closes it over `VoiceManager`, and looks the
method up on that closed type; `VoicePatches` puts a Harmony **prefix** on exactly that method.

`AudioManager!KanKikuchi.AudioManager.AudioManager`1[[KanKikuchi.AudioManager.VoiceManager, …]].GetAudioClip(string)`

A prefix, rather than writing into `_audioClipDict`, because:

- it covers a **new** name as well as an override — the dictionary route only overrides what the
  game already loaded;
- it never mutates the game's own cache, which `AudioManager<T>.LoadAudioClipAsync` replaces wholesale at
  startup, discarding anything written before it;
- `Voice.Enabled=false` turns it off at runtime with no state left behind.

The prefix answers only when the name is registered; anything else falls through to the game
untouched. It also checks that the instance really is a `VoiceManager`: Mono shares one compiled
body between reference-type instantiations of a generic method, so a patch on
`AudioManager<VoiceManager>` can be reached from `BGMManager` and `SEManager` too, and without that
check a registered voice name could answer a music lookup.

`BulbulVoiceTargets` registers the closed-generic target with `PatchTargetRegistry`, so a game
update that moves it says `PATCH TARGET MISSING` at startup instead of failing quietly later.

## Pack convention

`audio/voice/<VoiceName>.ogg` or `.wav`, direct children of that folder. The file name without its
extension is the voice name. See `packs/cwy.example/audio/voice/README.md`.

| Rule | Where it lives |
|---|---|
| Name = file name without extension, must match `^[A-Za-z0-9_\-]+$` | `VoiceNameRules` |
| Lookup key the game computes = `Path.GetFileNameWithoutExtension` | `VoiceNameRules.KeyFromAudioPath`, mirroring the game's `AudioManager<T>.PathToName` |
| `.wav` → synchronous managed decode, `.ogg` → the engine's decoder over several frames | `VoiceLoadPlan` → `AudioLoader` |
| Which file wins when two claim a name | `VoiceLoadPlan.Build` (load order) then `VoiceRegistry` (priority) |

## Override semantics

Names are **case-sensitive** — the game's cache is an ordinary `Dictionary<string, AudioClip>`, so
`Voice_A` and `voice_a` are two different lines there too.

1. A free name goes to whoever asks first.
2. **Later pack over earlier pack.** Packs are registered in dependency order, so a pack that loads
   after another overrides it.
3. **Runtime over pack.** A clip a mod registered outranks a file on disk, so re-scanning the packs
   never swallows a line a mod just generated. Pass `keepPack: true` to defer to a pack
   instead.
4. Runtime over runtime: last one in wins, and the log line names who displaced whom.

Two files that would claim one name are settled earlier, in the load plan: only the last one by
load order is loaded at all. Without that, a `.wav` (decoded immediately) and an `.ogg` (decoded a
few frames later) would make the winner depend on decode timing.

Startup summary, in `LogOutput.log`:

```
VOICE: 2 clip(s) registered from packs (1 ogg, 1 wav), 0 failed
```

## API for mods

```csharp
// Made a clip at runtime? Give it a name and the game will play it.
VoiceFeature.Register("MyMod_Line_0001", clip);           // source defaults to "runtime"
VoiceFeature.Register("MyMod_Line_0002", clip, "mymod");  // or name the source yourself

VoiceFeature.TryGet("MyMod_Line_0001", out var registered);
VoiceFeature.Unregister("MyMod_Line_0001");
var names = VoiceFeature.Names;          // every registered name, ordinally sorted
var all   = VoiceFeature.Snapshot();     // name, source, path, priority

// Make Satone say it, with her mouth moving:
VoiceFeature.PlayAsSatone("MyMod_Line_0001");
```

`Register` sets `clip.name` to the voice name, because that is the identity the game's own cache
would have used. A destroyed clip is treated as unregistered, so the game falls back to its own
lookup rather than playing silence.

### Why `PlayAsSatone`, and not `VoiceManager.Play`

`VoiceManager.Play(name)` plays the audio and nothing else. **Satone's mouth animation is driven
inside `HeroineService.PlayVoice`** — it goes to `HeroineAI` → `HeroineVoiceController.PlayVoice`,
which sets the `Enable_Talk` animator bool, plays the clip, and clears the bool in the play
callback. `PlayAsSatone` therefore calls
`HeroineService.PlayVoice(name, isMoveMouse, isStory: false)`, so a clip we registered looks exactly
like a line the game shipped.

`isStory: false` on purpose: a story line is owned by the scenario reader and would cut whatever it
is currently reading.

`HeroineService` lives in the room scene's VContainer scope, so it is resolved lazily — first
`RoomLifetimeScope.Resolve<HeroineService>()`, then `FindObjectOfType` — and the log says which one
answered. Before `RoomScene` is up, `PlayAsSatone` returns false and says so.

## Language voice sets

`audio/voice/<lang>/<VoiceName>.ogg` (or `.wav`) replaces the game's clip **only while that language
is selected** — e.g. a pack's `audio/voice/th/<VoiceName>.ogg` plays while ไทย (Thai) is chosen,
and the game's Japanese clip plays in every other language. Files directly in
`audio/voice/` stay global overrides (above); `PackContentIndex` classifies the language folder's
files as `Other`, so the global preload never sees them.

| Rule | Where |
|---|---|
| Folder = language code in any spelling `SubtitleLanguages.Normalize` accepts (`th`, `TH`, `thai`); direct children only; hidden files ignored; later pack over earlier, later path over earlier within a pack | `Core/Voice/LanguageVoicePlan.cs` |
| Who answers a lookup: mod runtime clip → loaded clip of the set in force → global pack clip → the game | `Core/Voice/LanguageVoiceRules.cs`, applied in `VoicePatches.GetAudioClipPrefix` |
| Which set is in force: the game's current language (`LanguageFeature.CurrentCode`) read at every lookup, every second (`GameClockTick`) and at `RoomReady`; one `VOICE` line per change | `Core/Voice/LanguageVoiceSwitch.cs`, `LanguageVoices.Observe` |
| State and load order (asked-for first, then plan order; failures not retried; rescans keep unchanged clips) | `Core/Voice/LanguageVoiceTable.cs` |

**Switching** the language in the selector takes effect at the next line, with no restart. Nothing is
stopped or destroyed on a switch, so a line that is playing finishes in its own voice; the set's
loaded clips stay in memory for when the language comes back.

**Loading.** Clips stay **Vorbis-compressed in memory**, like the game's own — `LanguageVoiceLoader` drives
`UnityWebRequestMultimedia.GetAudioClip` with `DownloadHandlerAudioClip.compressed = true`,
`streamAudio = false` (by reflection: this adapter does not reference the UnityWebRequest modules).
The set in force preloads after `RoomReady` (or on a switch to it) on the shared dispatcher: at most
3 starts and 6 requests in flight, completions within 2 ms per frame. First in the queue: lines with
no game clip at all (pack-exclusive), then the lines of a scenario that is being readied
(`LanguageVoicePatches`: read-only postfix on `ScenarioReader.StartReady`, rows from
`_currentGroupLocalizations`), then a line that just fell back. A line asked for before its clip
loaded plays the next clip in line (pack, else the game's Japanese) that one time — never silence —
except a pack-exclusive line, which has nothing to fall back to: that case is a `VOICE … has no voice
this time` WARN, and `LanguageVoices.CanPlay(name)` lets a selection gate avoid it.

**Memory.** A compressed clip costs about a tenth of a decoded one: a 3 s line is 20–40 KB of
Vorbis, against ≈ 290 KB as 16-bit 48 kHz mono PCM. The preload's `VOICE … preloaded` line reports
the exact compressed bytes and the allocator delta measured in game.

**Timing.** Story text rows wait for the text and the voice (`ScenarioReader`) and the mouth runs
while the clip plays (`HeroineVoiceController.PlayVoice`), so a replacement take drives the advance
and the mouth by itself. The `GetAudioClip` postfix records the
clip that really plays (`VoicePlayback`); the subtitle hold keeps the text up for it + 0.3 s
(`Subtitles/README.md`).

API: `LanguageVoices.Codes`, `ActiveCode`, `CountsFor(code)`, `HasSet(code)`, `TryPeek(name, out clip)`,
`CanPlay(name)`, `Describe()`, `Hits` / `Fallbacks` / `Missing`, `LastSummary`;
`VoiceFeature.TryGetEffective(name, out clip)` — the overriding clip that would play now, without side
effects (use its `length` to measure a line).

Console lines: `SETUP … src=voice.lang` once at start; `VOICE th voices on: N clip(s) loaded= via=`
/ `VOICE th voices off` per switch; `VOICE th voices preloaded: …` when the set has loaded (seconds,
compressed MB, main-thread ms, longest frame, clips longer than 1.25× / shorter than 0.8× the game's,
the largest overrun). Trace file only: `voice/langFallback`, `voice/langMissing`,
`voice/langPrefetch`, `voice/langExclusiveFirst`, `voice/hit` (with `choice` and `lang`).

## Config (`Voice.*`)

| Key | Default | What it does |
|---|---|---|
| `Enabled` | `true` | Off = no patch, no preload, the game's clips completely untouched. |
| `PreloadPackClips` | `true` | Off = packs are not read; a mod can still register at runtime. |
| `LogEachRegistration` | `true` | Off = only the summary line, not one line per clip. |
| `LanguageVoices` | `true` | Off = language sets are ignored; every language plays the game's clips (global overrides still apply). Read at startup. |

## Diagnostics

- Smoke checks (`BepInEx/smoke-report.json`): `p1.voice.patched`, `p1.voice.registered`,
  `p1.voice.resolve` (the game's `GetAudioClip` hands back *our clip object*, proven by reference),
  `p1.voice.play`, `p1.voice.ogg` (passes with `no ogg fixture (ffmpeg absent)` when no pack ships
  one). Language voice sets, both quiet: `th.voice.sets` (sets, loaded/failed counts, hits/fallbacks) and
  `th.voice.resolve` (through the patched `GetAudioClip`, up to 5 names return the set's clip under
  the set's language and the game's clip under English, by reference — the language is pretended for
  the call, never switched; passes as "not proven" when no set has loaded because its language was
  never selected).
- `TraceBus` events under `sys: "voice"`: `registered`, `override`, `refused`, `unregistered`,
  `preload`, `play`, and `hit` — one per lookup the prefix answered, with the name and the source.

## Files

| File | What it is |
|---|---|
| `VoiceFeature.cs` | Public API, config, preload, the `HeroineService` bridge. |
| `VoicePatches.cs` | The Harmony prefix (who answers) and postfix (what plays) on the closed-generic `GetAudioClip`. |
| `VoiceSeam.cs` | All the reflection into `AudioManager.dll` (not referenced at compile time), incl. the game's clip cache. |
| `BulbulVoiceTargets.cs` | The closed-generic patch targets, verified at startup; `voice.lang.grouplocalizations`. |
| `VoiceSmokeChecks.cs` | The five in-game checks. |
| `LanguageVoices.cs` | Language voice sets — selection, switch lines, background/priority loading, fallbacks. |
| `LanguageVoiceLoader.cs` | Language voice sets: compressed-in-memory clip loading through UnityWebRequest (reflection). |
| `LanguageVoicePatches.cs` | Language voice sets: the read-only `ScenarioReader.StartReady` postfix (scene prefetch). |
| `LanguageVoiceSmokeChecks.cs` | Language voice sets: `th.voice.sets`, `th.voice.resolve`. |
| `VoicePlayback.cs` | Language voice sets: the last clip handed to the voice manager, for the subtitle hold. |
| `CwyLs.ModLoader.Core/Voice/` | Unity-free logic: registry, name rules, load plan; language plan/set/table/rules/switch/lengths (unit-tested). |
