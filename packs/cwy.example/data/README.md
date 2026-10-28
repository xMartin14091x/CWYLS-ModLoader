# cwy.example / data

The master-data files a content pack may ship. They are merged into
`Bulbul.MasterDataLoader`'s three collections as the game's own row types, so the game's story
reader, localization wrapper and story UI read them without knowing they came from a pack.

| File | What it is |
|---|---|
| `localization.json` | Lines of text in the eight languages the game ships, plus the voice clip that speaks each one. Array of rows, keyed by `ID`. |
| `novel.json` | The commands a scenario group is made of, in order. Array of rows. |
| `scenario_groups.json` | The named sequences the reader can play, and when the game may play them. Array of rows. |
| `subtitles/<lang>.json` | Text for a language the game has no column for. Flat object of `localizationId -> text`. This pack ships none. A table is shown only for a language some pack declares in `language.json` (see `docs/guide/manifest.md`). |

Member names are the game's own column names, matched **case-insensitively**: `ID`, `Id` and `id`
are the same member, and so are `ScenarioGroupID` and `scenarioGroupId`. A member the schema does
not know is refused with the file and row index, so a typo cannot fail silently - unless its name
starts with `x-`, which is reserved for your own notes and is ignored (the files here use it to
record where each row shape came from).

A row **replaces** the game's row with the same id whole. Overriding one language of an existing
line means writing the other columns out again.

## What this pack's rows do

One playable scenario group, `cwy_example_01`, of two rows:

1. `cwy_example_01_001` - `VoiceText`, `Arg1: "1"`. Satone says localization row
   `cwy_example_01_001` out loud with the mouth-talk motion; the clip is `Voice_CWY_Test`, the WAV
   in `audio/voice/` that the voice feature registers. The game's scenario reader looks the text up
   in its localization table, shows it in the story text box and plays the voice clip the row
   names.
2. `cwy_example_01_end` - `EndScenario`, the row every multi-row short group in the game ends with
   (same shape as the game's own end rows).

A language pack can give the same line in another language with a `data/subtitles/<code>.json`
row for `cwy_example_01_001`; the game shows it in its own text box when that language is picked in
the language selector.

## Why `MotionScenario`, and why episode 900

The group's `Scenario` is `MotionScenario` (22). That type needs no unlock, tutorial or
story-progress state, which is what makes the group safe to inject into a save that has never been
played:

- The game does not count type 22 as a long story and it is not `Tutorial`, so the scenario reader
  takes its short path: no main-story setup, no tutorial service, no skip button, no direction or
  window-view scan over the group.
- The reader's "middle talk" handling covers only small talk and two event types, so no facility
  lock/unlock events fire.
- `ScenarioGroupMasterWrapper` never enumerates type 22 - `PlayedMainScenario`, `PlayableScenario`,
  `GetPlayableExtraScenario` and the event queries all filter on other types - so the group cannot
  appear in a story menu, cannot affect unlock state, and its `ContentType` is never parsed.
- The game's own type-22 groups (`motion_scenario_001` and friends) are a single `VoiceText` row,
  which is the shape copied here.

`EpisodeNumber` is 900 on purpose. The game picks a motion scenario by its type and episode
number, and only ever asks for small numbers (1-4). An episode number the game never requests
means this group is only ever played deliberately, never as a side effect of Satone stretching.

## Checking it worked

With only this pack installed, the startup log carries one line:

```
MASTERDATA: localization +1/~0, novel +2/~0, scenario_groups +1/~0, subtitles(-) 0 rows; rejected 0
```

(other packs add their own rows to the counts, and a pack's text tables show up as
`subtitles(<codes>) N rows`) and the smoke report
carries `p2.masterdata.injected`, `p2.masterdata.wrapper_reads`, `p2.masterdata.text_tables` and
`p2.masterdata.scenario_playable`. The full format and the
injection rules are in `src/CwyLs.ModLoader.Bulbul/MasterData/README.md`.
