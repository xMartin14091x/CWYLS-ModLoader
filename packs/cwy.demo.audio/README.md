# cwy.demo.audio — hear pack audio at work

A content pack that changes two game sounds and adds one song, so you can **hear** that CWY:LS Mod Loader
really swapped them — and flip back to the game's own sound at any moment to compare.

| File | What it does |
|---|---|
| `audio/ambient/LightRain.wav` | Replaces the game's **Light Rain** ambience (the `LightRain` row). |
| `audio/ambient/HeavyRain.wav` | Replaces the game's **Rain** ambience (the `HeavyRain` row; the game's menu calls it just *Rain*). |
| `audio/music/rainy-desk.ogg` + `_tracks.json` | Adds **Rainy Desk (CWY demo)** to the music list (category *Other*). |

Nothing here comes from the game. The rain and the song were synthesised by the project's own
scripts: no samples, no downloads, no third-party sound assets. The game's own rain loops were
only measured (length and loudness), so the new loops can match them; none of their audio is in
this pack.

## How the new rain differs (on purpose)

- **Same length, rate and loudness as the original** (32.000 s / 24.576 s at 48 kHz; −29.8 / −14.4 LUFS),
  so an A/B compares the *sound*, not the volume. Each loop is seamless by construction.
- **Different texture:** wider stereo, audible separate droplets, and every second or so a rounder
  "plink" of water dripping off a window sill. Heavy rain adds a soft roof rumble. The game's rain is a
  smoother, more centred hiss.

## Compare original and pack in the game

1. Start the game with this pack deployed (`./deploy-packs.sh`, game closed). In `BepInEx/cwy-console.log`
   look for `AUDIO  ~ ambient/LightRain <- cwy.demo.audio/audio/ambient/LightRain.wav` and
   `AUDIO  + music 'Rainy Desk (CWY demo)' <- cwy.demo.audio`.
2. In the room, open **Environment → Scenery and Sound → Ambient Sounds**, switch **Light Rain** on and turn its
   volume up.
3. Open the console (**Ctrl+Shift+C**) and type:

   | Command | You hear |
   |---|---|
   | `audio ab LightRain original` | the **game's own** light rain (the loop keeps its position) |
   | `audio ab LightRain pack` | **this pack's** light rain |
   | `audio ab LightRain` | toggles between the two |
   | `audio ab all original` / `audio ab all pack` | every replaced sound at once |
   | `audio list replaced` | each replaced row: original length, pack clip, which one is selected, which one is playing |
   | `audio off` / `audio on` | every row back to the game's clips / the replacements back |

   Each switch prints one line, e.g. `AUDIO  ab ambient/LightRain -> original  mode=moved players=1 pos=12.4s`.
4. **Rain** (the `HeavyRain` row) works the same way: switch it on, then `audio ab HeavyRain original` / `pack`.
5. Open the music list: **Rainy Desk (CWY demo)** is at the end. Play it like any other track. `audio music`
   lists it with its `cwy-…` id.

The A/B choice is not saved: every launch starts with the pack sounds. The game's save never contains
the pack track's id (favourite it freely — CWY:LS Mod Loader remembers that in `BepInEx/data/audio/pack-music.json`,
and Steam Cloud only ever sees a vanilla music save).

## Remove it

Delete `CWYLS Mods/cwy.demo.audio/` (game closed). The game's own rain and music list come back exactly
as before; nothing in the game files or the save was changed.
