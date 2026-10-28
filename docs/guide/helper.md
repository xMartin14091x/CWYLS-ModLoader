<sub>[Guide](README.md) &nbsp;·&nbsp; [Getting started](getting-started.md) &nbsp;·&nbsp; [Manifest](manifest.md) &nbsp;·&nbsp; [Lifecycle](lifecycle.md) &nbsp;·&nbsp; **Helper** &nbsp;·&nbsp; [Troubleshooting](troubleshooting.md) &nbsp;·&nbsp; [Versioning](versioning.md) &nbsp;·&nbsp; [API reference](../api/README.md)</sub>

# The helper (`ICwyModHelper`)

Everything your mod does goes through one object, the helper. `Entry` receives it, and `Helper`
returns it anywhere else in your `CwyMod` class. It is made for your mod alone: its log lines,
commands, hotkeys, config and data carry your mod's id, every call into your code is wrapped, and
everything you registered is removed when your mod stops.

This page gives one short example per facet, then the reference notes, then the threading rules.
Exact signatures are in the [API reference](../api/CwyLs.ModLoader.Mods.md#icwymodhelper).

| Member | What it gives you |
|---|---|
| `ModId`, `Manifest`, `DirectoryPath` | Your id, your parsed `manifest.json`, your folder in `CWYLS Mods` |
| [`Log`](#log) | Console and trace lines tagged with your id |
| [`Config`](#config) | Your BepInEx config file, `BepInEx/config/cwy.mod.<id>.cfg` |
| [`Data`](#data) | Your own files in `BepInEx/data/mods/<id>/`, written atomically |
| [`Events`](#events) | Game events, per-frame ticks, the mods-ready signal |
| [`Satone`](#satone) | Expression, motion, gaze, restore, new spoken lines |
| [`GameData`](#gamedata) | The player's data: a read-only snapshot and change notifications |
| [`Language`](#language) | The language the game is showing, including pack languages |
| [`Content`](#content) | Files in your own folder: text, JSON, PNG, WAV, OGG |
| [`ContentPacks`](#contentpacks) | Content packs made for your mod |
| [`Input`](#input) | Hotkeys, rebindable in your config file |
| [`Console`](#console) | Commands for the in-game console |
| [`Ui`](#ui) | Windows in the game's own style |
| [`MainThread`](#mainthread) | The shared main-thread dispatcher |
| [`ModRegistry`](#modregistry) | Other mods and packs, and their APIs |

The game-specific facets (`Events`, `Satone`, `GameData`, `Language`, `Ui`) come from the game
adapter plugins. Without them a facet reports `IsAvailable = false` and does nothing; `Events`,
`Satone` and `GameData` also log one `MODS <id>: … not available …` warning the first time you use
them, and `Ui.CreateWindow` returns null with the reason. None of them throws for it.

<!-- src/CwyLs.ModLoader/Mods/ICwyModHelper.cs:14-70; backends: src/CwyLs.ModLoader/Mods/ModBackends.cs:50-121; warn-once: src/CwyLs.ModLoader/Mods/ModHelper.cs:225-239, e.g. ModFacets.Game.cs:30-35. -->

---

## Log

```csharp
helper.Log.Info("ready");                         // MOD  my.mod: ready
helper.Log.Warn("Volume out of range; using 5");  // same line at warning level
helper.Log.Trace("raw reply: {...}");             // trace file only
try
{
    helper.Data.WriteText("notes.txt", "…");
}
catch (Exception ex)
{
    helper.Log.Error("could not save notes", ex); // message on the console, stack in the trace
}
```

- `Info`, `Warn` and `Error` write one console line, tag `MOD`, text `<id>: <message>`. Line
  breaks are folded into spaces: one call, one line.
- `Error(message, exception)` appends `(ExceptionType: message)` to the line and puts the full
  stack in the trace file.
- `Trace` goes to the trace file only (`"sys":"mod"`, `"m":"<id>"`), never to the console.
- Safe from any thread.

<!-- src/CwyLs.ModLoader/Mods/ModFacets.Framework.cs:22-59; ICwyModFacets.cs:15-33; bus safe from any thread: src/CwyLs.ModLoader.Core/Bus/TraceBus.cs:68-71. -->

## Config

```csharp
ConfigEntry<bool> greet = helper.Config.Bind("General", "Greet", true, "Say hello when the room is ready.");
ConfigEntry<int> volume = helper.Config.Bind("General", "Volume", 5, "0 to 10.");
if (greet.Value && volume.Value > 0)
{
    helper.Log.Info("greeting at volume " + volume.Value);
}
```

- A standard BepInEx `ConfigFile` (`using BepInEx.Configuration;`), stored as
  `BepInEx/config/cwy.mod.<id>.cfg` and created the first time you touch `Config`.
- Your hotkeys live in the same file, section `[Hotkeys]` (see [Input](#input)).
- The player edits the file with the game closed; bind in `Entry` so the entries (with their
  descriptions) exist after the first run.

<!-- src/CwyLs.ModLoader/Mods/ModHelper.cs:55-64, :107; ICwyModHelper.cs:28-33. -->

## Data

```csharp
helper.Data.WriteText("count.txt", "3");
string? text = helper.Data.ReadText("count.txt");      // null when the file does not exist

helper.Data.WriteJson("state.json", new State { Arrivals = 3 });   // writes {"arrivals":3}
object? json = helper.Data.ReadJson("state.json");               // a JsonObject graph, or null
long arrivals = JsonPath.Get(json, "arrivals") is long n ? n : 0;
```

(`State` is any class of yours with public properties; `JsonPath` is in `CwyLs.ModLoader.Core.Json`.)

- Files live in `BepInEx/data/mods/<id>/`, created on the first write. `Data.DirectoryPath` is the
  absolute path.
- A **key** is a file name: letters, digits, `_`, `.` and `-`, 1 to 64 characters, not starting
  with a dot, no `..`. Anything else throws `ArgumentException`, so a key can never point outside
  the folder.
- **Writes are atomic**: the text goes to `<key>.tmp~` first, then replaces the file, so a crash
  never leaves half a file. Text is UTF-8.
- `WriteJson` writes public properties and fields, **keys in camelCase**, up to 12 levels deep;
  `ulong` values become strings (they exceed what JSON numbers hold exactly), enums their names,
  dates ISO-8601.
- `ReadJson` parses strict JSON into `JsonObject`, lists, `string`, `long`, `double`, `bool` or
  null; there is no typed deserialiser. A broken file throws `JsonParseException` with the
  line and column.
- `Exists`, `Delete` (true when a file was deleted) and `Keys()` (sorted) complete the set.
- Thread-safe.

<!-- src/CwyLs.ModLoader/Mods/ICwyModFacets.cs:35-84; ModFacets.Framework.cs:61-181; key rule: src/CwyLs.ModLoader.Core/Mods/ModDataKey.cs:15,:28-46; JSON writer rules: src/CwyLs.ModLoader.Core/Data/JsonWriter.cs remarks (camel-cased keys, ulong as string, depth 12). -->

## Events

```csharp
helper.Events.Subscribe<PomodoroEvent>(e =>
{
    if (e.Kind == PomodoroEventKind.WorkStarted)
    {
        helper.Log.Info("focus time: " + e.PomodoroType);
    }
});

helper.Events.Subscribe<LineSpoken>(e => helper.Log.Trace("she said: " + e.Text));

IDisposable ticker = helper.Events.EveryFrame(() =>
{
    // runs every frame on the main thread; keep it cheap
});
ticker.Dispose();   // stop early; otherwise it stops when the mod stops
```

The events (records in `CwyLs.ModLoader.Core.Events`):

| Event | When | Main fields |
|---|---|---|
| `RoomReady` | The room is up and its services resolve; once per room load | — |
| `SatoneStateChanged` | Her action state changed | `Previous`, `Next` |
| `LineSpoken` | She started speaking a line | `Text`, `TextJa`, `VoiceName`, `LocalizationId`, `Source`, `DurationSec` |
| `LineEnded` | She stopped speaking | `VoiceName`, `DurationSec` |
| `ScenarioStarted` / `ScenarioEnded` | A scenario started / finished | `GroupId` (+ `ScenarioType`, `ReadMode`) |
| `PomodoroEvent` | A pomodoro transition | `Kind` (Started, WorkStarted, WorkEnded, BreakStarted, BreakEnded, Completed, Paused, Unpaused), `PomodoroType` |
| `PomodoroTick` | The timer advanced (once a second) | `ElapsedSec`, `RemainingSec` |
| `MusicChanged` | A track started | `Title`, `Index`, `IsShuffle` |
| `WindowChanged` | A window view was switched on or off | `Window`, `Active` |
| `SaveSection` | The game saved a section of the player's data | `Section` |
| `SceneChanged` | A Unity scene loaded or unloaded | `Scene`, `Loaded` |
| `GameClockTick` | One second of wall-clock time passed | `Time` |

- `Subscribe<T>` returns a handle; dispose it to stop early. Everything stops when your mod stops.
- `SubscribeAny` delivers every event as an `AnyEvent` (`Name`, `Args`).
- `EveryFrame` runs once per frame, after the frame's queued main-thread work: at the game's frame
  rate setting while focused (60 by default on PC) and 24 times a second by default while not.
- `OnModsReady` runs once, when every mod has entered and published its API (see
  [lifecycle.md](lifecycle.md#talking-to-other-mods-getapi)). Registered later, it runs on the next
  frame.
- Handlers run on the main thread, inside the game's own call. A handler that throws is logged
  against your mod and keeps its subscription. Mods cannot publish events of their own yet.

<!-- ICwyModFacets.cs:86-120; ModFacets.Game.cs:13-80; event records: src/CwyLs.ModLoader.Core/Events/GameEvents.cs:64-365; fps: measured in game (unfocused median 23.8; the game's unfocused frame rate defaults to 24). -->

## Satone

```csharp
if (!helper.Satone.CanInteract)
{
    return;   // she is busy (a scene, a click reaction); try again later
}

SayHandle handle = helper.Satone.Say(new SayRequest
{
    Text = "Time for a short break?",
    TextJa = "ちょっと休憩しようか？",
    Facial = "smile",
});

if (handle.IsCompleted && handle.Outcome == SayOutcome.Rejected)
{
    helper.Log.Warn("Satone cannot say it: " + handle.Detail);
}
```

**Speaking.** `Say(string)` shows a line in the game's text box (no voice). `Say(SayRequest)`
adds translations, a voice clip name, an expression, a motion and a gaze. `SayWithClip(request,
clip)` speaks an `AudioClip` your mod made or loaded (for example with `Content.LoadAudioAsync`). Each returns a `SayHandle` at once: `Id`,
`IsCompleted`, `Outcome`, `Detail`, `Completion` (a `Task<SayOutcome>`) and `Cancel()`.

| `SayRequest` member | Meaning |
|---|---|
| `Text` | Required, at most 500 characters. Fills every language column of the game except Japanese |
| `TextJa` | The Japanese column; null uses `Text` |
| `Texts` | Translations keyed by language code, for languages packs declare (e.g. `vi`) |
| `VoiceName` | A voice clip name (letters, digits, `_`, `-`); null for a text-only line. With `SayWithClip` it may stay null: a name is generated |
| `Facial`, `Motion` | Expression / body motion by name, alias or id; null leaves them alone |
| `LookScale`, `LookSeconds` | Turn towards the player (0..1) over that many seconds; null leaves the gaze to the game |
| `Language` | The language `Text` is written in; null means English |
| `MoveMouth` | Mouth animation while a clip plays (default true) |

| Outcome | Meaning |
|---|---|
| `Pending` | Still queued or being spoken |
| `Spoken` | Played through the game's own scenario reader: text box, voice, motion, timing |
| `FellBack` | The game stayed busy, so only the voice played (her mouth moves, no text box, no scenario motion); a line without a voice then appears only in the log |
| `Cancelled` | You cancelled it before it started |
| `Rejected` | Never accepted: bad request, feature off, or the queue (8 lines by default) was full; `Detail` says which |
| `TimedOut` | Started, but the game never reported the end, so the queue moved on |

**Face, motion, gaze.** `Face("smile")`, `FaceReset()`, `Motion("wild001_good")`,
`LookAt(scale, seconds)`, `LookReset()` return true when the request was accepted (`Face` and
`Motion` return false for a name the table does not know).
`RestoreDefaults(scope)` hands her back to the game the game's own way (re-dispatches her current
state, resets the face, eases the gaze) and returns what it did. The names come from the
framework's table, `src/CwyLs.ModLoader.Core/Satone/satone-table.json`: 20 expressions (`smile`,
`joy`, `wink`, `sleepy`, …) and 181 motions; the console command `satone face <name>` or
`satone motion <name>` tries one out.

**State.** `CanInteract` (the game's own "can she be talked to now"; false while the room is not
loaded), `IsSaying` (one of the framework's lines is playing), `State` (her AI state name, e.g.
`work_pc`; `unknown` without a room).

- Safe from any thread: off the main thread a call is queued for the next frame.
- `Completion` is an ordinary `Task`; a continuation may run on another thread, so get back to the
  main thread with `helper.MainThread` before touching Unity or the game.
- Each call leaves a trace-file record naming your mod.

<!-- ICwyModFacets.cs:122-180; SayRequest: src/CwyLs.ModLoader.Core/Satone/Say/SayRequest.cs:29-120 (MaxTextLength :40); outcomes: SayOutcome.cs:4-34; queue 8/10 s/120 s: SayQueue.cs:40-46; generated voice name: src/CwyLs.ModLoader.Bulbul/Satone/Say/SatoneSayFeature.cs:842-850; restore: src/CwyLs.ModLoader.Bulbul/Satone/Motion/README.md; any thread: Satone.Say.cs:49-51; CanInteract false without room: src/CwyLs.ModLoader.Bulbul/Satone/Motion/Satone.cs:247-259; table counts: satone-table.json "counts". -->

## GameData

```csharp
helper.GameData.OnChanged(change =>
{
    if (change.Section != GameDataSection.Todos)
    {
        return;
    }

    // Leave the game's save first, then read.
    helper.MainThread.Post("count todos", () =>
    {
        GameDataSnapshot? snapshot = helper.GameData.Snapshot();
        if (snapshot is null)
        {
            return;
        }

        var open = 0;
        foreach (TodoListView list in snapshot.Todos)
        {
            open += list.OpenCount;
        }

        helper.Log.Info(open + " open todo(s)");
    });
});
```

- **Read-only.** `Snapshot()` returns an immutable copy of the player's data: `Player`, `Profile`,
  `Account`, `Pomodoro`, `Todos`, `Notes`, `Habits`, `Calendar`, `Settings`, `Music`,
  `Environment`, `Story`, `Achievements` (types in `CwyLs.ModLoader.Core.Data`). Main thread only:
  elsewhere it throws `InvalidOperationException`; use `helper.MainThread.InvokeAsync`.
- `IsReady` is false until the game has loaded the save; before that the snapshot holds defaults.
- `OnChanged` tells you which `GameDataSection` changed (`Todos`, `Notes`, `Pomodoro`, `All`, …). It
  runs inside the game's save, so keep it short and do the work in a posted item, as above.
- The data bus's privacy rules apply: the device id is never published, and local music paths are
  reduced to file names.
- Writing the player's data (todos, notes, habits, calendar) with backups and in-game confirmation
  is planned; it is not in the helper yet.

<!-- ICwyModFacets.cs:182-203; ModFacets.Game.cs:192-238; snapshot members: src/CwyLs.ModLoader.Core/Data/GameDataSnapshot.cs:22-89; sections: GameDataSection.cs:14; privacy: src/CwyLs.ModLoader.Core/Data/GameDataPrivacy.cs. -->

## Language

```csharp
string code = helper.Language.CurrentCode;    // "en", "ja", ..., or a pack language's code such as "vi"
return helper.Language.IsPackLanguage ? "pack language " + code : "game language " + code;
```

- `CurrentCode` is the language being shown: a pack language's code (e.g. `vi`) or one of the
  game's own (`en`, `ja`, `zh-hans`, `zh-hant`, `pt-br`, `ko`, `ru`). Empty before the save loads.
- `IsPackLanguage` is true while a pack-declared language is shown.
- To show your own text in the player's language, give Satone's lines a `Texts` table rather than
  switching on the code yourself: the game's text box then picks the right one.

<!-- ICwyModFacets.cs:205-216; game codes: src/CwyLs.ModLoader.Core/Subtitles/SubtitleLanguages.cs (InGame). Pack languages: manifest.md "language.json". -->

## Content

```csharp
if (helper.Content.Exists("data/lines.json"))
{
    object? lines = helper.Content.ReadJson("data/lines.json");
    helper.Log.Trace("lines: " + lines);
}

Texture2D? icon = helper.Content.LoadTexture("assets/icon.png");   // main thread only
helper.Log.Trace("icon: " + (icon != null));

helper.Content.LoadAudioAsync("audio/hello.ogg", clip =>
{
    if (clip != null)
    {
        helper.Satone.SayWithClip(new SayRequest { Text = "Hello!" }, clip);
    }
});
```

- Paths are relative to your mod folder, with forward slashes. `GetPath` gives the absolute path
  and throws `ArgumentException` for a path that leaves the folder; `Exists` returns false for it.
- `ReadText` (UTF-8) and `ReadJson` return null when the file does not exist.
- `LoadTexture` (PNG, cached by path and file time) and `LoadWav` (16-bit PCM WAV only) run on the
  main thread and return null when the file cannot load (the reason is logged). Off the main
  thread they throw `InvalidOperationException`.
- `LoadAudioAsync` loads OGG or WAV without blocking; call it from any thread, and your callback
  runs on the main thread with the clip, or null on failure.
- Your mod folder is also a content pack: files in the pack folder conventions (`textures/`,
  `audio/voice/`, `data/`) are applied like any pack's, before your code runs.

<!-- ICwyModFacets.cs:218-255; ModFacets.Framework.cs:183-260; code mod content = content pack: src/CwyLs.ModLoader/Mods/README.md:3-6. -->

## ContentPacks

```csharp
foreach (ICwyModInfo pack in helper.ContentPacks)
{
    helper.Log.Info("content pack " + pack.Id + " in " + pack.DirectoryPath);
}
```

The loaded content packs whose manifest says `"contentPackFor": "<your id>"`, in load order. Read
their files with ordinary file IO under `pack.DirectoryPath`, and their `x-` metadata from
`pack.Manifest.Extensions`.

<!-- ICwyModHelper.cs:53-54; ModHelper.cs:78-94. -->

## Input

```csharp
helper.Input.Register(
    "greet",                       // becomes <mod id>.greet
    "Satone greets you.",
    "Ctrl+Shift+G",                // primary chord
    null,                          // no alternate
    () => helper.Satone.Say("Hi!"));
```

- The id (letters, digits, `.`, `_`, `-`) is registered as `mod:<mod id>/<id>`, so it can never
  collide with a framework key or another mod's. The player can rebind both chords in
  `cwy.mod.<id>.cfg`, section `[Hotkeys]`, entries `<id>.Primary` and `<id>.Alt`.
- `Register` returns a `CwyRegistration`: dispose it to unbind the key. A refused registration
  (id already held, bad chord) comes back inactive with `Problem` set; a chord another key already
  uses still registers, with one warning.
- Chords are written like `Ctrl+Shift+G` or `F7`; key names are Unity `KeyCode` names. On macOS an
  F-key is a media key unless Fn is held, so make the primary a `Ctrl+Shift+letter` chord.
- Keys never fire while a text field has focus. The callback runs on the main thread.
- A bad alternate chord is dropped with a warning.
- Taken by the framework: `Ctrl+Shift+C` (console),
  `Ctrl+Shift+S` (smoke run), `Ctrl+Shift+R` (texture reload), `F8`, `F9`, `F10`; planned for 1.0:
  `Ctrl+Shift+T` (hold to talk) and `Ctrl+Shift+K` (game API off). The console command `keys` lists
  the table.
- When your mod stops or fails, the framework disposes its registrations, so its keys unbind.

<!-- ICwyModFacets.cs:257-272; ModFacets.Framework.cs:262-348; [Keys] naming: src/CwyLs.ModLoader/Input/Hotkeys.cs:30-36, :104-111; framework keys: src/CwyLs.ModLoader.Core/Input/HotkeyDefaults.cs; registrations disposed on stop: ModHelper.Deactivate; claims: src/CwyLs.ModLoader.Core/Mods/HotkeyClaims.cs. -->

## Console

```csharp
helper.Console.Register("mymod", "Shows or resets my mod's counter.", "mymod [reset]", args =>
{
    if (args.Count > 0 && args[0] == "reset")
    {
        _count = 0;
        return "counter reset";
    }

    return "counter: " + _count;
});
```

- The console opens with **Ctrl+Shift+C**. Your commands are listed under `[<mod id>]` in `help`.
- The name is the first word the player types: no spaces, matched case-insensitively.
- The handler gets the words after the name and returns the text to print (null prints nothing).
  It runs on the main thread. If it throws, the console prints
  `<name> failed; the error is in the log (MODS line)`.
- The usage string (`mymod [reset]`) is shown by `help` and drives Tab completion.
- A name another mod or the framework already uses is refused (false, one warning), never
  replaced. Registering your own name again replaces your command.

<!-- ICwyModFacets.cs:274-284; ModFacets.Framework.cs:350-405; name rules: src/CwyLs.ModLoader.Core/Console/ConsoleCommand.cs:16-66. -->

## Ui

```csharp
ICwyModWindow? window = helper.Ui.CreateWindow(
    new CwyWindowOptions { Title = "My Mod", InputPlaceholder = "Type here" },
    out string problem);
if (window is null)
{
    helper.Log.Warn("no window: " + problem);
    return;
}

window.AppendLine("<b>Hello</b> from my mod");
window.Submitted += text => window.AppendLine("> " + text);
window.CloseClicked += () => window.Hide();
window.Show();
```

- A window in the game's own style (title bar, close button, drag, resize, the game's fade) with a
  scrolling text view (TextMeshPro rich text, the last 500 lines) and, when `InputPlaceholder` is
  set, a one-line input along the bottom. Enter submits the line (`Submitted`) and clears it.
- It starts hidden; `Show()` and `Hide()` toggle it. The close button raises `CloseClicked` and does
  not hide the window by itself.
- Main thread only. Build it when you need it (on `RoomReady` or later): `CreateWindow` returns null
  with the reason in `problem` when the game's UI assets are not available.
- Destroyed when your mod stops; `Destroy()` does it earlier.
- The full toolkit (lists, buttons, tabs, chat bubbles, multi-line input) arrives with 1.0.

<!-- ICwyModFacets.cs:286-352; ModFacets.Game.cs:250-295; adapter: src/CwyLs.ModLoader.Bulbul/Mods/BulbulModBackends.cs:84-236 (MaxLines 500 :125, input clears on submit :231-235). -->

## MainThread

```csharp
// From a background task: hand the result to the main thread.
Task.Run(async () =>
{
    string reply = await Task.FromResult("an answer from somewhere slow");
    helper.MainThread.Post("show reply", () => window.AppendLine(reply));
});

// Ask the main thread for something and wait for it, with a timeout.
try
{
    GameDataSnapshot? snapshot = await helper.MainThread.InvokeAsync(
        "read todos", () => helper.GameData.Snapshot(), TimeSpan.FromSeconds(5));
}
catch (TimeoutException ex)
{
    helper.Log.Warn("the game did not answer in time: " + ex.Message);
}
```

| Member | Does |
|---|---|
| `IsMainThread` | True on Unity's main thread |
| `Now` | Monotonic seconds; safe on any thread |
| `Post(label, work)` | Runs `work` on a coming frame, always queued. False when your queue is full |
| `RunOrPost(label, work)` | Runs it now when already on the main thread, otherwise queues it |
| `PostAfter(seconds, label, work)` | Runs it on the main thread after a real-time delay; returns a `DelayedWork` (dispose or `Cancel()` to cancel; `IsAccepted`/`RejectReason` say whether it was queued) |
| `InvokeAsync(label, work, timeout?, token?)` | Runs it on the main thread; the task ends with its result, its exception, or a `TimeoutException` |
| `Invoke(label, work, timeout)` | The same, blocking; runs inline when called on the main thread |

The label names the work in failure lines. Work you queued is dropped when your mod stops, and
posting afterwards returns false (or a faulted task).

<!-- ICwyModFacets.cs:354-405; ModFacets.Framework.cs:407-467. -->

## ModRegistry

```csharp
bool greeterInstalled = helper.ModRegistry.IsLoaded("yourname.greeter");
ICwyModInfo? info = helper.ModRegistry.Get("cwy.sample.hello");   // Id, Manifest, DirectoryPath, IsCodeMod, State, Detail
IReadOnlyList<ICwyModInfo> all = helper.ModRegistry.GetAll();       // every resolved pack and code mod, in load order
```

`IsLoaded` is true for a content pack that resolved and for a code mod that loaded. `GetApi<T>` is
how mods share an API: [lifecycle.md](lifecycle.md#talking-to-other-mods-getapi).

<!-- ICwyModHelper.cs:109-157; ModFacets.Framework.cs:469-510. -->

---

## Threading rules

Unity and the game are **main-thread only**. Release builds do not check; a call from another
thread fails at random, or crashes the game.

1. **Everything the framework calls in your mod runs on the main thread:** `Entry`, `GetApi`,
   `OnShutdown`, event handlers, `EveryFrame`, mods-ready handlers, console commands, hotkeys,
   window events, `LoadAudioAsync` callbacks and work you post. In those you may touch anything.
2. **Your own threads and tasks may not touch Unity or the game.** From there, use
   `helper.MainThread.Post` (fire and forget) or `helper.MainThread.InvokeAsync` (get a result).
   Safe from any thread without a hop: `Log`, `Data`, `Satone` (queues itself), `MainThread`,
   `Content.ReadText`/`ReadJson`/`Exists`/`GetPath`/`LoadAudioAsync`.
3. **Main thread only:** `GameData.Snapshot()`, `Content.LoadTexture`, `Content.LoadWav`, `Ui` and
   every window member.
4. **Never block the main thread.** No `Thread.Sleep`, no `.Result` or `.Wait()` there: a task that
   needs the main thread cannot run while you wait for it. Use `PostAfter` for delays and `await`.
5. **Timeouts are yours, and wall-clock.** `InvokeAsync` waits 2 s unless you pass another timeout
   (`Timeout.InfiniteTimeSpan` waits forever). Work that has not started by then is skipped, never
   run late; work already running finishes and its result is discarded. One dispatch costs about
   one frame: 16 ms at 60 fps, about 42 ms at the 24 fps the game runs at while unfocused. A
   sleeping Mac stops the game loop entirely, so expect timeouts right after a wake.
6. **Frame budget.** Mods share `[MainThread] FrameBudgetMs` per frame (default 4 ms, at least one
   item), taking turns mod by mod; the rest waits, in order, for the next frame. The framework's own
   work runs first and is not budgeted, so a busy mod cannot delay the game or Satone's restores.
   `EveryFrame` tickers are not counted against it, so keep them cheap.
7. **Queue limits.** Each mod may have 256 items waiting and 64 delayed; beyond that the framework
   refuses the work: `Post`/`RunOrPost` return false, `Invoke`/`InvokeAsync` throw
   `MainThreadRejectedException` (reason `QueueFull`,
   `OwnerNotRunning` or `ShuttingDown`) and `PostAfter` returns a `DelayedWork` with
   `IsAccepted == false`.

<!-- Dispatcher: src/CwyLs.ModLoader/Mods/README.md "The main-thread dispatcher"; default timeout: src/CwyLs.ModLoader/Threading/MainThread.cs:49-54; budget :43-47, :86; caps: src/CwyLs.ModLoader.Core/Threading/MainThreadQueue.cs:143,:149; frame timings measured in game (p50 16.1/41.2 ms at 60/24 fps; a sleeping machine stalls the loop). -->

---

## Coming in 1.0

These facets are planned before the API freezes at 1.0. Their members are not settled yet, so none
are shown here. Until a facet works, do not work around it with your own game patches if you can
wait.

| Facet | What it will do |
|---|---|
| `EditData` | Add, replace, delete or reorder rows of any master-data table from pack JSON (modelled on Content Patcher's `EditData` for Stardew Valley) |
| `Audio` | Replace or add audio of every kind: music, ambience, sound effects |
| `UiSkin` | Reskin the game's UI sprites |
| `Conditions` | Pack conditions (time of day, weekday, date, pomodoro state, language, scenery, story progress, player level) and tokens a mod adds |
| `Actions` | Semantic game actions (music, pomodoro, panels, settings, time and weather, her activity), in read / act / write tiers; writes confirmed in the game |
| `Scenes` | Custom cutscenes: script commands of your own and triggers for when scenes play |
| `Camera` | The story cameras, where the PC build has them |
| `VoiceStream` | Streamed voice through Satone, chunk by chunk, with subtitles |
| `Mic` | Push-to-talk microphone input; off by default, and on macOS through an external helper |
| `Api` | The local game API for outside tools; loopback only, off by default |
| `Harmony` | A Harmony instance with your mod's id, unpatched automatically when your mod fails or the game quits |
| `Ui` (extended) | The window toolkit: lists, buttons, tabs, chat bubbles, multi-line input with IME |

<!-- Facet slots: src/CwyLs.ModLoader/Mods/ICwyModHelper.cs (Harmony, EditData, Audio, Grading, UiSkin, Conditions, Actions, Scenes, Camera, VoiceStream, Mic, Api) and src/CwyLs.ModLoader/Mods/Facets/README.md. -->
