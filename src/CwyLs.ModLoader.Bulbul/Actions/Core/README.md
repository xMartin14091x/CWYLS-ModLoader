# Bulbul/Actions/Core — game services and UI clicks for action families

Namespace `CwyLs.ModLoader.Bulbul.Actions`. What an action family (media, room, timer, camera, data
writes) uses to reach the game. Families themselves need no init line: put a class with
`[ActionFamily("<family>")]` in `Bulbul/Actions/<Family>/` and it is registered on the first frame
(`CwyLs.ModLoader.Core/Actions/README.md` → Families). Init: `BulbulActionsCore.Initialize()` from the adapter
plugin's Awake (seam check + smoke checks only).

## `GameServices.Resolve<T>()` / `Lookup<T>()`

The game's own services for handlers (main thread only; never throws):

1. room scope — the live `Bulbul.RoomLifetimeScope` object (`FindObjectOfType`), only while RoomScene is
   loaded; never `RoomLifetimeScope.Resolve`/`GetOrCreate`, which instantiate a scope when none exists.
   Registered there (`RoomRegister`): e.g. `PomodoroService`, `FacilityMusic`, `HeroineService`.
2. project scope — `ProjectLifetimeScope.GetOrCreate()` (no side effect; e.g. `MusicService`).
3. scene — `Object.FindObjectOfType(typeof(T))` for a component type.

Lookups use VContainer's `IObjectResolver.TryResolve`, so an unregistered type is
`null`, not an exception. Not cached (room services die with the room). `Lookup<T>()` also says where it
came from (`room`/`project`/`scene`) or why not (`not available (the room is not loaded)`).

Seams (`ActionsCoreTargets`, verified at startup, seamcheck): `actions.services.roomscope`,
`.projectscope`, `.container`, `.tryresolve`. A missing one turns that lookup path off with one WARN.

## `UiClick.Invoke(path)` / `Probe(path)` / `Fallback(semantic, path, what)`

The fallback path of an action (`ActionPath.UiClick`): one left click on the game's own control, the
way the player would. For families only — never a free-form action (a raw click could delete a todo
past the Write gate).

- `path` = the full hierarchy path from the scene root, as the trace's `BUTTON` lines print it, e.g.
  `Paremt/PCPlatform/Canvas/UI/ChangeOrderObjects/UI_FacilityEnviroment/EnviromentOther/WeatherChangeButtons/CloudyButton`.
  A shortened trace path (`…/`) is refused. Only active objects are found (`GameObject.Find`).
- Click receiver: the object or its nearest parent with an `IPointerClickHandler`
  (`ExecuteEvents.GetEventHandler`); not clicked when `Selectable.IsInteractable()` is false.
- Deny list (refused before anything is resolved, one `ACTION … UI click refused` WARN): any path
  segment holding a `UiClickPolicy` word (shop, account linking, cloud sync, SNS/browser — whole words,
  so `CloudyButton` stays clickable), and any control or ancestor carrying one of `UiClick.DeniedTypes`
  (`AuthButton`, `AuthUI`, `AccountTransferDialog`, `ShopOpenButtonView`, `PurchasePopover`,
  `TMP_Hyperlink`, `TermsDialog`, the mobile shop/account views).
- `Fallback(semantic, path, what)`: keeps the semantic result when it reached the game or was refused on
  purpose; otherwise clicks.

```csharp
private static ActionResult Play(ActionInvocation call)
{
    var music = GameServices.Lookup<global::Bulbul.FacilityMusic>();
    var semantic = music.Found ? PlaySemantic(music.Service!, call) : ActionResult.NotAvailable("FacilityMusic " + music.Problem);
    return UiClick.Fallback(semantic, "Paremt/PCPlatform/Canvas/UI/…/PlayButton", "pressed play");
}
```

## Smoke (quiet)

`e.actions.services` — in the room, `HeroineService` resolves via the room scope and `MusicService`
from a scope. `e.actions.uiclick` — a probe of `…/UI_FacilitySetting/SNS/X` is refused and a probe of
`Paremt/PCPlatform/Canvas` resolves; nothing is clicked.

## Files

| File | Role |
|---|---|
| `GameServices.cs` | `Resolve<T>()`, `Lookup<T>()`, `GameServiceLookup<T>` |
| `UiClick.cs` | `Invoke`, `Probe`, `Fallback`, `PathOf`, `DeniedTypes` |
| `ActionsCoreTargets.cs` | the four seams |
| `BulbulActionsCore.cs` | init: seam check, smoke checks, SETUP line |
