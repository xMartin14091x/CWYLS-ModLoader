# Actions — the action runner in the game

`ActionsFeature.Initialize(Config)` (core plugin's Awake, right after `CoreRegistrySinks.Install()` and
before `ModLoader.Initialize`) binds `[Actions] Enabled`, registers the `act` console command and two
smoke checks, installs `ActionRunner` on `ActionServices.Shared` with the shared main-thread dispatcher
(`MainThreadActionDispatcher` → `MainThread.InvokeAsync`, no host of its own) and `ActionAudit.Publish`
as the audit sink, and posts the `[ActionFamily]` discovery for the first frame (before mods load, so
`OnModsReady` sees every family). Contract and runner rules: `CwyLs.ModLoader.Core/Actions/README.md`.

## Config

`BepInEx/config/cwyls.modloader.cfg`: `[Actions] Enabled` (true). Off: no runner, every
action answers `NotAvailable`, `act` explains why. Writes also need `[Data] AllowWrites`
(data plugin's cfg, read through `ActionServices.UseWritesSwitch`); the API's Act/Write calls need
`[Api] AllowActions` (local API plugin, `UseApiActionsSwitch`).

## Console (`framework` group)

| Line | Does |
|---|---|
| `act` / `act status` | runner, registered count, counters, switches (and whether they are connected), confirmation dialog, timeouts, family outcomes, self-test |
| `act list [prefix]` | every action: name, tier, description; one line per parameter (type, required, values, bounds, `[private]`) |
| `act run <name> [key=value ...]` | runs as caller `Console`, id `console`, through the same gate and audit; quotes from the console tokenizer (`title="Rain on glass"`); a run that waits for the main thread prints `running …` and its result when it ends |

## Smoke (quiet)

`e.actions.runner` — runner installed, families discovered, none failed. `e.actions.selftest` — a
private action (`cwy.selftest.ping`, its own registry and services, never reachable by players, scripts
or the API) answered Ok via Semantic from the main thread through the real dispatcher.

## Lines

```
SETUP   runner installed; families register on the first frame  src=actions timeout=2s confirm="in-game dialog or decline"
ACTION  2 families: 2 registered (12 actions), 0 skipped, 0 failed  registered=12
ACTION  music.play ok  via=semantic by=console:console args=title=Rain ms=31.2 detail="playing Rain"
ACTION  todo.add denied  via=- by=api:my-client args=title=<private:8> ms=0.1 detail="the game API may only read ([Api] AllowActions=false)"
WARN    ACTION  family music failed: Register threw …          (Warn level; skipped families are Info)
```

Trace `sys=action`: `run` (every audit record; Read Ok only here), `families`, `family`, `selftest`,
`uiclick` (Bulbul), `services` (Bulbul).

## Files

| File | Role |
|---|---|
| `ActionsFeature.cs` | init, runner install, first-frame discovery (`DiscoverFamilies(assembly)` for late plugins), self-test, `Status()` |
| `MainThreadActionDispatcher.cs` | `IActionDispatcher` over `MainThread.InvokeAsync` |
| `ActionCommands.cs` | `act` |
| `ActionSmokeChecks.cs` | `e.actions.runner`, `e.actions.selftest` |
