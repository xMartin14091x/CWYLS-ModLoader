# CWYLS Game Data — the Game Data Bus

`cwyls.modloader.bulbul.data` · depends on `cwyls.modloader` and
`cwyls.modloader.bulbul`

One typed door onto everything the player owns in *Chill With You: Lo-Fi Story* — todos, notes,
habits, pomodoro, calendar, profile, settings, music, environment, story, achievements — plus change
notifications, a JSON file for tools outside the game, an extension point for your own sections, and
a small guarded write API.

Everything the game keeps about the player hangs off one class, `Bulbul.SaveDataManager`. The bus
finds that instance, copies it into immutable views, and tells you when it changes.

---

## Read

```csharp
using CwyLs.ModLoader.Bulbul.Data;

if (!GameData.IsReady) return;            // save data not loaded yet

foreach (var list in GameData.Todos)
{
    Log.LogInfo($"{list.Title}: {list.OpenCount} open / {list.Items.Count}");
    foreach (var todo in list.Items)
        Log.LogInfo($"  [{(todo.State == TodoViewState.Complete ? "x" : " ")}] {todo.Text}");
}

var page  = GameData.Notes.FirstOrDefault();      // page.Text may be null — see Privacy
var level = GameData.Player?.Level.CurrentLevel;
var work  = GameData.Player?.PomodoroTotalWorkSeconds;
```

Accessors: `Player` `Profile` `Account` `Pomodoro` `Todos` `Notes` `Habits` `Calendar` `Settings`
`Music` `Environment` `Story` `Achievements`, and `IsReady` / `Options` / `Write` / `Snapshot()` /
`RegisterProvider`.

**Rules of the road**

- **Main thread only.** Every accessor reads live game objects.
- **Copies, never references.** You get freshly built immutable views. The bus never hands out a
  live game object, because writing to one would change the player's data without the game saving it.
- **They rebuild on every call.** Cheap for todos and settings; `Notes`, `Habits` and `Calendar` can
  page files in from disk. Read once per frame at most, and subscribe rather than poll.
- **`IsReady` matters.** Before it is true the accessors describe the game's default-constructed
  state, not the player's.

## Subscribe

```csharp
GameData.Changed += change =>
{
    if (change.Section == GameDataSection.Todos)
        RefreshMyPanel();            // change.Reason is e.g. "save:SaveTodoList"
};

// or, if you already compose R3:
GameData.ChangedObservable
    .Where(c => c.Section == GameDataSection.Pomodoro)
    .Subscribe(c => Log.LogInfo(c.ToString()));
```

Handlers run **synchronously inside the game's save call**, so keep them short. A handler that
throws is logged; the others still run.

Two sources feed the stream, and they overlap on purpose:

- **Saves** — every public `Save*` and `Delete*` method of `SaveDataManager` — 46 of them in game
  v1.17.3; the startup log prints the exact count. `Reason` is `save:<MethodName>`. Methods ending in
  `Throttled` are not hooked: they only reserve a save that arrives moments later as the real
  method. The bus does not patch these itself: it listens to the game adapter's shared
  save hook (`CwyLs.ModLoader.Bulbul/Events/SaveDataHook.cs`), whose one postfix per method also feeds
  the event bus's `SaveSection`. The method-to-section table is `Core/Data/SaveMethodRules`.
- **Game events** — R3 observables the game already exposes. `Reason` is `event:<Name>`.
  `PomodoroService` (`OnStartPomodoro/OnStartWork/OnEndWork/OnStartBreak/OnEndBreak/OnCompletePomodoro`)
  and `MusicService` (`OnChangeMusic/OnPlayMusic`) arrive through the event bus: its `EventBridge`
  is their only subscriber and hands each firing on as a `GameSignal`; `Core/Data/GameDataSignals`
  picks the ones above. `NoteService` (`OnAddPage/OnRemovePage/OnEndEditNote`) and `FacilityTodo`
  (`OnAddTodo/OnRemoveTodo/OnCompleteTodo/OnUncompleteTodo`) are subscribed here directly: nothing
  else in the framework uses them.

Every game member the bus touches outside the save family - the load seams, the methods the write
API calls, the note and to-do observables - is in `DataPatchTargets` and is checked at startup,
so a game update that breaks one prints `PATCH TARGET MISSING` instead of failing a write later.

`SaveAll` therefore produces about 28 notifications in one burst. That is intentional — the file
writer debounces, and a mod that wants coarse-grained updates can filter on `GameDataSection.All`.

## Snapshot

`GameData.Snapshot()` returns a `GameDataSnapshot` (Unity-free, in `CwyLs.ModLoader.Core.Data`).
`SnapshotSerializer.ToJson(snapshot)` turns it into JSON.

The plugin does this for you: **500 ms after the last change** it writes
`BepInEx/data/snapshot.json` atomically (temp file, then rename) and logs

```
DATA snapshot written (7 todos, 3 note pages, 2 habits)
```

One more snapshot is written on `Application.quitting`, right after the game's own `SaveAll` — the
most complete one of the session.

Top-level keys (camelCase, schema version **1**):

```
schemaVersion  time  gameVersion  pluginVersion  isReady
player  profile  account  pomodoro  todos  notes  habits
calendar  settings  music  environment  story  achievements  extra
```

Reading it from outside:

- **Unknown keys must be ignored.** Adding a field is not a version bump; removing one or changing
  its meaning is.
- **A section the game has not loaded is `null`.** List sections are never null, only empty.
- **64-bit ids are strings.** The game mints ids from `DateTime.Now.Ticks` (~6.4 × 10¹⁷), well past
  the 2⁵³ a JSON float holds exactly, so `id` fields are quoted. Parse them as strings.
- **Retry a missing file.** The rename is atomic on every platform that matters, but a reader can
  still catch the moment between delete and rename.

## Register a provider

Publish your own section onto the same bus; it lands in `extra` on every snapshot.

```csharp
sealed class MoodProvider : IGameDataProvider
{
    public string SectionName => "mymod.mood";          // namespace it like a mod id
    public object? Capture() => new { mood = _mood, since = _since };
}

GameData.RegisterProvider(new MoodProvider());
```

`Capture()` runs on the main thread while a snapshot is built, so return plain data you already
hold — primitives, strings, enums, lists, dictionaries, simple objects. Not live game objects: the
serializer walks reflectively and gives up at depth 12, replacing reference cycles with
`"<cycle>"`. A provider that throws is logged and skipped for that snapshot; nobody else's section
is lost. Registering a `SectionName` twice replaces the earlier provider.

## Write (gated)

```csharp
// BepInEx/config/...bulbul.data.cfg → [Data] AllowWrites = true
var r = GameData.Write.AddTodo(listId: 0, text: "write the report");
if (r.Ok) Log.LogInfo($"added {r.Id}, ui refreshed: {r.UiRefreshed}");

GameData.Write.CompleteTodo(todoId);
GameData.Write.AddNotePage("from Satone", "you said you'd start at 14:00.");
```

- **Off by default.** Every method refuses unless `Data.AllowWrites` is true. The player has no undo.
- **Every accepted write is logged at Info**, with what it changed.
- **Through the game's own methods.** `TodoListData.AddTodo`, `TodoData.SetCompleteTodoDatetime`,
  `NoteService.AddPage` — then the matching `SaveDataManager.SaveXxx`. The save file ends up exactly
  as the game would have written it, and the bus's own postfix publishes the change.
- `AddTodo` refuses blank text: the game will not let the player complete a blank todo.
- `CompleteTodo` also mirrors the completion onto today's calendar day, as the game does, so the
  statistics screen and the todo panel agree.
- `listId: 0` means "the first list", which is the one the panel opens on.

**UI refresh.** `TodoListUI` is a pure view: it builds one row per item when its presenter
`Bulbul.FacilityTodo` walks the data, and never re-reads — not even when the panel is reopened.
Writing to the data alone leaves an open panel stale. So todo writes nudge the game's own
presenter (`FacilityTodo.OnClickButtonSelectTodoList`), which is exactly what a click on the list
button does: tear the rows down, rebuild from data. `WriteResult.UiRefreshed` says whether that
happened; `false` means the room UI was not up, and the data is still correct. Notes need no nudge —
`SelectPageListUI` listens to `NoteService.OnAddPage`, which the service raises itself.

> Note: the game's `NoteService.AddPage(title, body)` sets the title on the in-memory index but
> never saves it. The bus calls `SaveNoteList()` afterwards, or the title would vanish on restart.

## Diagnostic stream

Everything the bus does is also published to `CwyLs.ModLoader.Core.Bus.TraceBus` under `sys = "data"`,
which the Trace plugin folds into its JSONL stream. Four events, with exactly these payload keys:

| `name` | Payload | When |
|---|---|---|
| `ready` | `via` `patched` | Once, the first frame the bus is ready (`via` is `harmony` / `singleton` / `container`; `patched` is the save/delete method count) |
| `changed` | `section` `reason` `time` | Every `GameDataChanged`, emitted before subscribers run so a throwing handler cannot cost the trace the record |
| `snapshot` | `todos` `notePages` `habits` `bytes` `path` | Every snapshot written, including the one on quit |
| `write` | `op` `ok` `uiRefreshed` `detail` | Every write-API call — accepted, refused or failed |

`TraceBus` buffers up to 2000 events before the Trace plugin attaches, so the startup burst is not
lost, and it never throws back into the bus.

## Config — `[Data]`

| Key | Default | What it does |
|---|---|---|
| `WriteSnapshot` | `true` | Write `BepInEx/data/snapshot.json` on change. Off leaves the in-process API fully working. |
| `DebounceMs` | `500` | Quiet period after the last change before writing. |
| `IncludeNoteText` | `false` | Publish note bodies and diary entries. **See Privacy.** |
| `AllowWrites` | `false` | Let mods modify the player's save data. |
| `RecentDays` | `14` | Days of habit and calendar history to publish. |

## Privacy

The snapshot is a plain file on disk that any process can read, and a mod may forward it to a
remote service. Two rules are enforced in `CwyLs.ModLoader.Core.Data.GameDataPrivacy`, as pure
functions with unit tests, so they cannot regress quietly:

- **The device id is never published.** `AccountData.DeviceID` is a stable per-install identifier.
  The snapshot carries only `account.hasDeviceId`. Not hashed, not truncated — a stable pseudonym
  is still a correlatable identifier.
- **Imported music paths are reduced to file names.** The directory part leaks the OS account name
  and the player's folder layout.

Also excluded: the custom profile icon image (`profile.hasCustomIcon` only — the snapshot is a text
file, not an image carrier).

`IncludeNoteText` is **off by default** (since the 0.9 public release): it is the most private thing
in the save file. A player who wants an assistant mod to help with their notes turns it on. Off,
note titles, ids and ordering still publish; only bodies and diary
entries are withheld — as `null`, which is deliberately distinct from `""` ("the player wrote
nothing"). `NotePageView.TextAvailable` tells the two apart, and also covers the third case: the
game keeps only three note pages in memory at a time, so a body that was never loaded reads as
unavailable rather than empty.

## How it attaches

`SaveDataManager` is a plain lazy static singleton, not a VContainer registration. The bus tries
three paths and logs which one won (`DATA SaveDataManager obtained via …`):

1. `harmony` — captured from a postfix on `SaveDataManager.Load()`.
2. `singleton` — `SaveDataManager.Instance`, guarded by `HasInstance` so we never *construct* an
   empty save file and hand it out as the player's.
3. `container` — `ProjectLifetimeScope.TryResolve`, kept in case a future version moves it into DI.

Readiness is a separate signal: a postfix on `SaveDataManager.LoadAllPlatformSetting()`, which is
called from exactly one place — the middle of `Load()`'s synchronous block, after player, pomodoro,
todo, habit, note and setting data are in memory. (`LoadSetting()` would be wrong: `EntryBehavior`
calls it long before any save data is read.) The RoomScene load is the fallback.

Everything with a lifecycle — the debounced writer, the retrying event bridge — lives on a hidden
`DontDestroyOnLoad` object, not on the plugin component: in this game that component receives
`OnDestroy` about 0.2 s after startup. The Harmony patches are static and survive it. The bus is
wound down only by `Application.quitting`.

## Smoke checks

Registered into the framework's shared registry and run after RoomScene settles (see
`BepInEx/smoke-report.json`):

| Id | Passes when |
|---|---|
| `p1.data.ready` | Save data observed as loaded; reports how the instance was found and how many methods were patched |
| `p1.data.snapshot_written` | `snapshot.json` exists and is non-empty (skip-passes if `WriteSnapshot` is off) |
| `p1.data.todos_nonempty` | At least one todo list exists; passes with a count even if the player has no items |
| `p1.data.changed_fires` | At least one change notification arrived this session |
