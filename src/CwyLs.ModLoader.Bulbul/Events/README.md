# `CWYEvents` — the typed game event bus

One place to find out what is happening in *Chill With You: Lo-Fi Story*: what Satone is doing, what
she just said, which pomodoro interval is running, what is playing, what the player saved.

```csharp
using CwyLs.ModLoader.Bulbul.Events;
using CwyLs.ModLoader.Core.Events;

private IDisposable? _sub;

private void Awake()
{
    _sub = CWYEvents.Subscribe<LineSpoken>(
        line => Logger.LogInfo($"{line.VoiceName}: {line.Text} ({line.Source})"));
}

private void OnDestroy() => _sub?.Dispose();
```

Reference it from your plugin project:

```xml
<ProjectReference Include="../CwyLs.ModLoader.Bulbul/CwyLs.ModLoader.Bulbul.csproj"><Private>false</Private></ProjectReference>
<ProjectReference Include="../CwyLs.ModLoader.Core/CwyLs.ModLoader.Core.csproj"><Private>false</Private></ProjectReference>
```

and add `[BepInDependency(BulbulPlugin.PluginGuid)]` to your plugin class so the bus is up before
your `Awake` runs.

---

## The events

Every event type lives in `CwyLs.ModLoader.Core.Events` and is a C# `record`: value equality, a readable
`ToString()`, and a one-line `Describe()` used by the console mirror.

| Event | Arguments | Raised when |
|---|---|---|
| `SatoneStateChanged` | `Previous`, `Next` (state names), `PreviousValue`, `NextValue` (ints) | Her action state machine moves — `WantTalk`, `WorkPC`, `BreakMovie`, … |
| `LineSpoken` | `VoiceName`, `Text`, `TextJa`, `LocalizationId`, `Attribution`, `NoTextInMaster`, `Language`, `IsStory`, `ScenarioType`, `ScenarioGroupId`, `Source`, `MoveMouth`, `DurationSec` | A voice clip actually starts playing |
| `LineEnded` | `VoiceName`, `DurationSec` (how long she really spoke) | The clip finished, was cancelled, or was cut off by the next line |
| `ScenarioStarted` | `ScenarioType`, `GroupId`, `ReadMode` | `ScenarioReader.StartReady` — a scripted scene begins |
| `ScenarioEnded` | `GroupId` (nullable) | `ScenarioReader.EndStory` — **once per `ScenarioStarted`**: the game calls `EndStory` again one frame after the game-start greeting and from the exit button, and those repeats go to the trace file only, as `scenario/endSuppressed` (`groupId`, `reason`, `caller`) |
| `PomodoroEvent` | `Kind`, `PomodoroType` | Started / WorkStarted / WorkEnded / BreakStarted / BreakEnded / Completed / Paused / Unpaused |
| `PomodoroTick` | `PomodoroType`, `ElapsedSec`, `RemainingSec` | The timer advances — **throttled to 1/s per timer type** (the game raises it every frame) |
| `WindowChanged` | `Window`, `Active` | A window view is switched on or off (`Sakura`, `Snow`, `Night`, …) |
| `MusicChanged` | `Title`, `Index`, `IsShuffle` | A track starts, or shuffle is toggled. `Index` is the position in the current playlist, `-1` if unknown |
| `SaveSection` | `Section` | The game wrote part of the save (`TodoList`, `Note`, `PomodoroData`, …) — **throttled to one per section per 250 ms**, because the game saves in bursts |
| `SceneChanged` | `Scene`, `Loaded` | Any Unity scene loads or unloads |
| `GameClockTick` | `Time` | Once a second. The game's clock *is* `DateTime.Now` (`DateService.Setup`) |
| `RoomReady` | — | `RoomScene` is loaded **and** its services resolve: the first moment it is safe to touch Satone |

### `LineSpoken` in detail

This is the one most mods want, so it is deliberately complete.

* `VoiceName` — the clip, e.g. `Voice_Tutorial_1_001`. Always present.
* `Text` — the line in the player's current language; `null` if the clip has no master-data row.
* `TextJa` — the Japanese original, always resolved when a row exists (it is the authored column).
* `LocalizationId` — the master-data row id the text came from; `null` when there is no row.
* `Attribution` — **how** that id was resolved, one of `LineAttribution.Row`, `VoiceIndex`, `None`
  (`Unknown` only if a non-framework publisher ever builds one). See *Which row is she speaking?*
  below. If you are checking that a line you started is the line that played, compare the
  `LocalizationId` **and** require `Attribution == LineAttribution.Row`.
* `NoTextInMaster` — `true` when the voice name has no row in the game's master data at all, so
  `Text` and `TextJa` are `null` and no text was ever available. This is the normal state of a clip
  a content pack registered (`audio/voice/<VoiceName>.ogg`) or a mod generated at runtime, and it is
  reported explicitly — in the payload and as `(no text in master)` in the console line — so an
  unknown clip reads as "not in the game's data" instead of as a failed lookup. A clip that *does*
  have a row whose text simply did not resolve is **not** marked.
* `Language` — which language `Text` is in, from the game's own `LanguageSupplier.Get()`.
* `Source` — why she is talking: `Story`, `SelfTalk`, `Pomodoro`, `ClickReaction`, `Tutorial`,
  `Other`. Derived from the scenario type that is playing.
* `ScenarioType` / `ScenarioGroupId` — `null` when no scenario is playing.
* `IsStory` — the caller asked for story priority (a story line interrupts an ongoing one).
* `MoveMouth` — the mouth-talk motion was requested.
* `DurationSec` — the clip's length. `0` when the clip could not be measured, which in practice
  means the game could not find it either.

Pair it with `LineEnded` to know when she stops:

```csharp
CWYEvents.Subscribe<LineSpoken>(l => _subtitle.Show(l.Text ?? l.TextJa, l.DurationSec));
CWYEvents.Subscribe<LineEnded>(_ => _subtitle.Hide());
```

### Which row is she speaking?

A voice clip name is not a key. Two master-data rows may name the same clip — the shipped v1.17.2
table has two such names (`Voice_MainScenario_6_009`, `Voice_ClickHeroine_Word_Work_013`) out of
1354, and a content pack or a `Satone.Say` line can add more — so a reverse index from clip name to
row has to pick a winner and every line playing that clip inherits it. That is how a `Satone.Say`
line once reported a content pack's id and the pack's text.

So the bus resolves in two steps:

1. **The row the reader is reading.** A Harmony *prefix* on `ScenarioReader.CommandText`,
   `CommandVoiceText`, `CommandPomodoroVoiceText` and `CommandTextLoopPlayText` — the four private
   `UniTask Command*(NovelData)` commands that call `HeroineService.PlayVoice` — records that row's
   `LocalizationID`, `ScenarioGroupID`, voice column and frame. When the clip that then starts is
   the clip that row names, the line is attributed to it: `Attribution == LineAttribution.Row`, and
   the id and both texts come from that row and nothing else. The hint is consumed by the line it
   answers and cleared again at `EndStory`, so it can never name a later clip.
2. **The reverse voice-name index**, otherwise: `Attribution == LineAttribution.VoiceIndex`. This is
   the right answer for a clip no scenario started — a mod calling `VoiceManager.Play`, a mod's runtime clip,
   an idle line — and the only one available for them. `LineAttribution.None` means neither source
   had a row, which is also what `NoTextInMaster` reports.

The voice-name guard is what makes step 1 safe: `HeroineVoiceController.PlayVoice` awaits one frame
before the clip starts, and anything may play in that window.

The index is rebuilt when a **content fingerprint** over the table's `(id, voice)` pairs changes,
not when the row count changes — an overridden row keeps the count identical. Each rebuild logs one
`EVENT voice index built: …` line, and the first time a clip name is seen claimed by more than one
row it logs one warning naming up to ten of them. That notice is expected on a normal session (two
names) and is a statement about the data, not a failure.

### One `LineSpoken` per line

A subscriber sees each line once. A second publish of the **same row for the same clip** within
250 ms is dropped as a republish (`SpeechRepeat`), counted, and logged — loudly the first time,
because the game cannot produce one on its own. Two *different* lines that merely share a clip name
are two lines and both are published: the loud smoke pass does exactly that, playing
`Voice_CWY_Test` once straight through `VoiceManager.Play` (`p1.voice.play`) and once through a Say
scenario (`p2.say.scenario`).

### The catch-all stream

```csharp
CWYEvents.SubscribeAny(e => Logger.LogInfo(e.Describe()));   // e is (Name, Args)
```

`AnyEvent.Args` is the same instance the typed channel published; cast it when `Name` matches.

---

## Threading

* **Every handler is called on the Unity main thread.** You may touch `GameObject`s, coroutines and
  the rest of the engine directly.
* Publishers already on the main thread (all of the Harmony postfixes, in practice) call straight
  through, so you see the event inside the same frame the game did the thing.
* Anything published off the main thread is queued and drained by the bus's hidden
  `DontDestroyOnLoad` runner on the next `Update`. The queue holds 4096 entries; beyond that the
  oldest are dropped and a warning is logged once (`CWYEvents.DroppedFromQueue`).
* `Subscribe` / `Unsubscribe` are safe from any thread, and are safe to call from inside a handler —
  the change takes effect on the next publish, not the one in flight.

## Never throw in a subscriber

The bus publishes from inside the game's own call stack: a `LineSpoken` handler runs in the middle of
`VoiceManager.Play`, a `SaveSection` handler in the middle of the player's save. If you throw:

* the exception **is** caught, so the game is never damaged;
* it is logged as `EVENT <event>: subscriber <YourType>.<YourMethod> threw …`;
* the remaining subscribers still run;
* and nothing else happens — no retry, no unsubscribe.

That is a safety net, not a design. Wrap your own risky work in `try`/`catch` and keep the handler
short: you are on the game's frame budget.

Handlers also must not block. Kick long work onto a `UniTask`/thread and come back to the bus.

## Lifetime

* The bus starts in `BulbulPlugin.Awake` and lives for the whole process — its patches are static
  and its runner is a hidden `DontDestroyOnLoad` object, because in this game the BepInEx plugin
  component is destroyed about 0.2 s after startup.
* It shuts down only on `Application.quitting`.
* `CWYEvents.IsReady` tells you whether start-up succeeded. You may subscribe before it is true;
  you just will not receive anything until the game produces it.
* Subscriptions are **not** cleared between scenes. Dispose your tokens yourself.

## Configuration

`BepInEx/config/cwyls.modloader.bulbul.cfg`, section `[Events]`:

| Key | Default | Effect |
|---|---|---|
| — | — | `MirrorToConsole` and `MirrorTicks` are gone. There is one console sink now, and one owner per event kind: an event either has a tag in `Core/Logging/ConsoleEventMap` (`SCENE`, `SCENARIO`, `MUSIC`, `POMODORO`, `WINDOW`, `READY`, and `SPEECH`/`STATE` for the two Satone ones) or it stays in the trace file. Ticks, `SaveSection` and `LineEnded` are file-only. See `src/CwyLs.ModLoader/README.md` → *Reading the log*. |

## Diagnostics

* Every event is emitted to `CwyLs.ModLoader.Core.Bus.TraceBus` **once**, payload = `ICwyEvent.Fields()`.
  The `CwyLs.ModLoader.Trace` plugin writes and mirrors it.
* `LineSpoken` goes out as `satone/speech` and `SatoneStateChanged` as `satone/state` — the two
  curated names the diagnostic console formats. Every other event goes out as
  `event/<EventTypeName>`, `LineEnded` included. These two used to be published under both `event/*`
  and `satone/*`, which put an identical payload on the bus twice (28 pairs in one measured
  session); the typed channels are unchanged.
* Smoke checks, in `BepInEx/smoke-report.json`: `p1.events.ready`, `p1.events.scene`,
  `p1.events.room_ready`, `p1.events.satone_state`, `p1.events.music`, `p1.events.speech_detail`,
  `p1.events.attribution`, `p1.events.pomodoro_wired`, `p1.events.pomodoro_seen`. Several of them only report what has
  already been *observed* this session, so they cannot pass three seconds into a launch. No
  smoke run starts by itself (`Smoke.AutoRunOnRoomScene=false`, `Smoke.SecondPassSeconds=0`;
  set it to e.g. 60 for a second pass a minute after `RoomScene` loads). Counters run for the whole
  session, so run the checks on demand after exercising a feature: **Ctrl+Shift+S** (or F9), the
  `BepInEx/smoke.run` trigger file, or console `smoke run`.

## Where the events come from

| Event | Seam |
|---|---|
| `SatoneStateChanged` | Harmony prefix+postfix on `HeroineAI.ChangeState` |
| `LineSpoken` | Harmony postfix on `KanKikuchi.AudioManager.VoiceManager.Play`, attributed to the row captured by the prefixes on `ScenarioReader.{CommandText, CommandVoiceText, CommandPomodoroVoiceText, CommandTextLoopPlayText}(NovelData)` or, failing that, to `MasterDataLoader.LocalizationList` by voice name |
| `LineEnded` | Polled from `HeroineService.IsFinishedVoice`, or the clip length as a fallback |
| `ScenarioStarted` / `ScenarioEnded` | Harmony postfix on `ScenarioReader.StartReady` / `EndStory` |
| `PomodoroEvent` / `PomodoroTick` | R3 subscription to `PomodoroService`'s observables |
| `WindowChanged` | Harmony postfix on `WindowViewService.{ChangeWindowView, ChangeWeatherAndTime, ActivateWindow, DeactivateWindow}` |
| `MusicChanged` | R3 subscription to `MusicService.{OnPlayMusic, OnEnableShuffle, OnDisableShuffle}` |
| `SaveSection` | The shared save hook (`SaveDataHook`): one Harmony postfix on every public `SaveDataManager.Save*`/`Delete*` method (found by reflection, `*Throttled` excluded); `SaveSection` is published for the `Save*` ones |
| (no typed event) | `MusicService.OnChangeMusic`: subscribed only to hand it to the data bus |
| `SceneChanged` | `SceneManager.sceneLoaded` / `sceneUnloaded` |
| `GameClockTick` / `RoomReady` | The bus's own runner |

Every one of those members is declared in `BulbulEventTargets` and verified against the running game
at startup. If a game update moves one, the log says `EVENT PATCH TARGET MISSING <id>: …` and
`p1.events.ready` fails — the affected event stops, the rest keep working.

## One observation per game event

The event bus and the data bus (`CwyLs.ModLoader.Bulbul.Data`) used to hook the same things twice: both
postfixed the `SaveDataManager.Save*` family and both subscribed to the pomodoro and music
observables. Now each is observed once, here, and the data bus listens to the event bus:

* **Saves.** `SaveDataHook` owns the only CWY postfix on each save/delete method (46 in game 1.17.3:
  41 `Save*`, 5 `Delete*`). Its listeners run in load order: the event bus (`EventPatches.OnSaved`,
  throttled `SaveSection` for `Save*`), then the data bus (`GameData.Changed` with reason
  `save:<Method>`). Whichever starts first installs it. The data plugin writes the census to the
  trace file at startup: `events/saveHook ... listeners=2 busPostfixesPerMethod=1` (the trace plugin's own diagnostic hooks are a separate layer and not counted).
* **Pomodoro and music.** `EventBridge` is the only subscriber. After publishing the typed event it
  hands each discrete firing on as a `GameSignal` (`PomodoroService.OnStartWork`, …;
  `EventBridge.SubscribeSignals`, internal to the adapter and its data plugin). The data bus maps the
  ones it reports (`Core/Data/GameDataSignals`) to the same `event:<Name>` reasons as before. The
  per-frame `OnUpdatePomodoro` is not a signal.
* Both hooks are framework plumbing, visible to `CwyLs.ModLoader.Bulbul.Data` only
  (`AssemblyInfo.cs`, `InternalsVisibleTo`). Mods keep using `CWYEvents` and `GameData.Changed`.
