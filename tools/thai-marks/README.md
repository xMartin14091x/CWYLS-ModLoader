# tools/thai-marks — the built-in Thai mark-positioning table

`derive.py` reads the game's own Thai font, `IBMPlexSansThaiLooped-Medium.otf`, and writes
`src/CwyLs.ModLoader.Core/Subtitles/thai-marks.json`: the table the loader embeds and installs into the
game's Thai font assets (`Base_TH`, `Dynamic_TH`) with or without a language pack. Numbers only; the
font itself is never copied into this repository.

```
python3 tools/thai-marks/derive.py --font <path to the game's IBMPlexSansThaiLooped-Medium.otf>
python3 tools/thai-marks/derive.py --font <...> --check    # exit 1 if the built-in table is stale
python3 tools/thai-marks/derive.py --font <...> --out X    # write somewhere else
```

Needs `fontTools` (`python3 -m pip install fonttools`), HarfBuzz's `hb-shape` (Homebrew `harfbuzz`) and the
game's own copy of the font: use the font from your installed game, because the table records its glyph ids.
Run it again after a game update that changes the font.

## Why the table exists

TextMeshPro 3.0.9 runs none of the font's GSUB, `mark` or `mkmk` features. A tone mark
(U+0E48–U+0E4B) that follows an upper vowel (ั ิ ี ึ ื ็ ํ) is therefore drawn at its default
height, inside the vowel. The one positioning TMP honours is a pair-adjustment record's
`yPlacement`/`xPlacement` on the second glyph (`TextMeshProUGUI.cs:1701-1707`, `:1762`).

## Rules

- **F-1** Offsets come from GPOS lookup type 6 (mark-to-mark) with lookup flag `0x200` (the upper-mark
  attachment class). Exactly one such lookup must exist, or the script refuses to guess.
- **F-2** `dx`/`dy` = mark2 anchor − mark1 anchor, in font units (`unitsPerEm` must be 1000). The
  `fallbackGap` field (30) is recorded for a future metric fallback; the runtime does not read it.
- **F-3** `font.family`/`font.style` are the typographic names (name IDs 16/17), which is what TMP's
  `faceInfo.familyName` reports (`Base_TH.asset`: `IBM Plex Sans Thai Looped` / `Medium`). The runtime
  applies the table only to font assets of that family.
- **F-4** Output is deterministic (sorted by code point); `--check` compares byte-for-byte.

The runtime side is script-neutral: `CwyLs.ModLoader.Core.Subtitles.MarkTable` parses the file,
`MarkPlacement` plans the records per font asset, and `CwyLs.ModLoader.Bulbul.Language.LanguageFonts`
installs them into every font asset the language declares (`Base_TH`, `Dynamic_TH`).
