# In-game console

A console that shows what the framework is doing and lets you drive its APIs without restarting
the game. It is drawn as one of the game's own windows whenever the game's
UI assets can be found, and as the plain IMGUI overlay otherwise. Framework half lives here; the
pure logic (parser, registries, history, completion, scrollback, colours, window maths, key
routing, fallback and Hide-UI rules) is in `src/CwyLs.ModLoader.Core/Console/` and is unit-tested; the
game-specific commands, tabs and the game-style surface are in `src/CwyLs.ModLoader.Bulbul/Console/`,
and the reusable game-style window is in `src/CwyLs.ModLoader.Bulbul/UI/`.

## Keys

| Key | What |
|---|---|
| `Ctrl+Shift+C` | Open / close: the primary chord of the `console.toggle` hotkey. |
| `F10` | Same thing, the alternate. On a Mac an F-key is a media key unless Fn is held, so use the chord there. |
| `Esc` | Close. Only the console: in fullscreen the game stays fullscreen (see *Escape and the game's fullscreen switch*). |
| `Enter` | Run the line. |
| `Tab` | Complete the word at the end of the line (command names, then the literal words of the command's usage). |
| `Up` / `Down` | Walk the history. The half-typed line comes back when you come down past the newest entry. |
| `PageUp` / `PageDown` | Scroll the output. |
| mouse wheel, scrollbar | Scroll the output (game-style window). |
| drag the title bar / bottom-right grip | Move / resize the game-style window. It always stays on screen. |

Both chords are config entries: `[Keys] console.toggle.Primary` and `console.toggle.Alt` in
`cwyls.modloader.cfg`. Rebind them like any other CWY key (`src/CwyLs.ModLoader/Input/`).

## Settings

`[Console]` in `cwyls.modloader.cfg`:

| Key | Default | What |
|---|---|---|
| `Enabled` | `true` | Off registers the commands but draws nothing and binds no key. |
| `Opacity` | `0.85` | Background opacity, 0.2 to 1. |
| `FontScale` | `1.0` | Text size multiplier on top of the size picked from the window height (0.5 to 3). |
| `Style` | `Game` | `Game`: the game's own window style whenever its UI assets can be found, IMGUI otherwise. `Imgui`: always the plain overlay. |
| `FollowHideUI` | `true` | An open game-style console fades out with the game's Hide UI and back in with it. The toggle key shows it anyway. |

`Opacity` on the game-style window stops at the game's own 90 % black.

## Tabs

- **Console** - the scrollback (last 300 lines) and the command line. Every CWY log line above
  Debug is mirrored here, as are Errors and Fatals from any source - the same rule
  (`Core/Logging/ConsoleLogFilter`) as `BepInEx/cwy-console.log`, so the overlay and the file keep
  the same lines.
- **Packs** - what loaded, what was refused and why, plus a **rescan** button.
- **Smoke** - the last `BepInEx/smoke-report.json`, check by check, plus a **run** button
  (a quiet run (loud checks skipped); use `smoke run loud` to include them).
- **Targets** - the patch targets the framework depends on; **verify** resolves them all again.
- **Keys** - every bound hotkey and both of its chords.
- Anything a mod registered through `ConsolePanels` (the game adapter adds **Stream** and **State**).

The built-in tabs are snapshots, refreshed when you open them or press **refresh**. A registered
panel renders live, once per frame while its tab is open - keep its renderer cheap.

## Commands

`help` lists them all; `help <command>` explains one. Framework commands (group `framework`):

| Command | What |
|---|---|
| `help [command]` | The list, or one entry in full. |
| `commands` | Every command name on one line. |
| `version` | Framework, Unity and game versions; loaded plugins, packs, commands, panels, keys. |
| `paths` | Where packs, traces, data, the smoke report and the trigger file live. |
| `stats` | Frame rate, screen, scene, scrollback and diagnostic-bus counters. |
| `packs list\|rescan` | The pack table, or scan `<game root>/CWYLS Mods` again. |
| `smoke run\|report\|list` | Run every check now, show the last report, or list what is registered. |
| `targets [verify]` | The patch-target table, optionally re-resolved against the running game. |
| `keys` | Every bound hotkey. |
| `panels [name]` | List the extra tabs, or print one into the output. |
| `trace on\|off\|full\|status` | The diagnostic trace, off by default. `on` = key events (small files), `full` = every game method (developers). The trace patches at startup, so `on`/`full` start at the next launch; `off` also stops a running trace at once. `trace level off\|events\|trace` still works. |
| `bus emit <sys> <name> [k=v ...]` / `bus stats` | Publish on `TraceBus`, or show its counters. `k=v` values are parsed as bool, integer, number or string. |
| `log <text>` | Write a line to the BepInEx log; the mirror brings it straight back. |
| `dump [path]` | Write the scrollback to a file (default `BepInEx/cwy-console-dump.txt`). |
| `clear` | Empty the scrollback. |

Game commands (group `game`, from `CwyLs.ModLoader.Bulbul.Console`): `voice`, `tex`, `events`, `data`,
`satone`, `masterdata`. `data notes` truncates note text at 80 characters unless you pass `--full`,
and the data bus stays read-only here. (`subtitle` was retired with the overlay panel it drove;
`satone say <text>` puts a line in the game's own text box.)

## Adding a command or a tab from a mod

A command is a name, a description, a usage line, a group and a handler that takes the arguments
after the name and returns the text to print:

```csharp
CwyLs.ModLoader.Console.ConsoleFeature.Register(
    "hello", "Says hello.", "hello [name]", "mymod",
    args => "hello, " + (args.Count > 0 ? args[0] : "world"));
```

The usage line is also the argument completion: `hello [name]` offers nothing, `mode on|off`
offers `on` and `off`.

A tab is a name and a function returning the text to draw:

```csharp
CwyLs.ModLoader.Core.Console.ConsolePanels.Register("MyMod", () => "whatever is happening");
```

Both registries are process-wide and replace an existing entry in place, so a plugin whose `Awake`
runs twice cannot register twice. A handler or renderer that throws is caught: the console shows
the failure and the log records it. Neither registry needs a reference to the console itself -
`ConsolePanels` and `CommandRegistry.Shared` live in `CwyLs.ModLoader.Core`.

## Surfaces: how the console is drawn

`ConsoleFeature` owns the state; a surface (`IConsoleSurface`) only draws it, and every surface
drives the same `ConsoleSession` (tabs, views, tab buttons, run, completion, history), so a
fallback mid-session keeps the selected tab.

| Surface | Where | When |
|---|---|---|
| `game-style` | `CwyLs.ModLoader.Bulbul.Console.GameStyleConsoleSurface` on `CwyLs.ModLoader.Bulbul.UI.GameStyleWindow` | `Console.Style=Game`, the adapter registered it, and the game's UI assets resolve (in `RoomScene`). |
| `imgui` | `ConsoleWindow` drawn from the hidden `CWY.ConsoleHost` | Everything else: before the room loads, `Style=Imgui`, a game update that renamed an asset, or a game-style failure. |

The choice is made on every open (`ConsoleSurfacePolicy`) and logged once per change of state:
`CONSOLE  game-style console: all game UI assets found`, or at Warning
`CONSOLE  game-style console unavailable, using the IMGUI console: missing t_ui_close`, and
`... available again` after a recovery. A game-style surface that throws while running is off for
the session (`CONSOLE game-style console failed and is off for this session ...` at Error) and the
IMGUI overlay takes over at once. The console never becomes unusable. Smoke check
`b.console.gamestyle` reports whether the assets resolve without opening anything.

### Hide UI

With `FollowHideUI` on, an open game-style console fades with the game's `UI` object (its
`CanvasGroup` alpha, 0 when the game deactivates it). The toggle key never leaves an invisible
open console behind (`ConsoleVisibility`): pressing it while the console is faded out reveals it;
opening the console while the UI is hidden shows it at once. A reveal lasts until the game's UI
comes back. The IMGUI overlay does not follow Hide UI.

## Keyboard

### Game-style window

The input is a real TextMeshPro input field, the kind the game's own note and to-do fields are,
so OS input-method composition behaves as it does there. While it is selected:

- the hotkey registry ignores every chord (`Hotkeys.IsTyping` sees a text field), so typing never
  fires a framework hotkey; the console handles its own toggle chord and Escape itself
  (`ConsoleKeyRouter`) and the per-frame debounce in `ConsoleFeature.Toggle` makes sure a press
  acts once even on the frame the input is deselected;
- a character typed with Ctrl (not AltGr) or Command held is dropped, so Ctrl+Shift+C leaves no
  letter behind (macOS reports Ctrl+C as U+0003, which the field turns into a newline);
- Return is taken from the field as a newline and turned into "run" (the field is multi-line
  underneath), so running a command does not produce a `TYPED` trace line of its own;
- while the input method is composing, Escape, Tab, the arrows and the page keys are the input
  method's.

On the Console tab the input takes the keyboard back whenever nothing else holds it, except when
the player is typing into one of the game's own text fields. On the other tabs there is no input:
the hotkeys are live and only Escape and the page keys are the console's.

Known leftover: closing with Escape ends the field's edit, which the trace plugin mirrors as one
`TYPED ""` line.

### Escape and the game's fullscreen switch

The game reads Escape itself every frame (`RoomGameManager.Update`, game 1.17.x) and calls `SettingService.ChangeWindowMode(Window)`, which saves the
setting and takes a fullscreen game to windowed. That is a device poll, so no surface can hide the
key from it. The Escape that closes the console does not do that:

- every frame it is on screen, the surface reports who owns Escape
  (`ConsoleFeature.EscapeGuard.Observe`, owner from `ConsoleEscapeGuard.OwnerOf`, i.e. the key
  router's own answer); a surface that closes on Escape claims that frame (`Claim`);
- the game adapter prefixes `SettingService.ChangeWindowMode` (`CwyLs.ModLoader.Bulbul/Console/
  ConsoleEscapePatch.cs`, patch target `console.settingservice.changewindowmode`) and skips it only
  when the game asked for `Window`, Escape went down this frame, and the console claimed it this
  frame or is open and owned Escape in this or the previous frame. Both orders of the two `Update`
  calls in a frame are covered; the IMGUI overlay (drawn in `OnGUI`, after every `Update`) relies on
  its per-frame report;
- everything else runs as before: Escape with the console closed, Escape while the player types in
  one of the game's own fields (the key router gives it to the game), the console faded out with
  Hide UI, and the settings window's own Window / Fullscreen buttons (a click, no Escape).

Each skipped switch writes one trace-file line, `console/escapeKept` (frame, claimed,
consoleOpen, fullscreen, blocked). The decision is `Core/Console/ConsoleEscapeGuard` and is
unit-tested (`ConsoleEscapeGuardTests`).

### IMGUI overlay

While the **Console** tab is open the input field is focused every frame and the keys the console
uses (Return, Tab, Up, Down, Escape, PageUp, PageDown, and the character events for Tab and the
newlines) are consumed with `Event.Use()`, so IMGUI passes them to nobody else.

That is the whole of what IMGUI can do. A game that polls `UnityEngine.Input` directly reads the
device, not the event stream, and cannot be blocked from here - typing into the console can still
trigger such a key. The one exception handled is the game's Escape-to-windowed switch (above). The framework's own hotkeys are unaffected either way: every one of them needs
an F-key or Ctrl+Shift, which plain typing never produces.

## Where the IMGUI overlay lives

It is drawn from a hidden `DontDestroyOnLoad` host (`CWY.ConsoleHost`), because this game destroys
BepInEx plugin components about 0.2 s after boot. The host component is
enabled only while the IMGUI surface is the one on screen, so a closed console - or one drawn by
the game-style surface - costs no `OnGUI` call at all.

Two Unity module references are needed for it (`UnityEngine.IMGUIModule`,
`UnityEngine.TextRenderingModule`): `UnityEngine.dll` only type-forwards `GUI`, `GUIStyle` and
`Font`. Both are declared in `src/CwyLs.ModLoader/CwyLs.ModLoader.csproj`.
