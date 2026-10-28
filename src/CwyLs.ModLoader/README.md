# `CwyLs.ModLoader` — reading the log, keys and smoke runs

Three things in this assembly the user touches directly: the console log, the keys, and how a
smoke run is asked for.


## Reading the log

Every line CWY writes has one shape. Two spaces separate the three columns; single spaces are
free inside the primary and inside a quoted value; a missing value is `-`; there is never more than
one physical line per event.

```
HH:mm:ss.fff  TAG  <primary>  k=v k="quoted value"
```

The stamp is the **event's** time, not the time the line reached the sink, and the lines are
printed on the thread that published them — so the order of the console is the order of the game.

Where it goes:

| Sink | What it holds |
|---|---|
| `BepInEx/cwy-console.log` | every CWY line, in this grammar. Appended to; the previous session is rolled to `cwy-console.<yyyyMMdd-HHmmss>.log` (last 5 kept) |
| in-game console (`Ctrl+Shift+C`) | the same lines without the stamp |
| `BepInEx/trace/trace-*.jsonl` | **everything**, including the ticks, phases and observations the console does not show. Past sessions are compressed to `.jsonl.gz` about 30 s after launch (read with `gzip -dc`); oldest whole sessions are dropped only above `[Trace] RetainMaxTotalMB` (1024), keeping the newest `RetainMinSessions` (2) |
| `BepInEx/LogOutput.log` | BepInEx's own copy, with its `[Info:CWYLS Mod Loader]` prefix |

Watch it live: `tools/mac/watch-log.py` (colour by tag, `--quiet` hides the grey ones,
`--strict` fails on a line that does not match the grammar).

### The tags

One upper-case word per event family. The console answers *what happened in the game*; the trace
file answers *what our code did about it*.

| TAG | Fires when | Example |
|---|---|---|
| `SPEECH` | Satone speaks a line | `14:02:11.019  SPEECH  "I made you some tea." 「お茶を淹れたよ。」  src=Tutorial dur=2.3s voice=Tutorial_1_001 id=Tutorial_1_001 attr=row` |
| `SAY` | a mod's own line starts (or fails) | `14:02:11.019  SAY  #7 "Hello from the Mod Loader."  via=scenario voice=Voice_CWY_Test facial=smile motion=wild001_good` |
| `STATE` | her action state changes | `14:02:11.019  STATE  WantTalk -> BreakMovie` |
| `FACE` | an expression is requested | `14:02:11.019  FACE  smile  id=10` |
| `MOTION` | a motion is requested | `14:02:11.019  MOTION  wild001_good  id=61` |
| `LOOK` | a gaze change is requested | `14:02:11.019  LOOK  1  id=-1` |
| `RESTORE` | face/motion/gaze are put back | `14:02:11.019  RESTORE  face in 0.77s, motion re-dispatched (WorkTyping), gaze reset  scope=face+motion+gaze state=WorkTyping gaze=true detail=-` |
| `SUBTITLE` | a line in a pack-declared language reaches the game's own text box (the only place the framework shows text) | `14:02:11.019  SUBTITLE  "Time for a short break?"  lang=th via=textbox id=my_line_001 min=2.5` |
| `MUSIC` | a track starts | `14:02:11.019  MUSIC  "Sleeping Bloom"  index=0 shuffle=true` |
| `POMODORO` | a pomodoro transition | `14:02:11.019  POMODORO  WorkStarted  type=Work` |
| `SCENARIO` | a scenario starts or ends | `14:02:11.019  SCENARIO  start game_start_02_001  type=GameStart_LessTowDays mode=Auto` |
| `SCENE` | a Unity scene loads or unloads | `14:02:11.019  SCENE  RoomScene loaded` |
| `WINDOW` | a window view is switched | `14:02:11.019  WINDOW  Sakura on` |
| `READY` | the room is up and services resolve | `14:02:11.019  READY  room ready` |
| `BUTTON` | a game UI button is pressed; controls under a `CWY.*` root (the console, mod windows) are not traced | `14:02:11.019  BUTTON  "Start"  path=Canvas/Home/StartButton control=Button event=press` |
| `TYPED` | text is submitted into a game field; fields under a `CWY.*` root are not traced | `14:02:11.019  TYPED  "buy milk"  path=Canvas/Note/Input control=TMP_InputField event=submit` |
| `CLICK` | the player clicks, except on the framework's own UI (topmost UI hit under a `CWY.*` root) | `14:02:11.019  CLICK  (612, 380)` |
| `HEALTH` | a spike, low fps, low memory, focus or pause | `14:02:11.019  HEALTH  spike 303.4ms  frame=2 cause=-` |
| `UNITY` | a Unity error, exception or failed assertion | `14:02:11.019  UNITY  Exception "NullReferenceException: …"  count=1` |
| `DATA` | player data changed, snapshotted, or the bus became ready | `14:02:11.019  DATA  snapshot written  todos=12 notes=3 habits=2 bytes=48213` |
| `VOICE` | a clip is registered, overridden, refused or played | `14:02:11.019  VOICE  + Voice_CWY_Test  via=wav source=cwy.example` |
| `TEXTURE` | an apply pass, a reload or a restore | `14:02:11.019  TEXTURE  1 rule applied  failed=0 trigger=RoomScene+2s mode=Shared files=1` |
| `PACKS` | the pack scan result (the summary's own `PACKS:` prefix is dropped on the console), then one warning-level line per loaded pack made for another game version (`my.pack: made for game 1.17.2, running 1.17.3 (newer patch version); loaded anyway`) | `14:02:11.019  PACKS  1 loaded (cwy.example@0.1.0); 0 skipped  loaded=1 skipped=0` |
| `MASTERDATA` | pack rows merged into the game's loader | `14:02:11.019  MASTERDATA  localization +1/~0, novel +2/~0, groups +1/~0  trigger=startup packs=1 rows=1357 rejected=-` |
| `KEYS` | the hotkey table, and each press | `14:02:11.019  KEYS  smoke.run = Ctrl+Shift+S \| F9, console.toggle = Ctrl+Shift+C \| F10  count=2` |
| `SMOKE` | one line per run, one per real failure | `14:02:11.019  SMOKE  ok=true  passed=46/49 mode=quiet trigger=key skipped=3 session=5ce36f09 pass=-` |
| `CONSOLE` | the overlay is ready | `14:02:11.019  CONSOLE  15 command(s)  toggle="Ctrl+Shift+C \| F10" commands=15` |
| `SETUP` | one startup summary per feature | `14:02:11.019  SETUP  text box only  src=subtitles enabled=true hold="max(2.5s, chars/15)" seams=ok sources="packs (data/subtitles)"` |
| `TRACE` | the trace plugin's own status, and each time its clock re-anchors to the system clock (after a sleep, a clock change or a time-zone change; `reason=` is `poll`, `focus` or `resume`) | `14:02:11.019  TRACE  patched 2626 methods across 433 classes in 4487 ms; 0 failed` · `14:26:11.019  TRACE  clock re-anchored +24m00.0s: the trace clock had fallen behind the system clock (the machine slept, or the clock was set forward)  reason=resume` |
| `WARN` | any direct `LogWarning` | `14:02:11.019  WARN  SATONE face smile timed out after 2.0s  src=game-adapter` |
| `ERROR` | any direct `LogError` | `14:02:11.019  ERROR  PATCH TARGET MISSING Bulbul.HeroineService.PlayVoice  src=mod-loader` |

File-only, by design: ticks (`GameClockTick`, `PomodoroTick`), `phase=` diagnostics, the
`observed` half of every Satone call, polling notices, per-keystroke input, saves (`event/SaveSection`), `health/interval`,
Unity warnings, per-rule texture lines and the `TEXDIAG` blocks. One spoken sentence therefore
costs at most **3** console lines (`SPEECH`, `SUBTITLE` while a pack language is chosen, and `SAY`
when a mod asked for it) instead of 23.

### Publishing a line from a mod

One publish, on the bus, naming its own tag, primary column and field order. There is no second
"also log it" call.

```csharp
using CwyLs.ModLoader.Core.Logging;

ConsoleEvents.Emit(
    "mymod", "load", ConsoleTags.Mod,         // sys, name, tag
    "\"" + title + "\"",                      // the primary column
    "src,dur:durationSec",                    // the console fields, in order ("column:payloadKey")
    ("src", "disk"),
    ("durationSec", 2.4d),
    ("path", path));                          // not listed above -> trace file only

ConsoleEvents.Setup("mymod", "ready", ("folder", "notes"));   // one startup line per feature
ConsoleEvents.File("mymod", "load", ("phase", "started"));    // diagnostics: file only
```

`ConsoleEvents.EmitAt(..., ConsoleLevel.Warn, ...)` puts the line on the warning channel while
keeping its tag. A real `LogWarning`/`LogError` is still fine — the sink wraps it as `WARN`/`ERROR`
with `src=` naming the plugin.

## Keys

Every framework function is bound to **two** chords. On macOS an F-key is a media key unless `Fn`
is held, so the F-key alone never reaches the game; the `Ctrl+Shift+<letter>` chord always does.
The **chord is therefore the primary** on macOS and Windows alike — the .cfg files read the same on
both and the advertised key is the one that always works. Both chords stay live; use whichever is
comfortable.

| Id | Primary | Alternate | Owner .cfg | What it does |
|---|---|---|---|---|
| `smoke.run` | `Ctrl+Shift+S` | `F9` | `cwyls.modloader` | Runs the smoke checks **quietly** and rewrites `BepInEx/smoke-report.json` |
| `console.toggle` | `Ctrl+Shift+C` | `F10` | `cwyls.modloader` | Opens / closes the in-game console overlay |
| `textures.reload` | `Ctrl+Shift+R` | `F8` | `cwyls.modloader.bulbul` | Re-reads every texture in the installed packs and applies them again |

`textures.reload` is registered by the adapter (`CwyLs.ModLoader.Bulbul/Textures/TextureReload.cs`),
chord-first like the others; an old .cfg that still has `F8` as primary
is migrated once (`HotkeyDefaults`).

On the first frame the registry writes one line naming everything that is bound, always in the
human form (`HotkeyChord.Format()`), never BepInEx's serialised `S + LeftControl + LeftShift`:

```
14:02:11.019  KEYS  smoke.run = Ctrl+Shift+S | F9, console.toggle = Ctrl+Shift+C | F10  count=2
```

The console's own startup line prints the same form, and `keys` in the overlay lists every binding
with its description.

Both chords are config entries, in the `.cfg` of the plugin that registered the function:

```ini
[Keys]
smoke.run.Primary = S + LeftControl + LeftShift
smoke.run.Alt = F9
```

A key never fires while a text field has focus, so typing a note, a todo or a profile name can
never trigger a function (the "ignored" notice is at Debug level, so it stays out of the console).

### The one-time migration

A `.cfg` written by an older build holds the old pair (`Primary = F9`, `Alt = S + LeftControl + LeftShift`).
When an entry is **exactly** that pair, the registry rewrites it once and says so:

```
KEYS: migrated smoke.run to chord-first defaults
```

If either chord differs from the old default — i.e. you chose it — both are left alone. The rule
is pure and tested: `CwyLs.ModLoader.Core.Input.HotkeyDefaults.ShouldMigrate`.

### Registering one

```csharp
using CwyLs.ModLoader.Input;

Hotkeys.Register(
    "mymod.panel",
    "Opens the chat panel.",
    new KeyboardShortcut(KeyCode.P, KeyCode.LeftControl, KeyCode.LeftShift),  // primary: the chord
    new KeyboardShortcut(KeyCode.F7),                                         // alternate: the F-key
    () => Panel.Toggle(),
    Config);
```

Two optional trailing arguments declare a migration, when your own defaults change:

```csharp
Hotkeys.Register(id, description, primary, alternate, action, Config,
    legacyPrimaryDefault: new KeyboardShortcut(KeyCode.F7),
    legacyAlternateDefault: new KeyboardShortcut(KeyCode.P, KeyCode.LeftControl, KeyCode.LeftShift));
```

`Hotkeys.List()` returns every binding with both chords, `Hotkeys.Find(id)` one of them, and
`Hotkeys.Chord(shortcut)` turns a BepInEx shortcut into the printable form.

## Smoke runs

A **loud** check plays the test clip, repaints the character and moves her face, motion and look;
a **quiet** run skips all of them. The everyday triggers are quiet, so pressing the key
during play changes nothing you can see or hear.

| Trigger | Mode | Loud checks |
|---|---|---|
| `smoke.run` key (`Ctrl+Shift+S` / `F9`) | `Quiet` | skipped |
| `touch "<game root>/BepInEx/smoke.run"` | `Quiet` | skipped |
| `smoke run` or `smoke run quiet` in the console | `Quiet` | skipped |
| `smoke run loud` in the console | `Loud` | run, then restored |
| `touch "<game root>/BepInEx/smoke.loud"` | `Loud` | run, then restored |
| `RoomScene` load, only with `AutoRunOnRoomScene = true` | `Automatic` | skipped |

Both trigger files are polled twice a second and deleted before the run starts, so a script
can ask for a run while the game does not have keyboard focus. A skipped loud check is reported
`ok` with the detail ``skipped (quiet run; use `smoke run loud`)`` — it has not failed, nobody asked
it to run.

The run writes **one** line, and it names the mode:

```
14:02:11.019  SMOKE  ok=true  passed=46/49 mode=quiet trigger=key skipped=3 session=5ce36f09 pass=-
```

### Restoring what a loud check touched

A loud check that changes visible state hands in an undo, and the runner runs it in a `finally`
after the body — so it runs even when the check throws:

```csharp
SmokeRegistry.Register(
    new SmokeCheck("p2.satone.face", "P2", Face),
    loud: true,
    restore: () => Satone.Face(before));      // or new SmokeCheck(id, phase, run, loud, restore)
```

An undo that itself throws is a warning and never aborts the run:

```
14:02:11.019  WARN  SMOKE restore failed p2.satone.face: NullReferenceException: …  src=mod-loader
```

Checks that should adopt `Restore` (all in `CwyLs.ModLoader.Bulbul`):

| Check | What it changes | Undo |
|---|---|---|
| `p2.satone.face` | facial index | write the index it read back |
| `p2.satone.motion` | motion index | write the index it read back |
| `p2.satone.look` | look target + scale | restore the target and scale it read |
| `p2.say.scenario` / `p2.say.fallback` | queues a line | cancel it if it has not started; the Say restore runs at `ScenarioEnded` |
| `p1.textures.restore` / `p1.textures.reload` | the character's textures | already re-applies; declare it so the guarantee is explicit |
| `p1.voice.play` | plays a clip | none needed — it is momentary |

### Settings

```ini
[Smoke]
AutoRunOnRoomScene = false   ; run automatically a few seconds after RoomScene loads
SecondPassSeconds = 0        ; repeat an automatic run this many seconds after the scene load; 0 = off
HotkeyEnabled = true         ; bind the smoke.run key at all (takes effect at the next launch)
```

`AutoRunOnRoomScene = true` restores the old hands-off behaviour, minus the noise: an automatic run
**skips** the loud checks and reports them as ``skipped (automatic run; use `smoke run loud` to
include)``, which is ok, not a failure. Set `SecondPassSeconds = 60` as well if you want the
"observed this session" checks to pass without touching anything — that is the value that made a
plain launch end green. `HotkeyEnabled = false` turns the key off entirely; the trigger files and
the console command still work.

### Reading the run

Two kinds of red in the log, and they are not the same thing:

| Line | Level | Means |
|---|---|---|
| `SMOKE FAIL <id>: …` | Error | the seam is broken — act on it |
| `SMOKE not yet <id>: …` | Warning | the game has not done this yet this session (`SmokeCheckResult.NotYetObserved`) |
| `SMOKE restore failed <id>: …` | Warning | the check ran but could not put the game back |

The first two are `ok:false` in `smoke-report.json`; the second carries `"informational":true`, so a
viewer can colour it the way the log does. Use `NotYetObserved` for every "not yet observed" /
"not exercised" verdict and keep `Fail` for a seam that is actually broken.
