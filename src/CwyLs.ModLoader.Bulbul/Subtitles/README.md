# Subtitles — the game's own text box, in the language you chose

Every line is shown in the game's own story text box and nowhere else. The framework's job is to
put the right language *in* that box (through the game's text seam) and to keep each line it is
responsible for up long enough to read. There is no overlay panel any more, and no smoke check draws
anything.

Entry point: `SubtitleFeature.Initialize(Config)`, called once from `BulbulPlugin.Awake`, right
after `Language/LanguageFeature.Initialize(Config)` — pack languages and the text seam are two
halves of one feature, and the seam reads the language registry on every call. If the subtitle
feature ever starts first it logs one `SUBTITLE started before LanguageFeature.Initialize` warning.

| File | Role |
|---|---|
| `SubtitleFeature.cs` | Config, initialisation, the retired `Show` stub |
| `SubtitleNative.cs` | Postfixes on `LocalizationMasterWrapper.Get` (serves pack-language rows) and `ScenarioTextMessage.StartText` (what the box shows, since when) |
| `SubtitleHold.cs` | Prefix on `ScenarioReader.AutoNext(float)`: the reading-time hold |
| `MasterDataSubtitleSource.cs` | Publishes the packs' `data/subtitles/<code>.json` as an `ISubtitleSource` |
| `SubtitlePatchTargets.cs` | The five seams, verified at startup |
| `SubtitleSmokeChecks.cs` | `p2.subtitles.native`, `.tables`, `.hold` — all read-only |
| `Subtitles.cs` | The mod-facing facade |

## The text seam

`LocalizationMasterWrapper.Get(LocalizationData, GameLanguageType)` is the one overload every
localized string funnels through. The postfix returns at once
for the game's own seven languages. For a pack language (value 100+) it asks
`Core.Language.LanguageText.Resolve`:

1. the language's own table (`SubtitleSources`: the packs' `data/subtitles/<code>.json`, then
   `Satone.Say`'s own lines),
2. each step of the language's declared fallback chain — another pack language's table, or one of
   the game's own columns (read through the same `Get`, so the game's own mapping is used),
3. nothing found: the game's answer is left (its default branch is Japanese).

A row served from a pack table gets the language's line-break helper (Thai: syllable-boundary
zero-width spaces, `Core/Subtitles/ThaiText.cs`) when `Subtitles.LineBreaks` is on. The helper is
chosen by the declared *script*, never by the language code.

## The reading-time hold

Every line the framework causes to be shown stays up for at least
`max(2.5 s, visible characters / 15 per second)` (`Core/Subtitles/ReadingTime.cs`; combining marks,
zero-width characters, control characters and rich-text tags do not count).

"Lines we cause to be shown" = every `Satone.Say` line (group id `cwy_say_*`) and every story line
while a pack language is active. The game's own lines in its own languages keep the game's timing.

**Voiced lines.** While a pack language is active and the line has its own
voice clip, the rule is `max(2.5 s, characters / 15, clip length + 0.3 s)`, the clip part measured from when the clip
started (`ReadingTime.RemainingFor(text, shownAt, now, clipStartedAt, clipSeconds)`). "Its own clip"
= the last clip the voice manager was handed (`Voice/VoicePlayback`, recorded by the `GetAudioClip`
postfix, so it is the clip that really plays — a pack language's set clip, a fallback, or the game's)
when it started between 0.5 s before and 2 s after the text reached the box
(`ReadingTime.ClipBelongsToLine`; the game asks for the clip a frame after `StartText`). A wordless
row (the box still shows the previous text) gets nothing once that text is older than 2 s, and at
most the 0.3 s tail otherwise. Since every voiced row
already waits for the clip before `AutoNext`, this adds at most the 0.3 s tail; it guarantees our
hold never ends a line under its voice clip. The `hold`
trace event carries `clip` and `clipSeconds`. In the game's own languages (including `Satone.Say` lines
there) the rule above is unchanged.

How: `ScenarioReader.CommandVoiceText` waits for the typewriter and the clip, then calls `AutoNext()`,
which waits `waitSeconds` and reads the next row; for a
Say group that row is `EndScenario`, which closes the box. The prefix on `AutoNext(float
waitSeconds)` raises `waitSeconds` to what the line still needs, measured from its `StartText`. It
runs in the async method's stub, before the argument is copied into the state machine. It is the
game's own pause (the same one a `Text` row's `Arg2` uses), so a click or skip still
cancels it and nothing new is drawn. Each extension is a trace-file `subtitle/hold` event.

## Lines the game plays with no visible text box

The retired overlay's `OverlayForUndisplayedLines` existed for these. No replacement is built;
they are listed for a later decision.

| Case | Where | Count / evidence |
|---|---|---|
| Non-verbal motion vocalisations (stretch, sigh, laugh, thinking, "interest", jump): `VoiceText` rows whose `Ja` and `En` are empty, so `CommandVoiceText` skips `StartText` and only plays the clip | `motion_scenario_001-004, 010-016, 018-019` (`Voice_Motion_StretchStart_001-003`, `StretchEnd_001`, `DrinkToCool_001`, `Thinking_001-002`, `Laugh_001-003`, `JumpUpStart_001`, `Interest_001-002`), triggered by `MotionSoundController` through `WantPlayVoiceTextScenario` | 13 rows in `NovelMaster`; traces show `Voice_Motion_JumpUpStart_001` with "no text in any language" |
| One `PomodoroVoiceText` row with empty `Ja`/`En` | `clickheroine_rest_morning_005` (`Voice_ClickHeroine_Word_Break_Morning_005`) | 1 row |
| Our own clips played outside a scenario | `VoiceFeature.PlayAsSatone` (the loud smoke checks' `Voice_CWY_Test`), and `Satone.Say`'s fallback path when the scenario route is refused (`SatoneSayFeature.Fallback`: voice only; it does not call the retired `Subtitles.Show`, and its `FellBack` detail says "voice only, no text box") | traces: the only non-probe overlay lines in 9 test sessions were `Voice_CWY_Test` |
| The player hid the UI (`UIShowManagerForPC.AllUIDeactivate`) | the box receives the text but its canvas is off | not covered by the overlay either (StartText fired) |

Every other voiced row reaches the box: all `HeroineService.PlayVoice` calls in the game come from
the four `ScenarioReader` text commands, each of which calls `StartText` when the text is non-empty
in game 1.17.x; in the same 9 sessions 199 spoken lines were confirmed
displayed by the box.

## Configuration (`[Subtitles]` in the adapter's .cfg)

| Key | Default | Meaning |
|---|---|---|
| `Enabled` | `true` | The reading-time hold and the console mirror. A pack language's text is served either way. |
| `HoldForReading` | `true` | The reading-time hold above (with the voiced-line clip rule). |
| `LineBreaks` | `true` | Run a pack language's line-break helper on its rows. |
| `MirrorToConsole` | `true` | One `SUBTITLE "…" lang=th via=textbox id=… min=…` line per translated line the box displays. |

Retired keys (left in an existing .cfg as orphans, never read): `Language`, `Fallback`,
`FontSizePercent`, `BottomPercent`, `ShowUnknownVoices`, `Mode`, `OverlayForUndisplayedLines`,
`ThaiWordBreaks`. The language is now the game's own setting (the selector), and the fallback is the
pack's declaration.

## Diagnostics

| Id | Passes when |
|---|---|
| `p2.subtitles.native` | Both seams are patched; while a pack language is active, a story label reads back the last pack row served (not yet observed while the box is idle). |
| `p2.subtitles.tables` | At least one text source is registered and every pack language has rows. |
| `p2.subtitles.hold` | The `AutoNext` prefix is in place; the detail is its last decision. |

Trace events (`sys: satone`): `subtitle` (the console line, and `Show` refusals, file-only),
`textOverride` (first read of each row served from a pack), `hold` (each extension).

## Public API

```csharp
Subtitles.Language;          // "th" while Thai is chosen; "en", "ja", ... otherwise
Subtitles.Enabled;           // hold + console mirror
Subtitles.Show(text);        // retired: returns false, draws nothing (use Satone.Say)
```

## Threading and lifetime

All patched methods run on Unity's main thread. The hold and the log share one `Stopwatch` clock
(`SubtitleNative.Now`). `SubtitleSources.TryGet` reads a copy-on-write snapshot, so the text seam
takes no lock and allocates nothing per string.
