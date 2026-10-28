# Grading — Lightroom-style photo grading over the room

A preset is a JSON file of Lightroom-style sliders. CWY:LS Mod Loader maps it onto URP volume overrides
and renders it through **one global URP volume of its own**, layered **on top of** the game's own
time-of-day look. The game's look keeps changing underneath (day, sunset, night, cloudy); the preset
follows it, and can carry its own block per time of day.

- Core (pure, unit-tested): `CwyLs.ModLoader.Core/Grading/` — controls and ranges (`GradingControls`),
  the preset model and time-of-day/blend logic (`GradingPreset`), parser and writer
  (`GradingPresetParser`, `GradingPresetWriter`), the mapping (`GradingMapper`, `GradingToneCurves`,
  `HslAdjustment`), LUTs (`Lut3D`), preset lookup (`GradingCatalog`).
- This folder (Unity): `GradingFeature` (state, config, hot reload), `GradingVolume` (our volume),
  `GradingUrp` (URP/Beautify by reflection), `GradingLutTexture`, `GradingCommands` (`grade`),
  `GradingSmokeChecks`, `GradingPatchTargets`, `GradingFacetBackend` (mod facet `ICwyModGrading`).

Wired with one call from `BulbulPlugin.Awake`: `GradingFeature.Initialize(Config);`

## How it sits on the game's look

Observed in game 1.17.3 before anything was written:

| Fact | Detail |
|---|---|
| The game's looks are global URP volumes, one per time of day, priority 0, weight 1, layer 1 | one `Volume` under each `DateTime/{day,sunset,night,Cloudy,rain}` object of the room scene; a start-scene volume has priority 1 and weight 0 and is inactive |
| Their profiles hold **Beautify + LiftGammaGain only** | the day, sunset, night and cloudy profiles each pair a Beautify profile with a LiftGammaGain override; the start-scene profile holds Beautify only |
| The game switches looks by activating one `DateTime/<tod>` object | `WindowViewService.ChangeWeatherAndTime`; the current one is `WindowViewService.CurrentTimeOfDayType` |
| Room cameras render URP post-processing and see volumes on layers 0 and 1 | the main room camera and the three view-point cameras have post-processing on and a volume layer mask of layers 0 and 1 |
| Volumes update every frame | every quality level's URP pipeline asset updates volumes every frame, and the cameras follow the pipeline's setting |
| Grading is HDR, LUT size 32 | the same pipeline assets |
| Beautify runs **before** URP's post-processing | Beautify's renderer feature runs at `AfterRenderingTransparents` (500); URP post-processing runs at 550 |
| UI is never graded | the game's root canvases are Screen Space Overlay and its other canvases are nested under them; our own windows use `RenderMode.ScreenSpaceOverlay` (`UI/GameStyleCanvas.cs`). Overlay canvases are drawn after camera post-processing, and the cameras' culling mask excludes layer 5 (UI) |

So our volume:

- is a hidden `CWY.Grading` GameObject (DontDestroyOnLoad, layer 0, global, priority
  `[Grading] Priority` = 100), weight 1 (graded) or 0 (`grade ab` original);
- overrides **only URP components the game's profiles do not use** (ColorAdjustments, WhiteBalance,
  SplitToning, ShadowsMidtonesHighlights, ColorCurves, ColorLookup, Vignette, FilmGrain). Nothing
  overrides the game's LiftGammaGain or Beautify colour settings, so the day/sunset/night look is
  kept and graded on top: URP's colour grading LUT and user LUT run in the post pass **after**
  Beautify;
- touches Beautify for one thing only, bloom, as a **factor of the game's own bloom intensity**
  (read from the active game volume, re-read when the time of day changes), with every other
  Beautify parameter's override off;
- never edits a game profile asset: every apply builds a new in-memory profile, swaps it in and frees
  the old one a second later. `grade off`, `[Grading] Enabled=false` and quitting destroy it all.

What it covers: everything the room camera renders - scenery outside the window, the room, Satone.

## Pack format: `grading/<name>.json` (schema 1)

Any pack may ship presets as direct children of `grading/`. The player's own presets live in
`BepInEx/data/grading/` (`grade save`). Names: `warm-film` (the player's own wins, else the pack
loaded last), or exactly `cwy.demo.scenery:warm-film`, `user:mine`. Files starting with `_` are not
presets (`_export.json`).

```json
{
  "schema": 1,
  "name": "Warm film",
  "description": "one sentence",
  "blend": 1,
  "basic": { "exposure": 0.1, "contrast": -8, "highlights": -15, "shadows": 12, "whites": -12, "blacks": 35 },
  "whiteBalance": { "temperature": 14, "tint": 3 },
  "presence": { "vibrance": 12, "saturation": -6 },
  "hsl": { "oranges": { "hue": 0, "saturation": -8, "luminance": 6 }, "blues": { "luminance": -8 } },
  "splitToning": { "shadowHue": 190, "shadowSaturation": 28, "highlightHue": 38, "highlightSaturation": 32, "balance": 15 },
  "colorGrading": {
    "shadows": { "hue": 200, "saturation": 10, "luminance": 0 },
    "midtones": { "hue": 30, "saturation": 5, "luminance": 0 },
    "highlights": { "hue": 40, "saturation": 8, "luminance": 0 },
    "global": { "hue": 0, "saturation": 0, "luminance": 0 },
    "balance": 0
  },
  "toneCurve": { "master": [[0, 0], [60, 52], [190, 200], [255, 255]], "red": [[0, 0], [255, 250]] },
  "effects": {
    "vignette": { "amount": -22, "midpoint": 45, "roundness": 0, "feather": 70 },
    "grain": { "amount": 28, "size": 35 },
    "bloom": 10
  },
  "lut": { "file": "luts/film.cube", "amount": 80 },
  "night": { "basic": { "exposure": 0.2 }, "blend": 0.85 }
}
```

Every member is optional; an absent control leaves the game alone. Rules:

- `schema` is the format version. Missing → read as 1 (warning). Newer than this build → the file
  is refused (update CWY:LS Mod Loader).
- Out-of-range values are clamped (hues wrap) with a warning. A value of the wrong type costs that
  member (error). Unknown members are warnings; members starting with `x-` are yours and ignored.
- `toneCurve.<master|red|green|blue>`: 2-16 `[input, output]` points on 0..255 (display values, as
  in Lightroom), sorted for you; two points at the same input cost the curve. Left of the first and
  right of the last point the curve is flat (Lightroom's behaviour).
- `lut.file`: a 3D `.cube` (`LUT_3D_SIZE` 2..65, `DOMAIN_MIN/MAX` honoured; 1D LUTs refused) or a
  strip `.png` (width = size², height = size, Unity layout), relative to the preset's folder and
  inside it. Resampled to the pipeline's LUT size.
- `day`, `sunset`, `night`, `cloudy`: blocks with the same groups (not `schema`/`name`, no nesting).
  While the game shows that look, every control, curve channel or LUT the block sets replaces the
  base one.
- `blend` (0..1): how much of the preset is applied. Done on the sliders (each strength moves toward
  neutral, curve points toward the diagonal, LUT amount scales), not with the URP volume weight,
  because URP does not interpolate curves, LUT textures or switches.

### Lightroom settings with no faithful mapping

Recognised, warned about, ignored: `presence.clarity`, `presence.texture` (local contrast),
`presence.dehaze` (a local haze estimate), `effects.grain.roughness`, `effects.vignette.highlights`,
`effects.vignette.style`, `colorGrading.blending`, `toneCurve.parametric`. A global approximation of
clarity or dehaze would not look like Lightroom's, so there is none.

## Mapping table

Units are Lightroom's unless stated. Constants live in `GradingMapper` / `GradingToneCurves` /
`HslAdjustment` and are tuning choices, not physics.

| Control (id) | Range | URP target | Mapping |
|---|---|---|---|
| `basic.exposure` | -5..5 EV | ColorAdjustments.postExposure | 1:1 (EV; URP multiplies by 2^EV before the LUT) |
| `basic.contrast` | -100..100 | ColorAdjustments.contrast | × 0.5 (+100 → URP 50: log-space slope ×1.5 around mid grey) |
| `basic.highlights` | -100..100 | ShadowsMidtonesHighlights.highlights.w | + → +0.25 at 100 (×2 linear in the bright range), − → −0.5 at −100 (×0.5) |
| `basic.shadows` | -100..100 | ShadowsMidtonesHighlights.shadows.w | same as highlights, on the dark range |
| `basic.whites` | -100..100 | ColorCurves.master | levels: + clips white sooner (input 1 − 0.15·w → 1), − dims white (1 → 1 − 0.15·|w|) |
| `basic.blacks` | -100..100 | ColorCurves.master | levels: + lifts/fades black (0 → 0.15·b), − crushes (input 0.15·|b| → 0) |
| `whiteBalance.temperature` | -100..100 | WhiteBalance.temperature | 1:1 (+ warmer) |
| `whiteBalance.tint` | -100..100 | WhiteBalance.tint | 1:1 (Lightroom: + magenta; URP's sign is confirmed in the in-game pass) |
| `presence.vibrance` | -100..100 | ColorCurves.satVsSat | saturation × (1 + 0.6·v·(1−s)²) for +, × (1 + 0.8·v·(1−s)²) for −; greyish colours move most. No skin protection |
| `presence.saturation` | -100..100 | ColorAdjustments.saturation | 1:1 (−100 = grey, +100 = ×2) |
| `hsl.<band>.hue` | -100..100 | ColorLookup (baked LUT) | turns the band up to 30° toward its neighbour |
| `hsl.<band>.saturation` | -100..100 | ColorLookup | HSV saturation × (1 + v/100) |
| `hsl.<band>.luminance` | -100..100 | ColorLookup | linear colour × (1 + 0.6·v/100), weighted by saturation (greys never move) |
| `splitToning.shadowHue/Saturation` | 0..360 / 0..100 | SplitToning.shadows | HSL(hue, sat/100, 0.5); sat 0 = URP's neutral grey |
| `splitToning.highlightHue/Saturation` | 0..360 / 0..100 | SplitToning.highlights | same |
| `splitToning.balance` | -100..100 | SplitToning.balance | 1:1 (+ favours highlights) |
| `colorGrading.<shadows/midtones/highlights>.hue/saturation` | 0..360 / 0..100 | ShadowsMidtonesHighlights.<range>.xyz | linear multiplier = lerp(1, pure hue / its mean, sat/100 × 0.35), passed gamma-encoded |
| `colorGrading.<range>.luminance` | -100..100 | ShadowsMidtonesHighlights.<range>.w | as basic.highlights/shadows; added to them |
| `colorGrading.global.*` | | all three ranges | colour multiplied into, luminance added to, each range |
| `colorGrading.balance` | -100..100 | SMH shadowsEnd / highlightsStart | + moves both limits down (more highlights): end 0.3 − 0.15·b, start 0.55 − 0.25·b |
| `toneCurve.master` | points 0..255 | ColorCurves.master | composed after whites/blacks, mapped into URP's curve space (below) |
| `toneCurve.red/green/blue` | points 0..255 | ColorCurves.red/green/blue | mapped the same way |
| `effects.vignette.amount` | -100..100 | Vignette.color | − darkens: edge colour 1 − |a|/100; + lightens: 1 + 0.6·a/100 |
| `effects.vignette.midpoint` | 0..100 (50) | Vignette.intensity | 0.75 − 0.40·m/100 (low midpoint reaches further in) |
| `effects.vignette.roundness` | -100..100 (0) | Vignette.rounded | ≥ 50 → circular, else fitted to the screen (URP has no in-between) |
| `effects.vignette.feather` | 0..100 (50) | Vignette.smoothness | 0.1 + 0.9·f/100 |
| `effects.grain.amount` | 0..100 | FilmGrain.intensity | /100 |
| `effects.grain.size` | 0..100 (25) | FilmGrain.type | 10 textures, Thin1 (0) … Large02 (100); response fixed 0.8 |
| `effects.bloom` | -100..100 | Beautify.bloomIntensity | game's current value × (1 + b/100): −100 off, +100 doubled |
| `lut.amount` | 0..100 (100) | ColorLookup | the LUT file's share, mixed with the HSL bake |
| `blend` | 0..1 (1) | every control | see above |

**The tone-curve domain.** URP's HDR LUT builder evaluates the YRGB curves on
`FastTonemap(linear) = c / (1 + max(r, g, b))`, so display white (linear 1) is curve input 0.5 and
the upper half is HDR headroom. A Lightroom point (x, y) is placed at (f(x), f(y)) with
f(v) = L/(1+L), L = sRGB→linear(v/255); the headroom from (0.5, value at white) to (1, 1) is a
straight segment (an identity when white is untouched). Exact for greys; for saturated colours the
shared `max` makes it an approximation. Keys get Fritsch-Carlson (monotone, no overshoot) tangents.

**The baked LUT.** HSL and `lut.file` are composed into one ColorLookup strip at the pipeline's LUT
size (32): `out = lerp(hsl(c), file(hsl(c)), amount)`, HSL first, the LUT file last. URP applies it
after the internal grading LUT (HDR grading) in sRGB space, contribution 1. Baked once per change
(cached by content), ~33k evaluations.

## Console

```
grade                         status: preset, time of day, components, volume
grade list                    presets (* = applied)
grade apply <name>            apply and remember ([Grading] Preset)
grade off                     remove the grading and forget it
grade set <control> <value>   one slider; ids or aliases (exposure, temp, blues.lum, vignette, grain, bloom, split.highlightHue ...)
grade save <name>             BepInEx/data/grading/<name>.json, then applied as user:<name>
grade ab                      ORIGINAL (game's look) <-> GRADED, instantly
grade export                  the current values as JSON (also BepInEx/data/grading/_export.json)
grade controls [filter]       every control, range and alias
grade reload                  re-read the preset file now
```

Each change prints one line (tag `TEXTURE` until a dedicated tag exists):
`TEXTURE  grade set basic.exposure=+0.3  look=ColorAdjustments+SplitToning timeOfDay=night`.
`grade set` edits the applied preset in memory (or starts `custom`); `grade save` keeps it. Editing
the applied file on disk re-applies it within `[Grading] WatchPollSeconds` (1 s) and drops unsaved
`grade set` changes.

## Config (`cwyls.modloader.bulbul.cfg`)

| Key | Default | Effect |
|---|---|---|
| `Grading.Enabled` | `true` | Off removes our volume (the game's own look); on re-applies the preset. |
| `Grading.Preset` | empty | The preset applied when the room comes up; set by `grade apply`/`save`, cleared by `grade off`. |
| `Grading.Priority` | `100` | Our volume's priority (2..10000); above the game's 0 and 1. |
| `Grading.WatchPollSeconds` | `1` | Hot-reload check of the applied file and its LUT; 0 = off. |

## Fail soft

- URP's volume machinery or any URP component/field above missing → one `TEXTURE` warning
  (`grading off: URP volume machinery not found reason=…`), the `grade` command answers
  "unavailable", nothing else changes. The members are also patch targets (`grading.*`), so
  `PATCH TARGET MISSING` names them at startup and in seamcheck.
- Beautify missing or its `bloomIntensity` renamed → one warning, only the bloom control is off.
- A LUT file that is missing or unreadable → one warning per preset and problem; HSL still applies.
- A preset that fails to parse on reload → one warning; the current look stays.

## Known limits (check in the in-game pass)

- **Film grain and URP bloom need shader variants.** URP's grain is a keyword (`_FILM_GRAIN`) in
  UberPost. The game's Beautify profiles have Beautify's automatic build optimisation on (it strips
  Unity post-processing variants the profiles do not use, and they use no URP grain). If grain is not
  visible in game, the variant was stripped from the build and cannot be added at runtime; everything
  else (colour grading, curves, LUT, vignette) is uniform-driven and needs no variant. That is why
  bloom goes through Beautify (compiled and used by the game), not URP Bloom.
- Basic highlights/shadows are global (URP's range-based SMH), not Lightroom's local tone mapping.
- `tint` sign: Lightroom's + is magenta; URP's direction is not documented explicitly - confirm with `grade set tint 60` (expect magenta).
- One scene is graded for every URP camera whose volume mask includes layer 0 (all room cameras).

## Smoke checks

| Id | Kind | Proves |
|---|---|---|
| `d.grading.backend` | quiet | URP types and members bound (else the reason) |
| `d.grading.volume` | quiet | with a preset applied: `CWY.Grading` exists and the plan rendered components (vacuous without a preset) |
| `d.grading.camera` | quiet | the main camera renders post-processing and its volume mask includes our layer |
| `d.grading.ab` | loud | `ShowOriginal(true)` → weight 0, `false` → weight 1, restored afterwards |

Trace: `sys=grading` events `apply`, `set`, `reload`, `timeOfDay`, `room ready`, `enabled`, `off`,
`save`, `ab`, `export`, `unavailable`, `beautify`.
