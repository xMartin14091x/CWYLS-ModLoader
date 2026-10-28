# Scenario — script commands in pack scenes

A pack scene (a scenario group a content pack adds, `../MasterData/README.md`) can use the game's 50
script commands **and** CWY script commands: rows whose `Command` is a dotted name such as `cwy.do`,
numbered 5000-5999. The game's reader would stall on them; this feature runs them. It also keeps pack
scenes out of the game's save, lints pack scenes, and gives the console a `scene` command.

Entry point: `ScenarioFeature.Initialize(Config)`, called once from `BulbulPlugin.Awake` after
`MasterDataFeature.Initialize`. Pure logic (contract, runner, waits, action lines, flags, deferred queue,
lint, play rules) is in `CwyLs.ModLoader.Core/Scenario/` with unit tests (`src/CwyLs.ModLoader.Tests/Scenario*Tests.cs`).
Number ranges and how framework features add `cwy.*` commands: `../../CwyLs.ModLoader.Core/Scenario/README.md`.

## 1. Writing a script row

A row in `data/novel.json`, with the command's words in `Arg1` (the other columns keep their defaults):

```json
{ "ID": "my_scene_040", "ScenarioGroupID": "my_scene", "Command": "cwy.wait", "Arg1": "3" }
```

| Command | Arg1 | What happens |
|---|---|---|
| `cwy.do` (5000) | `<action> [name=value …]`, e.g. `music.play title="Rainy Desk"` | runs the game action now through the action runner (caller `Script`, caller id = the scene's group); the scene holds until it answers (≤ 30 s), then goes on. One `SCRIPT` line with the answer (WARN when not Ok) |
| `cwy.defer` (5001) | the same | queues the action; it runs once the scene has ended, the room's main state is `Idle` (a long story's fade and tidy are done) and both held for 1 s. ≤ 16 per scene, dropped (WARN) after 15 min without a quiet room |
| `cwy.wait` (5002) | seconds, `1.5` (invariant), 0 < s ≤ 300 | holds the scene; a skip or end stops the wait. Unlike the game's `WaitForSeconds`, it ends with the scene |
| `cwy.flag` (5003) | `set <name>` / `clear <name>` | sets or clears a flag (1-64 of `A-Z a-z 0-9 _ . -`, case-insensitive) that `flag:<name>` conditions read; kept in `BepInEx/data/script/flags.json`, never in the game's save |

Actions available to `cwy.do`/`cwy.defer`: everything in the action registry (`act list`). This feature adds one: **`scene.play group=<id>`** (tier Act) plays a pack scene, so
`cwy.defer scene.play group=my_scene_02` chains scenes. A row a command cannot use (bad `Arg1`, unknown
action, no handler) warns once and the scene goes on: a broken row never stalls a scene.

Where a row runs: a short scene reads every row from the first. A **long** story (Main/After/DLC/Extra/
Special) starts at its first Text/ChangeMotion/AddTextLoop/DirectionGameStartCamera/WaitForSeconds row;
rows before it are only pre-scanned by `ScenarioReader`, so a script row there never runs
(lint S24).

## 2. Playing a pack scene

| Way | Notes |
|---|---|
| console `scene play <groupId>` (or a row id of it) | the authoring aid; the scene starts from its beginning. Also the game's own click lines by group id (`scene play <clickGroupId>`: types 8-10, 500-503, 550-553, played as a click-style talk, nothing saved; handy for side-by-side captures of one line); and the game's own story episodes by group id (`scene play main_05`): an episode already seen replays exactly as the game's history screen does (`StartStory(type, episode, isTalkLog: true)`, as `StorySelectUI` calls it); an unseen one only with `scene unseen on` (`[Script] AllowUnseenGameStories`), which backs the save folder up to `BepInEx/data/backups/save-scene-*` (newest 10) first and skips its played log, so it stays unseen |
| action `scene.play group=<id>` | from `cwy.defer`, `act run` or the local API |
| `helper.Scenes.Play(groupId)` | a code mod |
| triggers | planned (`data/triggers.json`) |

Rules (`ScenePlayPlanner`): only scenes a pack added, plus the game's own click lines (no other game scene, so
its progress and save stay its own); no lint errors; not Tutorial; the room loaded, no scene playing, `RoomGameManager.CurrentMainState
== Idle`, and Satone able to talk (`HeroineService.IsPossibleTalk`). Long story types go through the game's
own pipeline — `IStoryController.StartStory(type, episode)`, exactly what her "wants to talk" click does
(fade, music pause, UI, Skip button, tidy, window/decoration restore) — and need the save guard. Every
other type plays as a click-style talk: `StartReady(…, isUseMask: false)` + `StartClickHeroineReaction()`
(as `FacilityClickHeroine` does), then `EndClickHeroineReaction()` and her look/face reset one frame
after it ends (as `RoomGameManager.OnEndPlayedReaction` does).

**A long pack scene should be an `ExtraScenario` with episode ≥ 1000 and `"ContentType": "SmallTalk_Cicada"`.**
Pack scenes are never logged as seen (section 3), and the game offers every unseen Extra scene whose ContentType
is `None` (or a purchased `PurchaseEnvironment`) through her "wants to talk" state
(`ScenarioGroupMasterWrapper`, `HeroineAI`) — it would come back again and again. Any
other ContentType keeps the game from offering it; lint S05 enforces this.

## 3. Save safety

`FacilityStory.SaveScenarioPlayedLog` is what a long story's end writes:
main-story progress (`FinishReadEpisode`), collaboration progress (`SpecialService.OnEndStory`) and the
group id in `ScenarioProgressData.PlayedScenarioGroupIDs`, which Steam Cloud syncs. A prefix skips the
whole method for a pack scene (`ScenarioSaveRule.IsPackScene`: a group id the game's own data does not
have) and prints `SCRIPT <id> not logged as played`. This is the pack-lines rule (`PackLineSaveFilter` keeps
pack-line ids out of the same list at `SaveScenarioProgressData`) applied to the story path; every pack
line is a pack scene to it, which `ScenarioLintTests` checks. A vanilla game loads the
save unchanged in meaning. Click-style talks are never logged by the game. Flags live in our own file.

## 4. Lint

After every master-data injection the installed packs are linted (`ScenarioLint`, rules S01-S25 in the
Core README): one `SCRIPT lint: …` line, every error as a WARN (≤ 20), warnings in `scene lint`. A scene
with errors cannot be played with `scene play`. Groups with `Languages` (pack lines) get only
the script-row rules; the pack-lines screen checks the rest. While the feature is off or has no seam, every script
row is an S20 error (it would stall the game's reader).

## 5. How rows run (the seam and its fallback)

| | Seam (normal) | Fallback (seam missing after a game update) |
|---|---|---|
| Where | prefix on the private `ScenarioReader.ReadSentence(int)` (the `async UniTaskVoid` stub): for a 5000-5999 row it sets `_currentMessageIndex`/`_currentNovel` (as the original does), runs the handler and returns false | after each injection script rows become `ReadNextForScript` (command 50) rows; a 4 Hz poll runs the handler when the reader sits on one |
| Continue | the reader's own `ReadNext()` | `ReadNextForScript()` |
| JumpTo | `SelectionJump(row)`, like `Jump` and `IfBetweenDateTime` | not available (WARN, continues) |
| WaitFor | a per-frame poll; the scene must still be at the row | same |
| Side effects | none | the game hides the text box during the row and re-enters Satone's click state when it resumes |

A chain of more than 24 script rows in one frame continues on the next frame, so a loop of script rows
cannot overflow the stack. If neither path can be installed the feature is off with one WARN and
`ScenarioCommandRegistry.Shared.RuntimeAvailable` stays false.

## 6. Config, console, smoke, trace

| Item | Value |
|---|---|
| Config | `[Script] Enabled = true` (adapter .cfg). Off: no row runs (lint reports them), no guard, no `scene play`. `[Script] ForceFallback = false`: testing only, runs rows through the fallback although the seam is available (the final pass uses it once) |
| SETUP | `SETUP  script commands ready  src=script mode=seam (ScenarioReader.ReadSentence prefix) guard=on commands=cwy.do,cwy.defer,cwy.wait,cwy.flag,scene.play flags=0 actions=runner` |
| Console lines (tag `SCRIPT`) | `lint: …`, `playing <id> (…)`, `<id> not logged as played …`, `cwy.do <action> -> <result>`, `cwy.defer <action> queued; …`, `flag <name> set`, WARNs |
| Console command | `scene list` · `scene lint [groupId]` · `scene play <groupId|rowId>` · `scene flags` |
| Smoke (quiet) | `es.script.seam`, `es.script.builtins`, `es.script.guard`, `es.script.lint` |
| Smoke (loud) | `es.script.demo` — plays `cwy_demo_script_01` (pack `cwy.demo.script`) |
| Trace (file only, sys `script`) | `row`, `wait`, `waitEnd`, `moved`, `nextFrame`, `deferRun`, `answerDropped`, `play`, `playRefused`, `playEnd`, `guard`, `lint`, `fallbackRows`, `patched`, `reset`, `warn`, `flagsReplaceFallback` |
| Patch targets | `ScenarioPatchTargets.All` (28: seam 6, fallback 3, save guard 3, room 16), registered by `Initialize` as `CWYLS Game Adapter (script)` |
| Harmony | id `cwyls.modloader.bulbul.script` (two prefixes); independent of the language voice sets' `StartReady` postfix and the pack-lines save filter |
| Facet | `helper.Scenes`: `RegisterCommand`, `TryGetCommandId`, `Play`, `IsFlagSet`, `SetFlag` (backend `ScenesFacetBackend`) |

Privacy: lines name actions, never their arguments.

## 7. Known limits

- A pack scene is "unseen" to the game forever; anything that needs "played once" keeps its own record
  (e.g. a flag).
- A click-style talk started by `scene play` does not lock the timer menu unless its type is a "middle
  talk" (SmallTalk/AprilFool/Birthday), exactly like the game's own clicks.
- `cwy.do` holds the scene up to 30 s; an action still running then finishes on its own and its answer is
  logged when it arrives.
- Plain pack small talk (a pack `SmallTalk` group **without** `Languages`) that the game's lottery draws is
  added to the played list by `SmallTalkSelector.OnEndTalk`, which neither this guard (story path only) nor
  the pack-lines filter (pack lines only) covers today.
