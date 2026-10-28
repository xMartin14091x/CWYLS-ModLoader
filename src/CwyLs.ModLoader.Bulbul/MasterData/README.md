# Master-data patching

Adds and overrides the game's dialogue rows, scenario commands and scenario groups from content
packs, and carries the text tables for languages the game does not have (the rows a pack
language shows; see `../Language/README.md`).

Entry point: `MasterDataFeature.Initialize(Config)`, called once from `BulbulPlugin.Awake`.

## The seam

`Bulbul.MasterDataLoader` holds the whole of the game's dialogue data in three public fields
(game 1.17.3):

| Field | Concrete type once loaded |
|---|---|
| `IReadOnlyList<NovelData> NovelMasterList` | the `NovelMaster` ScriptableObject's own `Items` list |
| `IReadOnlyDictionary<string, LocalizationData> LocalizationList` | a fresh `Dictionary` keyed by each row's `ID` |
| `IReadOnlyList<ScenarioGroupData> ScenarioGroupMasterList` | the `ScenarioGroupMaster` ScriptableObject's own `Items` list |

Everything downstream reads those fields live: `LocalizationMasterWrapper.TryGet/Get/Bind`,
`ScenarioGroupMasterWrapper`, and `ScenarioReader.StartReady`, which filters `NovelMasterList` by
group id afresh on every play. Replacing the field values is therefore enough - nothing caches the
collections.

## Where the injection happens, and why not in a `Load` postfix

`MasterDataLoader.Load` is `async UniTask`. The compiler turns its body into a state machine, so
the method returns as soon as it reaches its first `await` - the one on
`Addressables.LoadAssetsAsync`. **A Harmony postfix on `Load` runs there**, roughly a second before
the three fields are assigned. The same is true of `LoadCompletionAsync`, which is also `async`.

So the postfix (`MasterDataPatches`) does only what is true at that moment: it captures the loader
instance and records that a load has begun, which drops any previous injection.

The completion point used instead is the loader's own flag: **`MasterDataLoader.IsLoaded`**,
set `true` after all three assignments and just before the load task completes. `MasterDataHost`'s hidden `DontDestroyOnLoad` runner reads it
once a frame, and injects the first frame it is set and all three collections are non-null.

Polling rather than patching `set_IsLoaded`: that setter is a one-line auto-property setter called
from the compiler-generated `MoveNext`, and Mono is free to inline it, which would make a Harmony
patch silently never fire. Reading a public flag from our own code has no such failure mode and
costs three null checks a frame.

After the first injection the host drops to one check every 30 frames, looking for
`LocalizationList` having gone null - `Dispose` nulls the collections but leaves
`IsLoaded` true, so a null collection is the only honest signal that the game released our rows and
will load again.

## Idempotence

`MasterDataInjector` snapshots the game's own rows once per load (`Capture`) and every injection
**rebuilds** the three collections from that snapshot plus the merged pack rows. Re-injecting after
`PacksResolved` therefore produces exactly the same result rather than appending again.

The injected rows are new `LocalizationData`/`NovelData`/`ScenarioGroupData` instances, and the
collections are new `Dictionary`/`List` objects. The game's own ScriptableObject lists are never
mutated: `NovelMasterList` *is* `novelMaster.Items`, the asset's list, and appending to
it would survive a `Dispose`/`Load` cycle and double our rows.

The three fields are public, so they are assigned directly; `AccessTools.Field` would add a failure
mode and nothing else. The one reflected member is `NovelData.LookEaseType`, whose type is
`DG.Tweening.Ease` - this assembly does not reference DOTween, so that field is set through
`AccessTools.Field` + `Enum.ToObject`. A failure there leaves the row at `Ease.Unset`, which is what
every one of the game's own rows stores, and is warned about once.

## Pack file formats

All four files live under `data/` in a pack. Member names are the game's own column names matched
case-insensitively (`id` == `ID`, `scenarioGroupId` == `ScenarioGroupID`). An unknown member is
rejected with `file[index].member: reason`, unless it starts with `x-`, which is reserved for the
pack's own notes and ignored.

### `data/localization.json` - array of rows

```json
[
  { "ID": "cwy_example_01_001", "Ja": "…", "En": "…", "ZhHans": "…", "ZhHant": "…",
    "Pt": "…", "Ko": "…", "Ru": "…", "Voice": "Voice_CWY_Test" }
]
```

`ID` is required; every other column defaults to `""`. `Voice` is a voice name - the game's own, or
one a pack registered under `audio/voice/` (see `../Voice/README.md`).

### `data/novel.json` - array of rows

```json
[
  { "ID": "cwy_example_01_001", "ScenarioGroupID": "cwy_example_01", "Command": "VoiceText",
    "Arg1": "1", "Arg2": 0.0, "Arg3": "0", "Arg4": "0", "Talker": "Heroine",
    "LocalizationID": "cwy_example_01_001", "BodyMotion": -1, "FacialMotion": -1,
    "LookScale": 0.0, "LookSpeedSeconds": 1.0, "LookEaseType": "Unset" }
]
```

`ID`, `ScenarioGroupID` and `Command` are required. Defaults are the neutral values the game's own
rows carry: `Arg1`/`Arg3`/`Arg4` `"0"`, `Arg2` `0`, `Talker` `None`, `LocalizationID` `"0"`,
`BodyMotion`/`FacialMotion` `-1`, `LookScale` `0`, `LookSpeedSeconds` `1`, `LookEaseType` `Unset`.

The `Arg*` columns are strings in the game's own data and are kept as strings here - a number is
rejected with "quote the value", because `ScenarioReader` calls `int.Parse(Arg1)` on them.

### `data/scenario_groups.json` - array of rows

```json
[
  { "ID": "cwy_example_01", "TitleLocalizationID": "0", "SubtitleLocalizationID": "0",
    "Scenario": "MotionScenario", "EpisodeNumber": 900.0, "UnlockLevel": 0,
    "UnlockStoryEpisode": "None", "PlayTiming": "None", "ContentType": "None",
    "Arg1": "0", "Arg2": "0", "Arg3": "0" }
]
```

`ID` and `Scenario` are required. `TitleLocalizationID`/`SubtitleLocalizationID`/`Arg*` default to
`"0"` and `UnlockStoryEpisode`/`PlayTiming`/`ContentType` to `"None"`, as in the game's own rows.

Two more members are the loader's, not game columns: `Languages` (an array of language codes)
marks the group as a *pack line* the game offers only while one of those languages is shown, and
`Pool` names the pool it joins where its `Scenario` feeds several. Such groups are screened
(`CwyLs.ModLoader.Core.PackLines.PackLineScreen`) and injected only while the pack-lines feature is
active (`PackLinesAdmitted`); otherwise they are held back. A file that does not use the two members
loads exactly as before. Format, rules and worked examples: `../PackLines/README.md`.

### `data/subtitles/<lang>.json` - flat object

```json
{ "cwy_example_01_001": "<the line in this file's language>" }
```

The file name is the language code. `LocalizationData` has exactly seven text columns and no room
for another, so a language the game does not ship cannot live in `localization.json`.
`MasterDataFeature.Subtitles` (an `ISubtitleLookup`) answers `TryGet(localizationId, language)` and
`IdsFor(language)`. A table only becomes visible in the game when some pack also *declares* that
language (`language.json`, see `../Language/README.md`).

Note that `PackContentIndex` classifies these files as `PackContentKind.Other`; the pack reader
picks them out by path (`SubtitleTables.LanguageOf`).

### Enums

`Command`, `Talker`, `Scenario` and `LookEaseType` accept a member **name** or a declared **number**
(`"VoiceText"`, `"20"` and `20` are all the same). A name that is not a member, and a number the
enum does not declare, are rejected - so a typo is caught in the log rather than becoming a command
the reader silently ignores. The tables are in `CwyLs.ModLoader.Core.MasterData.MasterDataEnums`,
match the game's own enums (game v1.17.2) and are covered by `MasterDataEnumTests`.

## Merge rules

Packs are merged in `PackManager`'s dependency order.

- A row whose id the game already has **overrides** it; a new id is **added**.
- A row replaces the game's row **whole**. A localization override that only sets `En` leaves the
  other languages empty.
- A later pack's row replaces an earlier pack's row for the same id, in the earlier row's position,
  and is counted as a `PackOverrides` - not an error.
- A novel row whose id names **more than one** row in the game's own data is refused: 26 of the
  game's 2289 novel ids repeat, and there is no way to tell which row was meant. Give the row a new
  id instead. (Localization and scenario-group ids are unique in the game's data.)
- A bad row never costs a pack its good ones. Every refusal is logged as
  `MASTERDATA rejected <file>[<index>].<member>: <reason>`, capped at 20 lines per pass.

## Config

| Key | Default | Effect |
|---|---|---|
| `MasterData.Enabled` | `true` | Off leaves the game's own rows completely untouched. |
| `MasterData.LogEachRow` | `false` | On writes one `MASTERDATA + localization <id>` line per injected row. |

## Log and trace

One summary line per injection (here with `cwy.example` and a language pack installed):

```
MASTERDATA: localization +1/~0, novel +2/~0, scenario_groups +1/~0, subtitles(th) 8 rows; rejected 0
MASTERDATA loader now holds 2112 localization, 2291 novel, 611 scenario group row(s) (loaded)
```

and one `TraceBus` event, `sys: "data"`, `m: "masterdata"`, carrying the per-kind added/overridden
counts, the final collection sizes, the subtitle languages and every rejection string.

`PackManager.PacksResolved` fires on every rescan - the smoke runner and the texture feature both
rescan - so `Reload` fingerprints the packs' data files (`MasterDataFingerprint`, FNV-1a 64 over the
file contents, order-sensitive) and skips a rescan whose files have not changed:

```
MASTERDATA: packs unchanged, injection kept (packsResolved, p1:l2447:6d1f…)   [Debug]
```

That keeps one injection and one summary line per session instead of one per rescan. `ApplyNow`
deliberately has no such shortcut: it is the way to force a rebuild. A new game load re-arms
through the `Load` postfix, which clears the fingerprint, so a reload always re-injects.

Because the injection changes `LocalizationList.Count`, `SatoneSpeech.RefreshIndex` (event bus) rebuilds
its voice-name index on its next pass, so an injected row's `Voice` is visible to the event bus and
a spoken line reports our text.

## Smoke checks

| Id | What it proves |
|---|---|
| `p2.masterdata.injected` | Every merged row is present in the loader's collections. |
| `p2.masterdata.wrapper_reads` | `LocalizationList[id]` holds the pack's `En` **and** `Ja` exactly, and the game's own `LocalizationMasterWrapper.Get(data, English)` returns a non-empty string (reported, whatever it is). The wrapper's value is not asserted because `Get` is a legitimate extension point - the language feature postfixes it to serve a pack language's rows, which must not fail a master-data check. |
| `p2.masterdata.text_tables` | Every `data/subtitles/<code>.json` a loaded pack ships was read and has rows (a sample row per language is reported). Not yet observed when no pack ships one. |
| `p2.masterdata.scenario_playable` | The injected group is in `ScenarioGroupMasterList`, its novel rows are in `NovelMasterList`, every line it speaks resolves to a localization row, and its scenario type takes `StartReady`'s short path. **The scenario is not started** - `StartReady` would take over the story UI and Satone's animation, which a smoke pass must not do. |

The rows come from `packs/cwy.example/data/`; see its README for why the example group uses
`MotionScenario` and episode 900.

## Public API

```csharp
MasterDataFeature.Initialize(config);          // once, from the plugin's Awake
MasterDataFeature.IsInjected;                  // the packs' rows are in the game's master data
MasterDataFeature.Subtitles;                   // ISubtitleLookup: TryGet(localizationId, code, out text), IdsFor(code)
MasterDataFeature.Merged;                      // the merged rows and the merge report
MasterDataFeature.PackFingerprint;             // what the current injection was built from
MasterDataFeature.Reload("reason");            // re-read the packs; re-injects only if they changed
MasterDataFeature.ApplyNow("reason");          // re-inject what is already merged
MasterDataFeature.PackLinesAdmitted;           // pack lines: language-exclusive groups are injected
MasterDataFeature.AdmitPackLines(true, "who"); // pack lines: called by PackLinesFeature; re-injects when loaded
MasterDataFeature.LineScreen;                  // pack lines: accepted lines, refused and held-back groups
```
