# Core/Actions — the action contract and runner

Pure, Unity-free contract and runner for semantic game actions: the types, the registry, the default
tier gate, the "not available" runner and confirmation gate, the runner itself, the schema step, the
`ACTION` audit line with redaction, `[ActionFamily]` discovery, the gate switches, the `key=value` text
forms, the LLM/MCP tool schema and the UI-click deny list. Families' Core parts live in
`Core/Actions/<Family>/` (media, room, timer).
Consumers: console (`act`), scripts (`cwy.do`, `cwy.defer`), the local API (`/v1/actions`) and the
clients behind it (e.g. an MCP server), and mods (`helper.Actions`).

The game side: `CwyLs.ModLoader/Actions/README.md` (runner install, `act`, smoke, config) and
`CwyLs.ModLoader.Bulbul/Actions/Core/README.md` (`GameServices.Resolve<T>()`, `UiClick.Invoke(path)`, how to
write a family).

## One door for every caller

```csharp
var result = await ActionServices.Shared.RunAsync(
    new ActionRequest("music.play", ActionArgs.Of(("title", "Rain")), ActionCaller.Api, "my-client",
                      requestId: idempotencyKey, timeout: TimeSpan.FromSeconds(2)),
    cancellationToken);   // never throws, never faults: every outcome is an ActionResult
```

`ActionRunner.RunAsync`: look up (`NotFound`) → schema (`ActionArgsValidation`, `InvalidArgs`) → gate
(`ActionGate.Decide` with `ActionServices.GateSettings`, `Denied`) → confirmation when the gate says so
(`ActionServices.ConfirmationGate`, `Declined`) → main-thread hop through `IActionDispatcher` with the
request's timeout (null → 2 s, at most 60 s, ≤ 0 → `InvalidArgs`; `TimedOut`) → the handler (a throw →
`Failed`) → one `ActionAuditRecord`. Mod callers run in their own dispatcher lane (`mod:<id>`), everyone
else under owner `actions`. A handler that started but outlived the timeout is reported `TimedOut` to the
caller and gets a second, **late** audit record with its real outcome. Never wait for the task
synchronously on the main thread: the hop is queued for a later frame.

## Types

| Type | Role |
|---|---|
| `ActionTier` (`Read`, `Act`, `Write`) | how much it may change; published as MCP read-only/destructive hints |
| `ActionCaller` (`Mod`, `Console`, `Script`, `Api`) | who asks |
| `ActionDescriptor` | name (`family.verb`, lower-case dotted, ≤ 96), tier, description, `ActionParameter`s, `IsDestructive`; `Family` = text before the last dot |
| `ActionParameter`, `ActionValueKind` | one argument: name, JSON type, required, allowed values, **`IsPrivate`**, **`Minimum`/`Maximum`** |
| `ActionArgs` | immutable, case-insensitive name → text / long / double / bool / null; invariant typed getters |
| `ActionRequest` | name + args + caller + caller id (+ idempotency key, timeout) |
| `ActionInvocation`, `ActionHandler` | what a handler gets after gating; the handler returns an `ActionResult` on the main thread |
| `ActionResult`, `ActionStatus`, `ActionPath` | `Ok` / `Failed` / `InvalidArgs` / `NotFound` / `Denied` / `Declined` / `TimedOut` / `NotAvailable`; path `Semantic` or `UiClick` |
| `ActionRegistry` (`Shared`) | name → binding; refuses duplicates (never replaces); a mod registers only `<modId>.*`; `All`/`Descriptors` sorted by name |
| `ActionGate`, `ActionGateSettings`, `ActionGateDecision` | the default gating table below |
| `IConfirmationGate`, `ConfirmationRequest` (+ **`Arguments`**, unredacted, for the dialog only), `ConfirmationResult`, `DenyingConfirmationGate` | in-game yes/no; the stub never approves |
| `IActionRunner`, `UnavailableActionRunner`, `ActionServices` (`Shared`) | the runner, gate and **switch** slots (`UseWritesSwitch`, `UseApiActionsSwitch`, `GateSettings`); `RunAsync` never throws or faults |
| `ActionRunner`, `ActionRunnerStats`, `IActionDispatcher` | the runner above; counters; the main-thread hop |
| `ActionArgsValidation` | the schema step: unknown names, missing required, types, allowed values, bounds; messages never repeat a value |
| `ActionAuditRecord`, `ActionAudit` | the `ACTION` line: redaction, console vs file, `Publish` |
| `ActionFamilyAttribute`, `IActionFamily`, `ActionFamilyContext`, `ActionFamilyCatalog`, `ActionFamilyOutcome`, `ActionFamilyState` | `[ActionFamily("music")]` discovery: adding a family is adding a file |
| `ActionWords`, `ActionWordsResult` | `key=value` words → `ActionArgs` (console `act run`, script `cwy.do`); `act list` and result text |
| `ActionToolSchema` | an action as an LLM/MCP tool: name, description, tier, `readOnly`, `destructive`, JSON-schema `inputSchema` |
| `UiClickPolicy`, `UiClickVerdict`, `UiClickResult`, `UiClickStatus` | the UI-click deny list (shop, account, cloud sync, SNS/browser) and the click outcome |

## Default gate (`ActionGate.Decide`, first match wins)

| Rule | Verdict |
|---|---|
| Read tier | Allow (every caller) |
| API caller, Act or Write, `[Api] AllowActions=false` (default) | Deny |
| Write tier, `[Data] AllowWrites=false` (default) | Deny |
| destructive (a delete), any caller | Confirm |
| Write from the API | Confirm |
| otherwise | Allow |

The switches are read on every call from the plugin that owns each config entry:
`ActionServices.Shared.UseWritesSwitch(() => GameData.Options.AllowWrites)` (data plugin; the one
`[Data] AllowWrites` in `cwyls.modloader.bulbul.data.cfg`) and
`UseApiActionsSwitch(...)` (local API plugin). A switch nobody installed, or whose reader throws, is off.

A Confirm verdict with the default `DenyingConfirmationGate` ends as `Declined` (answer `Unavailable`),
so nothing is ever confirmed silently. An in-game dialog plugs in through
`ActionServices.Shared.UseConfirmationGate(...)`; the runner is installed by `ActionsFeature`.

## Privacy (the `ACTION` line and every log)

An argument value is printed only for a declared parameter that is not `IsPrivate` and, on a Write
action, is not text; everything else prints as `<private:N>` (N = characters), including every value of
an unknown action name. A Write handler's own message and a Write handler's exception message are
withheld (length / type only). Validation messages never repeat values. `ConfirmationRequest.Message`
is redacted; `ConfirmationRequest.Arguments` is not and must never be logged.

Line: `ACTION  music.play ok  via=semantic by=console:console args=title=Rain ms=12.3 detail="playing Rain"`.
Read-tier calls that worked are trace-file only (`action/run`); failed, timed-out and late records are
Warn.

## Families

```csharp
[ActionFamily("music")]
internal sealed class MusicActions : IActionFamily
{
    public void Register(ActionFamilyContext context)
    {
        if (!MusicSeams.Ok) { context.Skip("seam FacilityMusic.PlayMusic missing"); return; }
        context.Add(new ActionDescriptor("music.play", ActionTier.Act, "Plays a track by its title.",
            new[] { new ActionParameter("title", ActionValueKind.String, true, "The track title.") }), Play);
    }
}
```

Found in assemblies named `CwyLs.ModLoader*` on the first frame; each class created once (parameterless
constructor, any visibility; no game calls in the constructor). Names must start with `<family>.`;
a refused `Add` is a WARN, a throw marks the family Failed and takes its actions back.

Tests: `ActionsContractTests`, `ActionsRegistryTests`, `ActionsGateTests`, `ActionsRunnerTests`,
`ActionsValidationTests`, `ActionsAuditTests`, `ActionsFamilyTests`, `ActionsWordsTests`,
`ActionsUiClickPolicyTests`, `ActionsServicesTests`.
