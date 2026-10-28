#!/usr/bin/env python3
"""Colourised, digestible live view of the CWY console log.

Usage: watch-log.py [--last N] [--quiet] [--strict] [--no-follow] [path]
  default path: <game root>/BepInEx/cwy-console.log
  --quiet      hide the grey tags (SETUP, TRACE, KEYS, PACKS, SAVE, CONSOLE, NOTE, COND)
  --last N     print the last N lines first (default 30)
  --no-follow  print and exit
  --strict     exit non-zero if any line does not match the grammar (the regression alarm)

Grammar (F3, one regex, one tag table - see src/CwyLs.ModLoader.Core/Logging/):

    HH:mm:ss.fff  TAG  <primary>  k=v k="quoted value"

Two spaces separate the three columns; single spaces are free inside the primary and inside a
quoted value; a missing value is "-". Nothing else is parsed: the colour comes from the TAG
alone, and an unknown TAG prints dim rather than being dropped, so a new event kind never needs
a new release of this script.

Parity with the C# grammar is checked by tools/mac/tests/console-golden.txt, which the unit
tests regenerate:  python3 tools/mac/watch-log.py --strict --no-follow tools/mac/tests/console-golden.txt
"""
import os
import re
import sys
import time

DEFAULT = os.path.expanduser(
    "~/Library/Application Support/Steam/steamapps/common/Chill with You Lo-Fi Story/BepInEx/cwy-console.log"
)

# ---- colours ----
R = "\033[0m"
B = "\033[1m"
WHITE, GREY, RED, GREEN, YELLOW, BLUE, MAGENTA, CYAN = "97", "90", "31", "32", "33", "34", "35", "36"


def c(code, s):
    return f"\033[{code}m{s}{R}"


# ---- the grammar: one regex ----
LINE = re.compile(
    r"^(?P<t>\d\d:\d\d:\d\d\.\d{3})\x20\x20(?P<tag>[A-Z][A-Z0-9]{1,11})\x20\x20(?P<rest>.*)$"
)

# key=value, value either "quoted with \" escapes" or a bare run of non-spaces
FIELD = re.compile(r'([^\s=]+)=("(?:[^"\\]|\\.)*"|\S*)')

# ---- the tag table: colour, and whether the tag is low-value (hidden by --quiet) ----
TAGS = {
    "SPEECH": (MAGENTA, False),
    "SAY": (MAGENTA, False),
    "STATE": (MAGENTA, False),
    "SUBTITLE": (MAGENTA, False),
    "FACE": (MAGENTA, False),
    "MOTION": (MAGENTA, False),
    "LOOK": (MAGENTA, False),
    "RESTORE": (MAGENTA, False),
    "MUSIC": (CYAN, False),
    "POMODORO": (CYAN, False),
    "SCENARIO": (CYAN, False),
    "SCENE": (CYAN, False),
    "WINDOW": (CYAN, False),
    "LANGUAGE": (CYAN, False),
    "READY": (GREEN, False),
    "BUTTON": (GREEN, False),
    "TYPED": (GREEN, False),
    "CLICK": (GREEN, False),
    "HEALTH": (YELLOW, False),
    "UNITY": (RED, False),
    "ERROR": (RED, False),
    "WARN": (YELLOW, False),
    "DATA": (BLUE, False),
    "VOICE": (BLUE, False),
    "TEXTURE": (BLUE, False),
    "MASTERDATA": (BLUE, False),
    "SMOKE": (GREEN, False),
    "CONSOLE": (GREY, True),
    "SETUP": (GREY, True),
    "TRACE": (GREY, True),
    "KEYS": (GREY, True),
    "PACKS": (GREY, True),
    "SAVE": (GREY, True),
    "NOTE": (GREY, True),
    # W0-CONTRACTS (Phases C-S); one entry per src/CwyLs.ModLoader.Core/Logging/ConsoleTags.cs constant
    # (LoggingConsoleTagsTests fails when a tag has no colour here).
    "MODS": (BLUE, False),      # the framework about code mods (red when one failed, below)
    "MOD": (BLUE, False),       # a code mod's own log line (content family, as in game)
    "ACTION": (GREEN, False),   # an action ran or was refused (audit)
    "API": (GREEN, False),      # one local game API request
    "SCRIPT": (CYAN, False),    # a script command row, scene play or scene lint
    "TRIGGER": (CYAN, False),   # a cutscene trigger fired, queued or skipped
    "COND": (GREY, True),       # condition evaluations and overrides (diagnostic)
    "AUDIO": (BLUE, False),     # pack ambient / SE / music clips
    "UISKIN": (BLUE, False),    # pack UI sprites
    "EDITDATA": (BLUE, False),  # EditData patches to master tables
    "MIC": (GREEN, False),      # microphone state and utterances
    "CAMERA": (CYAN, False),    # camera presets and hand-back
}

# HEALTH turns red for the readings that mean the game is struggling.
HEALTH_RED = ("spike", "lowFps", "lowMemory")

# MODS turns red when a mod failed (ModConsoleLines.Failure: "<id> failed in <Stage>...").
MODS_RED = " failed in "

TAG_WIDTH = 10


def unquote(value):
    """A field value as the producer wrote it: "-" is missing, quotes are stripped and unescaped."""
    if value == "-":
        return None
    if len(value) >= 2 and value[0] == '"' and value[-1] == '"':
        return re.sub(r"\\(.)", r"\1", value[1:-1])
    return value


def parse(line):
    """(time, tag, primary, [(key, value)]) or None when the line is not a console record."""
    m = LINE.match(line.rstrip("\r\n"))
    if not m:
        return None

    rest = m.group("rest")
    primary, _, details = rest.partition("  ")
    fields = [(k, unquote(v)) for k, v in FIELD.findall(details)] if details else []
    return m.group("t"), m.group("tag"), (None if primary == "-" else primary), fields


def colour_of(tag, primary):
    colour, low = TAGS.get(tag, (GREY, True))

    if tag == "HEALTH" and primary:
        head = primary.split(" ", 1)[0]
        if head in HEALTH_RED:
            return RED, False

    if tag == "SMOKE" and primary:
        if primary.startswith("FAIL") or primary == "ok=false":
            return RED, False

    if tag == "MODS" and primary and MODS_RED in primary:
        return RED, False

    return colour, low


def render(line, quiet):
    """The rendered line, or None when --quiet hides it. Unparsed lines are returned dim."""
    parsed = parse(line)
    if parsed is None:
        text = line.rstrip("\r\n")
        return None if not text else c(GREY, f"{'?':<{TAG_WIDTH}} {text}")

    when, tag, primary, fields = parsed
    colour, low = colour_of(tag, primary)
    if quiet and low:
        return None

    body = c(WHITE, B + primary) if primary else c(GREY, "-")
    details = "  ".join(f"{c(GREY, k + '=')}{'' if v is None else v}" for k, v in fields)

    out = f"{c(GREY, when)} {c(colour, B + f'{tag:<{TAG_WIDTH}}')} {body}"
    return out + ("  " + c(GREY, details) if details else "")


def follow(path, last, quiet, do_follow, strict):
    """Prints the tail and then follows. Returns the number of unparsable lines seen."""
    bad = 0

    def open_file():
        f = open(path, "r", encoding="utf-8", errors="replace")
        return f, os.fstat(f.fileno()).st_ino

    while not os.path.exists(path):
        print(c(GREY, f"waiting for {path} …"))
        time.sleep(1)

    f, ino = open_file()
    lines = f.readlines()
    tail = lines if last <= 0 else lines[-last:]
    for ln in tail:
        if not ln.strip() or ln.lstrip().startswith("#"):
            continue   # blank lines and a fixture's own comments are not console records
        if parse(ln) is None:
            bad += 1
            if strict:
                print(c(RED, B + "STRICT: line does not match the grammar: " + ln.rstrip()))
        r = render(ln, quiet)
        if r:
            print(r)

    if not do_follow:
        return bad

    f.seek(0, os.SEEK_END)
    while True:
        ln = f.readline()
        if ln:
            if ln.strip() and parse(ln) is None:
                bad += 1
                if strict:
                    print(c(RED, B + "STRICT: line does not match the grammar: " + ln.rstrip()), flush=True)
            r = render(ln, quiet)
            if r:
                print(r, flush=True)
            continue

        time.sleep(0.2)
        try:
            st = os.stat(path)
            if st.st_ino != ino or st.st_size < f.tell():
                f.close()
                f, ino = open_file()
                print(c(YELLOW, B + "──── new log file ────"))
        except FileNotFoundError:
            pass


def main(argv):
    quiet = "--quiet" in argv
    nofollow = "--no-follow" in argv
    strict = "--strict" in argv

    last = 30
    if "--last" in argv:
        last = int(argv[argv.index("--last") + 1])
    if nofollow and "--last" not in argv:
        last = 0  # a one-shot read means the whole file, not its tail (0 = all)

    paths = [a for a in argv if not a.startswith("--") and not a.isdigit()]
    path = paths[0] if paths else DEFAULT

    bad = follow(path, last, quiet, not nofollow, strict)

    if strict and bad:
        print(c(RED, B + f"STRICT: {bad} line(s) did not match the console grammar"), file=sys.stderr)
        return 1

    return 0


if __name__ == "__main__":
    try:
        sys.exit(main(sys.argv[1:]))
    except KeyboardInterrupt:
        pass
