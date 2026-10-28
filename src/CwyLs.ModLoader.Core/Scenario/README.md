# Core/Scenario — script commands, their runtime logic, flags and the pack-scene lint

Pure, Unity-free half of custom script commands in pack scenes: the command contract, the runtime
logic, the built-ins' argument rules, flags, the deferred-action queue, the lint and the `scene play`
rules. The game side (the Harmony prefix on `ScenarioReader.ReadSentence(int)`, the save-log
guard, the console, smoke checks) is `src/CwyLs.ModLoader.Bulbul/Scenario/` — its README is the author's guide.

## Numbers (reserved ranges)

| Range | Owner | Meaning |
|---|---|---|
| 0-1000 | the game | its 50 `CommandType` members (`MasterDataEnums.Command`, unchanged) |
| 5000-5003 | script commands | `cwy.do` 5000, `cwy.defer` 5001, `cwy.wait` 5002, `cwy.flag` 5003 (built in, registered by `ScenarioFeature`) |
| 5004-5009 | conditions | `cwy.if` 5004; 5005-5009 free for more condition commands |
| 5010-5019 | camera | `cwy.camera.preset` 5010, `cwy.camera.vcam` 5011, `cwy.camera.reset` 5012 |
| 5020-5029 | voice streaming | `cwy.say.stream` 5020 |
| 5030-5099 | framework | reserved; a new fixed number needs an entry in `ScenarioCommandIds` |
| 5100-5999 | mods / unnamed | handed out by name, in order of first use, per session |

A pack names a command (`"Command": "my.mod.wave"`); only built-ins may be written as numbers. The pack
parser resolves names with `MasterDataEnums.TryParseCommand`, which reserves a number on first sight, so
a row parsed before its mod loaded still reaches the handler registered later.

**How a framework feature adds its command — no edit here:** from its own `Initialize`,
`ScenarioCommandRegistry.Shared.TryRegister(ScenarioCommandRegistry.FrameworkOwner, new ScenarioCommandDescriptor("cwy.if", "…", args), handler, out var registration, out var problem)`.
`cwy.*` names in the table above get their fixed number; any other `cwy.*` name gets a 5100+ number.
The handler gets an `IScenarioCommandContext` and decides with `Continue()`, `JumpTo(rowId)` (`cwy.if`) or
`WaitFor(check, timeout)` (`cwy.camera.*` easing, `cwy.say.stream` until the stream ends). Mods use
`helper.Scenes.RegisterCommand` (names `<modId>.*`).

## Types

| Type | Role |
|---|---|
| `ScenarioCommandIds` | range constants, the built-in name/number table |
| `ScenarioCommandDescriptor`, `ScenarioCommandArg` | name (lower-case dotted), description, meaning of Arg1-Arg4 |
| `ScenarioCommandRow` | read-only view of the game's `NovelData` row being run |
| `IScenarioCommandContext`, `ScenarioCommandHandler` | `Row`, `GroupId`, `ScenarioType`, `HasDecided`, `Continue()`, `JumpTo(rowId)`, `WaitFor(isDone, timeout ≤ 5 min)` |
| `ScenarioCommandRegistry` (`Shared`) | `TryResolve`, `TryGetName`, `IsKnownId`, `TryRegister`, `TryGetBinding`, `Bindings`, `SetRuntime`/`RuntimeAvailable`/`RuntimeDetail` |
| `ScenarioCommandContext`, `ScenarioCommandDecision`, `ScenarioCommandOutcome` | the context the runtime hands a handler; enforces the contract below |
| `ScenarioCommandRunner.Run` | look up the handler, call it, turn anything (no handler, a throw) into a decision |
| `ScenarioWaitBook`, `ScenarioWaitEnd(Kind)` | held rows: polled once a frame; Done / TimedOut / Threw / Abandoned |
| `ScriptActionLine` (+`Parse` → `ScriptActionLineParse`) | `cwy.do`/`cwy.defer`'s Arg1: `music.play title="Rainy Desk" volume=0.5` → name + `ActionArgs` + `ActionRequest` (caller `Script`) |
| `ScenarioBuiltInArgs` | the four built-ins' descriptors and Arg1 rules (`TryParseWait`, `TryParseFlag`, `Check`), shared by handlers and lint |
| `ScenarioFlags` (`Shared`), `ScenarioFlagChange` | named on/off flags (`flag:<name>` reads them); names = the condition argument rule; case-insensitive; ≤ 512; `ToJson`/`Load` |
| `DeferredActionQueue`, `DeferredAction`, `DeferredTake` | `cwy.defer`'s queue: released when the room is quiet for 1 s; 16 per scene, 64 total, 15-minute expiry |
| `ScenarioLint`, `ScenarioLintContext`, `ScenarioLintReport`, `ScenarioLintFinding`, `ScenarioGroupKey` | pack-scene lint, rules S01-S25 |
| `ScenarioSaveRule.IsPackScene` | a group a pack added never reaches the game's save — the pack-lines rule, for every pack scene |
| `ScenePlayPlanner`, `ScenePlayFacts`, `ScenePlayPlan`, `ScenePlayPath` | the rules of `scene play` |
| `ScenarioResult` | accepted / refused + one line (play, flags, defer) |

## Handler contract (enforced by `ScenarioCommandContext` / `ScenarioCommandRunner`)

Decide at most once (`Continue`, `JumpTo` or `WaitFor`); later calls are ignored with a warning.
Returning without deciding means `Continue`. A throw is logged against the owner and the scene goes on
with the next row (even if the handler had decided). A row whose number has no handler goes on with a
warning. An unknown `JumpTo` target warns and continues. `WaitFor` polls once a frame on the main thread;
at the timeout the scene goes on with a warning; a throwing check counts as done; a wait longer than
5 minutes is clamped. A scene that ends, is skipped or replaced while a row holds abandons the wait
silently (trace only).

## Lint rules (`ScenarioLint`)

A *pack scene* is a group a pack added. Groups with `Languages` are pack lines: only S20-S24 run
on them (the pack-lines screen checks the rest). Rows packs add to the game's own groups get S20-S25.

| Rule | Severity | Finding |
|---|---|---|
| S01 | error | no rows (the game reads row 0) |
| S02 | error | long story without Text/ChangeMotion/AddTextLoop/DirectionGameStartCamera |
| S03 | error | (Scenario, EpisodeNumber) shared with another pack scene or a game scene |
| S04 | warning | episode below 1000 (the mod range) |
| S05 | error | Extra/AprilFool/Birthday scene with a ContentType the game would offer by itself (use `SmallTalk_Cicada`) |
| S06 | warning | long pack scene of a type other than ExtraScenario |
| S10 / S11 | error / warning | WaitInput, Tutorial stall / ReadNextForScript, tutorial commands |
| S12 | error | Selection/Jump/TutorialSelection/IfBetweenDateTime/StartTextLoop target not in the scene |
| S13 | error | missing LocalizationID (checked against the game only when its data is known) |
| S14 | error | an argument the game parses would throw (float/int.Parse, Enum.Parse, ParseExact) |
| S15 | warning | a branch the previous row falls into |
| S16 | warning | short scene starts story music/ambience and never stops it |
| S20 | error | script row while the seam is not installed (it would stall) |
| S21 | warning | script command nobody handles (yet) |
| S22 | error | a built-in's Arg1 its handler refuses |
| S23 | warning | `cwy.do`/`cwy.defer` names an unregistered action |
| S24 | warning | in a long story, a script row before the first line (never runs) |
| S25 | warning | pack row of a group nobody has |

Tests: `ScenarioCommandRegistryTests`, `MasterDataScriptCommandTests`, `ScenarioRuntimeTests`,
`ScenarioBuiltInsTests`, `ScenarioLintTests` (the last one lints the shipped packs and checks the save
rule against the pack-lines save filter).
