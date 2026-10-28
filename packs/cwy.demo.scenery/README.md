# cwy.demo.scenery — grading presets and a desk recolour

| File | What it does |
|---|---|
| `grading/warm-film.json` | Warm film stock: teal shadows / warm highlights (split toning), faded blacks, soft contrast, muted greens, grain, gentle vignette, a little more of the game's bloom. Brighter at night (`night` block). |
| `grading/rainy-evening.json` | A cool rainy evening: blue-grey white balance, muted colour (HSL: yellows and greens down, aquas up), cool midtones, a blue toe in the blue curve, heavier vignette, less bloom. Lighter by day, softer at night (`day` / `night` blocks). |
| `textures/_rules.json` | Walnut desk: tints `mt_desk_1` (the PC desk), `mt_desk_2` (desk skin 2) and the lamp table's `mt_desk` / `mt_desk_dark`. |

Both presets sit **on top of** the game's own day/sunset/night look and follow it. Nothing here
replaces a game file; everything is restored by `grade off`, `scenery restore`, or removing the pack.
Format and mapping: `src/CwyLs.ModLoader.Bulbul/Grading/README.md`; desk/scenery rules:
`src/CwyLs.ModLoader.Bulbul/Textures/README.md`. To find materials you can target, type
`scenery scan` (or `scenery scan <pattern>`) in the console: it lists the loaded scenery, desk and
window materials.

## See the difference (`grade ab`)

1. Open the room and the console (**Ctrl+Shift+C**).
2. `grade list` — shows `cwy.demo.scenery:warm-film` and `cwy.demo.scenery:rainy-evening`.
3. `grade apply warm-film` — the room turns warm with teal shadows, grain and a soft vignette. One
   `TEXTURE grade apply cwy.demo.scenery:warm-film ...` line names the URP components in use.
4. `grade ab` — **instantly** back to the game's own look (`grade ab ORIGINAL ...`). `grade ab` again —
   graded (`grade ab GRADED ...`). Flip a few times while looking at Satone, the window and the desk.
5. `grade apply rainy-evening`, then `grade ab` the same way.
6. Change the time of day in the game (window view: day → sunset → night): the grade follows the
   game's look; `rainy-evening` switches to its `day`/`night` blocks (`grade ... follows night`).
7. Tweak live: `grade set temp -40`, `grade set split.highlightSaturation 45`, `grade set blues.lum -40`,
   `grade set vignette -50`, `grade set blend 0.5` — one line per change. `grade export` prints the
   values as JSON; `grade save my-look` keeps them as `user:my-look` (applied at the next start too).
8. `grade off` — everything removed, the game's look is back and stays back after a restart.

## The desk

The walnut tint is applied with the textures pass when the room loads. `scenery list` shows the four
rules; `scenery scan mt_desk*` shows the live `_Color` values; `scenery restore` puts the game's
colours back (`scenery apply` re-applies). Changing the desk skin in the game's decoration menu keeps
the tint (the pass re-runs after every decoration change).
