# cwy.example

The reference content pack. It exists so a launch can prove that discovery, validation and the
game-version gate all work (`p1.packs.example_loaded`), and so a new pack can be made by copying
this folder and renaming it.

It ships two test voice clips (`audio/voice/`) and one spoken line as master data (`data/`). The
`textures/` folder is empty (a `.gitkeep` holds it): add your own PNGs there as described below.
Everything in this pack is made by the project; nothing is taken from the game.

Deploy it with `./deploy-packs.sh` from the repo root, then restart the game.

## Folder conventions

Everything below is relative to this folder. Paths are matched case-insensitively and always with
forward slashes. A file that matches nothing is carried in the index as `Other` and never loaded —
READMEs, source art and notes are safe to keep here.

| Path | What it is |
|---|---|
| `manifest.json` | Required. Identity, version and dependencies of the pack. |
| `textures/<rendererName>/<slot>.png` | A texture replacement, e.g. `textures/Body/_MainTex.png`. `<slot>` is a shader property or an alias (`base`, `rim`, `reflect`, …). See `src/CwyLs.ModLoader.Bulbul/Textures/README.md` for the renderer names, the full alias table and the restore semantics. |
| `textures/@<materialName>/<slot>.png` | A texture replacement by **material asset**, e.g. `textures/@mt_Tee_A/_MainTex.png`. Applies wherever that material is used — the only form that reaches a costume Satone is currently wearing, whose renderers are instantiated at runtime. |
| `textures/_rules.json` | Optional. Advanced mapping — `{"material":"mt_Tee_A",...}`, a material index other than 0, or a file whose name is not the slot. |
| `textures/_*/**` | Yours. A folder under `textures/` whose name starts with `_` is never read as a renderer, so source art and works in progress live here safely. |
| `audio/voice/*.ogg`, `audio/voice/*.wav` | Voice clips. The file name without its extension is the voice name the game looks up, so `Voice_Tutorial_1_001.ogg` replaces that line and any new name adds one. Direct children only. |
| `audio/music/*` | Music tracks. Direct children only; any audio extension. |
| `data/localization.json` | Localization rows, merged into the game's master data by id. |
| `data/novel.json` | Novel rows. |
| `data/scenario_groups.json` | Scenario group rows. |
| `data/subtitles/<code>.json` | Text for a language the game has no column for, as a flat object `localizationId -> text`. Shown only for a language some pack declares in `language.json`. |
| `language.json` | Optional. Declares a language this pack adds to the game; see `docs/guide/manifest.md` ("`language.json`"). |

Ambient sounds, pack music with `_tracks.json` and grading presets are shown by `packs/cwy.demo.audio`
and `packs/cwy.demo.scenery`, with their formats in `src/CwyLs.ModLoader.Bulbul/Audio/README.md` and
`src/CwyLs.ModLoader.Bulbul/Grading/README.md`.

`.gitkeep` files hold the empty folders in git. The loader ignores every dot-file, so they are never
mistaken for content.

## manifest.json

```json
{
  "id": "cwy.example",
  "name": "CWY Example Pack",
  "version": "0.1.0",
  "author": "CWY:LS",
  "description": "Reference pack: the folder layout every CWY content pack uses.",
  "gameVersion": "1.17.3",
  "loaderVersion": "0.1.0",
  "dependsOn": []
}
```

See `packs/README.md` for what each member means and which are optional.

## The same line, from code

`data/localization.json` + `data/novel.json` + `data/scenario_groups.json` are the authored form of
one spoken line; `Satone.Say(...)` (`src/CwyLs.ModLoader.Bulbul/Satone/Say/README.md`) is the
runtime form of the same thing. It builds exactly this row shape — a `LocalizationData`, a
`VoiceText` `NovelData`, an `EndScenario` `NovelData` and a `MotionScenario`
`ScenarioGroupData` — under generated ids (`cwy_say_<n>`, episode 9000 + n), adds them to the
game's master data and hands the group to the game's scenario reader, so a line your mod creates at
runtime is performed by the same code that performs this pack's `cwy_example_01`. Use the pack files
for lines you write ahead of time; use `Satone.Say` for lines that only exist at runtime.
