#!/usr/bin/env python3
"""Derive the loader's built-in Thai mark table from the game's Thai font.

TextMeshPro 3.0.9 implements one piece of OpenType positioning - the GPOS *pair* adjustment
table - and none of GSUB, `mark` or `mkmk`.
So a Thai tone mark (U+0E48-U+0E4B) that follows an upper vowel (ิ ี ึ ื ั ็ ํ) is drawn at its
own default height, inside the vowel, and disappears.

This script reads the displacement the font itself asks for - the `mkmk` (mark-to-mark) anchors of
the upper-mark attachment class - and writes them out as a table of per-mille offsets. The table
is embedded in the loader (`src/CwyLs.ModLoader.Core/Subtitles/thai-marks.json`, read by
`BuiltInMarkTables`) and installed with or without a language pack; a pack may still name its own
table in its `language.json` "marks" member. At runtime the framework (`CwyLs.ModLoader.Core.Subtitles.MarkPlacement` + `Bulbul/Language/LanguageFonts`)
turns each row into a TMP pair-adjustment record whose *second* glyph carries the offset, which is
the one lever TMP does honour.

Letter rows (2026-10-03): for ป ฝ ฟ ฬ with a mark above and ฎ ฏ with a vowel below, the font moves
the mark (and often swaps in a variant glyph) through GSUB + the GPOS `mark` feature. Those rows are
measured with HarfBuzz (`hb-shape`, Homebrew `harfbuzz`), which runs the font's real rules.

    python3 tools/thai-marks/derive.py --font <game's IBMPlexSansThaiLooped-Medium.otf>           # write the table
    python3 tools/thai-marks/derive.py --font <...> --check   # exit 1 if the checked-in file is stale

The output is byte-for-byte deterministic: every collection is sorted by code point before it is
written and nothing depends on dict or filesystem iteration order, so running it twice produces no
diff. See README.md for every rule and for how the numbers were cross-checked.
"""

from __future__ import annotations

import argparse
import json
import os
import shutil
import subprocess
import sys

try:
    from fontTools.pens.boundsPen import BoundsPen
    from fontTools.ttLib import TTFont
except ImportError:  # pragma: no cover - the message is the whole point
    print(
        "fontTools is required: python3 -m pip install fonttools",
        file=sys.stderr,
    )
    raise

# --------------------------------------------------------------------------------------------
# Locations. Everything is relative to the repository root, two levels above this file.
# --------------------------------------------------------------------------------------------

REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

FONT = os.path.join(
    REPO, "dump/project/ExportedProject/Assets/Font/IBMPlexSansThaiLooped-Medium.otf"
)
OUTPUT = os.path.join(REPO, "src/CwyLs.ModLoader.Core/Subtitles/thai-marks.json")

SCHEMA = 1

# GPOS lookup type 6 is mark-to-mark attachment. Lookup flag 0x200 is
# MarkAttachmentType = 2, which in this font's GDEF is the *upper* mark class - the one that
# stacks a tone mark on a vowel. The sibling lookup with flag 0x100 (class 1) is the below-mark
# case (ุ ู), which TMP already places acceptably and which this fix deliberately leaves alone.
MKMK_LOOKUP_TYPE = 6
UPPER_MARK_LOOKUP_FLAG = 0x200

# The Thai Unicode block. A glyph outside it (uniEC07, the `.narrow`/`.small` variants, the
# precomposed uni0E310E4A) has no code point a string can contain, so no pair record can ever be
# keyed on it: TMP looks records up by the glyph the *cmap* produced.
THAI_BLOCK = (0x0E00, 0x0E7F)

# The marks that may FOLLOW - the ones this fix moves: the four tone marks, thanthakhat and
# nikhahit. Every one of them is drawn above the line and every one of them is what a user types
# after a vowel.
FOLLOWING_MARKS = (0x0E48, 0x0E49, 0x0E4A, 0x0E4B, 0x0E4C, 0x0E4D)

# Mark-to-base on the letters whose own shape is in the way (2026-10-03, user: ป่า and ป๋า wrong in
# game). TMP draws every mark at its default spot over the letter; the font moves it - and often
# swaps in a narrower variant - with GSUB + the GPOS `mark` feature, which TMP does not run. These
# rows are measured by HarfBuzz itself (`hb-shape`, the reference shaper): for letter + mark, the
# offset that puts the cmap glyph TMP draws where HarfBuzz draws the font's choice (same glyph:
# HarfBuzz's offset; variant glyph: centres aligned horizontally, the edge facing the letter
# aligned vertically). Stacked marks on these letters (ปั่น: 4 in the whole translation) keep the
# mark-to-mark row of the vowel and are only approximately placed.
TALL_CONSONANTS = (0x0E1B, 0x0E1D, 0x0E1F, 0x0E2C)  # ป ฝ ฟ ฬ - ascender on the right
UPPER_MARKS = (0x0E31, 0x0E34, 0x0E35, 0x0E36, 0x0E37, 0x0E47,
               0x0E48, 0x0E49, 0x0E4A, 0x0E4B, 0x0E4C, 0x0E4D)
DESCENDER_CONSONANTS = (0x0E0E, 0x0E0F)  # ฎ ฏ - tail below the line (ญ ฐ need a letter swap: not done)
LOWER_MARKS = (0x0E38, 0x0E39, 0x0E3A)
BASE_MIN_SHIFT = 5  # per mille; a smaller correction is rounding noise and is not written

# Extra gap, in per mille of the em, used by the metric fallback formula (README rule F-2). It is
# recorded in the file so the C# fallback and this script cannot drift apart.
FALLBACK_GAP_PER_MILLE = 30


def load_font() -> TTFont:
    if not os.path.isfile(FONT):
        raise SystemExit(f"font not found: {FONT}")
    return TTFont(FONT)


def code_points(font: TTFont) -> dict:
    """Glyph name -> code point, from the font's own cmap. Last writer would win, so the lowest
    code point is kept: a glyph reachable by two code points is still one glyph."""
    mapping: dict = {}
    for code, name in font.getBestCmap().items():
        if name not in mapping or code < mapping[name]:
            mapping[name] = code
    return mapping


def mkmk_subtable(font: TTFont):
    """The one mark-to-mark subtable for the upper-mark class. Refuses to guess: if the font ever
    grows a second one, that is a real change and the operator must look at it."""
    found = []
    for index, lookup in enumerate(font["GPOS"].table.LookupList.Lookup):
        if lookup.LookupType != MKMK_LOOKUP_TYPE:
            continue
        if lookup.LookupFlag != UPPER_MARK_LOOKUP_FLAG:
            continue
        for subtable in lookup.SubTable:
            found.append((index, lookup.LookupFlag, subtable))

    if len(found) != 1:
        raise SystemExit(
            f"expected exactly one GPOS type {MKMK_LOOKUP_TYPE} lookup with flag "
            f"0x{UPPER_MARK_LOOKUP_FLAG:X}, found {len(found)} - the typeface changed, re-read "
            "the font's GPOS lookups before trusting this table"
        )

    index, flag, subtable = found[0]
    if subtable.Format != 1:
        raise SystemExit(f"unsupported MarkMarkPos format {subtable.Format}")
    return index, flag, subtable


def bounds(font: TTFont, name: str):
    """(bottom, top) of a glyph's ink, in per mille of the em. An empty glyph reports (0, 0)."""
    glyph_set = font.getGlyphSet()
    pen = BoundsPen(glyph_set)
    glyph_set[name].draw(pen)
    if pen.bounds is None:
        return 0, 0
    _, y_min, _, y_max = pen.bounds
    return int(round(y_min)), int(round(y_max))


def ink(font: TTFont, name: str):
    """(x_min, y_min, x_max, y_max) of a glyph's ink, in per mille of the em."""
    glyph_set = font.getGlyphSet()
    pen = BoundsPen(glyph_set)
    glyph_set[name].draw(pen)
    return pen.bounds or (0, 0, 0, 0)


def shape(text: str) -> list:
    """HarfBuzz's glyphs for a string: [{g, dx, dy, ax, ...}]."""
    tool = shutil.which("hb-shape")
    if tool is None:
        raise SystemExit("hb-shape is required for the letter rows: brew install harfbuzz")
    out = subprocess.run([tool, "--output-format=json", "--no-glyph-names", FONT, text],
                         check=True, capture_output=True, text=True).stdout
    return json.loads(out)


def base_pairs(font: TTFont):
    """(letter, mark) rows from HarfBuzz, and the letters they need in the "marks" glyph list."""
    cmap = font.getBestCmap()
    order = font.getGlyphOrder()
    rows, letters = [], set()
    for letters_of, marks_of, upper in ((TALL_CONSONANTS, UPPER_MARKS, True),
                                        (DESCENDER_CONSONANTS, LOWER_MARKS, False)):
        for letter in letters_of:
            for mark in marks_of:
                glyphs = shape(chr(letter) + chr(mark))
                default = font.getGlyphID(cmap[mark])
                # The font may swap the letter for an alternate of the same width (ฎ ฏ ฬ do); the
                # mark's offset is measured from the pen after the letter, so it still holds. A
                # different width, or more or fewer glyphs, is a change a pair cannot express.
                if len(glyphs) != 2 or glyphs[0]["ax"] != font["hmtx"][cmap[letter]][0]:
                    continue
                drawn = glyphs[1]
                if drawn["g"] == default:
                    dx, dy = drawn["dx"], drawn["dy"]
                else:
                    want = ink(font, order[drawn["g"]])
                    have = ink(font, cmap[mark])
                    dx = drawn["dx"] + (want[0] + want[2]) / 2 - (have[0] + have[2]) / 2
                    dy = drawn["dy"] + (want[1] - have[1] if upper else want[3] - have[3])
                dx, dy = int(round(dx)), int(round(dy))
                if abs(dx) < BASE_MIN_SHIFT and abs(dy) < BASE_MIN_SHIFT:
                    continue
                rows.append({"preceding": f"{letter:04X}", "mark": f"{mark:04X}", "dx": dx, "dy": dy})
                letters.add(letter)
    return rows, letters


def build() -> dict:
    font = load_font()
    units_per_em = font["head"].unitsPerEm
    if units_per_em != 1000:
        # Every number below is "per mille"; with a different em the anchors would need scaling
        # and the C# side's unitsPerEm handling would be reading a stale assumption.
        raise SystemExit(f"expected unitsPerEm 1000, found {units_per_em}")

    lookup_index, lookup_flag, subtable = mkmk_subtable(font)
    names_to_codes = code_points(font)

    # Mark1 = the mark being attached (the one that follows). Mark2 = the mark it attaches to (the
    # one already on the letter, i.e. the preceding upper vowel).
    mark1_order = {name: i for i, name in enumerate(subtable.Mark1Coverage.glyphs)}
    mark2_order = {name: i for i, name in enumerate(subtable.Mark2Coverage.glyphs)}

    following = {}
    for name, order in mark1_order.items():
        code = names_to_codes.get(name)
        if code in FOLLOWING_MARKS:
            record = subtable.Mark1Array.MarkRecord[order]
            following[code] = (name, record.Class, record.MarkAnchor)

    preceding = {}
    for name, order in mark2_order.items():
        code = names_to_codes.get(name)
        if code is None or not THAI_BLOCK[0] <= code <= THAI_BLOCK[1]:
            continue
        preceding[code] = (name, subtable.Mark2Array.Mark2Record[order].Mark2Anchor)

    pairs = []
    for prev_code in sorted(preceding):
        prev_name, prev_anchors = preceding[prev_code]
        for mark_code in sorted(following):
            mark_name, mark_class, mark_anchor = following[mark_code]
            anchor = prev_anchors[mark_class]
            pairs.append(
                {
                    "preceding": f"{prev_code:04X}",
                    "mark": f"{mark_code:04X}",
                    "dx": int(round(anchor.XCoordinate - mark_anchor.XCoordinate)),
                    "dy": int(round(anchor.YCoordinate - mark_anchor.YCoordinate)),
                }
            )

    letter_rows, letters = base_pairs(font)
    pairs.extend(letter_rows)
    cmap = font.getBestCmap()
    # Marks only the letter rows move (ุ ู ฺ) still need their glyph id for an asset whose
    # character table is empty at install time (Dynamic_TH).
    letter_marks = {int(row["mark"], 16) for row in letter_rows}

    marks = []
    for code in sorted(set(preceding) | set(following) | letters | letter_marks):
        name = preceding[code][0] if code in preceding else following[code][0] if code in following else cmap[code]
        bottom, top = bounds(font, name)
        marks.append(
            {
                "code": f"{code:04X}",
                "glyph": name,
                "gid": font.getGlyphID(name),
                "bottom": bottom,
                "top": top,
                "precedes": code in preceding or code in letters,
                "follows": code in following or code in letter_marks,
            }
        )

    # Name IDs 16/17 (typographic family/subfamily) before 1/2 (legacy family/subfamily): FreeType,
    # and therefore TMP's FaceInfo, reports the typographic pair. This font's legacy names are
    # "IBM Plex Sans Thai Looped Medm" / "Regular"; Base_TH.asset stores
    # m_FamilyName: IBM Plex Sans Thai Looped, m_StyleName: Medium - i.e. 16/17. The installer
    # compares faceInfo.familyName with this value before trusting the measured table, so getting
    # it wrong would silently switch every asset to the metric fallback.
    name_table = font["name"]
    family = name_table.getDebugName(16) or name_table.getDebugName(1) or ""
    style = name_table.getDebugName(17) or name_table.getDebugName(2) or ""

    return {
        "schema": SCHEMA,
        "font": {
            "family": family,
            "style": style,
            "file": os.path.basename(FONT),
            "unitsPerEm": units_per_em,
        },
        "source": {
            "table": "GPOS",
            "feature": "mkmk",
            "lookup": lookup_index,
            "lookupFlag": f"0x{lookup_flag:X}",
            "format": subtable.Format,
            "note": (
                "dy = mark2Anchor.y - mark1Anchor.y, in per mille of the em; positive raises the "
                "following mark. Applied as TMP_GlyphValueRecord.yPlacement on the SECOND glyph "
                "of a pair-adjustment record (TextMeshProUGUI.cs:1704-1707, :1762)."
            ),
        },
        "fallbackGap": FALLBACK_GAP_PER_MILLE,
        "marks": marks,
        "pairs": pairs,
    }


def render(table: dict) -> str:
    return json.dumps(table, indent=1, ensure_ascii=False, sort_keys=False) + "\n"


def main() -> int:
    global FONT
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--check",
        action="store_true",
        help="do not write; exit 1 when the file on disk differs from a fresh generation",
    )
    parser.add_argument("--out", default=OUTPUT, help="output path (default: the loader's built-in table)")
    parser.add_argument("--font", default=FONT, help="the game's own IBMPlexSansThaiLooped-Medium.otf (default: the local dump)")
    args = parser.parse_args()
    FONT = args.font

    table = build()
    text = render(table)

    if args.check:
        if not os.path.isfile(args.out):
            print(f"missing: {args.out}", file=sys.stderr)
            return 1
        with open(args.out, "r", encoding="utf-8") as handle:
            current = handle.read()
        if current != text:
            print(f"stale: {args.out} differs from a fresh generation", file=sys.stderr)
            return 1
        print(f"up to date: {args.out}")
        return 0

    os.makedirs(os.path.dirname(args.out), exist_ok=True)
    with open(args.out, "w", encoding="utf-8", newline="\n") as handle:
        handle.write(text)

    by_preceding = {}
    for pair in table["pairs"]:
        by_preceding.setdefault(pair["preceding"], set()).add(pair["dy"])

    print(f"wrote {args.out}")
    print(f"  {len(table['pairs'])} pairs over {len(table['marks'])} marks")
    for code in sorted(by_preceding):
        values = sorted(by_preceding[code])
        shown = values[0] if len(values) == 1 else values
        print(f"  U+{code} dy {shown}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
