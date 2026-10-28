# Satone.Say

One call that makes Satone speak a line the game never shipped — and lets the *game* perform it.

```csharp
using CwyLs.ModLoader.Bulbul.Satone.Motion;

// text only: the story text box, no voice
Satone.Say("Welcome back.");

// a clip of your own, the text for a pack language (by its code),
// an expression and a motion
var handle = Satone.Say("Hello from the Mod Loader.", new SayOptions
{
    Clip    = myClip,                 // or: VoiceName = "Voice_CWY_Test"
    Facial  = "smile",                // motion-table name, alias or id
    Motion  = "wild001_good",
    LookScale = 1f,
    Texts   = { ["th"] = "<the line in Thai>" }
});

var outcome = await handle.Completion; // Spoken | FellBack | Cancelled | Rejected | TimedOut
```

`Satone.Say` returns as soon as the line is **queued**. It never throws: a refused line comes back
as a handle that is already completed, with `Outcome = Rejected` and a `Detail` that says why.
Safe to call from any thread — a background task, a file watcher, an async continuation.

## What the game does for you

The line becomes the same master-data rows one of the game's own writers would have authored, and
then `ScenarioReader` plays them. Everything below is the game's own code, not ours:

| | Where it comes from |
|---|---|
| The clip plays | `HeroineService.PlayVoice(voice, isMoveMouse, isStory: true)` |
| Her mouth moves | `HeroineVoiceController` sets the `Enable_Talk` animator bool for exactly as long as the clip runs |
| The text box types the line out | `IStorySystemUI.CurrentTextMessage().StartText(text)`, at the player's text speed |
| The line is in the player's language | `LocalizationMasterWrapper.Get(row, language)` reads our row like any other |
| The line waits for both the text and the voice | `ScenarioReader.CommandVoiceText` waits on `_isFinishedDisplayingText && IsFinishedVoice` (30 s timeout) |
| The scenario closes itself | our `EndScenario` row → `EndStory()` → `ScenarioEnded` on the event bus |
| The talk log, the unlock hooks, `IsPlayingScenario` | all see a normal scenario |

Three things the reader's `VoiceText` path does **not** read from the row — expression, body motion
and gaze — are applied by this feature just before the scenario starts, through the `Satone` motion API
(`../Motion/`), which
calls exactly the `HeroineService.ChangeHeroineAnimationForInteger` /
`ChangeHeroineFacialAnimation` / `ChangeLookScaleAnimation` the reader's own `ChangeMotion` command
calls. The values stay on the row as well, because that is where the game's writers put them.

## The rows

One line becomes four rows, the shape of the game's own `MotionScenario` groups (and of
`packs/cwy.example/data/*.json`):

| Row | Contents |
|---|---|
| `LocalizationData` `cwy_say_<n>_001` | `Ja` = `TextJa` (or the line), every other column = the line, `Voice` = the clip name or `""` |
| `NovelData` `cwy_say_<n>_001` | `Command = VoiceText`, `LocalizationID` = the row above, `Arg1 = "1"` to move her mouth, `BodyMotion` / `FacialMotion` / `LookScale` filled in |
| `NovelData` `cwy_say_<n>_end` | `Command = EndScenario`, every argument `0` (see below) |
| `ScenarioGroupData` `cwy_say_<n>` | `Scenario = MotionScenario`, `EpisodeNumber = 9000 + n`, everything else neutral |

**The end row carries no delay, on purpose.** It used to hold `Arg2 = 1.25`, copied from the
example pack, as if it were a tail pause. `ScenarioReader.CommandEndScenario` does nothing but
end the story, so no column of that row is ever read; and the speak row's `Arg2` would not have
helped either, because `CommandVoiceText` ignores it and moves on with no wait — unlike
`CommandText`, which honours it (game 1.17.3). A `VoiceText` group therefore closes as soon as the
clip finishes. The hold after a Say line comes from the restore instead (below); if a real tail
pause is ever wanted, the game's own way is a `CommandType.WaitForSeconds` row before the end row.

Ids are session-unique and monotonic (`SayIdGenerator`). The episode number starts at 9001 because
the game also looks a group up by `(ScenarioType, EpisodeNumber)` and its own `MotionScenario`
groups use 1..29. Rows are kept after the line is spoken — up to 500 lines, after which the oldest
line's rows are recycled.

`MasterDataLoader`'s three collections are public fields, so the rows are written directly rather
than through reflection. Which collection is there matters, though: after the master-data feature
(`../../MasterData/`) has injected, the
loader holds the injector's own `Dictionary`/`List`s and rows are added **in place** (which keeps
the injector's reference-identity check, and therefore its next baseline, correct). With that
feature switched off the loader holds the ScriptableObjects' own `Items` lists, so this feature copies them
once into collections of its own instead — appending to the assets' lists would survive a
`Dispose`/`Load` cycle. While it is enabled but has not injected yet, nothing is written at all
and the line simply waits a few frames.

## Handing her back

`ScenarioReader.EndStory()` restores **nothing** on Satone; the game's own restore
lives in `EndTidyingForStory` → `HeroineService.OnEndStory`, which only the story controller
reaches. A Say line calls `StartReady` + `StartVoiceTextScenario` directly, so without us her
expression would simply stay where the line put it — the smile Animator state has no outgoing
transition at all.

So: whenever `ApplyPerformance` actually changed something, the line is remembered, and
`Satone.RestoreDefaults(RestoreScope.All)` runs when it ends —

| Path | Trigger |
|---|---|
| scenario | `ScenarioEnded` for our group (the `EndStory` postfix; `_isPlayingScenario` is already false by then, so the restore's own scenario guard lets it through) |
| fallback | `LineEnded` matching the line's voice name — there is no scenario to end |
| abandoned / cancelled | the queue's `Abandon` step, and `Reset` when the room scene goes |
| backstop | `SatoneMotionHost.PostAfter(8 s)`, armed with the performance, in case an end event is ever lost |

Whichever fires first wins; the rest are no-ops, and a restore is skipped entirely when another
line has already started speaking, because that line owns her pose and will restore her itself.
A text-only line arms nothing: it changed nothing to hand back.

Both smoke checks also carry a `SmokeCheck.Restore` hook. `p2.say.fallback`'s takes the probe
line back out of the queue (which the check already does inline; the hook makes it
exception-safe). `p2.say.scenario`'s deliberately does **not** cancel its line — speaking is the
whole point of the check, and the line restores her when it ends — it only calls
`Satone.RestoreDefaults` for a line that completed without speaking, and so would never restore.

## Busy policy

One line speaks at a time, and a line never interrupts the game. Once a frame the queue asks
whether a scenario could start: `RoomScene` loaded, rows writable, `ScenarioReader.IsPlayingScenario()`
false, and `HeroineService.IsPossibleTalk()` true (the same question `Satone.CanInteract` asks).

- **Free** → the head line starts through `StartReady(MotionScenario, groupId, Auto, isTalkLog: false, isUseMask: false)` + `StartVoiceTextScenario()`, the pair the game itself uses in `FacilityVoiceTextScenario`.
- **Busy** → the line waits. `Satone.SayBlockedReason` says what for.
- **Busy for longer than `Say.MaxWaitSeconds` (10 s)** and `Say.FallbackWhenBusy` (default on) → the line is spoken the simple way: `VoiceFeature.PlayAsSatone` (her mouth still moves). No story text box, no scenario motion, and nothing else draws the text, so on this path the text is only in the console's `SAY` line. The handle completes `FellBack`, and its detail says `voice only, no text box` (or, for a line without a voice, that it is only in the log).
- **Queue full** (`Say.MaxQueue`, default 8) → the newest line is refused; the queue never drops a line it already accepted.
- **A started line the game never reports ending** → abandoned after 120 s so one lost event cannot wedge the queue; the handle completes `TimedOut`.

`handle.Cancel()` works only while the line is still waiting — a scenario the game has begun is the
game's to finish.

## Text and translations

A Say line has a real localization row whose every game column holds the caller's text (`Ja`
holds `TextJa` when given), so the game's own story box types it like any story line, and
`LineSpoken` carries its id. In the game's own languages that is the whole story.

While a **pack language** is active (for example `th` from a language pack), the game's text seam
(`LocalizationMasterWrapper.Get`, see `../../Subtitles/`) asks the registered text tables for the
row in that language. `Texts` is registered as one of them (`SatoneSaySubtitles`), keyed by the
line's id, so the box shows the caller's translation; a line without one falls back along the pack
language's chain to the row's own text. `SayObservation` records those asks for the smoke check.

On the fallback path there is no text box at all, so no translation is shown either.

## Config (`Say.*`)

| Key | Default | What it does |
|---|---|---|
| `Enabled` | `true` | Off makes every call a refused handle and touches nothing. |
| `DefaultReadMode` | `Auto` | `Auto` closes the line itself; `Click` waits for the player. |
| `MaxQueue` | `8` | How many lines may wait. |
| `FallbackWhenBusy` | `true` | Speak without the scenario after `MaxWaitSeconds`. |
| `MaxWaitSeconds` | `10` | How long a line waits for the scenario path. |
| `LogEachLine` | `true` | Kept for the trace file's detail level; the console always gets exactly one `SAY` line per started line and one per refusal. |

Every line that starts writes **one** console line and publishes one `TraceBus` event under sys
`satone`, name `say`:

```
14:02:11.019  SAY  #7 "Hello from the Mod Loader."  via=scenario voice=Voice_CWY_Test facial=smile motion=wild001_good
```

Queuing and a normal `Spoken` finish are trace-file only — the terminal state is what the
`SPEECH` line already reports — and a line that did NOT speak gets a `SAY` line on the warning
channel with the reason. The expression, motion and gaze the line applies are file-only as well:
this line already names them. So one `Satone.Say` costs `SAY` + `SPEECH`, plus `SUBTITLE` when a
pack language's translation reached the box.

## Speaking your own clip

```csharp
AudioClip clip = LoadMyClip();                        // any AudioClip your mod made or loaded
var handle = Satone.Say("Time for a short break?", new SayOptions
{
    Clip   = clip,                                    // registered as CWY_Say_<n>
    Facial = "smile",                                 // "smile", "shy_joy", …
});
if (await handle.Completion != SayOutcome.Spoken) { /* she was busy; the line still played */ }
```

Register the clip yourself first (`VoiceFeature.Register(name, clip, "mymod")`) and pass
`VoiceName = name` instead when the same clip is reused across several lines.

## Smoke checks

Both are **loud** — they make Satone speak, so only an explicit trigger (the smoke hotkey or
`BepInEx/smoke.run`) runs them.

- `p2.say.scenario` — says the sample line with `smile` and `wild001_good`. When the game is showing a pack language it also attaches a translation for that language, tagged with its code (`[th] Hello from the Mod Loader.`), so the box proves the line's own table served it; in one of the game's own languages it attaches none. It asserts the request was accepted and its rows are in `MasterDataLoader`, and reports the **previous** pass's verdict (did the voice start; did the text seam ask for the pack language and get the line's translation). It borrows the example pack's **clip** and lets the line register it under its own generated `CWY_Say_<n>` name, rather than reusing `Voice_CWY_Test`: that name is also played by `p1.voice.play` in the same loud pass (which made one Say line look like two spoken lines) and is claimed by the example pack's own `cwy_example_01_001` row in the reverse voice index. No line deliberately collides any more; the attribution regression is covered by the scenario-row resolution in `SpeechAttribution` instead.
- `p2.satone.restore` (in `../Motion/`) — proves the restore end to end; `p2.say.scenario` relies on the same restore to leave her face neutral after the line.
- `p2.say.fallback` — queues a probe line and cancels it again, proving the queue admits and releases a line without speaking it; reports whether the game was busy, the configured fallback policy and the session's counters.

## Files

| File | What it is |
|---|---|
| `Satone.Say.cs` | The public API: the `Satone` partial class's speaking half. |
| `SayOptions.cs` | The engine-facing options (the only place an `AudioClip` appears). |
| `SatoneSayFeature.cs` | Config, queue, the two paths, events, logging. |
| `SatoneSayRows.cs` | Request → the game's master-data rows, and how they get in. |
| `SatoneSayHost.cs` | The main-thread marshal and the queue's frame tick, on the shared dispatcher (`CwyLs.ModLoader.Threading.MainThread`, owner `satone.say`). |
| `SatoneSaySubtitles.cs` | `ISubtitleSource` over the live lines. |
| `SayObservation.cs` | What the bus and the text seam saw, for the next smoke pass. |
| `SatoneSaySmokeChecks.cs` | The two in-game checks. |
| `../../../CwyLs.ModLoader.Core/Satone/Say/` | `SayRequest`, `SayLine`, `SayQueue`, `SayHandle`, `SayIdGenerator` — Unity-free and unit-tested (`SatoneSay*Tests`). |
