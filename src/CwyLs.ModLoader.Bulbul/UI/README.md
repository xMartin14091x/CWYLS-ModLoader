# Game-style windows and mod windows

Windows that look and move like the game's own. The CWY console is the first user
(`src/CwyLs.ModLoader.Bulbul/Console/GameStyleConsoleSurface.cs`); code mods build windows here
through `helper.Ui.CreateWindow(spec)` - see "Mod windows" below.

## Which game window it imitates, and why

The game's **facility widgets** - the note (`UI_FacilityNote`), habit tracker
(`UI_FacilityHabitTracker`) and to-do windows in the game's `RoomScene` - share one kit and are the
game's general-purpose, draggable windows. The note window is the closest match for a console: it
already has every part the console needs (title, close button, drag handle, scroll view with a
scrollbar, a TextMeshPro input field, buttons). The settings window (`UI_FacilitySetting`) is a
fixed modal and was used only for its tab plates, because the console has tabs and the facility
widgets do not.

Measured from the game's `RoomScene` windows and their sprites (game 1.17.2):

| Part | Game source | Here |
|---|---|---|
| Body | `t_ui_generalbase_rbabbable`, uniform black at 90 % (drawn tiled by the game) | stretched; `Console.Opacity` can lower it |
| Title | TextMeshPro `Base_JP` (IBM Plex Sans JP Medium) 28 pt white at (97, -47) | same font and size |
| Separators | `t_ui_back_bar` 9-slice, 4 px, at y -65 and 22 px above the bottom | same |
| Close | `t_ui_close` 28x28 at (-24, -24), hover `t_ui_close_active` | same |
| Drag handle | `t_ui_grab_horizontal` 24x16 top centre (`DraggableUI` on it), hover `_active` | same icon; the whole title bar drags |
| Buttons | `t_ui_listchange` pill (habit tracker), hover `_active` | same |
| Tabs | settings `t_ui_settings_tab` / `_select` / `_active`, label `#6E6E93`, white when selected | same |
| Text areas | `t_ui_generalbase_outline` 9-slice (dark navy, lavender outline) | log panel and input |
| Scrollbar | handle `t_ui_scrollbar` 9-slice | same |
| Palette | cyan `#00D5EF` and magenta `#EA00B2` hover outlines, lavender `#9999BF` bars, navy `#2C2946` fills | `GameStyleUi`, `ConsoleLineColors` |
| Motion | `InteractableUI` hover fade 0.15 s in / 0.2 s out; `HoldButtonAnimation` scale 1.05 hover, 0.95 press, 0.1 s; `NoteUI.Activate` fade in over 0.2 s while rising 8 px | same timings |
| Dragging | `DraggableUI.AdjustPos` keeps the whole window on screen | `WindowRectMath` (Core, unit-tested) |

## How it is built

**uGUI built at runtime from the game's assets, found by name** (`GameStyleAssets`, names in
`Core/Console/GameStyleAssetSet.cs`), rather than cloning a game window:

- a cloned `UI_FacilityNote` would bring `NoteUI`, VContainer `[Inject]` fields, DOTween-driven
  `InteractableUI`/`HoldButtonAnimation` and the localisation behaviours along, all of which would
  have to be stripped - and re-stripped after every game update;
- by-name lookup keeps our logic independent of game scripts: a game update that renames one
  sprite costs that part (drawn as a plain tinted shape) or, for the four required assets
  (`Base_JP`, `t_ui_generalbase_rbabbable`, `t_ui_back_bar`, `t_ui_close`), makes `TryCreate` fail
  with the missing names so the caller can fall back.

The assets are in memory while `RoomScene` is loaded (its inactive facility windows reference
them). Before that the lookup fails; the console then uses its IMGUI overlay.

Hover and press are our own `GameStyleClickable`, not `UnityEngine.UI.Button`: the trace plugin
mirrors every `Button.Press` to the console as a `BUTTON` line, and the console's own buttons must
not talk about themselves.

## Canvas, sorting, scaling, Hide UI

- One overlay canvas for every window, `CWY.GameStyleCanvas` (`GameStyleCanvas`), created on first
  use under `DontDestroyOnLoad`, sorting order 32600: above the game's canvases (max 999 in
  `RoomScene`, dialogs 100-120). An earlier subtitle overlay sat at 32000; that overlay is gone and
  the number was kept.
- Scaling follows the game's own canvas every frame (`GameUiBridge.FollowGameScaling`: the game's
  `CanvasScaler`, 1920x1080 ScaleWithScreenSize, whose match value `CanvasScalerAdjustor` moves with
  the aspect ratio), so a window is exactly as large as the game's windows at any resolution.
- `GameUiBridge.GameUiAlpha` reads the game's Hide UI state from `Bulbul.UICanvasProvider`
  (`UIParent` deactivated, `UIParentCanvasGroup` alpha). A window's `ExternalAlpha` can follow it;
  the console does, through `ConsoleVisibility`. It is the only game type the toolkit touches, and
  a missing type degrades to "UI shown, default scaling" with one warning.

## API (what exists today)

```csharp
if (GameStyleWindow.TryCreate(new GameStyleWindowOptions { Title = "My mod", MinWidth = 480f }, out var window, out var problem))
{
    var log = window.AddTextView(window.Body);         // scrolling rich-text lines, only the visible ones rendered
    // each frame: log.Render(lines.Count, added, version, i => lines[i]);
    var line = window.AddInputLine(window.Body, "say something");
    var ok = window.AddButton(window.Body, "send");
    window.CloseClicked += () => window.Hide();
    window.Updated += () => { /* per-frame work while shown */ };
    window.Show();
}
else
{
    // problem says which game assets are missing; fall back to something that needs none.
}
```

Parts: `GameStyleWindow` (frame, title, status, close, drag, resize grip, show/hide animation,
`ExternalAlpha`), `GameStyleTabStrip`, `GameStyleTextView`, `GameStyleInputLine`,
`GameStyleButton`, and the building blocks in `GameStyleUi` / `GameStyleInteraction`. Callers lay
their parts out inside `Body` with plain RectTransform anchors. Every per-frame handler is guarded:
one that throws hides the window and raises `Failed` once.

## The line view (`GameStyleTextView`)

`Render(total, appended, version, lineAt)` takes one rich-text line at a time. The view measures
the lines it needs with a hidden TextMeshPro label of the same font, size and wrapping (at the text
box width, cached per distinct line until the width or font size changes), and
`LineLayout.Plan` (Core) picks the lines and the edge to pin them to:

- scrolled to the top, or everything fits: line 0 starts exactly at the text box's top edge
  (TopLeft alignment), the bottom edge may cut a line;
- following the newest line, or scrolled part-way: the chosen line ends exactly at the bottom edge
  (BottomLeft alignment), the top edge may cut a line.

The text box sits 8 units (`LineLayout.Padding`) inside the panel at the top and bottom, the game
note window's body-text inset (7.3 bottom / 8.2 left from its outlined background in the game's
`RoomScene`, text area insets plus TMP margins, mask on the same edge). The RectMask2D is on
the text box, so a cut line never reaches the padding, the panel outline or anything outside.
"At the top" is kept as an intent (`LineScrollState.PinnedToTop`), so a snapshot opened at line 0
stays at line 0 when the window is resized or the content grows.

This replaced a first version, which drew floor(height / one row) logical lines
bottom-aligned: wrapped lines pushed line 0 under the top edge for good, and short content left an
empty band above it.

Not yet: checkboxes, sliders, dropdowns, images in mod windows. (Lists, chat bubbles, multi-line
input, remembered places and click-to-front came with mod windows.)

## Mod windows

A code mod describes a window and gets it back hidden; the framework builds it on
`GameStyleWindow`, names it `CWY.Mod.<modId>.<windowId>`, and destroys it when the mod stops.

```csharp
using CwyLs.ModLoader.Core.Ui;

var spec = new CwyWindowSpec("notes")               // id within the mod; also the remembered-place key
{
    Title = "My mod", Width = 640f, Height = 720f,
    CloseOnEscape = false,                          // Esc only takes the keyboard away from the box
    Parts =
    {
        new CwyChatSpec("log"),                     // shares the room the fixed parts leave
        new CwyInputSpec("say") { Lines = 3, Placeholder = "Type a message" },  // Enter sends, Shift+Enter breaks the line
        new CwyButtonsSpec("actions") { Buttons = { new CwyButtonSpec("send", "Send") } },
    },
};

var result = helper.Ui.CreateWindow(spec);          // main thread, at RoomReady or later
if (!result.Ok) { helper.Log.Warn(result.Problem); return; }
var window = result.Window!;
var chat = window.Chat("log");
window.Input("say").Submitted += text =>
{
    chat.Add(CwyChatRole.Player, text);
    var reply = chat.Add(CwyChatRole.Character);    // empty bubble to stream into
    reply.Typing = true;                            // animated dots
    StartMyWork(text, chunk => reply.Append(chunk), // any thread: drawn at the next frame
        done: () => { reply.Typing = false; reply.AddButton("copy", "Copy"); });
};
chat.ButtonClicked += (bubble, button) => { /* bubble.Tag holds the mod's own data */ };
window.Show();
window.Input("say").Focus();
```

| Part | Spec | Handle | Notes |
|---|---|---|---|
| text | `CwyTextSpec` (`MaxLines` 500) | `ICwyTextView` | rich-text lines on `GameStyleTextView`; follows the newest line unless scrolled up; escape untrusted text with `CwyRichText.Escape` |
| list | `CwyListSpec` | `ICwyListView` | one-line plain items, 36-unit rows, only visible rows exist (`GameStyleListView`); `ItemClicked`, `Selected`, `ScrollTo` |
| chat | `CwyChatSpec` (`MaxBubbles` 200) | `ICwyChatView`, `ICwyChatBubble` | `GameStyleChatView`: character left (game panel), player right (cyan tint), system centred; `Append` streams, `Typing` dots, up to 6 bubble buttons, `Tag`; 8000 characters per bubble; plain text |
| input | `CwyInputSpec` (`Lines` 1-12, `SubmitOnEnter`, `ClearOnSubmit`, `MaxLength`) | `ICwyInputBox` | `GameStyleInputLine` in text-box mode (`GameStyleInputOptions`); OS input method (Thai, Kotoeri); `Submitted`, `Focus`, `Release`, `Enabled` |
| buttons | `CwyButtonsSpec` + `CwyButtonSpec(id, label)` | `ICwyButtonRow` | game pills, as wide as their labels; `Clicked(id)`, `SetLabel`, `SetButtonVisible` |
| tabs | `CwyTabsSpec` (`Labels`, `Selected`) | `ICwyTabStrip` | settings-window tabs; parts with `Tab = n` show only on tab n; `SelectedChanged` |

Layout: parts stack top to bottom with a 12-unit gap; inputs (44, or 18 + 26 per line), buttons
(44) and tabs (40) keep their height, the others share the rest (`StackLayout`). The window's
minimum height grows to fit the busiest tab (`CwyWindowSpecRules.MinimumHeight`).

**Threads.** The window's own members (`Show`, `Hide`, `Destroy`, `Title`, `BringToFront`, ...)
are main-thread only and throw `InvalidOperationException` elsewhere. Every part is a Core model
(`Core/Ui/PartModels.cs`, `ChatViewModel.cs`) that is safe from any thread; the view redraws when
the model's version moved, once a frame while the window is shown. Events (`Submitted`,
`ItemClicked`, `ButtonClicked`, `Clicked`, `SelectedChanged`, `CloseRequested`) run on the main
thread through the mod's guard, so a handler that throws is logged against the mod.

**The manager** (`ModWindows`), once a frame in the overlay canvas's `LateUpdate`
(`GameStyleCanvas.LateTick`, after every `Update`):

- *Order.* A mouse press over a CWY window (the console included) brings it to the front; over a
  mod window it also makes that window the active one; anywhere else no mod window is active.
  `Show` puts a window on top and makes it active (`WindowStack`).
- *Hide UI.* Windows with `FollowHideUi` take the game's UI alpha (`GameUiBridge.GameUiAlpha`);
  below 0.5 a window takes no clicks and gives the keyboard back.
- *Clicks.* The canvas has no full-screen graphic, so nothing outside a window's rectangle is ever
  taken. Inside one, the game ignores the press (`FacilityClickHeroine` checks
  `InputController.CurrentFrameEventSystemRaycastResult`).
- *Escape* (`EscapeRouting`, in order): the console already used it → nothing; a mod window's text
  box has the keyboard → composing: the input method's, otherwise the window closes
  (`CloseOnEscape`) or the box lets go of the keyboard; a game text field has it → the game; the
  console is open → the console; the player is in a mod window that closes on Escape → it
  closes; otherwise the game. Whenever a mod window owns the press, the game's own
  fullscreen-to-windowed switch (`RoomGameManager` → `SettingService.ChangeWindowMode`) is
  skipped: the manager records the owner of the next Escape (`WindowEscapeGuard`) and the console's
  Escape prefix (`Bulbul/Console/ConsoleEscapePatch.cs`) asks it next to the console's guard.
- *Hotkeys* need nothing extra: the registry ignores every chord while any text field is selected
  (`Hotkeys.IsTyping`), and the room reads no other game key (`CameraManager`/`Camera_Controller`
  are not in `RoomScene`). The trace plugin skips everything under `CWY.*` roots, so typed text is
  never mirrored as `TYPED`.
- *Remembered places* (`RememberRect`): written 1 s after a drag or resize ends and at quit to
  `BepInEx/data/ui/windows.json` (`WindowRectStore`), restored clamped onto the current canvas.

**Thai.** Every label uses `Base_JP`, whose fallback table reaches `Base_TH`/`Dynamic_TH` with the
pack's mark records (`Language/LanguageFonts.cs`). Chat bubbles add zero-width syllable breaks
before measuring (`ThaiText.AddSoftBreaks`) so unspaced Thai wraps between syllables; multi-line
inputs keep 4 units above the first line for stacked tone marks. `s.ui.thai` checks the glyphs and
mark positions in game.

**Older windows API.** `CreateWindow(CwyWindowOptions, out problem)` still works (deprecated, Notice):
with this backend it builds a spec with a text part `lines` and an input part `input`; without it,
the older adapter in `Bulbul/Mods/BulbulModBackends.cs`.

Config `[Ui] Enabled` (true). Console `windows [status]|list|demo|forget <modId/windowId>|forget all`
(`windows demo` opens a window with every part, Thai text and streaming). Smoke: `s.ui.backend`,
`s.ui.registry`, `s.ui.thai` (quiet), `s.ui.demo` (loud). Trace `windows/created|destroyed|close|
escape|refused|rectsSaved|lost|windowFailed|enabled|handlerFailed` (file only), and `console/escapeKept`
with `by=window`.

| File | Role |
|---|---|
| `ModWindows.cs` | feature init, `helper.Ui` backend, registry, per-frame order / Hide UI / Escape, places file, `windows` command |
| `ModWindow.cs` | one window: frame, part views, layout, close, keyboard |
| `ModWindowParts.cs` | part views over the Core models |
| `GameStyleChatView.cs`, `GameStyleListView.cs` | the chat and list views |
| `ModWindowSmokeChecks.cs`, `ModWindowDemo.cs` | smoke checks and the demo window |
| Core `Ui/` | specs, interfaces, models, `StackLayout`, `ScrollState`, `ChatLayout`, `TextBoxKeys`, `WindowStack`, `EscapeRouting`, `WindowRectStore`, `ThaiMarkCheck` (unit-tested) |

## Unit-tested pure parts (in `src/CwyLs.ModLoader.Core/Console/`)

`WindowRectMath` (placement, drag and resize clamping), `LineLayout` (which lines to draw and the
edge they are pinned to, for any line heights), `LineScrollState` (follow / stay / pinned-to-top
through new lines and resizes), `LineScroll` (paging and scrollbar mapping), `ConsoleLayout` (the
console's log area on every tab), `ConsoleKeyRouter` (key ownership and typed-character filter),
`GameStyleAssetSet` (required vs optional assets), `ConsoleLineColors` (tag to colour, rich text),
`ConsoleSurfacePolicy` and `ConsoleVisibility` (console fallback and Hide UI rules).
