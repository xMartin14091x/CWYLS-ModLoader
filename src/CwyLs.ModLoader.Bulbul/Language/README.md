# Language — languages declared by content packs

A content pack can add a language to the game: it declares the language in `language.json`, ships
its rows in `data/subtitles/<code>.json`, names the game's font assets that draw it, and may ship a
mark-positioning table. The framework does the rest. Nothing here knows any particular language;
the `language.json` schema is in `docs/guide/manifest.md`.

Entry point: `LanguageFeature.Initialize(Config)`, called once from `BulbulPlugin.Awake`, after
MasterData and just before `SubtitleFeature.Initialize` (the text seam and the reading-time hold
read the registry). Idempotent: a second call is logged at debug level and ignored.

| File | Role |
|---|---|
| `LanguageFeature.cs` | Config, startup, the public API (`Registry`, `Active`, `CurrentCode`, `Select`) |
| `LanguagePackReader.cs` | Reads every loaded pack's `language.json` and mark table into a `Core.Language.LanguageRegistry` |
| `LanguagePatches.cs` | The Harmony patches below |
| `LanguageSelector.cs` | One entry per pack language in the game's own selector |
| `LanguageSelectorList.cs`, `LanguageListScroller.cs` | The selector's list scrolls inside the game's open height |
| `LanguageFonts.cs` | Font reachability and mark-table installation |
| `LanguagePatchTargets.cs` | 9 mandatory + 18 optional seams, verified at startup |
| `LanguageSmokeChecks.cs` | `pb.language.*`, all read-only |

Pure logic (unit-tested) lives in `src/CwyLs.ModLoader.Core/Language/` — `GameLanguages`,
`LanguageCodes`, `LanguageDeclaration(Parser)`, `LanguageRegistry`, `PackLanguage`, `LanguageText`,
`LanguageChoice`, `LineBreakHelpers`, `SelectorListLayout` — and
`Core/Subtitles/{MarkTable,MarkPlacement,ReadingTime}.cs`.

## Runtime values

Each declared language gets a runtime `Bulbul.GameLanguageType` value: 100 for the first pack in
`PackManager.Loaded` order, 101 for the next. The game never enumerates the enum and never indexes an
array by it (game 1.17.x), so an undefined value is safe as long as every `switch` that reads it
is handled — which is what the patches do. The first pack to declare a code keeps it; a later one is
refused (`pb.language.registry` fails and names both packs). Values are only stable within a session;
the player's choice is stored as a code.

## What makes it safe

| Seam | Patch | Why |
|---|---|---|
| `FontSupplier.GetFontAsset` / `GetFontNormalMaterial` / `GetFontGradationMaterial` / `GetFontUnderlayMaterial` | prefix: pack value → base language | the originals log an error and return null; a null material throws in `TMP_Text.fontMaterial` for every label |
| `SettingData.SaveReady` | postfix: `_gameLanguageForSave` → base language | the save file never holds a pack value, so the game without the framework still opens |
| `SaveDataManager.LoadSetting` | postfix: re-apply the chosen pack language | before any UI reads it |
| `LanguageSupplier.Set` | postfix: record the choice in `Language.Selected` | every selector click lands here |
| `SettingUI.Setup` | postfix: add selector entries | see below |
| `TalkSpeedData.GetSpeed` | prefix: base language | the original logs an error per line |
| `NewsMaster.GetNewsDatas` | prefix: base language | `News.AsNewsData` throws for an unknown value |
| `TutorialView.OpenTutorial` | postfix: fill the null page image with the base language's | the switch gives `null` |
| `Bulbul.Web` `GetNews`, `GetNewsCurrent`, `Login`, `CheckLogin`, `StartupCheck`, `CheckPurchase` constructors | prefix: base language (`Language.SafeWebLanguage`) | the server would receive `l=100` |

The base language is the first of the game's own languages in the declared fallback chain (English
for Thai). The first four rows are mandatory: if one is missing after a game update, no pack
language is applied and the game stays in its own languages.

## The selector entry

A clone of the English entry (`_languageENInteractableUI`) under the same `Content` object, named
`LanguageSelectButton_cwy_<code>`, caption = the declared endonym, click =
`SettingService.ChangeGameLanguage(value)` + the click sound (the game's own two calls). The game's
highlight/caption code is a local function that cannot be patched by name, so the entry's highlight
and the pulldown caption are set from our own subscription to `SettingData.GameLanguage`, made after
the game's.

## The list scrolls

The game sized the pulldown for its seven rows (`_openPullDownSizeDeltaY` 338.4), and the pulldown
sits inside the settings page's scroll view, whose `Viewport` `Mask` ends just below that open list.
Growing the pulldown by a row (the first version) put the eighth row under that mask: only its top
edge showed, and only that edge could be clicked. Now the pulldown keeps the game's open height,
animation and position, and the list scrolls inside it, like the game's own to-do list picker
(`UI_FacilityTodo/TodoGroupSelect/PullDown/Scroll View`).

```
Pulldown  (the game's: Image + Mask, height tweened 40 <-> 338.4)
├─ TodoGroupText (TMP)            the game's caption
└─ LanguageList_cwy               ScrollRect + transparent Image (wheel/drag in the row gaps)
   ├─ LanguageListViewport_cwy    RectMask2D, exactly where Content was, as tall as the game's 7 rows
   │  └─ Content                  the game's (VerticalLayoutGroup + ContentSizeFitter), moved in
   └─ LanguageListScrollbar_cwy   copy of the settings page's scrollbar, 14.5 wide, 8.2 from the right
```

| Piece | File | Notes |
|---|---|---|
| Layout maths | `Core/Language/SelectorListLayout.cs` | viewport = the game's rows (or every row when they fit), content = every row, `Reveal(row, offset)`, `GrownOpenHeight` (fallback); unit-tested |
| Arrangement | `LanguageSelectorList.cs` | checks, measures, builds; puts `Content` back if the build fails |
| Scroll to the selection | `LanguageListScroller.cs` | on each open (`PulldownListUI.IsOpen` edge, `LateUpdate`): offset 0, then just far enough that the highlighted row is whole |

Scroll settings (sensitivity 50, inertia, deceleration 0.135, clamped) are read from the settings
page's `ScrollRect`; the scrollbar only shows when the rows do not fit (`AutoHide`).

**Fallback.** Checked before anything changes: `_pullDownParentRect` and `_openPullDownSizeDeltaY`
readable, `Content` inside that frame, a top-to-bottom `VerticalLayoutGroup`, a vertical
`ContentSizeFitter`, pivot y = 1, no scale or rotation up to the frame, at least one row of room. If
one fails (a game update), or the build throws, the pulldown grows as before and one line is logged:
`LANGUAGE the language list cannot scroll (<reason>); making the pulldown taller instead, …`.
`pb.language.entry` then fails with the same reason.

## Fonts and the mark table

**Built-in Thai table (0.9.1).** After the pack languages, `LanguageFonts.ApplyBuiltIn` installs the
loader's own Thai table (`Core/Subtitles/BuiltInMarkTables.cs`, embedded `thai-marks.json`, 113 pairs
from the font's GPOS `mkmk`) into `Base_TH` and `Dynamic_TH`, with or without a language pack, so Thai
anywhere the game's Thai font draws it (pack lines, subtitles, mod windows) has its marks placed. Pack
tables go first and an installed pair is never replaced, so a pack's own table wins. Console/trace:
`built-in Thai marks: Base_TH: 113 record(s) … (113 installed); Dynamic_TH: …`. It applies only to
assets of the table's family, so a game update that swaps the Thai font gets nothing rather than
wrong offsets.

At `RoomReady`, for each language: its named assets are found among the loaded font assets; the
first one is made reachable from `FontSupplier.GetFontAsset(base)` and each story label's font
(appended to a fallback table only where TMP's fallback search would not reach it — for Thai it
already does: `Base_JP → … → Base_TH`); and the mark table is installed into every named asset as
TMP pair-adjustment records (second glyph raised, first glyph untouched), inserted at list index 0
and into the lookup dictionary. Only assets whose `faceInfo.familyName` equals the table's font
family get records. Everything is in memory and removed on quit.

Which asset draws Thai in the story box: `Base_TH` (static). TMP's depth-first fallback search from
`Base_JP` reaches `Base_TH` before any dynamic asset — checked against TMP's own fallback order
(`TMP_FontAssetUtilities.GetCharacterFromFontAsset_Internal`) for the game's font assets.

## Configuration (`[Language]` in the adapter's .cfg)

| Key | Default | Meaning |
|---|---|---|
| `Enabled` | `true` | Off: no patches, no entries; the game keeps its seven languages. |
| `Selected` | `""` | The chosen pack language's code; written by the framework. Empty follows the game. A code no loaded pack declares is kept (the game uses its saved language) until the player picks another. |
| `SafeWebLanguage` | `true` | The six web requests carry the base language. |
| `MarkPlacement` | `Full` | `Full` (the font's own mkmk design: raise and shift), `VerticalOnly`, or `Off`. |

## Save compatibility

| Scenario | Result |
|---|---|
| Framework on, Thai chosen | memory 100; save file English; `Language.Selected=th` |
| Framework removed afterwards | the game reads English |
| Framework back | `LoadSetting` postfix restores Thai |
| the language pack removed or broken | the game opens in the saved English; `Selected=th` is kept for when the pack returns |
| Mandatory patch target missing | no pack language applied; `PATCH TARGET MISSING` logged |

## Diagnostics

| Id | Passes when |
|---|---|
| `pb.language.registry` | every `language.json` parsed and registered (fails naming each refusal) |
| `pb.language.patches` | the mandatory patches are in place; reports web/save rewrite counts |
| `pb.language.entry` | each language has a wired selector entry with its caption, and the list scrolls inside the game's open height so every entry can be brought whole into view (not yet observed before the settings window is set up) |
| `pb.language.save_guard` | after `SettingData.SaveReady()` (in memory only, no file written) the save field is not a pack value |
| `pb.language.text` | for each language, `LocalizationMasterWrapper.Get(row, value)` returns the pack's row, and an untranslated row returns the base language's text |
| `pb.language.fonts` | the named assets are loaded; the story label draws the table's first moving mark with an asset that holds the records |

Console lines (`LANGUAGE`): `selected th (persisted as en)`, `selector entry added caption=ไทย
index=7 pulldown=338.4 list=scroll` (`pulldown` = the open height, `list` = `scroll` / `fits` /
`grown`; the trace file also has `rows`, `viewport`, `maxOffset`, `listTop`, `scrollbar`),
`th fonts: Base_TH, Dynamic_TH marks=…`, `applied th over the saved en`. Each time the list opens,
the trace file gets `language/list.opened` (`row`, `offset`, `maxOffset`, `whole`, and `pageCut` =
units of the visible list below the settings page's viewport at that moment; 0 = all of it can be
seen and clicked).

## Not handled (known)

- Dates and times format as `en-US` (`LanguageSupplier.GetCultureInfo` default branch).
- The seven built-in captions and the version string are literals, not localized rows.
- A pack cannot ship its own font file yet (`"font": {"file": …}` is refused with a message).
- Thai: a lower vowel under ฐ or ญ can touch the letter; the font fixes it by GSUB glyph substitution,
  which TextMeshPro does not run.
- `Steam` language auto-detection for a pack language is not implemented.
