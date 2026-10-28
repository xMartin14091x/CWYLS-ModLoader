# Mods — the code-mod front door

A **code mod** is a pack whose `manifest.json` names an `entryDll`. It lives in
`<game root>/CWYLS Mods/<id>/` next to content packs, shares their dependency graph and version gates
(`packs/README.md`), and may carry content (textures, audio, data) of its own. Its DLL holds exactly
one concrete class deriving from `CwyMod`; the framework creates it and calls `Entry(helper)`.

This is the SMAPI model inside CwyLs.ModLoader: mods are not BepInEx plugins.

Start a mod from `templates/cwy-mod/`. The working example is `mods-src/cwy.sample.hello/`.

## Lifecycle

| When | What happens | Where |
|---|---|---|
| `ModLoaderPlugin.Awake` | the main-thread dispatcher is armed; packs are scanned and resolved (code mods included); the loader binds `[Mods]`, installs its `AssemblyResolve` handler, registers the `mods` command and two smoke checks, and schedules the load | `ModLoader.Initialize` |
| every other plugin's `Awake` | the game adapter registers the game-specific helper facets (`ModBackends.Use*`) | `CwyLs.ModLoader.Bulbul/Mods/BulbulModBackends.cs`, `CwyLs.ModLoader.Bulbul.Data/Mods/GameDataModBackend.cs` |
| first frame (hidden `CWY.MainThread` host) | for each code mod in dependency order: gate on required code mods → load the assembly → pick the entry class → construct → `Entry(helper)`; then every mod's `GetApi()`; then the mods-ready handlers; then one `MODS` summary line | `ModLoader.LoadAll` |
| `Application.quitting` | `OnShutdown()` in reverse load order, then every registration of each mod is taken back | `ModLoader.Shutdown` |

Mods load on the first frame, before the room exists: subscribe to `RoomReady` for anything that
needs the room or the player's data. There is no code reload: Mono cannot unload an assembly, so a
changed DLL needs a game restart, and a DLL loaded from a file stays memory-mapped — **never overwrite
a mod DLL while the game runs** (deploy with the game closed, as `build.sh` and `deploy-packs.sh` do).

## The helper (`ICwyModHelper`)

One helper per mod; every facet wraps a capability the framework already has.
Only the framework implements these interfaces, so members can be added without breaking mods.

| Facet | Wraps | Notes |
|---|---|---|
| `ModId`, `Manifest`, `DirectoryPath` | `LoadedPack`, `PackManifest` | `Manifest.Extensions` holds the manifest's `x-` members; `DependsOn` and `Extensions` are the view's own read-only copies and the JSON values inside are deep copies, so a mod cannot change what anyone else reads |
| `Log` (`Trace/Info/Warn/Error`) | `ConsoleEvents` | tag `MOD`, primary `<id>: message`; `Trace` is file-only; an exception's stack goes to the trace file |
| `Config` | BepInEx `ConfigFile` | `BepInEx/config/cwy.mod.<id>.cfg`, created on first use |
| `Data` | files under `ModLoaderPaths.DataRoot` | `BepInEx/data/mods/<id>/<key>`; keys `^[A-Za-z0-9_-][A-Za-z0-9._-]{0,63}$`, no `..`; writes go to `<key>.tmp~` and replace the file (`File.Replace`, copy fallback); `ReadJson`/`WriteJson` use `JsonReader`/`JsonWriter` |
| `Events` | `CWYEvents.Subscribe/SubscribeAny` (adapter), `MainThread.AddTicker` | `Subscribe<T>` for the records in `CwyLs.ModLoader.Core.Events`; `EveryFrame`; `OnModsReady` (first moment `GetApi` works) |
| `Satone` | `Satone.Face/Motion/LookAt/LookReset/FaceReset/RestoreDefaults/Say` (adapter) | `Say(string)`, `Say(SayRequest)`, `SayWithClip(SayRequest, AudioClip)`; `CanInteract`, `IsSaying`, `State`; each call leaves a file-only `mods/satone` line naming the mod |
| `GameData` | `GameData.IsReady/Snapshot/Changed` (data plugin) | read-only; `Snapshot()` on the main thread only |
| `Language` | `LanguageFeature.CurrentCode/Active` (adapter) | `CurrentCode`, `IsPackLanguage` |
| `Content` | `LoadedPack.FullPath`, `TextureLoader`, `AudioLoader`, `JsonReader` | paths stay inside the mod folder; texture and WAV loads on the main thread |
| `ContentPacks` | `PackManager.Loaded` | packs whose `contentPackFor` names this mod |
| `Input` | `Hotkeys.Register` | registry id `mod:<modId>/<id>`; chords like `Ctrl+Shift+H` (parsed by `HotkeyChord`); bound in the mod's `.cfg` as `[Hotkeys] <id>.Primary` / `<id>.Alt`; an id another owner holds is refused (one warning), never replaced; a chord another key uses registers with one warning; returns a `CwyRegistration`; never fires while a text field has focus |
| `Console` | `CommandRegistry.Shared` | listed under the mod id in `help`; a name another owner holds is refused, never replaced |
| `Ui` | `GameStyleWindow` + text view + input line (adapter) | `CreateWindow(options, out problem)`; main thread; destroyed when the mod stops. Spec-built windows (`src/CwyLs.ModLoader.Bulbul/UI/README.md`) extend this |
| `MainThread` | the shared dispatcher | owner `mod:<id>`; `Post`, `RunOrPost`, `PostAfter` (returns a `DelayedWork` handle: dispose = cancel), `InvokeAsync` (default timeout 2 s), `Invoke`; a refusal (queue full, mod not running, game quitting) is a `MainThreadRejectedException` |
| `ModRegistry` | the loader's table | `IsLoaded`, `Get`, `GetAll` (packs and mods), `GetApi<T>(id)` — direct interface sharing: both mods reference one contract DLL |

A facet whose backend is missing (no game adapter) reports `IsAvailable = false` and logs one
`MODS <id>: … not available` warning on first use instead of throwing.

The further facets (`Harmony`, `Conditions`, `Actions`, `Scenes`, `EditData`, `Audio`, `UiSkin`,
`Camera`, `VoiceStream`, `Mic`, `Api`) are described in `Facets/README.md`. Anything that registers
returns a `CwyRegistration` (`IDisposable`: dispose = unregister; a refused one comes back inactive
with `Problem` set). Adapter backends register through `ModBackends.Use*` (typed slots) or
`ModFacetBackends.Use<T>`; `ModBackends.Use*` is internal to the framework's assemblies
(`../AssemblyInfo.cs`), and `mods status` lists every installed backend.

## Isolation: a throwing mod never takes the game or another mod down

Every path into mod code is wrapped: constructor, `Entry`, `GetApi`, `OnShutdown`, event and data
handlers, frame ticks, mods-ready handlers, posted work, console commands, hotkeys, window events,
async audio callbacks. A failure is one console line and the full stack in the trace file, in the
same bus event:

```
MODS  my.mod failed in Event 'RoomReady': NullReferenceException: Object reference not set …
```

The stage (`ModStage`, numbers stable) is one of `Load`, `Construct`, `Entry`, `GetApi`, `Event`,
`Command`, `Hotkey`, `Tick`, `MainThread`, `ModsReady`, `Shutdown`, `Action`
(a game action the mod registered), `Script` (a scenario command handler) and `Token` (a condition
token provider).

Repeats at one site are counted for 10 s and reported with the next line (`(+37 earlier)`). A site
is the mod, the stage and the first word of the label (`chunk 17` and `chunk 18` are one site), and
at most 256 sites are tracked. A failure in the constructor or `Entry` marks the mod **Failed** and
takes back everything it registered: its helper registrations are disposed (subscriptions, tickers,
queued work, commands, windows; hotkeys stay listed but stop calling it) and its Harmony patches are
removed (`UnpatchSelf()` for the mod id, plus every patch whose patch method is declared in one of
the mod's assemblies; never an id-less `UnpatchAll`). Code mods that require it are skipped as
`DependencyFailed`. A failure anywhere else is counted (`mods list` shows `failures=N`) and the mod
keeps running. The exception reported is the mod's own: `TargetInvocationException` and
`TypeInitializationException` (a throwing static constructor) are unwrapped. An unexpected failure of
the loader itself while handling one mod skips that mod (`AssemblyLoadFailed`) and the others still
load; the mods-ready signal and the summary line always come.

Handlers are called directly through the `CwyMod` base (virtual dispatch), never through reflection,
so the exception that reaches the log is the mod's own.

What the framework cannot wrap: a mod's own threads and the code inside its Harmony patches. Patch
through `helper.Harmony` (id = the mod id), catch inside the patch and fall back to the original, and
never call an id-less `UnpatchAll()`.

## Loader rules and skip reasons

Refused **by the resolver** (the whole pack is skipped, content too): `PackSkipReason` —
`InvalidManifest`, `CoreVersionTooOld` (`loaderVersion` above `CwyApi.Version`),
`EntryDllMissing`, `MissingDependency`, `DependencyVersionTooLow`, `DependencyCycle`, … (`packs/README.md`).

Refused **by the loader** (content already applied; no code runs): `ModSkipReason` —

| Reason | Meaning |
|---|---|
| `NoEntryType` | no concrete `CwyMod` class with a parameterless constructor (the detail names abstract/generic ones, or a class built against another copy of CwyLs.ModLoader). Also what happens when the entry class itself uses an optional dependency's types (implements its interface, has a field of its type) and that mod is absent: the class cannot load. Keep such code in a separate class |
| `MultipleEntryTypes` | more than one; the detail lists them |
| `AssemblyLoadFailed` | not a .NET assembly, missing, the load threw, or the loader failed unexpectedly on this mod (stack in the trace) |
| `AssemblyAlreadyLoaded` | an assembly of the same name, or the same build (MVID), is already loaded: installed twice, or also in `BepInEx/plugins`. A second copy would cross-wire HarmonyX patches |
| `DependencyFailed` | a required code mod was skipped or failed |
| `Disabled` | `[Mods] Enabled=false` |

**Binding.** One `AppDomain.AssemblyResolve` handler, installed in `Awake` before any mod type is
touched, answers in the order of `ModAssemblyResolver` (Core, unit-tested):

| Who asked (`ModAssemblyRequester`) | Answer |
|---|---|
| anyone | an already-loaded assembly of that simple name |
| a mod (its entry DLL, or a DLL the resolver loaded from its folder) | its own folder → the other loaded mods' folders in load order → nothing |
| unknown (`RequestingAssembly` is null) | a mod folder only when exactly one has the name; several → nothing, one `MODS` warning naming them |
| the game, a BepInEx plugin, the framework | never a mod folder |

Every DLL loaded from a mod folder is mapped to that mod, so its own references resolve from the
same folder and deprecations it triggers name the mod. Deciding and loading run under one re-entrant
lock. Mono does not probe a mod's folder for its private dependencies, so this is what makes them
load. Never served from a mod folder, with one `MODS` warning per copy: a shared name - the fixed
families `CwyLs.ModLoader*`, `Assembly-CSharp*`, `UnityEngine*`, `Unity.*`, `BepInEx*`, `0Harmony`,
`HarmonyX`, `MonoMod*`, `Mono.Cecil*`, `mscorlib`, `netstandard` (`ModAssemblyRules.SharedFamilies`),
plus every DLL in the game's `Managed`, BepInEx `core` and `plugins` folders and everything already
loaded - and another code mod's entry DLL, which only ever loads from that mod's own folder (so a
mod that bundles it cannot get the real one refused as `AssemblyAlreadyLoaded`). `System.*` and
`Microsoft.*` are no longer whole families: what the game ships (`System.dll`, `System.Text.Json`,
`Microsoft.Bcl.AsyncInterfaces`, ...) is shared through the `Managed` listing, and what it does not
(`System.Collections.Immutable`, `Microsoft.Extensions.*`, `System.Reactive`, provider SDKs) loads
from the mod's folder. The template and the sample copy such private dependencies into the pack
folder at build time, minus the shared names.

**JIT trap.** Mono may compile a method before it runs. Code that touches another mod's contract
types (an optional dependency) belongs in a separate `[MethodImpl(MethodImplOptions.NoInlining)]`
method that is only called when `ModRegistry.IsLoaded` says the other mod is there.

**Load modes** (`[Mods] AssemblyLoad`): `LoadFrom` (default) or `Bytes` (developer switch:
`Assembly.Load(dll, pdb)`, nothing locked or mapped; every load is a new assembly, so a future
hot-swap must unpatch the old copy's Harmony id first).

## Versioning and deprecation

- `CwyApi.Version` (Core, `CwyLs.ModLoader.Core.Mods`) is **0.9.0** and is also CwyLs.ModLoader's plugin
  version. A manifest's `loaderVersion` is checked against it, for packs and mods alike.
- 0.x until **CWY:LS Mod Loader 1.0**: a minor version may
  change the API. After 1.0 only a major version breaks mods.
- Deprecating a member: mark it `[CwyDeprecated("0.9.x", DeprecationStage.…, Replacement = "…")]`
  and call `CwyDeprecations.Notify(MethodBase.GetCurrentMethod())` (or the explicit
  `Notify(member, since, stage, replacement)`) at its top; mark the member
  `[MethodImpl(MethodImplOptions.NoInlining)]`. The attribute may sit on the member, on the property
  or event whose accessor calls `Notify`, on the interface member a class method implements, or on
  the class (`DeprecatedMembers`, Core). The calling mod is found from the member's immediate caller;
  the stack is walked only once per member and calling assembly. Each mod gets one line per member
  per session: `Notice` → trace file only, `Info` → `MODS` line, `PendingRemoval` → `MODS` warning.
  Removal waits for the next breaking version.
- Read the version at run time (`CwyApi.Version`); `CwyApi.PluginVersionText` is the compile-time
  constant for the framework's own `[BepInPlugin]` (`CwyLsModLoader.PluginVersion` uses it).
  `CwyApi.VersionText` is `static readonly`, so a mod reads the running version, not the one it was
  compiled against.

## The main-thread dispatcher (`CwyLs.ModLoader.Threading.MainThread`)

One dispatcher for the framework and every mod; logic in Core `MainThreadQueue` (unit-tested),
pumped once a frame by the hidden `CWY.MainThread` host.

- `Post` (next frame, always queued), `RunOrPost` (inline on the main thread), `PostAfter`
  (real-time delay; returns a `DelayedWork` handle - dispose or `Cancel()` drops the work if it has
  not started, `IsAccepted`/`RejectReason` say whether it was taken), `InvokeAsync` (task; always
  queued, so awaiting on the main thread cannot deadlock), `Invoke<T>(Func<T>)` and `Invoke(Action)`
  (blocking; inline on the main thread), `AddTicker` (every frame, after the work).
- **Two lanes:** framework owners (anything without the `mod:` prefix) run in full every
  frame, first, as the per-feature pumps they replaced did - a mod backlog can never hold back
  Satone's restore. Mod work (`mod:<id>`) runs after it, within `[MainThread] FrameBudgetMs`
  (default 4) measured from the start of the mod lane, one item per mod in turn (round-robin), at
  least one item per frame; the rest waits. Tickers are not counted.
- **Order:** within a lane, work runs in the order it became ready - posted work when posted,
  delayed work when due - so a delayed item runs before anything posted after its due time.
  `CancelDelayed(owner)` also drops delayed items that are due but still waiting in a lane.
- **Timeouts are the caller's:** wall-clock, default 2 s (`MainThread.DefaultInvokeTimeout`; one
  dispatch takes about one frame, and the game runs at about 24 fps unfocused); longer than `int.MaxValue` ms (~24.8 days)
  or `Timeout.InfiniteTimeSpan` waits forever; a negative one is refused before anything is queued.
  Work whose caller gave up before it started is skipped, never run late; work already running
  finishes and its result is discarded.
- **Owners:** each item names one (`satone.motion`, `satone.say`, `mod:<id>`, `cwy`). Queues are capped
  per owner (256 waiting, 64 delayed) and in total; a refused post logs one `MAINTHREAD … is full`
  warning per owner per 10 s (the rest go to the trace file). `Cancel`, `CancelDelayed`,
  `RemoveTickers`, `Arm` and `PostAfterFor(owner, …)` are internal to the framework's assemblies.
- **Refusals:** `MainThreadRejectedException` (`Reason`: `QueueFull`, `OwnerNotRunning`,
  `ShuttingDown`; derives from `InvalidOperationException`). On `Application.quitting` the queue
  closes: posts return false, `PostAfter` hands back a refused handle, invocations fault at once.
- **Isolation:** nothing escapes a pump. Framework failures log `MAINTHREAD <owner> '<label>' threw …`
  (rate-limited per owner, work/tick and first word of the label); mod failures log the `MODS` line;
  an `InvokeAsync` failure goes to the caller's task and a file-only record.
- `mods status` shows both lanes: `…; framework pending=0 ran=N; mods pending=0 ran=M owners=K`.

Framework features on the dispatcher include `SatoneMotionHost` (queue, delayed calls → owner
`satone.motion`, one ticker for observation and the state cache) and `SatoneSayHost` (queue → owner
`satone.say`, one ticker).

## Config, command, smoke checks, console lines

`BepInEx/config/cwyls.modloader.cfg`: `[Mods] Enabled` (true), `[Mods] AssemblyLoad`
(`LoadFrom`), `[MainThread] FrameBudgetMs` (4, range 0.5–50).

Console: `mods` (list + status), `mods list`, `mods status`.

Smoke (quiet): `c1.mainthread.pump` (the host pumps), `c1.mods.loaded` (the loader ran; fails when a
mod failed).

Lines to expect:

```
SETUP  code-mod loader armed; mods load on the first frame  src=mods api=0.9.0 enabled=True assemblyLoad=LoadFrom
MOD    cwy.sample.hello: ready: a welcome once per session when the room is up; console command: hello [name]
MODS   cwy.sample.hello@0.1.0 loaded  type=CwySample.Hello.ModEntry ms=1.2
MODS   1 loaded (cwy.sample.hello@0.1.0); 0 skipped; 0 failed  api=0.9.0 backends=events,satone,gameData,language,ui
```

Trace-file-only records (`sys=mods`): `backend`, `api`, `resolve` (with `mod` and `requesterKind`),
`reserve`, `loaderFailed`, `deactivated`, `satone`, `deprecated` (Notice), `typeSkipped`, `shutdown`;
`sys=mainthread`: `failed`, `invokeFailed`, `dropped`, `refusedQuitting`. Warnings (console):
`mods/bundled`, `mods/bundledMod`, `mods/resolveIgnored`, `mods/resolveReserved`,
`mods/resolveAmbiguous`, `mods/typeLoad` (names the likely cause: an optional dependency that is not
running, a missing private DLL, or another CWY:LS Mod Loader version), `keys/conflict`.

## Files

| File | Role |
|---|---|
| `CwyMod.cs` | the mod base class |
| `ICwyModHelper.cs`, `ICwyModFacets.cs` | the helper and facet interfaces, `CwyWindowOptions` |
| `ModBackends.cs` | adapter-provided facet interfaces and their registration (`Use*` internal) |
| `Facets/` | the further facets, `CwyRegistration`, `ModFacetBackends` (`Facets/README.md`) |
| `../AssemblyInfo.cs` | `InternalsVisibleTo` for the framework's own assemblies only |
| `ModLoader.cs` | load, resolve, `GetApi`, mods-ready, shutdown |
| `ModRuntime.cs` | the entry table, manifest view, failure attribution |
| `ModHelper.cs`, `ModFacets.Framework.cs`, `ModFacets.Game.cs` | the per-mod helper and facets |
| `CwyDeprecations.cs` | the deprecation notifier |
| `ModCommands.cs` | `mods` command and the smoke checks |
| Core `Mods/` | `CwyApi`, `CwyDeprecatedAttribute`, `DeprecationLedger`, `DeprecatedMembers`, `ModStatus`/`ModState`/`ModSkipReason`/`ModStage`, `ModLoadPlanner`/`ModLoadGate`, `ModEntryTypeSelector`, `ModAssemblyRules`/`ModAssemblyResolver`/`ModAssemblyEnvironment`, `ModExceptions`, `ModDataKey`, `ModFailureLimiter`, `HotkeyClaims`, `ModConsoleLines` |
| Core `Threading/` (`MainThreadQueue`, `DelayedWork`, `MainThreadRejectedException`); `CwyLs.ModLoader/Threading/MainThread.cs`, `MainThreadHost.cs` | the dispatcher |
