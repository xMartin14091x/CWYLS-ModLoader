# Pack lines - new Satone lines for one language

A content pack can add new voiced lines to the game's own pools - the greeting right after the
startup screen, clicks on Satone, her self-talk, the pomodoro and count-up timer lines, the
after-pomodoro answer-choice talk and small talk - that the game offers **only while one of the
line's languages is shown** (a line marked `["th"]`: only while ไทย is selected). In every other
language the pools are the game's own: same episodes, same odds, same random draws, and nothing
reaches the save.

A line is **specific** (written for one pool) or **global** (`"Pool": "*"`: written once, offered in
every pool a single line can fill). Either can carry a **`Weight`**, the chance it takes a pick of its
pool (§5): `"Weight": 1` makes it play every time.

Entry point: `PackLinesFeature.Initialize(Config)`, called once from `BulbulPlugin.Awake` after
`MasterDataFeature` and `LanguageFeature`. Pure logic (the screen, the gate, the save filter, the
small-talk gate, the report) is in `CwyLs.ModLoader.Core/PackLines/` with unit tests
(`src/CwyLs.ModLoader.Tests/PackLines*Tests.cs`); this folder is the game side.

## 1. Marking a line

A pack line is an ordinary scenario group in the pack's `data/scenario_groups.json`, with its rows in
`data/novel.json` and `data/localization.json` (format: `../MasterData/README.md`), plus two CWY
members that the game never sees:

| Member | Required | Meaning |
|---|---|---|
| `Languages` | yes | Array of language codes the line is offered in, e.g. `["th"]`. Any spelling of a code works (`TH`, `tha`, `Thai`). A game language is allowed too (`["ja"]` = Japanese only). An empty array, a non-array or a blank code refuses the row. |
| `Pool` | for the clock and worked-time self-talk, and for spreads | Which pool the group joins (table below). For every other scenario type the pool follows from `Scenario`; if you write `Pool` anyway it must match. Two values spread one line over many pools: `"gamestart.any"` (every startup greeting kind) and `"*"` (a global line: every pool except `pomodoro.end`, which needs answer choices). A spread line writes `"Scenario": "None"`; the loader makes one copy per pool (below). |
| `Weight` | no | The chance, from 0 to 1, that this line takes a pick of its pool: `0.25` = a quarter of the picks, `1` = every pick (§5). A JSON number, not a string. Absent: one more card in the game's deck (an equal share with the game's lines for a startup greeting). |

Rules the screen (`PackLineScreen`) checks before anything is injected - a group that breaks one is
skipped with **one WARN** naming the row (`PACKLINES skipped scenario_groups row '<id>': <reason>`),
and the rest of the pack still loads:

1. `Scenario` is one of the pool types below; for `HeroineSelfShortTalk_CurrentTime` and
   `HeroineSelfShortTalk_WorkedTime` a `Pool` names the slot.
2. `EpisodeNumber` is a whole number from **1000** to 16777216, unique per `Scenario` across all
   packs (the game's own episodes are 1-23; 1000+ can never collide with them).
3. The group id is new (a pack line cannot override a game group).
4. It has at least one novel row, and at least one spoken row (`Text`, `VoiceText` or
   `PomodoroVoiceText`) with a `LocalizationID`; every `LocalizationID` other than `"0"` exists (in a
   pack or in the game).
5. Arguments the game parses parse: `Text` `Arg1` is a plain number (`"0"`, `"1.5"`) and `Arg3` a
   whole number; `VoiceText` `Arg1` and `PomodoroVoiceText` `Arg1`/`Arg3` are whole numbers.
6. Timer lines (pomodoro/count-up pools) have a `PomodoroVoiceText` row: the game looks for one
   (`HeroineAI`). In a click-style pool a `PomodoroVoiceText` row needs a `BodyMotion` (its voice
   waits for the motion's cue) or `Arg2` `-1`.
7. No `WaitInput`, tutorial or `ReadNextForScript` command (they stall a short talk); a `Selection`
   or `Jump` row's `Arg1` names a row of the same group.
8. A startup greeting has no `Selection` or `StartTextLoop` row: it reads in auto mode, and a choice
   would wait for the player.
9. `Weight`, when present, is a number from 0 to 1. A weighted small talk has `ContentType` `None` or
   `PurchaseEnvironment` (the roll does not apply the other types' own conditions).
10. Small talk: `PlayTiming` is `Break`, `GameStart` or `GameStartAndBreak`; `ContentType` is one of
   the game's five names (exact case); `Arg1`/`Arg2` are whole numbers 0-100; `Arg3` is a whole number
   for `SmallTalk_Cicada`/`SmallTalk_PassageOfTime`; `UnlockStoryEpisode` is `"None"` or a group id.
11. Every voice name the line speaks is a valid clip name (`[A-Za-z0-9_-]+`).

**Spread lines** (`"Pool": "gamestart.any"` or `"*"`). The loader copies the group into each pool:
group `<id>@<pool>` with that pool's `Scenario`, novel rows `<row>@<pool>`, the same `EpisodeNumber`,
`Languages`, `Weight` and localization rows. The clock slots share one `Scenario`
(`HeroineSelfShortTalk_CurrentTime`) and so do the worked-time slots (`HeroineSelfShortTalk_WorkedTime`):
there the first slot's copy keeps your `EpisodeNumber` and each later slot's copy gets the next free one
above it, so every slot has its own (a global line at 900002 is 900002-900005 in the clock slots). Each copy is screened as if written for its pool, and if
one copy fails, the whole line is refused with one WARN naming the pool. The group you wrote is not
injected itself. A global line's copies are adapted to the pool:
- **Timer pools.** A line with one spoken row speaks it as a `PomodoroVoiceText` row at once, with no
  motion and its face kept. A line with several spoken rows skips the timer pools, with one WARN.
- **Small talk.** The copy gets `PlayTiming` `Break`, `ContentType` `None`, `Arg1`/`Arg2` 25/5 unless
  your group already has valid small-talk columns.

A spread line claims its `EpisodeNumber` in every pool it joins (a global line also up to 3 more in the
clock slots and 2 in the worked-time slots; never one another group writes), so give spread lines a
range of their own, for example 900000 and up in steps of 4. `lines list` shows each copy's episode.

The line's text for its language goes in the pack's `data/subtitles/<code>.json` (for Thai,
`data/subtitles/th.json`), keyed by `LocalizationID`; the game's own text box shows it.
Fill `En` in the localization row with an English gloss: it is the fallback text and what the
console's `SPEECH` lines print. The voice clip goes in the pack's language voice set
(`audio/voice/<code>/<Voice>.ogg`, see `../Voice/README.md`), which resolves only while that language
is shown.

## 2. The pools (game 1.17.3; startup pools 1.18.0)

| Pool | `Scenario` | Game range | Where the game keeps it |
|---|---|---|---|
| `click.normal` | `HeroineClickNormal` | 1-23 | `ReactionTalkSelector._normalClickShortTalkList` |
| `click.work` | `HeroineClickWork` | 1-23 | `ReactionTalkSelector._workClickShortTalkList` |
| `click.work.morning` / `.noon` / `.evening` / `.night` | `HeroineClickWork_Morning` / `_Noon` / `_Evening` / `_Night` | 1-5 each | `_workClickShortTalk{Morning,Noon,Evening,Night}List` |
| `click.break` | `HeroineClickBreak` | 1-20 | `_breakClickShortTalkList` |
| `click.break.morning` / `.noon` / `.evening` / `.night` | `HeroineClickBreak_Morning` / ... | 1-10, 1-5, 1-5, 1-11 | `_breakClickShortTalk{Morning,Noon,Evening,Night}List` |
| `selftalk.break` | `HeroineSelfShortTalkBreak` | 1-22 | `_breakHeroineSelShortTalkfList` |
| `selftalk.break.morning` / `.noon` / `.evening` / `.night` | `HeroineSelfShortTalkBreak_Morning` / ... | 1-7, 1-9, 1-7, 1-7 | `_breakHeroineSelfShortTalk{Morning,Noon,Evening,Night}List` |
| `selftalk.time.7` / `.12` / `.17` / `.24` | `HeroineSelfShortTalk_CurrentTime` (+ `Pool`) | 1, 2, 3, 4-5 | `CurrentTime{7,12,17,24}HourSelfTalkSelector._episodeList` |
| `selftalk.worked.2h` / `.5h` / `.10h` | `HeroineSelfShortTalk_WorkedTime` (+ `Pool`) | 1, 2, 3-4 | `WorkedTime{2,5,10}HourSelfTalkSelector._episodeList` |
| `pomodoro.start` / `.continuous` / `.long` | `SpeakWord_PomodoroStart` / `_Pomodoro_WorkContinuousStart` / `_Pomodoro_WorkLongStart` | 1-15, 1-10, 1-10 | `NormalWorkStart`, `ContinuousWorkStart`, `LongWorkStart` `VoiceSelector` |
| `pomodoro.break` / `.shortworked` / `.longworked` | `SpeakWord_PomodoroBreak` / `_Pomodoro_ShortWorkedBreakStart` / `_Pomodoro_LongWorkedBreakStart` | 1-15, 1-10, 1-6 | `NormalBreakStart`, `ShortWorkedBreakStart`, `LongWorkedBreakStart` |
| `pomodoro.finish` / `.longwork` / `.shortwork` / `.midway` | `SpeakWord_PomodoroFinish` / `_Pomodoro_LongWorkFinish` / `_Pomodoro_ShortWorkFinish` / `_Pomodoro_MidwayFinish` | 1-20, 1-7, 1-10, 1-10 | `NormalPomodoroFinish`, `LongWorkFinish`, `ShortWorkFinish`, `PomodoroMidwayFinish` |
| `countup.start` / `.continuous` | `SpeakWord_CountupStart` / `_Countup_WorkContinuousStart` | 1-15, 1-8 | the same selectors' `_countupVoiceList` |
| `countup.break` / `.shortworked` / `.longworked` | `SpeakWord_CountupBreak` / `_Countup_ShortWorkedBreakStart` / `_Countup_LongWorkedBreakStart` | 1-13, 1-7, 1-6 | `_countupVoiceList` |
| `countup.finish` / `.shortwork` / `.longwork` | `SpeakWord_CountupFinish` / `_Countup_ShortWorkFinish` / `_Countup_LongWorkFinish` | 1-15, 1-6, 1-7 | `_countupVoiceList` |
| `pomodoro.end` | `ShortConversation` (the `pomodoroend_*` answer-choice talk) | 1-20 | `ReactionTalkSelector._answerChoiceEpisodeList` |
| `smalltalk` | `SmallTalk` | data-driven (1-7) | `SmallTalkSelector._cache` |
| `gamestart.sameday` | `GameStart_LessTowDays` (a relaunch the same day) | 1-17 | `StartDirectionData._lessTowDaysTalkList` (in the save) |
| `gamestart.newday` | `GameStart_LessTowDays_CameraTouch` (a new day, up to 2 days away) | 1-12 | `_lessTowDaysCameraTouchTalkList` |
| `gamestart.newday.morning` / `.noon` / `.evening` / `.night` | `GameStart_LessTowDays_CameraTouch_Morning` / `_Noon` / `_Evening` / `_Night` | 1-9, 1-4, 1-5, 1-7 | `_lessTowDaysCameraTouch{Morning,Noon,Evening,Night}TalkList` |
| `gamestart.days3to14` | `GameStart_LessHarfMonth_CameraTouch` | 1-8 | `_lessHarfMonthTalkList` |
| `gamestart.days15to30` | `GameStart_GreaterHarfMonth_CameraTouch` | 1-2 | `_greaterMonthTalkList` |
| `gamestart.days31plus` | `GameStart_GreaterMonth_CameraTouch` | 1 | always 1 (`GameStartTalkSelector`) |
| `gamestart.login100` | `GameStart_GreaterYear_AND_PlayOneHundred_CameraTouch` (every 100th launch) | 1 | always 1 |

53 pools; the catalogue is `CwyLs.ModLoader.Core.PackLines.PackLinePools` (ranges as observed in game
1.17.3 and, for the startup pools, 1.18.0; checked against the master data by `PackLinesModelTests`).
Spreads: `gamestart.any` = the 10 startup pools; `*` = the other 52, every pool except `pomodoro.end`
(it needs answer choices). Not pools: the very first launch's greeting (the tutorial's), the
limited-time event openings, game-end lines, leave-chair and snack lines, player-status reactions,
`HeroineSelfShortTalkNormal` (the game never plays it) and motion lines.

When does the game reach each pool: clicking her while the timer runs → `click.work*`; on a break →
`click.break*`; otherwise `click.normal` (and, with the timer stopped, the small-talk lottery first).
Self-talk runs during breaks with the "self talk" setting on: a clock line in its time window (07:00-07:29,
12:00-12:29, 17:00-17:29, 00:xx; 20 %, once a day per slot), else a worked-time line (after 2 h, 5 h,
10 h of work; once a day), else `selftalk.break*`. Timer lines play when a work session starts, a
break starts or the timer completes; the game first picks the kind (long/continuous/normal start,
short/long-worked/normal break, long/short/midway/normal finish) by its own rules, then a line from
that kind's pool. `pomodoro.end` plays on a click within 20 minutes of a completed pomodoro, with the
game's own probability. Small talk: a break click with the timer stopped, or the first launch of a day.
Startup: once per launch, right after the startup screen, unless it is the very first launch (the
tutorial) or a limited-time event opening plays instead (each event's once). The game picks the kind by
the time since the last launch: every 100th launch → `gamestart.login100`; the same day →
`gamestart.sameday`; a new day up to 2 days away → 35 % a time-of-day kind, else `gamestart.newday`;
3-14 days → `gamestart.days3to14`; 15-30 → `gamestart.days15to30`; more → `gamestart.days31plus`.

## 3. Worked examples

Each example is complete: append the rows to the four files of your pack, here `packs/my.lines/`
(`data/` and `data/subtitles/th.json`), and drop the clips in `packs/my.lines/audio/voice/th/`. The
`th.json` blocks show English stand-ins where a real pack has the line in Thai. The motions,
faces and arguments are copied from a game line of the same pool. These blocks are read and screened
by `PackLinesPacksTests.Every_worked_example_in_the_readme_is_accepted_as_written`, so they stay valid.
Ids and episode numbers only have to be unique; the scheme below (`my_<pool>_<nnn>`, episodes
1001, 1002, ... per `Scenario`) keeps them so.

### Click while working, morning (`click.work.morning`)

Copied from `clickheroine_work_morning_002`. One `Text` row: `Arg2` is the pause after the line,
`FacialMotion`/`BodyMotion` her face and body (-1 = leave as is), `LookScale` how far she looks at you.

```json scenario_groups.json
[
  {
    "ID": "my_click_work_morning_001",
    "Scenario": "HeroineClickWork_Morning",
    "EpisodeNumber": 1001,
    "Languages": ["th"],
    "Pool": "click.work.morning"
  }
]
```

```json novel.json
[
  {
    "ID": "my_click_work_morning_001",
    "ScenarioGroupID": "my_click_work_morning_001",
    "Command": "Text",
    "Arg1": "0",
    "Arg2": 1.0,
    "Arg3": "0",
    "Talker": "Heroine",
    "LocalizationID": "my_click_work_morning_001",
    "BodyMotion": -1,
    "FacialMotion": 1,
    "LookScale": 0.5,
    "LookSpeedSeconds": 1.0
  }
]
```

```json localization.json
[
  {
    "ID": "my_click_work_morning_001",
    "Ja": "",
    "En": "Morning! Let's get going.",
    "Voice": "Voice_My_ClickWorkMorning_001"
  }
]
```

```json th.json
{
  "my_click_work_morning_001": "Morning! Let's get going."
}
```

Clip: `packs/my.lines/audio/voice/th/Voice_My_ClickWorkMorning_001.ogg`.

### Self-talk on a break, night (`selftalk.break.night`)

Copied from `selftalk_break_night_001`. Any `click.*` or `selftalk.break*` pool takes this same
shape; change `Scenario`, `Pool` and the ids.

```json scenario_groups.json
[
  {
    "ID": "my_selftalk_break_night_001",
    "Scenario": "HeroineSelfShortTalkBreak_Night",
    "EpisodeNumber": 1001,
    "Languages": ["th"],
    "Pool": "selftalk.break.night"
  }
]
```

```json novel.json
[
  {
    "ID": "my_selftalk_break_night_001",
    "ScenarioGroupID": "my_selftalk_break_night_001",
    "Command": "Text",
    "Arg1": "0",
    "Arg2": 1.0,
    "Arg3": "0",
    "Talker": "Heroine",
    "LocalizationID": "my_selftalk_break_night_001",
    "BodyMotion": -1,
    "FacialMotion": 12,
    "LookScale": 0.0,
    "LookSpeedSeconds": 1.0
  }
]
```

```json localization.json
[
  {
    "ID": "my_selftalk_break_night_001",
    "Ja": "",
    "En": "It's getting late... I should rest my eyes a bit.",
    "Voice": "Voice_My_SelfTalkBreakNight_001"
  }
]
```

```json th.json
{
  "my_selftalk_break_night_001": "It's getting late... I should rest my eyes a bit."
}
```

Clip: `packs/my.lines/audio/voice/th/Voice_My_SelfTalkBreakNight_001.ogg`.

### Clock self-talk at seven (`selftalk.time.7`)

Copied from `selftalk_current_time_001`. All four clock slots share one `Scenario`, so `Pool` is
required (`selftalk.time.7`, `.12`, `.17`, `.24`); the worked-time slots likewise
(`HeroineSelfShortTalk_WorkedTime` with `selftalk.worked.2h`, `.5h`, `.10h`). A pack clock line counts
as that slot's line for the day: she still says one seven-o'clock line a day, not two.

```json scenario_groups.json
[
  {
    "ID": "my_selftalk_time7_001",
    "Scenario": "HeroineSelfShortTalk_CurrentTime",
    "EpisodeNumber": 1001,
    "Languages": ["th"],
    "Pool": "selftalk.time.7"
  }
]
```

```json novel.json
[
  {
    "ID": "my_selftalk_time7_001",
    "ScenarioGroupID": "my_selftalk_time7_001",
    "Command": "Text",
    "Arg1": "0",
    "Arg2": 1.0,
    "Arg3": "0",
    "Talker": "Heroine",
    "LocalizationID": "my_selftalk_time7_001",
    "BodyMotion": -1,
    "FacialMotion": 13,
    "LookScale": 0.8,
    "LookSpeedSeconds": 1.0
  }
]
```

```json localization.json
[
  {
    "ID": "my_selftalk_time7_001",
    "Ja": "",
    "En": "Seven o'clock already?",
    "Voice": "Voice_My_SelfTalkTime7_001"
  }
]
```

```json th.json
{
  "my_selftalk_time7_001": "Seven o'clock already?"
}
```

Clip: `packs/my.lines/audio/voice/th/Voice_My_SelfTalkTime7_001.ogg`.

### Pomodoro work start, with its count-up twin (`pomodoro.start`, `countup.start`)

Copied from `pomodorostart_word_001` / `countupstart_word_001`. A timer line is one
`PomodoroVoiceText` row: `Arg1` `"1"` is the voice cue the body motion fires (keep the pair
`BodyMotion` 55 + `Arg1` `"1"` from a game line of the same pool, or use `BodyMotion` -1 to speak at
once with no motion), `Arg3` `"0"` moves her mouth. The game keeps pomodoro and count-up lines in
separate pools; to offer a line in both modes, add a second group of the count-up type that points at
the same localization row (the game does exactly this for its own lines).

```json scenario_groups.json
[
  {
    "ID": "my_pomodoro_start_001",
    "Scenario": "SpeakWord_PomodoroStart",
    "EpisodeNumber": 1001,
    "Languages": ["th"],
    "Pool": "pomodoro.start"
  },
  {
    "ID": "my_countup_start_001",
    "Scenario": "SpeakWord_CountupStart",
    "EpisodeNumber": 1001,
    "Languages": ["th"],
    "Pool": "countup.start"
  }
]
```

```json novel.json
[
  {
    "ID": "my_pomodoro_start_001",
    "ScenarioGroupID": "my_pomodoro_start_001",
    "Command": "PomodoroVoiceText",
    "Arg1": "1",
    "Arg2": 0.0,
    "Arg3": "0",
    "Talker": "Heroine",
    "LocalizationID": "my_pomodoro_start_001",
    "BodyMotion": 55,
    "FacialMotion": -1,
    "LookScale": 0.0,
    "LookSpeedSeconds": 1.0
  },
  {
    "ID": "my_countup_start_001",
    "ScenarioGroupID": "my_countup_start_001",
    "Command": "PomodoroVoiceText",
    "Arg1": "1",
    "Arg2": 0.0,
    "Arg3": "0",
    "Talker": "Heroine",
    "LocalizationID": "my_pomodoro_start_001",
    "BodyMotion": 55,
    "FacialMotion": -1,
    "LookScale": 0.0,
    "LookSpeedSeconds": 1.0
  }
]
```

```json localization.json
[
  {
    "ID": "my_pomodoro_start_001",
    "Ja": "",
    "En": "Alright, let's go!",
    "Voice": "Voice_My_PomodoroStart_001"
  }
]
```

```json th.json
{
  "my_pomodoro_start_001": "Alright, let's go!"
}
```

Clip: `packs/my.lines/audio/voice/th/Voice_My_PomodoroStart_001.ogg`. Every other timer pool
takes this shape: change `Scenario`/`Pool`, and copy `BodyMotion`/`Arg1`/`Arg2` from a game line of
that pool.

### After-pomodoro answer-choice talk (`pomodoro.end`)

Copied from `pomodoroend_01`: her question, two to three `Selection` rows in a row (their
`LocalizationID` is the button text, no voice; `Arg1` is the row to jump to), and one reaction per
choice, each closed by an `EndScenario` row so the next reaction is not read on.

```json scenario_groups.json
[
  {
    "ID": "my_pomodoro_end_001",
    "Scenario": "ShortConversation",
    "EpisodeNumber": 1001,
    "Languages": ["th"],
    "Pool": "pomodoro.end"
  }
]
```

```json novel.json
[
  {
    "ID": "my_pomodoro_end_001_001",
    "ScenarioGroupID": "my_pomodoro_end_001",
    "Command": "Text",
    "Arg1": "0",
    "Arg2": 1.0,
    "Arg3": "0",
    "Talker": "Heroine",
    "LocalizationID": "my_pomodoro_end_001_001",
    "BodyMotion": 1000,
    "FacialMotion": 0
  },
  {
    "ID": "my_pomodoro_end_001_002_selection_1",
    "ScenarioGroupID": "my_pomodoro_end_001",
    "Command": "Selection",
    "Arg1": "my_pomodoro_end_001_003_reaction_1",
    "LocalizationID": "my_pomodoro_end_001_selection_1"
  },
  {
    "ID": "my_pomodoro_end_001_002_selection_2",
    "ScenarioGroupID": "my_pomodoro_end_001",
    "Command": "Selection",
    "Arg1": "my_pomodoro_end_001_003_reaction_2",
    "LocalizationID": "my_pomodoro_end_001_selection_2"
  },
  {
    "ID": "my_pomodoro_end_001_003_reaction_1",
    "ScenarioGroupID": "my_pomodoro_end_001",
    "Command": "Text",
    "Arg1": "1.5",
    "Arg2": 1.25,
    "Arg3": "0",
    "Talker": "Heroine",
    "LocalizationID": "my_pomodoro_end_001_reaction_1",
    "BodyMotion": 1001,
    "FacialMotion": 2
  },
  {
    "ID": "my_pomodoro_end_001_003_reaction_1end",
    "ScenarioGroupID": "my_pomodoro_end_001",
    "Command": "EndScenario",
    "Arg2": 1.25
  },
  {
    "ID": "my_pomodoro_end_001_003_reaction_2",
    "ScenarioGroupID": "my_pomodoro_end_001",
    "Command": "Text",
    "Arg1": "1.5",
    "Arg2": 1.25,
    "Arg3": "0",
    "Talker": "Heroine",
    "LocalizationID": "my_pomodoro_end_001_reaction_2",
    "BodyMotion": 1002,
    "FacialMotion": 1
  },
  {
    "ID": "my_pomodoro_end_001_003_reaction_2end",
    "ScenarioGroupID": "my_pomodoro_end_001",
    "Command": "EndScenario",
    "Arg2": 1.25
  }
]
```

```json localization.json
[
  {
    "ID": "my_pomodoro_end_001_001",
    "Ja": "",
    "En": "So, did you manage to focus this time?",
    "Voice": "Voice_My_PomodoroEnd_001_Question"
  },
  {
    "ID": "my_pomodoro_end_001_selection_1",
    "Ja": "",
    "En": "Pretty well!"
  },
  {
    "ID": "my_pomodoro_end_001_selection_2",
    "Ja": "",
    "En": "Not really..."
  },
  {
    "ID": "my_pomodoro_end_001_reaction_1",
    "Ja": "",
    "En": "Nice! I'd better keep up with you, then.",
    "Voice": "Voice_My_PomodoroEnd_001_Reaction_1"
  },
  {
    "ID": "my_pomodoro_end_001_reaction_2",
    "Ja": "",
    "En": "That's okay. We'll do better next round.",
    "Voice": "Voice_My_PomodoroEnd_001_Reaction_2"
  }
]
```

```json th.json
{
  "my_pomodoro_end_001_001": "So, did you manage to focus this time?",
  "my_pomodoro_end_001_selection_1": "Pretty well!",
  "my_pomodoro_end_001_selection_2": "Not really...",
  "my_pomodoro_end_001_reaction_1": "Nice! I'd better keep up with you, then.",
  "my_pomodoro_end_001_reaction_2": "That's okay. We'll do better next round."
}
```

Clips: `Voice_My_PomodoroEnd_001_Question.ogg`, `..._Reaction_1.ogg`, `..._Reaction_2.ogg` in
`packs/my.lines/audio/voice/th/`. The line is offered only once all three are loaded.

### Small talk (`smalltalk`)

Copied from `smalltalk_02`. Several `Text` rows played in order; the group's own columns are the
lottery's rules: `PlayTiming` (`Break` = a break click with the timer stopped, `GameStart` = after
the day's first greeting, `GameStartAndBreak` = both), `Arg1`/`Arg2` the % chance while unseen / once
seen this session, `UnlockStoryEpisode` `"None"` or a main episode that must have been read first,
`ContentType` `"None"` (or one of the game's special rules).

```json scenario_groups.json
[
  {
    "ID": "my_smalltalk_001",
    "Scenario": "SmallTalk",
    "EpisodeNumber": 1001,
    "UnlockStoryEpisode": "None",
    "PlayTiming": "Break",
    "ContentType": "None",
    "Arg1": "25",
    "Arg2": "5",
    "Arg3": "0",
    "Languages": ["th"],
    "Pool": "smalltalk"
  }
]
```

```json novel.json
[
  {
    "ID": "my_smalltalk_001_001",
    "ScenarioGroupID": "my_smalltalk_001",
    "Command": "Text",
    "Arg1": "0",
    "Arg2": 1.0,
    "Arg3": "0",
    "Talker": "Heroine",
    "LocalizationID": "my_smalltalk_001_001",
    "BodyMotion": -1,
    "FacialMotion": 7
  },
  {
    "ID": "my_smalltalk_001_002",
    "ScenarioGroupID": "my_smalltalk_001",
    "Command": "Text",
    "Arg1": "0",
    "Arg2": 1.25,
    "Arg3": "0",
    "Talker": "Heroine",
    "LocalizationID": "my_smalltalk_001_002",
    "BodyMotion": -1,
    "FacialMotion": 13,
    "LookScale": 1.0
  }
]
```

```json localization.json
[
  {
    "ID": "my_smalltalk_001_001",
    "Ja": "",
    "En": "When I was little, I loved watching the rain.",
    "Voice": "Voice_My_SmallTalk_001_001"
  },
  {
    "ID": "my_smalltalk_001_002",
    "Ja": "",
    "En": "The sound of it is so calming, isn't it?",
    "Voice": "Voice_My_SmallTalk_001_002"
  }
]
```

```json th.json
{
  "my_smalltalk_001_001": "When I was little, I loved watching the rain.",
  "my_smalltalk_001_002": "The sound of it is so calming, isn't it?"
}
```

Clips: `Voice_My_SmallTalk_001_001.ogg` and `_002.ogg` in `packs/my.lines/audio/voice/th/`.

### Startup greeting, every kind (`gamestart.any`)

The first line after the startup screen, on every launch that reaches a greeting, while ไทย is shown.
One `Text` row, as in the game's greetings: `BodyMotion` -1 lets the camera-touch kinds play their own
"touch the camera" motion first. `"Weight": 1` takes every launch (an unweighted line would get an
equal share with the game's greetings of that kind, e.g. 1 in 18 for `gamestart.sameday`).
`"Scenario": "None"` because the loader writes each copy's own (`my_hello@gamestart.sameday`, ...).

```json scenario_groups.json
[
  {
    "ID": "my_hello",
    "Scenario": "None",
    "EpisodeNumber": 900001,
    "Languages": ["th"],
    "Pool": "gamestart.any",
    "Weight": 1
  }
]
```

```json novel.json
[
  {
    "ID": "my_hello_001",
    "ScenarioGroupID": "my_hello",
    "Command": "Text",
    "Arg1": "0",
    "Arg2": 1.0,
    "Arg3": "0",
    "Talker": "Heroine",
    "LocalizationID": "my_hello_001",
    "BodyMotion": -1,
    "FacialMotion": 10,
    "LookScale": 0.0,
    "LookSpeedSeconds": 1.0
  }
]
```

```json localization.json
[
  {
    "ID": "my_hello_001",
    "Ja": "",
    "En": "Hey, what's up? Did you just call me?",
    "Voice": "Voice_My_Hello_001"
  }
]
```

```json th.json
{
  "my_hello_001": "Hey, what's up? Did you just call me?"
}
```

Clip: `packs/my.lines/audio/voice/th/Voice_My_Hello_001.ogg`. For one kind only, write its `Scenario`
(e.g. `"GameStart_LessTowDays"`) and leave `Pool` out, as for any other pool.

### A global line (`*`)

Written once, offered in every pool but the answer-choice talk (§2): clicks, all self-talk, timer lines,
small talk and the startup greeting. `"Weight": 0.05` = about one pick in twenty of each pool it joins.
In the timer pools the row is spoken as a `PomodoroVoiceText` row with no motion; in small talk the
copy plays on break clicks (`PlayTiming` `Break`).

```json scenario_groups.json
[
  {
    "ID": "my_global_001",
    "Scenario": "None",
    "EpisodeNumber": 900002,
    "Languages": ["th"],
    "Pool": "*",
    "Weight": 0.05
  }
]
```

```json novel.json
[
  {
    "ID": "my_global_001",
    "ScenarioGroupID": "my_global_001",
    "Command": "Text",
    "Arg1": "0",
    "Arg2": 1.0,
    "Arg3": "0",
    "Talker": "Heroine",
    "LocalizationID": "my_global_001",
    "BodyMotion": -1,
    "FacialMotion": 10,
    "LookScale": 0.5,
    "LookSpeedSeconds": 1.0
  }
]
```

```json localization.json
[
  {
    "ID": "my_global_001",
    "Ja": "",
    "En": "I'm glad you're here.",
    "Voice": "Voice_My_Global_001"
  }
]
```

```json th.json
{
  "my_global_001": "I'm glad you're here."
}
```

Clip: `packs/my.lines/audio/voice/th/Voice_My_Global_001.ogg`.

## 4. How the gate works

- **Injection.** The groups are merged with the pack's other rows, screened, and injected only while
  this feature is active (`MasterDataFeature.PackLinesAdmitted`). Off, not initialised, or a missing
  seam: every exclusive group is held back, so no pack line can reach the game in any language.
- **At pick time, not at injection.** Every pick goes through code the feature patches
  (`FacilityClickHeroine.OneWord` / `AnswerChoiceStory`, `PomodoroVoiceSelector.TakeNext*Voice`,
  `SmallTalkSelector.LotterySmallTalk`). Right before the game reads a pool, the gate brings its list
  in line with `LanguageFeature.CurrentCode`: the lines offered in the language shown join the game's
  `RandomList`, the others leave it. Switching language takes effect on the next pick, no restart.
- **Clips.** A line also waits until every clip it speaks would make a sound
  (`LanguageVoices.CanPlay`, language voice sets): a language-only clip that has not loaded yet has no game clip
  to fall back on, so that pick goes to another line and this one joins a later pick.
- **Byte-for-byte vanilla elsewhere.** While the language shown offers no pack line, the game's lists
  are exactly its own: nothing is inserted, no random number is drawn (the gate has its own
  generator; the game's `UnityEngine.Random` stream is never touched), the small-talk cache holds
  the game's entries in the game's order. `PackLinesGateTests.With_no_line_offered_the_game_draws_exactly_its_own_sequence`.
- **Shuffle and no-repeat kept.** `RandomList` deals a shuffled cycle of its range
  and never repeats across a cycle boundary. A pack episode is inserted at a random position in
  front of the cycle's last element: it plays exactly once per cycle, the last element (the game's
  `_beforeLast`) stays a game line, and nothing repeats back to back
  (`PackLinesGateTests.With_lines_offered_every_line_plays_once_per_cycle_and_never_twice_in_a_row`).
  A line that leaves mid-cycle (language switched away) stays owed and comes back if the language
  returns within the cycle; one that already played this cycle does not.
- **Timer lists.** Two-list timer selectors index a per-episode table with the drawn number
  (e.g. `NormalWorkStartVoiceSelector`); the feature answers for its own episodes before that line runs.
- **Weighted lines.** They never join the lists above. At a pick, a winning weighted line is put at
  the front of the list the game is about to read and taken back if the game read another list; the
  game's own episodes, their order and `_beforeLast` are untouched (§5).
- **Startup greetings.** A postfix on `GameStartTalkSelector.GetGameStartTalk` rolls the startup lines
  of the kind the game chose. On a win it first checks that the game's own lookup (the first group with
  that `Scenario` and `EpisodeNumber`, and its first novel row) finds exactly that line, then returns
  its episode; otherwise the game's greeting stays, with one WARN.

## 5. Weight

**A line with `Weight`** (0 to 1) is never dealt into the game's deck. At every pick of its pool, the
weighted lines offered there (in the language shown, clips loaded) share one roll, specific and global
lines alike:

- The chance that one of them takes the pick is the sum of their weights, capped at 1. Otherwise the
  game's own pick runs, which can still be an unweighted pack line.
- Which one: by weight. Two lines at `1` split the picks 50/50; `1` and `0.5` split them 2:1.
- No repeat: with two or more weighted lines offered, the one that played last in this pool sits the
  pick out, unless its weight is 1. Once the game's own line has played in between, it may come up again.
- `0` never plays (a quick way to switch a line off); `1` plays at every pick of its pool.

| Pool picks | Lines offered | Result |
|---|---|---|
| any | one line, `"Weight": 0.25` | about 1 pick in 4 is that line, the rest the game's |
| any | two lines, `0.25` each | about half the picks are one of them, never the same one twice in a row |
| any | one line, `"Weight": 1` | every pick is that line |
| `gamestart.*` | the line above as `gamestart.any`, `1` | it is the first line of every launch that reaches a greeting |

Where "a pick of its pool" is, pool by pool:
- **Two-list pools.** Work and break clicks and break self-talk choose 50/50 between the general list
  and the time-of-day list each time (`ReactionTalkSelector`). A weighted line rolls only when its own
  list is read: `click.work.morning` at `0.5` is half of the morning list's picks, about a quarter of
  all work clicks in the morning.
- **Timer kinds.** The game first decides the kind (long, continuous or normal start, and so on), then
  reads that kind's pool; a weighted line rolls when its kind comes up.
- **Clock and worked-time lines.** Their slot's once-a-day rule still applies; the roll happens when
  the slot plays.
- **Small talk.** The roll happens when the game would hold a small-talk lottery at all (its 10-minute
  interval has passed), among the weighted small talks the game itself would accept now (play timing,
  unlock, not the one just played). A winner takes the lottery before the `Arg1`/`Arg2` odds.
- **Startup greetings.** There is no deck to join (it lives in the save), so every startup line rolls.
  An unweighted one rolls at an equal share with the game's greetings of that kind,
  `1 / (game lines + unweighted pack lines)`: one line in `gamestart.sameday` (17 game lines) gets 1/18.

**A line without `Weight`** works as before:
- **Fixed pools (clicks, self-talk, timer lines, `pomodoro.end`).** A pack line is one more card in
  the deck: with `N` game lines and `K` pack lines offered, each line, game or pack, plays exactly
  once per `N + K`-pick cycle. Example: `click.work.morning` (5) + 2 pack lines = once per 7 morning
  picks each. In the two-list pools a line competes inside its own list only, so a line in
  `click.work.morning` (5 game lines) comes up far more often than one in `click.work` (23): about
  0.5 / (5 + K) against 0.5 / (23 + K') per pick.
- **Small talk.** Exactly the game's rules: the 10-minute interval, unlock, play timing and content
  type, then each candidate qualifies when its `Arg1` (unseen) / `Arg2` (seen) is at least a random
  0-100, and one qualifier is picked uniformly (`SmallTalkSelector.LotterySmallTalk`).
  Unseen scenes are asked first, so a pack scene must not look unseen forever: the save never
  records it, so the feature remembers heard pack lines in `BepInEx/data/packlines/seen.json` and puts
  them back into the in-memory played list before each lottery (`Core/PackLines/PackLineSeen.cs`;
  without it, a pack scene would keep counting as unseen and win almost every small talk across restarts).

## 6. Save safety (Steam Cloud)

- The only save list a pack line can reach is `ScenarioProgressData.PlayedScenarioGroupIDs`: a small
  talk adds its group id when it ends (`SmallTalkSelector`). Every write of that list goes
  through `SaveDataManager.SaveScenarioProgressData`, which serialises
  synchronously (`SaveDataIO.Save` → `ES3.Save`). A prefix takes every pack-line
  id (of any line registered this session) out of the list and a finalizer puts them back in place,
  so the file never holds them and the session keeps its "seen" state (`PackLineSaveFilter`,
  `PackLinesSaveAndSmallTalkTests`).
- Click, self-talk, timer and answer-choice lines are never logged: `FacilityClickHeroine` and
  `HeroineAI` play them without `FacilityStory.SaveScenarioPlayedLog`, and the
  game's selectors holding the pools are not saved (the only saved `RandomList`s are
  `StartDirectionData`'s game-start lists, which are not pools here).
- Achievements: `AchievementService.OnEndSmallTalk` reacts to episodes 1-4 only; pack episodes are
  1000+. Talk counters (`TALK_WORKING_COUNT`, ...) count clicks, not lines, exactly as in the game.
- Startup greetings: the game's greeting decks are `StartDirectionData`'s `RandomList`s, which are saved.
  They are never widened and never hold a pack episode: only the episode `GetGameStartTalk` returns
  changes, for that launch. After a pack pick, `UseGameStartTalk` is skipped once, so the deck keeps the
  game line it drew (the player did not hear it) and the save that call makes does not happen. The
  greeting is read without the talk log; a copy's id (`<id>@<pool>`) is held back by the filter above
  like any pack id. Which startup line played last (for the no-repeat rule) lives in
  `BepInEx/data/packlines/last.json`, never in the save.

## 7. Config, console, smoke checks, trace

| Item | Value |
|---|---|
| Config | `[PackLines] Enabled = true` (adapter .cfg). Off: no pack line is injected or offered. |
| SETUP line | at the first injection: `SETUP  30 line(s) in 12 pool(s) ready  src=packlines pools=click.work:3,... languages=th rejected=0 weighted=2 patches=22` |
| WARN lines | one per refused line: `PACKLINES skipped scenario_groups row '<id>': <reason>`; one per note on an accepted line (a global line at Weight 1, a global line with several spoken rows, a spread `Pool` without `Languages`); one when the feature turns off: `PACKLINES off: missing seam(s): ...` |
| Console | `lines list` - every line by pool, offered or not right now, the pool's live state, refusals, the armed test. `lines test <groupId>` - a click-style line (clicks, self-talk, `pomodoro.end`, small talk) plays now through the game's own click reaction (the slot the game uses for a pending line in `FacilityClickHeroine`); a timer line is armed and replaces the game's pick at the next matching timer moment. `lines test off` disarms. The line must be offered in the language shown. A startup line (its copy id or the id you wrote) answers that it plays only at launch, whether it is offered now and whether its clips are loaded. `lines list` shows each line's weight, each copy's source, and per pool the weighted lines and the one played last. |
| Smoke (quiet, read-only) | `pl.lines.patches`, `pl.lines.registry`, `pl.lines.pools` (the gate run on copies of the game's lists: any other language → 0 pack lines and the game's episodes untouched; each line language → exactly its owed lines), `pl.lines.savefilter`; `pl.lines.gamestart` (each startup line's group and first novel row are found the way the game looks them up). `pl.lines.pools` also fails when a weighted line sits in a game list between picks. |
| Trace (file only, sys `packlines`) | `armed`, `off`, `bound`, `gate`, `cycle`, `cycleRestored`, `pick`, `weighted`, `clock`, `test`, `saveFilter`, `seen`, `seenSaved`; startup: `startWarm` (clips queued before the room), `pick`, `startKept` (the game's greeting stayed, and why), `startDeckKept` (the save deck was not advanced) |
| Mod data (never the save) | `BepInEx/data/packlines/seen.json` (pack small talks heard), `cycles.json` (deck lines dealt this cycle), `last.json` (the startup line played last) |
| Patch targets | `PackLinesPatchTargets.All` (115: the 22 patched methods, the members read by reflection and `FacilityClickHeroine.ReactionReady` for `lines test`), registered by `Initialize`; `tools/seamcheck` finds the table by its IL scan (game 1.18.0: all 359 loader targets found) |

## 8. Known limits

- A pack small talk's "seen" state lasts for the session: the save never holds pack ids, so the next
  session treats it as unseen again (its `Arg1` odds apply until it plays).
- A line injected while the room is open joins the click and timer pools at once; it joins the
  small-talk lottery at once too (the lottery is rebound on each injection).
- `lines test` for a click-style line takes the slot the game uses for a pending "you came back" line;
  if one was pending it is dropped. Asleep, she only wakes up; wanting to talk, her story comes first.
- A pack line that reuses a game line's localization row is not clip-checked (its clip is the game's).
- Startup lines never play on the very first launch (the tutorial greeting) or on a launch where a
  limited-time event opening plays instead (Halloween 2026, Christmas 2025; once each).
- A startup line plays only if its clips have loaded by the time the game picks the greeting, about
  half a second before the room is ready. The loader queues startup clips first, as soon as the lines
  are injected and the language is known; a line whose clip is still loading sits that launch out (the
  game's greeting plays, and the trace `startKept` names the clip). It never plays silent.
- Global lines skip `pomodoro.end` (§2), and a global line with several spoken rows skips the timer
  pools.
