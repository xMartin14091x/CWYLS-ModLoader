# Satone motion — named expressions, motions and look control

`Satone.Face("smile")` instead of `ChangeHeroineFacialAnimation(10)`. The names come from
`CwyLs.ModLoader.Core`'s embedded table (`Satone/satone-table.json`), which lists the game's own
facial, motion and Animator state names (game 1.17.3); this folder is the thin Unity half that calls the game.

Wire-up is one call from the adapter plugin's `Awake`:

```csharp
SatoneMotionFeature.Initialize(Config);   // binds Satone.*, arms the host, registers the checks
```

## The API

```csharp
using CwyLs.ModLoader.Bulbul.Satone.Motion;

Satone.Face("smile");                       // expression by name
Satone.Face("shy_rest");                    // ... by FacialType alias
Satone.Face(10);                            // ... by id
Satone.FaceReset();                         // back to neutral

Satone.Motion("wild001_stretch_shoulder");  // motion by name
Satone.Motion("stretch_shoulder");          // ... by short alias
Satone.Motion("Wild001_Motion2_StretchShoulder");  // ... by the game's own enum name
Satone.Motion("work_base002_page_flip", trigger: true);

Satone.LookAt(1f, 0.4f);                    // turn towards the player over 0.4 s
Satone.LookReset();                         // hand the gaze back to the game

Satone.RestoreDefaults();                   // put her back: expression, motion and gaze
Satone.RestoreDefaults(RestoreScope.Face);  // ... or one channel

using (Satone.FaceScoped("smile")) { ... }  // smile for the block, restored on dispose
using (Satone.Scoped(RestoreScope.All)) { ... }

Satone.Capture();          // SatoneSnapshot: facial, motion, look, state, right now
Satone.LastSnapshot;       // what she looked like before our last call
Satone.TouchedScopes;      // which channels we have changed since the last restore
Satone.LastRestore;        // RestoreResult of the last RestoreDefaults

Satone.State;              // "work_pc"                 (HeroineAI.ActionStateType, named)
Satone.CurrentMotion;      // "wild001_stretch_shoulder" (AnimationType, named)
Satone.IsAnimationEnded;   // HeroineService.IsEndAnimation(0)
Satone.CanInteract;        // HeroineService.IsPossibleTalk()
```

Every method returns `true` when the request was **accepted** — the name resolved and the call
either ran (main thread) or was queued for the next frame. It never means "Satone visibly did it":
the Animator needs a frame or two, and that verdict arrives later (see *Confirmation*).

An unknown name never throws. It logs

```
[Warning:CWYLS Game Adapter] SATONE unknown motion 'strech_shoulder'. Did you mean: stretch_shoulder, wild001_stretch_shoulder?
```

and returns false. Suggestions come from `SatoneTable.Suggest`: prefix match, then substring, then
within three edits.

## Namespace note

The public facade is `CwyLs.ModLoader.Bulbul.Satone.Motion.Satone`. It cannot live in
`CwyLs.ModLoader.Bulbul` — a namespace `CwyLs.ModLoader.Bulbul.Satone` and a type of the same name in the
same parent are a compile error, so the feature's namespace is
`CwyLs.ModLoader.Bulbul.Satone.Motion`. The class is declared `partial`, so `Satone.Say` can be
added from its own file in this namespace without either file knowing about the other.

## Seams

| What | Where | Note |
|---|---|---|
| Expression | `HeroineService.ChangeHeroineFacialAnimation(int)` | → `HeroineAI.ChangeFacial` → `HeroineFacialController` → `Animator.SetInteger("Facial", n)` |
| Expression reset | `HeroineService.InitHeroineFacial()` | `ChangeFacial(0)` |
| Expression reset (delayed) | `HeroineService.InitHeroineFacialAfterDelay(float)` | the reset every game path pairs with a facial change; the delay is `MyDefine.InitFacialDelaySecondsAfterTalk` = 0.77 s |
| State re-dispatch | `HeroineService.DebugChangeState(ActionStateType)` | → `HeroineAI.DebugChangeState` → `ChangeState` → `_actionStateMachine.Dispatch((int)state)` — the identical dispatch `HeroineAI.FinishHeroineClickReaction` makes |
| Motion | `HeroineService.ChangeAnimation(AnimationType)` | records before/next, then `ChangeHeroineAnimationForInteger`, which sets `MotionType` and, for 22 ids, also a trigger |
| Motion (trigger) | `HeroineService.ChangeHeroineAnimationForTrigger(AnimationType)` | only for ids the table marks `trigger: true` |
| Look | `HeroineService.ChangeLookScaleAnimation(float, float, Ease)` | **reflected** — see below |
| Look reset | `HeroineService.LookInitSlowly()` | `PlayFromLook(1.5s)`, after a 0.5 s delay |
| Current motion | `HeroineService.GetCurrentAnimationType()` | the value the game last stored, not the clip on screen |
| Current state | `HeroineService.GetCurrentAIState()` | `HeroineAI.ActionStateType` |
| Animation finished | `HeroineService.IsEndAnimation(int)` | |
| Confirmation | `HeroineService.IsCurrentAnimation(string)` | `MyAnimatorUtil` → `AnimatorStateInfo.IsName`, so the string is an Animator **state** name — the table's `state` field |
| Interruptible | `HeroineService.IsPossibleTalk()` | asks the current AI state whether it accepts a click reaction |

All of them as declared by game 1.17.3.
`SatoneMotionTargets` registers the eight of these that `BulbulPatchTargets` does not already
carry, so a game update that renames one says `PATCH TARGET MISSING` at startup instead of turning
`Satone.CanInteract` quietly false forever.

### Two reflected seams, and why

**`ChangeLookScaleAnimation`** has a DOTween `DG.Tweening.Ease` parameter with a default. Calling
it in C# — even leaving the optional argument out — makes the compiler materialise that default,
which would drag a DOTween reference into this adapter for one argument that is always
`Ease.Unset`. `SatoneSeam` looks the method up once and builds the argument from the parameter's
own enum type, so a DOTween version change cannot desynchronise it.

**`UnityEngine.Animator`** lives in `UnityEngine.AnimationModule`, which this adapter does not
reference (`UnityEngine.dll` only type-forwards it). Reading `MotionType`, `Facial` and `LookScale`
back off the Animator is what turns "we called the method" into "the game holds the value", and it
is the *only* evidence the facial path can produce — nothing on `HeroineService` exposes the current
expression. The type is resolved by name at runtime. Read-back is a diagnostic: when it is
unavailable the API still works and simply reports less.

## Threading and the host

`SatoneMotionHost` runs on the framework's shared main-thread dispatcher (`CwyLs.ModLoader.Threading.MainThread`;
its hidden `CWY.MainThread` host survives the game destroying BepInEx plugin components
about 0.2 s after boot). Its queued and delayed calls use the owner name `satone.motion`,
and its per-frame work (observation, state cache) is one ticker on that host.

Every game call goes through `SatoneMotionHost.Post`. On the main thread it runs immediately, so
`Satone.Motion(...)` stays synchronous for the common case; from any other thread — a background
task, an async completion, a file watcher — it is queued and runs on the next frame (within the
dispatcher's 4 ms frame budget). Unity's API is main-thread-only, so this is not optional. The
`satone.motion` owner may have 256 calls waiting and 64 delayed; anything past that is dropped with
a warning and counted in `SatoneMotionHost.Dropped`. A call that throws is logged as
`MAINTHREAD satone.motion '<label>' threw …`.

The read-only properties work the same way: live values on the main thread, and the host's
periodic snapshot (`SatoneStateCache`, refreshed every 15 frames) anywhere else. Nothing is polled
at all until something actually reads from another thread.

## Confirmation

The call is synchronous; the effect is not. Each request arms one `SatoneObservation`, which the
host advances every frame for `SatoneMotionFeature.ConfirmSeconds` (default 2 s):

| Kind | Confirmed when |
|---|---|
| `motion` | `IsCurrentAnimation(<table state>)` — the Animator really entered that state |
| `face` | the Animator's `Facial` parameter equals the id |
| `look` | the Animator's `LookScale` is within 0.05 of the target |

The verdict is logged and published on the trace bus (`sys: satone`, `phase: observed`). A motion
or face timeout is a warning. A **look** timeout is not: while the game is playing a click or
short-talk scenario, `ChangeLookScaleByManualForStory` takes a branch that drives a private weight
and never writes the Animator parameter, so an unobserved look there is expected, not news.

Only the newest request is watched — `SatoneMotionHost.LastObservation`.

### Known gap: 13 motions cannot be confirmed

Thirteen of the 181 ids carry `state: null` in the table, so a request for one of them is settled
`Unobservable` rather than watched: **0, 151, 402, 403, 404, 405, 800, 802, 1724, 1726, 1729,
1730, 6500** (`Base001`, `Wild003_LeaveChair_Sofa`, the three `Desk_Click_*_Reaction`s,
`Enable_Talk`, `BreakBase005`, `BreakBase005_GetUpSlowly`, four `Story_SubBase008_02_*`,
`GameStart_Base_001`).

They are still *playable* — `Satone.Motion("base001")` sets `MotionType` exactly like any other id
— but the table cannot say which Animator state they land in. Twelve of them are reached
through range conditions (`MotionType > n` / `< n`) inside sub-state machines, which the table
does not model; id 0 is the opposite case, two states are both called `Base001` and the table
does not guess. Accepted as a known limit.

## Logging

One console line per accepted call (`Satone.LogEachCall`, default true):

```
14:02:11.019  FACE  smile  id=10
14:02:11.019  MOTION  wild001_stretch_shoulder  id=51
14:02:11.019  RESTORE  face in 0.77s, motion re-dispatched (WorkTyping), gaze reset  scope=face+motion+gaze state=WorkTyping gaze=true detail=-
```

One publish per call, with the tag, the primary column and the field order named at the emit
site. The **observation** (`Confirmed after 183 ms`) is trace-file only — the console already showed
the request — except a timeout, which is a `WARN` line. A call `Satone.Say` makes for itself is
file-only too: its `SAY` line already printed `facial=` and `motion=`.

The `TraceBus` event carries the full payload either way, with `sys: satone` and
`m: face|motion|look|restore`, `phase` (`called`, `observed`, `unknown`), the name, the id, and the
state/clip/category. `Satone.LogEachCall=false` now means "trace file only" rather than "one line
instead of two".

## Config

| Key | Default | Meaning |
|---|---|---|
| `Satone.Enabled` | `true` | Master switch. False makes every `Satone.*` call a no-op returning false. |
| `Satone.LogEachCall` | `true` | One `FACE`/`MOTION`/`LOOK` console line per accepted call. False = trace file only; the bus event is published either way. |
| `Satone.ConfirmSeconds` | `2` | How long a request is watched for its effect before it is reported as a timeout. |

## Smoke checks

| Id | What it proves |
|---|---|
| `p2.satone.table` | The table is embedded, parses, and is the real one: ≥20 facials, ≥100 motions, and the names the API documents resolve. |
| `p2.satone.face` | `Satone.Face("smile")` leaves the Animator's `Facial` parameter at 10. |
| `p2.satone.motion` | `Satone.Motion(...)` leaves `GetCurrentAnimationType()` at the requested id, and arms the state watcher. Rotates through three short wild idles, so three explicit triggers play three different motions. |
| `p2.satone.look` | The reflected look seam bound and accepted a request. |
| `p2.satone.restore` | End to end: snapshot → `Satone.Face("smile")` → `RestoreDefaults(All)` → read back. The verdict is asynchronous and reported by the **next** pass, like the observations. |

The last four move Satone on screen, so they are registered with `loud: true`: an
automatic pass reports them as skipped, and only an explicit trigger — the smoke hotkey or
`BepInEx/smoke.run` — actually runs them. `p2.satone.table` only reads data, so it stays quiet and
runs on every pass.

All four skip — pass, with a `skipped: ...` detail — when `HeroineService.IsPossibleTalk()` is
false (asleep, mid-scenario, leaving the chair) or the room scope is not resolvable, rather than
shoving a motion into a state machine that did not ask for one. Each also reports the *previous*
request's observation, so a pass names what the Animator did last time.

Each loud check's body is wrapped in `try`/`finally`, and the `finally` schedules
`Satone.RestoreDefaults` through `SatoneMotionHost.PostAfter(0.5 s, ...)` — so an early `return`
or a thrown exception still hands her back, and the detail line says `restore scheduled in 0.5 s`.
The same scheduler is also handed to the registry as the `SmokeCheck.Restore` hook, which the runner
calls in its own `finally`; it is idempotent per pass (the check arms exactly one restore and
whichever `finally` gets there first disarms it), so the two together schedule one restore, and a
check that changed nothing schedules none. The hook is the net for a body that never reached its
own `finally` at all.

The restore is *scheduled*, not run inline, on purpose: the registry hook fires microseconds after
the body, and restoring the motion there would re-dispatch her state before the one-shot clip had
a single frame to play — the async observation would then always time out.
Half a second is long enough for the observation watcher to see the change first (the Animator
reaches the requested state in a few frames) and short enough for the restore target,
"facial/motion/look return to the pre-run values within 1 s"; a one-shot wild clip is therefore cut
off part-way, which is the deliberate trade.

## Restore

The game has **no automatic expression reset**. `HeroineFacialController`'s own state machine is
inert, and the smile Animator state has no outgoing transitions — once `Facial = 10` is set she holds
the last frame of that clip until someone sets it back. Every game path that changes a facial
pairs it with an explicit reset; the gaze is the same; only the body motion self-heals, and only
after a random delay that is serialized in the prefab rather than visible in the game's code.

`Satone.RestoreDefaults(scope)` is the game's own triple, in the order
`RoomGameManager.OnEndPlayedReaction` runs it:

| Channel | Call | Timing |
|---|---|---|
| gaze | `HeroineService.LookInitSlowly()` | 0.5 s delay, then a 1.5 s ease-out |
| expression | `HeroineService.InitHeroineFacialAfterDelay(0.77f)` | `Facial = 0` after the game's own delay |
| motion | `HeroineService.DebugChangeState(GetCurrentAIState())` | immediate; re-enters the state, so its `OnEnter` re-arms the base loop |

Three things are deliberate:

* **Motion is restored by re-dispatching her state**, not by `ChangeAnimation(Base001)`. Only the
  state's `OnEnter` knows which loop belongs to it — `WorkPC` wants 200, `BreakMovie` 600,
  `BreakReadBook` 650 — and `Base001` is what the game uses when it is *parking* her.
* **The 0.77 s is read out of the running game**, not referenced: `MyDefine.InitFacialDelaySecondsAfterTalk`
  is a `const`, so a C# reference would inline it into our DLL and a retune in a game update would
  be invisible. `SatoneSeam.FacialResetDelaySeconds` reads the literal by reflection and falls back
  to 0.77 with a warning.
* **Nothing here arms an observation.** The restore is meant to land *after* the change it undoes
  has been watched, and only the newest observation is kept, so watching here would erase the
  evidence for the call being undone.

### Which states may be re-dispatched

`SatoneRestorePolicy` (Core, unit-tested) is an allow-list, because `Dispatch` re-enters a state
and re-runs its `OnEnter`:

* **allowed** — `work_pc`, `work_book`, `work_report`, `break_movie`, `break_read_book`,
  `break_listen_music`, `break_tea_time`, `break_sleep`, `break_forward`. Exactly
  `HeroineAI.GetCurrentStateActionType`'s `Work` + `Break` members minus the
  three `LeaveChair*` states.
* **directed, never re-dispatched** — `none`, `game_start_direction`, `want_talk`, `main_story`,
  `click_heroine`, `leave_chair_preparing`, `leave_chair_go_sofa`, `leave_chair_go_far`,
  `game_end_talk`, `event_new_year_countdown`. Re-entering any of these restarts a sequence: the
  opening direction, the want-talk bubble and its R3 event, the walk to the kitchen, the end talk,
  the countdown.
* **everything else** (the wild idles, the desk-to-desk transitions) — refused too, because
  re-entering replays a one-shot clip instead of restoring a loop.

When the motion is refused the expression and the gaze are still restored, and the reason travels
out in the result and the log line.

### Guards

* A restore is a **no-op while the game's own scenario reader is mid-scenario** and the scenario is
  not one of ours (`ScenarioReader.IsPlayingScenario()`, group id tested with
  `SayIdGenerator.IsSayGroup`) — otherwise we would wipe a facial the reader just set.
* The AI is asked whether it is between action changes before the re-dispatch
  (`HeroineAI.IsPossibleChangeAction()`, the same test the game makes in
  `FinishHeroineClickReaction`). The field
  that reaches it is private, so this is best-effort: when it cannot be read the restore goes
  ahead, because an unavailable diagnostic must not block the fix for a stuck pose.
* `LookInitSlowly` silently no-ops while a previous ease is still running
  (`HeroineLookDirectionService`). Harmless — the gaze is already on its way back.

### One line, one owner

```
SATONE restore face=in 0.77s motion=work_pc gaze=easing
SATONE restore face=in 0.77s motion=skipped(click_heroine: the game leaves this state through FinishHeroineClickReaction (HeroineAI.cs:1193)) gaze=easing
SATONE restore nothing: the game is playing Tutorial tutorial_01, which owns her pose (scope all)
```

That single line is the console owner for a restore; the same payload goes on the trace bus as
`sys: satone`, `m: restore`, with `phase` = `done` / `refused` / `queued`.

### Delayed work

`SatoneMotionHost.PostAfter(seconds, label, work)` schedules main-thread work on the shared
dispatcher (`MainThread.PostAfter`, owner `satone.motion`), which runs it on the first frame after
it is due. The delay is measured on the dispatcher's `Stopwatch`, not `Time.realtimeSinceStartup`,
so it can be called from any thread and is unaffected by `Time.timeScale`. It holds 64 calls;
`CancelDelayed(reason)` drops them all (only this owner's; other features' delayed work stays).
