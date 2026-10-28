#!/bin/bash
# CWY:LS Mod Loader - installer for macOS.
#   Double-click install.command, or run:  bash install.command
#   Unattended (testing, automation):
#     bash install.command --game "<game folder>" --install | --uninstall | --uninstall-all | --trace on|full|off | --status
#
# Nothing is ever deleted: uninstalling moves files into a dated folder inside the game folder.
# The animated screens need a terminal window of at least 60 x 23 (macOS Terminal and iTerm2 are asked
# for 100 x 32). A smaller window, a pipe or TERM=dumb get a plain numbered menu with the same actions.
# Written for macOS's /bin/bash 3.2.

export LC_ALL="${LC_ALL:-en_US.UTF-8}"
VERSION="__VERSION__"
VERSION_UI="v${VERSION%%-*}"
HERE="$(cd "$(dirname "$0")" && pwd)"
FILES="$HERE/files"
BEPINEX_VERSION="5.4.23.6"
BEPINEX_ZIP="$FILES/BepInEx_macos_universal_$BEPINEX_VERSION.zip"
GAME_FOLDER="Chill with You Lo-Fi Story"
APP="Chill With You.app"
PLUGIN_DIR_NAME="CwyLsModLoader"
MODS_DIR_NAME="CWYLS Mods"
TRACE_CFG_NAME="cwyls.modloader.trace.cfg"
STEAM="$HOME/Library/Application Support/Steam"
GAME_ROOT="${CWY_GAME:-}"
ERR=""; MOVED_TO=""; MIGRATED=0
SUMMARY=()

stamp() { date +%Y%m%d-%H%M%S; }
log_line() {
  [ -n "$GAME_ROOT" ] && [ -d "$GAME_ROOT/BepInEx" ] &&
    printf '%s  %s\n' "$(date '+%Y-%m-%d %H:%M:%S')" "$*" >> "$GAME_ROOT/BepInEx/cwyls-installer.log" 2>/dev/null
  return 0
}

# ================================================================ the game
steam_libraries() {
  printf '%s\n' "$STEAM"
  local vdf="$STEAM/steamapps/libraryfolders.vdf"
  [ -f "$vdf" ] && sed -n 's/^[[:space:]]*"path"[[:space:]]*"\(.*\)"[[:space:]]*$/\1/p' "$vdf"
}
find_game() {
  local lib candidate
  while IFS= read -r lib; do
    [ -n "$lib" ] || continue
    candidate="$lib/steamapps/common/$GAME_FOLDER"
    [ -d "$candidate/$APP" ] && { printf '%s\n' "$candidate"; return 0; }
  done < <(steam_libraries | awk '!seen[$0]++')
  return 1
}
clean_path() {  # a dragged-in path: trailing spaces, quotes and escaped spaces removed
  local p="$1"
  p="${p%"${p##*[![:space:]]}"}"; p="${p#"${p%%[![:space:]]*}"}"
  p="${p#\'}"; p="${p%\'}"; p="${p#\"}"; p="${p%\"}"
  p="${p//\\ / }"
  [ "$p" != "/" ] && p="${p%/}"
  printf '%s' "$p"
}
is_game() { [ -n "$1" ] && [ -d "$1/$APP" ]; }
is_demo() {
  local n; n="$(basename "$1" | tr '[:upper:]' '[:lower:]')"
  case " $n " in *" demo "*) return 0 ;; esac
  return 1
}
game_running() { pgrep -f "$APP/Contents/MacOS/" >/dev/null 2>&1; }

# ================================================================ what is installed
installed_version() {
  local f="$GAME_ROOT/BepInEx/plugins/$PLUGIN_DIR_NAME/VERSION.txt"
  [ -f "$f" ] && head -1 "$f" | tr -d '[:space:]'
}
bepinex_installed() { [ -f "$GAME_ROOT/BepInEx/core/BepInEx.dll" ] && [ -f "$GAME_ROOT/run_bepinex.sh" ]; }
loader_installed() { [ -d "$GAME_ROOT/BepInEx/plugins/$PLUGIN_DIR_NAME" ]; }
trace_level() {
  local cfg="$GAME_ROOT/BepInEx/config/$TRACE_CFG_NAME" level=""
  [ -f "$cfg" ] && level="$(sed -n 's/^[[:space:]]*Level[[:space:]]*=[[:space:]]*\([A-Za-z]*\).*/\1/p' "$cfg" | head -1)"
  case "$level" in Events) echo on ;; Trace) echo full ;; *) echo off ;; esac
}
mods_count() {
  local n=0 d
  for d in "$GAME_ROOT/$MODS_DIR_NAME"/*/; do [ -f "${d}manifest.json" ] && n=$(( n + 1 )); done
  echo $n
}
other_mods() {
  local out="" p
  for p in "$GAME_ROOT/BepInEx/plugins/"*; do
    [ -e "$p" ] || continue
    [ "$(basename "$p")" = "$PLUGIN_DIR_NAME" ] && continue
    out="${out:+$out, }$(basename "$p")"
  done
  printf '%s' "$out"
}
launch_line() { printf '"%s/run_bepinex.sh" %%command%%' "$GAME_ROOT"; }
copy_verified() {  # put $1 on the clipboard and read it back; true only when it matches exactly
  printf '%s' "$1" | pbcopy 2>/dev/null || return 1
  [ "$(pbpaste 2>/dev/null)" = "$1" ]
}
steam_ready() {  # Steam already starts this game through run_bepinex.sh (read-only look at Steam's settings)
  local f
  for f in "$STEAM"/userdata/*/config/localconfig.vdf; do
    [ -f "$f" ] && grep -qF "$GAME_ROOT/run_bepinex.sh" "$f" 2>/dev/null && return 0
  done
  return 1
}
needs_migration() {
  [ -d "$GAME_ROOT/BepInEx/plugins/CWY" ] && return 0
  [ -d "$GAME_ROOT/CWYPacks" ] && [ ! -e "$GAME_ROOT/$MODS_DIR_NAME" ] && return 0
  local c; for c in "$GAME_ROOT/BepInEx/config"/com.*.cwy.patcher*.cfg "$GAME_ROOT/BepInEx/config"/com.*.cwyls.modloader*.cfg; do [ -f "$c" ] && return 0; done
  return 1
}

# ================================================================ the work (each sets ERR and returns non-zero on failure)
do_nothing() { return 0; }
do_migrate() {  # builds from before the rename; nothing is deleted
  local old="$GAME_ROOT/BepInEx/_old/$(stamp)" cfg name new
  MIGRATED=0
  if [ -d "$GAME_ROOT/BepInEx/plugins/CWY" ]; then
    mkdir -p "$old" && mv "$GAME_ROOT/BepInEx/plugins/CWY" "$old/plugins-CWY" || { ERR="Could not move the earlier build out of BepInEx/plugins/CWY."; return 1; }
    MIGRATED=1
  fi
  for cfg in "$GAME_ROOT/BepInEx/config"/com.*.cwy.patcher*.cfg "$GAME_ROOT/BepInEx/config"/com.*.cwyls.modloader*.cfg; do
    [ -f "$cfg" ] || continue
    name="$(basename "$cfg")"
    case "$name" in
      *cwy.patcher*) new="$GAME_ROOT/BepInEx/config/cwyls.modloader${name#*cwy.patcher}" ;;
      *) new="$GAME_ROOT/BepInEx/config/cwyls.modloader${name#*cwyls.modloader}" ;;
    esac
    [ -f "$new" ] || cp "$cfg" "$new"
    mkdir -p "$old/config" && mv "$cfg" "$old/config/$name"
    MIGRATED=1
  done
  if [ -d "$GAME_ROOT/CWYPacks" ] && [ ! -e "$GAME_ROOT/$MODS_DIR_NAME" ]; then
    mv "$GAME_ROOT/CWYPacks" "$GAME_ROOT/$MODS_DIR_NAME" || { ERR="Could not rename CWYPacks to \"$MODS_DIR_NAME\"."; return 1; }
    MIGRATED=1
  fi
  return 0
}
do_bepinex() {
  [ -f "$BEPINEX_ZIP" ] || { ERR="BepInEx is missing from this download (files/$(basename "$BEPINEX_ZIP")). Download the release zip again."; return 1; }
  local tmp f
  tmp="$(mktemp -d "${TMPDIR:-/tmp}/cwyls-bepinex.XXXXXX")" || { ERR="Could not create a temporary folder."; return 1; }
  unzip -q -o "$BEPINEX_ZIP" -d "$tmp" || { ERR="Could not unpack BepInEx."; return 1; }
  mkdir -p "$GAME_ROOT/BepInEx" && cp -R "$tmp/BepInEx/." "$GAME_ROOT/BepInEx/" || { ERR="Could not copy BepInEx into the game folder."; return 1; }
  # Everything but BepInEx's changelog.txt, which would sit next to the game's own files.
  for f in run_bepinex.sh libdoorstop.dylib .doorstop_version; do [ -e "$tmp/$f" ] && cp -p "$tmp/$f" "$GAME_ROOT/$f"; done
  cp -p "$tmp/changelog.txt" "$GAME_ROOT/BepInEx/bepinex-changelog.txt" 2>/dev/null
  mkdir -p "$GAME_ROOT/BepInEx/plugins" "$GAME_ROOT/BepInEx/patchers" "$GAME_ROOT/BepInEx/config"
  chmod +x "$GAME_ROOT/run_bepinex.sh"
  xattr -dr com.apple.quarantine "$GAME_ROOT/BepInEx" "$GAME_ROOT/run_bepinex.sh" "$GAME_ROOT/libdoorstop.dylib" 2>/dev/null
  find "$tmp" -type f -delete 2>/dev/null; find "$tmp" -depth -type d -empty -delete 2>/dev/null
  return 0
}
do_loader() {
  local dest="$GAME_ROOT/BepInEx/plugins/$PLUGIN_DIR_NAME"
  ls "$FILES/$PLUGIN_DIR_NAME/"*.dll >/dev/null 2>&1 || { ERR="The mod loader is missing from this download (files/$PLUGIN_DIR_NAME). Download the release zip again."; return 1; }
  mkdir -p "$dest" && cp -p "$FILES/$PLUGIN_DIR_NAME/"*.dll "$dest/" || { ERR="Could not copy the mod loader into BepInEx/plugins."; return 1; }
  printf '%s\n' "$VERSION" > "$dest/VERSION.txt"
  xattr -dr com.apple.quarantine "$dest" 2>/dev/null
  return 0
}
do_mods_folder() {
  mkdir -p "$GAME_ROOT/$MODS_DIR_NAME" || { ERR="Could not create the \"$MODS_DIR_NAME\" folder."; return 1; }
  [ -f "$GAME_ROOT/$MODS_DIR_NAME/README.txt" ] ||
    printf '%s\n' "Put mods for CWY:LS Mod Loader here, one folder per mod (each with a manifest.json)." \
      "The game reads this folder when it starts." > "$GAME_ROOT/$MODS_DIR_NAME/README.txt"
  return 0
}
do_uninstall_loader() {
  local name="uninstalled-$(stamp)" dest cfg
  dest="$GAME_ROOT/BepInEx/_old/$name"
  mkdir -p "$dest" && mv "$GAME_ROOT/BepInEx/plugins/$PLUGIN_DIR_NAME" "$dest/" || { ERR="Could not move the mod loader out of BepInEx/plugins."; return 1; }
  for cfg in "$GAME_ROOT/BepInEx/config"/cwyls.modloader*.cfg; do
    [ -f "$cfg" ] || continue
    mkdir -p "$dest/config" && mv "$cfg" "$dest/config/"
  done
  MOVED_TO="BepInEx/_old/$name"
  return 0
}
do_uninstall_all() {
  local name="_CWYLS-uninstalled-$(stamp)" f
  mkdir -p "$GAME_ROOT/$name" || { ERR="Could not create $name in the game folder."; return 1; }
  for f in BepInEx run_bepinex.sh libdoorstop.dylib .doorstop_version; do
    [ -e "$GAME_ROOT/$f" ] && { mv "$GAME_ROOT/$f" "$GAME_ROOT/$name/" || { ERR="Could not move $f out of the game folder."; return 1; }; }
  done
  MOVED_TO="$name"
  return 0
}
set_trace() {  # off | on | full
  local level
  case "$1" in on) level=Events ;; full) level=Trace ;; off) level=Off ;; *) ERR="trace must be on, full or off"; return 1 ;; esac
  local cfg="$GAME_ROOT/BepInEx/config/$TRACE_CFG_NAME"
  mkdir -p "$GAME_ROOT/BepInEx/config" || { ERR="Could not create BepInEx/config."; return 1; }
  if [ -f "$cfg" ] && grep -q '^[[:space:]]*Level[[:space:]]*=' "$cfg"; then
    sed -i '' "s/^[[:space:]]*Level[[:space:]]*=.*/Level = $level/" "$cfg"
  elif [ -f "$cfg" ] && grep -q '^\[Trace\]' "$cfg"; then
    sed -i '' "s/^\[Trace\]/[Trace]\\
Level = $level/" "$cfg"
  else
    printf '[Trace]\n\nLevel = %s\n' "$level" >> "$cfg"
  fi
  return 0
}
full_install() {  # the whole install, in order; used by the plain and unattended modes
  do_migrate && do_bepinex && do_loader && do_mods_folder
}

# ================================================================ unattended mode (flags) and the plain menu
ACTION=""; TRACE_ARG=""
while [ $# -gt 0 ]; do
  case "$1" in
    --game) GAME_ROOT="$(clean_path "${2:-}")"; shift ;;
    --install) ACTION=install ;;
    --uninstall) ACTION=uninstall ;;
    --uninstall-all) ACTION=uninstall-all ;;
    --trace) ACTION=trace; TRACE_ARG="${2:-}"; shift ;;
    --status) ACTION=status ;;
    *) echo "unknown option: $1" >&2; exit 2 ;;
  esac
  shift
done

say() { printf '%s\n' "$*"; }
plain_status() {
  say "  Game:        $GAME_ROOT"
  local iv; iv="$(installed_version)"
  say "  Mod loader:  ${iv:-not installed}"
  if bepinex_installed; then say "  BepInEx:     installed"; else say "  BepInEx:     not installed"; fi
  say "  Trace:       $(trace_level)"
  say "  Mods:        $(mods_count) in \"$MODS_DIR_NAME\""
}
plain_check_game() {
  if ! is_game "$GAME_ROOT"; then say "ERROR: \"$GAME_ROOT\" does not contain \"$APP\"."; return 1; fi
  if is_demo "$GAME_ROOT"; then say "ERROR: This looks like the free demo. CWY:LS Mod Loader only works with the full game on Steam."; return 1; fi
  return 0
}
plain_install() {
  if game_running; then say "The game is running. Quit it first."; return 1; fi
  if full_install; then
    log_line "installed $VERSION (plain)"
    say "Installed CWY:LS Mod Loader $VERSION_UI and BepInEx $BEPINEX_VERSION. Your save files were not touched."
    [ -n "$(other_mods)" ] && say "Note: other BepInEx mods are installed ($(other_mods)). CWY:LS Mod Loader is standalone and not tested with them."
    if steam_ready; then say "Steam already starts the game through BepInEx: just press Play."
    else
      say "One step left: in Steam, right-click the game > Properties... > Launch Options, and paste this whole line:"
      say "  $(launch_line)"
      if [ -t 1 ] && copy_verified "$(launch_line)"; then say "  (It is on your clipboard: press Command V in the Launch Options box.)"; fi
    fi
    return 0
  fi
  say "ERROR: $ERR (nothing was deleted)"; return 1
}
plain_uninstall() {
  if game_running; then say "The game is running. Quit it first."; return 1; fi
  loader_installed || { say "The mod loader is not installed."; return 0; }
  if do_uninstall_loader; then log_line "uninstalled the loader (plain) -> $MOVED_TO"; say "Removed. Moved to $MOVED_TO; delete it whenever you like. BepInEx and your mods stay."; return 0; fi
  say "ERROR: $ERR"; return 1
}
plain_uninstall_all() {
  if game_running; then say "The game is running. Quit it first."; return 1; fi
  if do_uninstall_all; then say "BepInEx and the mod loader moved to $MOVED_TO. Your mods and saves stay. Also clear the game's Launch Options in Steam."; return 0; fi
  say "ERROR: $ERR"; return 1
}

if [ -n "$ACTION" ]; then
  [ -n "$GAME_ROOT" ] || GAME_ROOT="$(find_game)"
  [ -n "$GAME_ROOT" ] || { say "ERROR: game not found; pass --game"; exit 1; }
  plain_check_game || exit 1
  case "$ACTION" in
    install) plain_install; exit $? ;;
    uninstall) plain_uninstall; exit $? ;;
    uninstall-all) plain_uninstall_all; exit $? ;;
    trace) if set_trace "$TRACE_ARG"; then log_line "trace $TRACE_ARG (flag)"; say "Diagnostic trace: $TRACE_ARG (from the next game start)."; exit 0; else say "ERROR: $ERR"; exit 2; fi ;;
    status) plain_status; exit 0 ;;
  esac
fi

# Ctrl+C in the plain menu: the first press only warns; a second within 3 seconds quits.
LAST_CTRLC=-100; CTRLC_HIT=0
plain_ctrlc() {
  if (( SECONDS - LAST_CTRLC <= 3 )); then printf '\n'; exit 130; fi
  LAST_CTRLC=$SECONDS; CTRLC_HIT=1
  printf '\n  (Press Ctrl+C again to quit.)\n'
}
plain_menu() {
  trap plain_ctrlc INT
  say ""; say "  CWY:LS  M O D   L O A D E R   $VERSION_UI"; say "  for Chill With You: Lo-Fi Story  -  unofficial fan project"; say ""
  if [ -z "$GAME_ROOT" ]; then
    say "I could not find the game in your Steam libraries."
    printf 'Drag the game folder (the one with "%s") here, then press Enter: ' "$APP"
    IFS= read -r line || exit 1
    GAME_ROOT="$(clean_path "$line")"
  fi
  plain_check_game || exit 1
  while true; do
    say ""; plain_status; say ""
    say "  [1] Install or update      [2] Diagnostic trace       [3] Open the mods folder"
    say "  [4] Uninstall the loader   [5] Uninstall everything   [Q] Quit"
    printf '\n  Choose: '
    CTRLC_HIT=0
    if ! IFS= read -r c; then [ "$CTRLC_HIT" = 1 ] && continue; exit 0; fi
    # The work itself cannot be interrupted: Ctrl+C is ignored until the action is done.
    trap '' INT
    case "$c" in
      1) plain_install ;;
      2) printf '  Trace [on/full/off]: '; IFS= read -r t; if set_trace "$t"; then say "  Trace: $t (from the next game start)."; else say "  $ERR"; fi ;;
      3) mkdir -p "$GAME_ROOT/$MODS_DIR_NAME"; open "$GAME_ROOT/$MODS_DIR_NAME" 2>/dev/null; say "  Opened \"$MODS_DIR_NAME\"." ;;
      4) plain_uninstall ;;
      5) printf '  Move BepInEx and the mod loader out of the game folder? [y/N] '; IFS= read -r yn; case "$yn" in y|Y) plain_uninstall_all ;; *) say "  Nothing changed." ;; esac ;;
      q|Q) exit 0 ;;
    esac
    trap plain_ctrlc INT
  done
}

[ -n "$GAME_ROOT" ] || GAME_ROOT="$(find_game)"

# ================================================================ the animated installer
ESC=$'\033'; R="${ESC}[0m"; BOLD="${ESC}[1m"
FIRST_RUN=1; [ -n "$GAME_ROOT" ] && loader_installed && FIRST_RUN=0

# ---------------------------------------------------------------- terminal size and capabilities
ROWS="${CWY_ROWS:-}"; COLS="${CWY_COLS:-}"
if [ -z "$ROWS" ] || [ -z "$COLS" ]; then
  S="$(stty size 2>/dev/null)"; ROWS="${S% *}"; COLS="${S#* }"
fi
case "$ROWS" in ''|*[!0-9]*) ROWS=$(tput lines 2>/dev/null || echo 24) ;; esac
case "$COLS" in ''|*[!0-9]*) COLS=$(tput cols 2>/dev/null || echo 80) ;; esac

# Terminal programs cannot change the font size, but macOS Terminal and iTerm2 accept a request for a
# bigger window (CSI 8 ; rows ; cols t). The full layout needs 84 x 30; the window is put back on exit.
RESIZED=""
if [ -z "${CWY_ROWS:-}" ] && [ -t 1 ] && { [ "$ROWS" -lt 30 ] || [ "$COLS" -lt 84 ]; }; then
  case "${TERM_PROGRAM:-}" in
    Apple_Terminal|iTerm.app)
      RESIZED="$ROWS;$COLS"
      printf '\033[8;32;100t'; sleep 0.25
      S="$(stty size 2>/dev/null)"; case "$S" in *[0-9]\ [0-9]*) ROWS="${S% *}"; COLS="${S#* }" ;; esac ;;
  esac
fi

MODE=full
if [ ! -t 1 ] && [ -z "${CWY_ROWS:-}" ]; then MODE=plain
elif [ "${TERM:-dumb}" = dumb ]; then MODE=plain
elif [ "$ROWS" -ge 30 ] && [ "$COLS" -ge 84 ]; then MODE=full
elif [ "$ROWS" -ge 23 ] && [ "$COLS" -ge 60 ]; then MODE=compact
else MODE=plain; fi

COLORS=$(tput colors 2>/dev/null || echo 8); case "$COLORS" in ''|*[!0-9]*) COLORS=8 ;; esac

# Light or dark background? CWY_THEME first, then the terminal's own answer (OSC 11), then COLORFGBG,
# then macOS Terminal's profile ("Basic" follows the system appearance). Unknown means dark.
THEME="${CWY_THEME:-}"
if [ -z "$THEME" ] && [ -t 0 ] && [ -t 1 ] && [ "${TERM:-dumb}" != dumb ]; then
  _saved="$(stty -g 2>/dev/null)"
  stty -echo -icanon min 0 time 0 2>/dev/null
  printf '\033]11;?\033\\'
  _resp=""
  for _i in 1 2 3 4 5 6; do sleep 0.05; _resp+="$(dd bs=64 count=1 2>/dev/null)"; case "$_resp" in *rgb:*) break ;; esac; done
  [ -n "$_saved" ] && stty "$_saved" 2>/dev/null
  _re='rgb:([0-9a-fA-F][0-9a-fA-F])[0-9a-fA-F]*/([0-9a-fA-F][0-9a-fA-F])[0-9a-fA-F]*/([0-9a-fA-F][0-9a-fA-F])'
  if [[ $_resp =~ $_re ]]; then
    _lum=$(( (299 * 0x${BASH_REMATCH[1]} + 587 * 0x${BASH_REMATCH[2]} + 114 * 0x${BASH_REMATCH[3]}) / 1000 ))
    if [ $_lum -ge 128 ]; then THEME=light; else THEME=dark; fi
  fi
fi
if [ -z "$THEME" ] && [ -n "${COLORFGBG:-}" ]; then
  case "${COLORFGBG##*;}" in 7|15) THEME=light ;; *) THEME=dark ;; esac
fi
if [ -z "$THEME" ] && [ "${TERM_PROGRAM:-}" = Apple_Terminal ]; then
  case "$(defaults read com.apple.Terminal "Default Window Settings" 2>/dev/null)" in
    Basic) if defaults read -g AppleInterfaceStyle >/dev/null 2>&1; then THEME=dark; else THEME=light; fi ;;
    Novel|"Man Page"|"Silver Aerogel") THEME=light ;;
  esac
fi
THEME="${THEME:-dark}"

CC=(); BGC=()
if [ "${CWY_COLORS:-$COLORS}" -ge 256 ]; then
  for ((n=0; n<256; n++)); do CC[n]="${ESC}[38;5;${n}m"; BGC[n]="${ESC}[48;5;${n}m"; done
else
  # 16-colour fallback, chosen per palette entry so pills and text stay readable
  for ((n=0; n<256; n++)); do
    if   (( n >= 211 && n <= 219 && n != 216 )); then CC[n]="${ESC}[95m"; BGC[n]="${ESC}[105m"
    elif (( n == 216 || n == 222 )); then CC[n]="${ESC}[93m"; BGC[n]="${ESC}[103m"
    elif (( n == 147 || n == 153 || n == 183 )); then CC[n]="${ESC}[94m"; BGC[n]="${ESC}[104m"
    elif (( n == 117 )); then CC[n]="${ESC}[96m"; BGC[n]="${ESC}[106m"
    elif (( n == 114 || n == 121 )); then CC[n]="${ESC}[92m"; BGC[n]="${ESC}[102m"
    elif (( n >= 248 )); then CC[n]="${ESC}[97m"; BGC[n]="${ESC}[47m"
    elif (( n >= 232 )); then CC[n]="${ESC}[90m"; BGC[n]="${ESC}[100m"
    else CC[n]="${ESC}[30m"; BGC[n]="${ESC}[40m"; fi
  done
fi
if [ "$THEME" = light ]; then
  if [ "${CWY_COLORS:-$COLORS}" -ge 256 ]; then
    # the grey ramp flips (light text becomes dark), and each pastel gets a deeper version for white
    for ((n=232; n<=255; n++)); do m=$(( 487 - n )); CC[n]="${ESC}[38;5;${m}m"; BGC[n]="${ESC}[48;5;${m}m"; done
    for pair in 217:167 218:168 219:170 183:97 147:61 153:67 216:166 215:166 221:136 222:136 \
                121:29 120:28 122:30 123:30 159:31 117:31 116:31 211:161 212:162 213:163 114:28 203:160 231:235; do
      n=${pair%%:*}; m=${pair#*:}; CC[n]="${ESC}[38;5;${m}m"; BGC[n]="${ESC}[48;5;${m}m"
    done
  else
    for ((n=0; n<256; n++)); do
      case "${CC[n]}" in
        *"[97m") CC[n]="${ESC}[30m" ;; *"[95m") CC[n]="${ESC}[35m" ;; *"[94m") CC[n]="${ESC}[34m" ;;
        *"[96m") CC[n]="${ESC}[36m" ;; *"[92m") CC[n]="${ESC}[32m" ;; *"[93m") CC[n]="${ESC}[33m" ;;
      esac
      case "${BGC[n]}" in *"[47m") BGC[n]="${ESC}[100m" ;; *"[100m") BGC[n]="${ESC}[47m" ;; esac
    done
  fi
fi

# ---------------------------------------------------------------- plain mode (tiny terminal, no TTY, dumb)
if [ "$MODE" = plain ]; then
  plain_menu
  exit 0
fi

# ---------------------------------------------------------------- full-screen setup
STTY_SAVED="$(stty -g 2>/dev/null)"
cleanup() {
  [ -n "$STTY_SAVED" ] && stty "$STTY_SAVED" 2>/dev/null
  printf '%s%s[?7h%s[?25h%s[?1049l' "$R" "$ESC" "$ESC" "$ESC"
  [ -n "$RESIZED" ] && printf '%s[8;%st' "$ESC" "$RESIZED"
  if [ ${#SUMMARY[@]} -gt 0 ]; then
    printf '\nCWY:LS Mod Loader installer %s\n' "$VERSION_UI"
    local l; for l in "${SUMMARY[@]}"; do printf '  - %s\n' "$l"; done
    printf '\n'
  fi
}
trap cleanup EXIT
trap 'exit 0' INT TERM
# alternate screen, no line wrap at the right edge, hidden cursor, clear
printf '%s[?1049h%s[?7l%s[?25l%s[2J' "$ESC" "$ESC" "$ESC" "$ESC"
# -isig: Ctrl+C, Ctrl+Z and Ctrl+\ arrive as keys instead of signals, so they can never stop a copy
# or a move halfway; Ctrl+C is handled below (twice to quit).
stty -echo -icanon -isig min 0 time 0 2>/dev/null

at() { printf '%s[%d;%dH' "$ESC" "$1" "$2"; }
FAST=0
KEY=""
poll_key() {  # non-blocking: VMIN=0, so dd returns at once with nothing when no key is waiting
  KEY=""
  local h; h=$(dd bs=3 count=1 2>/dev/null | od -An -tx1 | tr -d ' \n')
  [ -z "$h" ] && return 1
  case "$h" in
    1b5b41*|1b4f41*) KEY=up ;; 1b5b42*|1b4f42*) KEY=down ;; 1b5b43*|1b4f43*) KEY=right ;; 1b5b44*|1b4f44*) KEY=left ;;
    0a*|0d*) KEY=enter ;;
    03*) KEY=ctrlc ;;
    1b|1b1b*) KEY=esc ;;
    71*|51*) KEY=q ;;
    31*) KEY=1 ;; 32*) KEY=2 ;; 33*) KEY=3 ;; 34*) KEY=4 ;; 35*) KEY=5 ;; 36*) KEY=6 ;; 37*) KEY=7 ;;
    *) KEY=other ;;
  esac
  return 0
}
frame() {  # one frame: wait $1 s, and any key turns the rest of the current animation instant
  [ "$FAST" = 1 ] && return
  sleep "$1"
  if poll_key; then
    case "$KEY" in ctrlc|q) quit_key ;; *) FAST=1 ;; esac
  fi
}
# Leaving takes two presses: Ctrl+C (anywhere) or q (on the main menu), twice within 3 seconds.
# The first press only shows a hint on the bottom row. Choosing Quit in the menu leaves at once.
LAST_QUIT=-100; HINT_UNTIL=0; KR=""; KARGS=()
quit_key() {
  local name="Ctrl+C"; [ "$KEY" = q ] && name="q"
  if (( SECONDS - LAST_QUIT <= 3 )); then farewell; fi
  LAST_QUIT=$SECONDS
  hint "Press $name again to quit"
}
hint() {
  at "$ROWS" 1; printf '%s[2K' "$ESC"
  local t="$1"; at "$ROWS" $(( (COLS - ${#t}) / 2 + 1 )); printf '%s%s%s%s' "$BOLD" "${CC[222]}" "$t" "$R"
  HINT_UNTIL=$(( SECONDS + 3 ))
}
hint_tick() {  # called while waiting: the hint goes away after 3 seconds (and the key row comes back)
  (( HINT_UNTIL > 0 && SECONDS >= HINT_UNTIL )) || return 0
  HINT_UNTIL=0
  at "$ROWS" 1; printf '%s[2K' "$ESC"
  [ "$KR" = "$ROWS" ] && keys "${KARGS[@]}"
  return 0
}

# ---------------------------------------------------------------- layout
ART=(
" ██████╗ ██╗    ██╗ ██╗   ██╗     ██╗      ███████╗"
"██╔════╝ ██║    ██║ ╚██╗ ██╔╝ ██╗ ██║      ██╔════╝"
"██║      ██║ █╗ ██║  ╚████╔╝  ╚═╝ ██║      ███████╗"
"██║      ██║███╗██║   ╚██╔╝   ██╗ ██║      ╚════██║"
"╚██████╗ ╚███╔███╔╝    ██║    ╚═╝ ███████╗ ███████║"
" ╚═════╝  ╚══╝╚══╝     ╚═╝        ╚══════╝ ╚══════╝"
)
W=${#ART[0]}
PAL=(217 218 219 183 147 153)
X0=$(( (COLS - W) / 2 + 1 )); [ $X0 -lt 1 ] && X0=1

# Rows. The wordmark, horizon and caption sit on top. Under them, in turn: the big THANK YOU, the
# features panel, and finally the setup panel with the menu panel.
if [ "$MODE" = full ]; then
  BLOCK=28; Y0=$(( (ROWS - BLOCK) / 2 + 1 )); [ $Y0 -lt 2 ] && Y0=2
  HR=$(( Y0 + 7 )); CAP1=$(( HR + 1 )); CAP2=$(( HR + 2 ))
  MSG=$(( HR + 4 )); TY=$(( MSG + 1 )); FP=$(( MSG + 1 ))
  SP=$MSG; MP=$(( SP + 7 )); KEYS2=$(( MP + 9 ))
else
  BLOCK=23; Y0=$(( (ROWS - BLOCK) / 2 + 1 )); [ $Y0 -lt 1 ] && Y0=1
  HR=$(( Y0 + 6 )); CAP1=$(( HR + 1 )); CAP2=0
  MSG=$(( HR + 2 )); TY=$(( MSG + 1 )); FP=$MSG
  SP=$MSG; MP=$(( SP + 6 )); KEYS2=$(( MP + 8 ))
fi
KEYS1=$(( FP + 8 ))
PW=74; [ $PW -gt $(( COLS - 6 )) ] && PW=$(( COLS - 6 ))   # panel width
PX=$(( (COLS - PW) / 2 + 1 ))                                # panel left column
IW=$(( PW - 4 ))                                             # inner width (border + one space each side)
BOXL=$(( X0 - 6 )); [ $BOXL -lt 1 ] && BOXL=1
BOXW=$(( W + 12 )); [ $(( BOXL + BOXW - 1 )) -gt $COLS ] && BOXW=$(( COLS - BOXL + 1 ))
SPACES="$(printf '%*s' $BOXW '')"
clear_row() { at "$1" $BOXL; printf '%s' "$SPACES"; }       # wordmark rows: keep the stars beside them
clear_line() { at "$1" 1; printf '%s[2K' "$ESC"; }           # text rows: long lines and sliding cards
fit() {  # $1 shortened to the screen width, with … in the middle when it is too long
  local t="$1" max=$(( COLS - 4 ))
  if [ ${#t} -gt $max ]; then local half=$(( (max - 1) / 2 )); t="${t:0:half}…${t: -$(( max - 1 - half ))}"; fi
  printf '%s' "$t"
}
center() { local t; t="$(fit "$2")"; at "$1" $(( (COLS - ${#t}) / 2 + 1 )); printf '%s%s%s' "$3" "$t" "$R"; }

paint() {  # one wordmark row; columns hl-3..hl get the shine
  local row="$1" hl="${2:--99}"
  local out="" i ch len=${#row} n=${#PAL[@]}
  for ((i=0; i<len; i++)); do
    ch="${row:i:1}"
    if [ "$ch" = " " ]; then out+=" "
    elif (( i >= hl - 3 && i <= hl )); then out+="${CC[231]}$ch"
    elif [ "$ch" = "█" ]; then out+="${CC[${PAL[$(( i * n / len ))]}]}█"
    else out+="${CC[238]}$ch"; fi
  done
  printf '%s%s' "$out" "$R"
}
PAINTED=(); for r in "${ART[@]}"; do PAINTED+=("$(paint "$r")"); done

# ---------------------------------------------------------------- the starfield
SX=(); SY=(); LV=(236 238 240 243 246 250)
NSTARS=$(( ROWS * COLS / 55 ))
for ((s=0; s<NSTARS; s++)); do
  x=$(( RANDOM % COLS + 1 )); y=$(( RANDOM % ROWS + 1 ))
  if (( y >= MSG && y <= Y0 + BLOCK - 1 )); then continue; fi                       # none in the text rows
  if (( y >= Y0 - 1 && y < MSG && x >= BOXL && x < BOXL + BOXW )); then continue; fi  # none behind the wordmark
  SX+=($x); SY+=($y)
done
star() { at "${SY[$1]}" "${SX[$1]}"; printf '%s%s%s' "${CC[${LV[$2]}]}" "$3" "$R"; }
twinkle() { hint_tick; [ ${#SX[@]} -gt 0 ] || return; local i=$(( RANDOM % ${#SX[@]} )) g="·"; (( RANDOM % 9 == 0 )) && g="+"; star $i $(( RANDOM % 6 )) "$g"; }

# Defined before the intro: Ctrl+C or q twice can run the goodbye (farewell) at any moment.
# ---------------------------------------------------------------- big letters (3 rows, box drawing)
glyph() {  # sets G0 G1 G2 for one letter
  case "$1" in
    A) G0="╔═╗"; G1="╠═╣"; G2="╩ ╩" ;;  C) G0="╔═╗"; G1="║  "; G2="╚═╝" ;;
    D) G0="╔╦╗"; G1=" ║║"; G2="═╩╝" ;;  E) G0="╔═╗"; G1="║╣ "; G2="╚═╝" ;;
    F) G0="╔═╗"; G1="╠╣ "; G2="╚  " ;;  H) G0="╦ ╦"; G1="╠═╣"; G2="╩ ╩" ;;
    I) G0="╦";   G1="║";   G2="╩"   ;;  K) G0="╦╔═"; G1="╠╩╗"; G2="╩ ╩" ;;
    M) G0="╔╦╗"; G1="║║║"; G2="╩ ╩" ;;  N) G0="╔╗╔"; G1="║║║"; G2="╝╚╝" ;;
    O) G0="╔═╗"; G1="║ ║"; G2="╚═╝" ;;  R) G0="╦═╗"; G1="╠╦╝"; G2="╩╚═" ;;
    S) G0="╔═╗"; G1="╚═╗"; G2="╚═╝" ;;  T) G0="╔╦╗"; G1=" ║ "; G2=" ╩ " ;;
    U) G0="╦ ╦"; G1="║ ║"; G2="╚═╝" ;;  Y) G0="╦ ╦"; G1="╚╦╝"; G2=" ╩ " ;;
    L) G0="╦  "; G1="║  "; G2="╩═╝" ;;  V) G0="╦  ╦"; G1="╚╗╔╝"; G2=" ╚╝ " ;;
    B) G0="╔╗ "; G1="╠╩╗"; G2="╚═╝" ;;  G) G0="╔═╗"; G1="║ ╦"; G2="╚═╝" ;;
    P) G0="╔═╗"; G1="╠═╝"; G2="╩  " ;;  W) G0="╦ ╦"; G1="║║║"; G2="╚╩╝" ;;
    *) G0="  ";  G1="  ";  G2="  "  ;;
  esac
}
bigrows() {  # word -> B0 B1 B2
  local w="$1" i; B0=""; B1=""; B2=""
  for ((i=0; i<${#w}; i++)); do glyph "${w:i:1}"; B0+="$G0"; B1+="$G1"; B2+="$G2"; done
}
gradient() {  # text, palette (space separated), visible columns -> the text coloured left to right
  local t="$1" i out="" ch
  local -a pal=($2)
  local n=${#pal[@]} len=${#t}
  local upto=${3:-$len}
  for ((i=0; i<upto && i<len; i++)); do
    ch="${t:i:1}"
    if [ "$ch" = " " ]; then out+=" "; else out+="${CC[${pal[$(( i * n / len ))]}]}$ch"; fi
  done
  printf '%s%s' "$out" "$R"
}

# ================================================================ the installer screens
LTOP=$SP; LBOT=$(( Y0 + BLOCK - 1 ))        # the lower area every screen below draws in
SPINNER=(⠋ ⠙ ⠹ ⠸ ⠼ ⠴ ⠦ ⠧ ⠇ ⠏)
DUSK="${PAL[*]}"; MINT="121 120 122 123 159 153"; AMBER="222 221 215 216 217 218"; ROSE="211 212 213 218 219 183"
TTY_OUT=0; [ -t 1 ] && TTY_OUT=1          # Finder and the clipboard are only touched in a real terminal

clear_lower() { local r; for ((r=LTOP; r<=LBOT; r++)); do clear_line $r; done; }
trunc() { local t="$1" w=$2; [ ${#t} -gt $w ] && t="${t:0:$(( w - 1 ))}…"; printf '%s' "$t"; }
mid() {  # shorten in the middle, so the start and the end both stay readable
  local t="$1" w=$2 h; [ ${#t} -le $w ] && { printf '%s' "$t"; return; }
  h=$(( (w - 1) / 2 )); printf '%s…%s' "${t:0:h}" "${t: -$(( w - 1 - h ))}"
}
wrap() {  # text, width, lines -> WL[0..lines-1]
  local t="$1" w=$2 n=$3 i cut; WL=()
  for ((i=0; i<n; i++)); do
    if [ ${#t} -le $w ]; then WL[i]="$t"; t=""; continue; fi
    cut=$w; while [ $cut -gt 0 ] && [ "${t:cut:1}" != " " ]; do cut=$(( cut - 1 )); done
    [ $cut -eq 0 ] && cut=$w
    WL[i]="${t:0:cut}"; t="${t:cut+1}"
  done
  [ -n "$t" ] && WL[n-1]="${WL[n-1]:0:$(( w - 1 ))}…"
}
note_right() { at "$1" $(( PX + PW - 3 - ${#2} )); printf '%s%s%s' "${CC[${3:-246}]}" "$2" "$R"; }
toast() { clear_line "$1"; center "$1" "$2" "${CC[${3:-121}]}"; sleep 1.6; clear_line "$1"; }
bigword() {  # row, word, palette, wipe (1 = wipe in from the left)
  local row=$1 c; bigrows "$2"
  local x=$(( (COLS - ${#B0}) / 2 + 1 ))
  if [ "${4:-0}" = 1 ]; then
    for ((c=1; c<=${#B0}; c+=2)); do
      at $row $x; gradient "$B0" "$3" $c; at $(( row + 1 )) $x; gradient "$B1" "$3" $c; at $(( row + 2 )) $x; gradient "$B2" "$3" $c
      sleep 0.016
    done
  fi
  at $row $x; gradient "$B0" "$3"; at $(( row + 1 )) $x; gradient "$B1" "$3"; at $(( row + 2 )) $x; gradient "$B2" "$3"
}
wait_for() {  # wait for one of the given keys (Ctrl+C twice leaves); the stars keep twinkling
  local want=" $* "
  while true; do
    sleep 0.08; twinkle
    poll_key || continue
    [ "$KEY" = ctrlc ] && { quit_key; continue; }
    case "$want" in *" $KEY "*) return ;; esac
  done
}
num() { printf '%s%s%s  %s' "$BOLD" "${CC[222]}" "$1" "$R"; }

# ---- steps: a spinner while each piece of real work runs, then ✓ or ✗
step_wait() { inner "$1"; printf '%s○  %s%s' "${CC[240]}" "$2" "$R"; }
progress() {  # row, percent
  local w=$(( IW - 6 )) fill i out=""
  fill=$(( w * $2 / 100 ))
  for ((i=0; i<w; i++)); do
    if (( i < fill )); then out+="${CC[${PAL[$(( i * ${#PAL[@]} / w ))]}]}━"; else out+="${CC[238]}─"; fi
  done
  inner $1; printf '%s%s %s%3d%%%s' "$out" "$R" "${CC[250]}" $2 "$R"
}
run_steps() {  # panel title, accent, then "label|detail|function" entries; returns 1 when a step fails
  local title="$1" acc=$2; shift 2
  local n=$# i=0 pct=0 target e label detail fn f row
  clear_lower; panel $LTOP $(( n + 5 )) "$title" $acc
  for e in "$@"; do step_wait $(( LTOP + 2 + i )) "${e%%|*}"; i=$(( i + 1 )); done
  progress $(( LTOP + n + 3 )) 0
  i=0
  for e in "$@"; do
    label="${e%%|*}"; detail="${e#*|}"; fn="${detail#*|}"; detail="${detail%%|*}"
    row=$(( LTOP + 2 + i ))
    for ((f=0; f<6; f++)); do
      inner $row; printf '%s%s%s  %s%s%s' "${CC[218]}" "${SPINNER[$(( f % 10 ))]}" "$R" "${CC[253]}" "$label" "$R"
      twinkle; sleep 0.06
    done
    ERR=""
    if $fn; then
      inner $row; printf '%s✓%s  %s%s%s%s' "${CC[121]}" "$R" "$BOLD" "${CC[253]}" "$label" "$R"
      [ -n "$detail" ] && note_right $row "$detail" 246
    else
      inner $row; printf '%s✗%s  %s%s%s%s' "${CC[203]}" "$R" "$BOLD" "${CC[253]}" "$label" "$R"
      note_right $row "failed" 203
      sleep 0.8
      return 1
    fi
    i=$(( i + 1 )); target=$(( 100 * i / n ))
    while [ $pct -lt $target ]; do pct=$(( pct + 5 )); [ $pct -gt $target ] && pct=$target; progress $(( LTOP + n + 3 )) $pct; sleep 0.02; done
  done
  sleep 0.4
  return 0
}
error_screen() {  # title; the message is in ERR
  clear_lower; panel $LTOP 8 "$1" 203
  wrap "${ERR:-Something went wrong.}" $IW 3
  local i; for i in 0 1 2; do inner $(( LTOP + 1 + i )); printf '%s%s%s' "${CC[253]}" "${WL[i]}" "$R"; done
  inner $(( LTOP + 5 )); printf '%s%s%s' "${CC[248]}" "$(trunc "Nothing was deleted. Details: BepInEx/cwyls-installer.log" $IW)" "$R"
  keys $(( LTOP + 9 )) "enter" "back to menu"
  log_line "error: $ERR"
  wait_for enter esc
}

# ---- buttons and the confirm dialog (the safe choice is selected first)
buttons() {  # row, selected (0/1), label0, label1, accent, what label1 does
  # The selected button is a filled pill with a marker; the other is plain text. Reads the same on
  # light and dark backgrounds (a dark filled box must never look like the selected one).
  local A B w
  if [ $2 -eq 0 ]; then A="${BGC[121]}${CC[234]}${BOLD} › $3 ${R}"; B="${CC[246]}   $4  ${R}"
  else A="${CC[246]}   $3  ${R}"; B="${BGC[$5]}${CC[234]}${BOLD} › $4 ${R}"; fi
  w=$(( ${#3} + ${#4} + 14 ))
  inner $1; at $1 $(( (COLS - w) / 2 + 1 )); printf '%s    %s' "$A" "$B"
  local will
  if [ $2 -eq 0 ]; then will="${CC[246]}Enter will: ${CC[253]}${BOLD}$(echo "$3" | tr '[:upper:]' '[:lower:]')${R}"
  else will="${CC[246]}Enter will: ${CC[$5]}${BOLD}${6:-$4}${R}"; fi
  inner $(( $1 + 1 )); at $(( $1 + 1 )) $(( PX + 3 )); printf '%s' "$will"
}
confirm() {  # title, accent, line 1, line 2, cancel label, ok label, what ok does; 0 when confirmed
  local sel=0
  clear_lower; panel $LTOP 8 "$1" $2
  inner $(( LTOP + 1 )); printf '%s%s%s' "${CC[253]}" "$(trunc "$3" $IW)" "$R"
  inner $(( LTOP + 2 )); printf '%s%s%s' "${CC[248]}" "$(trunc "$4" $IW)" "$R"
  buttons $(( LTOP + 4 )) $sel "$5" "$6" $2 "$7"
  keys $(( LTOP + 9 )) "←→" "choose" "enter" "confirm" "esc" "back"
  while true; do
    wait_for left right enter esc
    case "$KEY" in
      left|right) sel=$(( 1 - sel )); buttons $(( LTOP + 4 )) $sel "$5" "$6" $2 "$7" ;;
      esc) return 1 ;;
      enter) [ $sel -eq 1 ] && return 0; return 1 ;;
    esac
  done
}
steam_panel() {  # row, title, accent, then the lines to show
  local row=$1 title="$2" acc=$3; shift 3
  panel $row $(( $# + 2 )) "$title" $acc
  local i=1 l
  for l in "$@"; do inner $(( row + i )); printf '%s' "$l"; i=$(( i + 1 )); done
}

# ---- the game is running: wait until it closes (esc cancels)
ensure_closed() {
  game_running || return 0
  local f=0
  clear_lower; panel $LTOP 7 "THE GAME IS RUNNING" 222
  inner $(( LTOP + 1 )); printf '%s%s%s' "${CC[253]}" "$(trunc "Quit Chill With You first: its files cannot change while it runs." $IW)" "$R"
  inner $(( LTOP + 2 )); printf '%s%s%s' "${CC[246]}" "$(trunc "Your progress is saved by the game as usual." $IW)" "$R"
  keys $(( LTOP + 8 )) "esc" "cancel"
  while game_running; do
    inner $(( LTOP + 4 )); printf '%s%s%s  %swaiting for the game to close…%s' "${CC[222]}" "${SPINNER[$(( f % 10 ))]}" "$R" "${CC[248]}" "$R"
    f=$(( f + 1 )); sleep 0.08; twinkle
    if poll_key; then [ "$KEY" = ctrlc ] && quit_key; [ "$KEY" = esc ] && return 1; fi
  done
  inner $(( LTOP + 4 )); printf '%s✓%s  %s%sThe game is closed. Carrying on…%s' "${CC[121]}" "$R" "$BOLD" "${CC[253]}" "$R"
  sleep 1
  return 0
}

# ---- the main screen: your setup + the menu
refresh_state() {
  IV="$(installed_version)"
  BEP=0; bepinex_installed && BEP=1
  LOAD=0; loader_installed && LOAD=1
  TRACE="$(trace_level)"; MODS="$(mods_count)"
  STEAM_OK=0; steam_ready && STEAM_OK=1
}
SHORT_PATH="${GAME_ROOT/#$HOME/~}"
[ ${#SHORT_PATH} -gt $(( IW - 12 )) ] && SHORT_PATH="…${SHORT_PATH: -$(( IW - 13 ))}"
setup_row() { inner "$1"; printf '%s%-12s%s' "${CC[247]}" "$2" "$R"; }
draw_setup() {
  local vw=$(( IW - 15 ))
  panel $SP 6 "YOUR SETUP"
  setup_row $(( SP + 1 )) "Game";     printf '%s✓%s  %s%sChill With You: Lo-Fi Story%s' "${CC[121]}" "$R" "$BOLD" "${CC[253]}" "$R"
  setup_row $(( SP + 2 )) "Location"; printf '%s%s%s' "${CC[250]}" "$SHORT_PATH" "$R"
  setup_row $(( SP + 3 )) "Mod loader"
  if [ $LOAD -eq 1 ] && [ "$IV" = "$VERSION" ]; then
    printf '%s✓%s  %s%s%s' "${CC[121]}" "$R" "${CC[253]}" "$(trunc "$VERSION_UI installed, with BepInEx $BEPINEX_VERSION" $vw)" "$R"
  elif [ $LOAD -eq 1 ]; then
    printf '%s↑%s  %s%s%s  %s' "${CC[117]}" "$R" "${CC[253]}" "$(trunc "v${IV%%-*} → $VERSION_UI" $(( vw - 8 )))" "$R" "$(pill 117 NEW)"
  elif [ $BEP -eq 1 ]; then
    printf '%s○%s  %s%s%s' "${CC[222]}" "$R" "${CC[253]}" "$(trunc "not installed (BepInEx is)" $vw)" "$R"
  else
    printf '%s○%s  %s%s%s' "${CC[222]}" "$R" "${CC[253]}" "$(trunc "not installed yet" $vw)" "$R"
  fi
  setup_row $(( SP + 4 )) "Steam"
  if [ $STEAM_OK -eq 1 ]; then printf '%s✓%s  %s%s%s' "${CC[121]}" "$R" "${CC[253]}" "$(trunc "starts the game through BepInEx" $vw)" "$R"
  elif [ $BEP -eq 1 ]; then printf '%s○%s  %s%s%s' "${CC[222]}" "$R" "${CC[253]}" "$(trunc "launch option not set yet (Install shows how)" $vw)" "$R"
  else printf '%s–  %s%s' "${CC[242]}" "$(trunc "nothing to set until BepInEx is installed" $vw)" "$R"; fi
}
MENU=("Install or update" "Diagnostic trace" "Open the mods folder" "Uninstall the mod loader" "Uninstall everything" "Quit")
menu_meta() {
  META=("" "" "" "" "" "")
  if [ $LOAD -eq 1 ] && [ "$IV" != "$VERSION" ]; then META[0]="@117:UPDATE"
  elif [ $LOAD -eq 1 ]; then META[0]="reinstall"; else META[0]="BepInEx + loader"; fi
  case $TRACE in off) META[1]="@244:OFF" ;; on) META[1]="@121:ON" ;; full) META[1]="@222:FULL" ;; esac
  if [ "$MODS" -eq 1 ]; then META[2]="1 mod"; elif [ "$MODS" -gt 1 ]; then META[2]="$MODS mods"; else META[2]="empty"; fi
  if [ $LOAD -eq 1 ]; then META[3]="keeps BepInEx"; else META[3]="not installed"; fi
  if [ $LOAD -eq 1 ] || [ $BEP -eq 1 ]; then META[4]="moves files aside"; else META[4]="nothing installed"; fi
}
draw_item() {
  local i=$1 y=$(( MP + 1 + i )) meta="${META[$i]}" pc pt
  at $y $(( PX + 1 ))
  if [ $i -eq $sel ]; then printf '%s%s▌ %s%s%-*s' "${BGC[236]}" "${CC[218]}" "$BOLD" "${CC[255]}" $(( IW - 1 )) "${MENU[$i]}"
  else printf '%s  %s%-*s' "$R" "${CC[252]}" $(( IW - 1 )) "${MENU[$i]}"; fi
  if [ "${meta:0:1}" = "@" ]; then
    pc="${meta:1}"; pt="${pc#*:}"; pc="${pc%%:*}"
    at $y $(( PX + PW - 3 - ${#pt} - 2 )); printf '%s' "$(pill $pc "$pt")"
  elif [ -n "$meta" ]; then
    at $y $(( PX + PW - 3 - ${#meta} ))
    if [ $i -eq $sel ]; then printf '%s%s%s' "${BGC[236]}${CC[252]}" "$meta" "$R"; else printf '%s%s%s' "${CC[246]}" "$meta" "$R"; fi
  fi
  [ $i -eq $sel ] && { at $y $(( PX + PW - 2 )); printf '%s %s' "${BGC[236]}" "$R"; }
  printf '%s' "$R"
}
draw_menu() { local i; menu_meta; for i in "${!MENU[@]}"; do draw_item $i; done; }
NOTICE=""
draw_main() {
  refresh_state
  clear_lower; draw_setup
  panel $MP $(( ${#MENU[@]} + 2 )) "WHAT WOULD YOU LIKE TO DO"
  draw_menu
  if [ -n "$NOTICE" ]; then toast $KEYS2 "$NOTICE" 222; NOTICE=""; fi
  keys $KEYS2 "↑↓" "move" "enter" "choose" "q" "quit"
}

# ---- 1. install or update
act_install() {
  ensure_closed || { NOTICE="Nothing changed."; return 0; }
  local title="INSTALLING"; [ $LOAD -eq 1 ] && title="UPDATING"
  local steps=("Game is closed|checked|do_nothing")
  needs_migration && steps+=("An earlier build|tidied up, nothing deleted|do_migrate")
  steps+=("BepInEx $BEPINEX_VERSION|stock, unmodified|do_bepinex" "CWY:LS Mod Loader $VERSION_UI|$(ls "$FILES/$PLUGIN_DIR_NAME/"*.dll 2>/dev/null | wc -l | tr -d ' ') files|do_loader" "Your mods folder|$MODS_DIR_NAME|do_mods_folder")
  if ! run_steps "$title" 218 "${steps[@]}"; then error_screen "THE INSTALL STOPPED"; return 0; fi
  log_line "installed $VERSION"
  SUMMARY+=("Installed CWY:LS Mod Loader $VERSION_UI and BepInEx $BEPINEX_VERSION in $GAME_ROOT")
  refresh_state
  clear_lower
  bigword $LTOP "ALL SET" "$MINT" 1
  local others; others="$(other_mods)"
  if [ -n "$others" ]; then
    center $(( LTOP + 3 )) "Installed. Your save files were not touched." "${CC[252]}"
    center $(( LTOP + 4 )) "$(trunc "Also in BepInEx/plugins: $others (not tested with CWY:LS)" $(( COLS - 4 )))" "${CC[222]}"
  else
    center $(( LTOP + 4 )) "Installed. Your save files were not touched." "${CC[252]}"
  fi
  local pw=$(( IW - 3 )) ll copied
  if [ $STEAM_OK -eq 1 ]; then
    steam_panel $(( LTOP + 7 )) "STEAM" 121 "${CC[121]}✓$R  ${CC[252]}$(trunc "Steam already starts the game through BepInEx. Just press Play." $(( IW - 3 )))$R"
    keys $(( LTOP + 11 )) "enter" "back to menu"
  else
    # The launch line is never shortened: Steam needs every character, so it wraps over as many
    # rows as it takes. It is on the clipboard too, and printed in full when the installer quits.
    ll="$(launch_line)"
    local step2="$(num 2)${CC[252]}$(trunc "Paste this whole line into Launch Options:" $pw)$R"
    # COPIED only when the clipboard reads back exactly the line; otherwise the line is copied by hand.
    if [ $TTY_OUT -eq 1 ] && copy_verified "$ll"; then
      step2="$(num 2)${CC[252]}In Launch Options press Cmd V  $R$(pill 121 COPIED)"
    elif [ $TTY_OUT -eq 1 ]; then
      step2="$(num 2)${CC[252]}$(trunc "Copying failed: select the whole line below and copy it" $(( pw - 2 )))$R"
    fi
    SUMMARY+=("Steam: right-click the game > Properties... > Launch Options, paste exactly:  $ll")
    local lines=("$(num 1)${CC[252]}$(trunc "Right-click the game in Steam > Properties…" $pw)$R" "$step2") rest="$ll"
    while [ -n "$rest" ]; do lines+=("   ${CC[218]}${rest:0:$pw}$R"); rest="${rest:$pw}"; done
    lines+=("$(num 3)${CC[252]}$(trunc "Press Play. ctrl shift C opens the console." $pw)$R")
    steam_panel $(( LTOP + 5 )) "ONE STEP LEFT  ·  STEAM" 222 "${lines[@]}"
    local kr=$(( LTOP + 5 + ${#lines[@]} + 3 )); [ $kr -gt $LBOT ] && kr=$LBOT
    keys $kr "enter" "back to menu"
  fi
  wait_for enter
}

# ---- 2. diagnostic trace
act_trace() {
  local names=("Off" "On" "Full") vals=(off on full) acc=(244 121 222)
  local descs=("nothing is recorded" "key events, small files: for bug reports" "every game method, large files: for developers")
  local sel=0 i y
  case $TRACE in on) sel=1 ;; full) sel=2 ;; esac
  clear_lower; panel $LTOP 9 "DIAGNOSTIC TRACE" 183
  inner $(( LTOP + 1 )); printf '%s%s%s' "${CC[252]}" "$(trunc "Records what the game and the mod loader do, so a bug can be found." $IW)" "$R"
  inner $(( LTOP + 2 )); printf '%s%s%s' "${CC[246]}" "$(trunc "It starts with the next game launch. Files go to BepInEx/trace." $IW)" "$R"
  draw_opts() {
    for i in 0 1 2; do
      y=$(( LTOP + 4 + i )); inner $y
      if [ $i -eq $sel ]; then printf '%s●%s  %s%s%-6s%s' "${CC[${acc[$i]}]}" "$R" "$BOLD" "${CC[255]}" "${names[$i]}" "$R"
      else printf '%s○  %s%-6s%s' "${CC[240]}" "${CC[248]}" "${names[$i]}" "$R"; fi
      printf '  %s%s%s' "${CC[246]}" "$(trunc "${descs[$i]}" $(( IW - 22 )))" "$R"
      [ "${vals[$i]}" = "$TRACE" ] && { at $y $(( PX + PW - 3 - 9 )); printf '%s' "$(pill 238 CURRENT)"; }
    done
  }
  draw_opts
  keys $(( LTOP + 10 )) "↑↓" "choose" "enter" "apply" "esc" "back"
  while true; do
    wait_for up down enter esc
    case "$KEY" in
      up) sel=$(( (sel + 2) % 3 )); draw_opts ;;
      down) sel=$(( (sel + 1) % 3 )); draw_opts ;;
      esc) return ;;
      enter)
        if set_trace "${vals[$sel]}"; then
          TRACE=${vals[$sel]}; draw_opts; log_line "trace ${vals[$sel]}"
          SUMMARY+=("Diagnostic trace: ${names[$sel]} (from the next game start)")
          toast $(( LTOP + 10 )) "✓  Trace set to $(echo "${names[$sel]}" | tr '[:lower:]' '[:upper:]'), from the next game start" 121
        else
          toast $(( LTOP + 10 )) "✗  $ERR" 203
        fi
        return ;;
    esac
  done
}

# ---- 3. the mods folder
act_open() {
  do_mods_folder
  local opened="Here it is"
  if [ $TTY_OUT -eq 1 ] && open "$GAME_ROOT/$MODS_DIR_NAME" 2>/dev/null; then opened="Opened in Finder"; fi
  refresh_state
  clear_lower; panel $LTOP 7 "YOUR MODS FOLDER" 117
  inner $(( LTOP + 1 )); printf '%s✓%s  %s%s%s%s' "${CC[121]}" "$R" "$BOLD" "${CC[253]}" "$opened" "$R"
  note_right $(( LTOP + 1 )) "$(if [ "$MODS" -eq 1 ]; then echo "1 mod"; else echo "$MODS mods"; fi)" 246
  inner $(( LTOP + 2 )); printf '   %s%s%s' "${CC[250]}" "$(mid "${GAME_ROOT/#$HOME/~}/$MODS_DIR_NAME" $(( IW - 3 )))" "$R"
  inner $(( LTOP + 4 )); printf '%s%s%s' "${CC[252]}" "$(trunc "Each mod is a folder with a manifest.json. Drop it in here." $IW)" "$R"
  inner $(( LTOP + 5 )); printf '%s%s%s' "${CC[246]}" "$(trunc "The game reads this folder when it starts. Only add mods you trust." $IW)" "$R"
  keys $(( LTOP + 8 )) "enter" "back to menu"
  wait_for enter esc
}

# ---- 4. uninstall the mod loader
act_uninstall() {
  if [ $LOAD -eq 0 ]; then toast $KEYS2 "The mod loader is not installed." 222; keys $KEYS2 "↑↓" "move" "enter" "choose" "q" "quit"; return 1; fi
  confirm "REMOVE THE MOD LOADER?" 222 "BepInEx and your \"$MODS_DIR_NAME\" folder stay where they are." \
    "The loader is moved to BepInEx/_old, never deleted." "Keep it" "Remove" "REMOVE the mod loader" || { NOTICE="Nothing changed: the mod loader is still installed."; return 0; }
  ensure_closed || { NOTICE="Nothing changed."; return 0; }
  if ! run_steps "REMOVING" 222 "Game is closed|checked|do_nothing" "CWY:LS Mod Loader|moved to BepInEx/_old|do_uninstall_loader"; then
    error_screen "NOTHING WAS REMOVED"; return 0
  fi
  log_line "uninstalled the loader -> $MOVED_TO"
  SUMMARY+=("Removed the mod loader (moved to $MOVED_TO)")
  clear_lower
  bigword $LTOP "REMOVED" "$AMBER" 1
  center $(( LTOP + 4 )) "Your saves were not touched. Reinstall any time from the menu." "${CC[252]}"
  center $(( LTOP + 5 )) "$(mid "Moved to $MOVED_TO; delete it whenever you like." $(( COLS - 4 )))" "${CC[246]}"
  keys $(( LTOP + 7 )) "enter" "back to menu"
  wait_for enter
}

# ---- 5. uninstall everything
act_uninstall_all() {
  if [ $LOAD -eq 0 ] && [ $BEP -eq 0 ]; then toast $KEYS2 "Nothing to remove: BepInEx and the loader are not installed." 222; keys $KEYS2 "↑↓" "move" "enter" "choose" "q" "quit"; return 1; fi
  confirm "REMOVE EVERYTHING?" 211 "BepInEx and the mod loader leave the game folder." \
    "Your \"$MODS_DIR_NAME\" folder and your saves stay. Nothing is deleted." "Keep everything" "Remove all" "REMOVE BepInEx and the mod loader" || { NOTICE="Nothing changed: BepInEx and the mod loader are still installed."; return 0; }
  ensure_closed || { NOTICE="Nothing changed."; return 0; }
  log_line "removing BepInEx and the mod loader"
  if ! run_steps "REMOVING EVERYTHING" 211 "Game is closed|checked|do_nothing" "BepInEx and the mod loader|moved aside|do_uninstall_all"; then
    error_screen "NOTHING WAS REMOVED"; return 0
  fi
  SUMMARY+=("Removed BepInEx and the mod loader (moved to $MOVED_TO). Clear the game's Launch Options in Steam.")
  clear_lower
  bigword $LTOP "ALL CLEAN" "$ROSE" 1
  center $(( LTOP + 4 )) "The game is back to how it shipped. Your saves were not touched." "${CC[252]}"
  center $(( LTOP + 5 )) "$(mid "Moved to $MOVED_TO in the game folder." $(( COLS - 4 )))" "${CC[246]}"
  local pw=$(( IW - 3 ))
  steam_panel $(( LTOP + 7 )) "ONE STEP LEFT  ·  STEAM" 222 \
    "$(num 1)${CC[252]}$(trunc "In Steam, right-click the game, then Properties…" $pw)$R" \
    "$(num 2)${CC[252]}$(trunc "Clear the Launch Options box." $pw)$R"
  keys $(( LTOP + 12 )) "enter" "back to menu"
  wait_for enter
}

# ---- the game was not found / this is the demo
screen_notfound() {
  local line
  while true; do
    clear_lower; panel $LTOP 8 "WHERE IS THE GAME?" 117
    inner $(( LTOP + 1 )); printf '%s%s%s' "${CC[253]}" "$(trunc "I could not find Chill With You in your Steam libraries." $IW)" "$R"
    inner $(( LTOP + 2 )); printf '%s%s%s' "${CC[246]}" "$(trunc "Drag the game's folder in here and press Enter." $IW)" "$R"
    [ -n "${1:-}" ] && { inner $(( LTOP + 6 )); printf '%s✗%s  %s%s%s' "${CC[203]}" "$R" "${CC[252]}" "$(trunc "$1" $(( IW - 3 )))" "$R"; }
    inner $(( LTOP + 4 )); printf '%s›%s ' "${CC[218]}" "$R"
    # line input: the terminal's own editing and drag and drop, then back to raw keys
    [ -n "$STTY_SAVED" ] && stty "$STTY_SAVED" 2>/dev/null
    printf '%s[?25h' "$ESC"
    CTRLC_HIT=0; trap plain_ctrlc INT
    IFS= read -r line || line=""
    trap - INT
    printf '%s[?25l' "$ESC"
    stty -echo -icanon min 0 time 0 2>/dev/null
    if [ -z "$line" ]; then set -- "Drag the game folder in here, or press Ctrl+C twice to quit."; continue; fi
    line="$(clean_path "$line")"
    if is_game "$line"; then GAME_ROOT="$line"; clear_lower; return 0; fi
    set -- "That folder has no \"$APP\" in it. Try again."
  done
}
screen_demo() {
  clear_lower; panel $LTOP 7 "THIS LOOKS LIKE THE DEMO" 211
  inner $(( LTOP + 1 )); printf '%s%s%s' "${CC[253]}" "$(trunc "Mods need the full game. Nothing was installed." $IW)" "$R"
  inner $(( LTOP + 2 )); printf '%s%s%s' "${CC[246]}" "$(trunc "The demo stays exactly as it is." $IW)" "$R"
  inner $(( LTOP + 4 )); printf '%s' "$(pill 211 "FULL GAME ONLY")"; printf '  %s%s%s' "${CC[252]}" "$(trunc "Get it on Steam for mods." $(( IW - 20 )))" "$R"
  keys $(( LTOP + 8 )) "enter" "close"
  wait_for enter esc
  farewell
}

# ---- goodbye: SEE YOU, then the name scatters back into stars
farewell() {
  local f p k e x y buf s
  FAST=0
  clear_lower
  bigword $(( LTOP + 1 )) "SEE YOU" "$DUSK" 1
  center $(( LTOP + 5 )) "Happy modding." "${CC[250]}"
  sleep 0.9
  for ((p=0; p<NP; p++)); do PSX[p]=$(( RANDOM % COLS + 1 )); PSY[p]=$(( RANDOM % ROWS + 1 )); PD[p]=$(( RANDOM % 8 )); OX[p]=${TX[p]}; OY[p]=${TYP[p]}; done
  for ((i=0; i<6; i++)); do clear_row $(( Y0 + i )); done
  for ((f=0; f<NE+8; f++)); do
    buf=""
    for ((p=0; p<NP; p++)); do
      k=$(( f - PD[p] )); (( k < 0 )) && k=0; (( k >= NE )) && k=$(( NE - 1 ))
      e=$(( 1000 - EASE[NE - 1 - k] ))
      x=$(( TX[p] + (PSX[p] - TX[p]) * e / 1000 )); y=$(( TYP[p] + (PSY[p] - TYP[p]) * e / 1000 ))
      (( OX[p] != x || OY[p] != y )) && buf+="${ESC}[${OY[p]};${OX[p]}H "
      if (( e < 150 )); then buf+="${ESC}[${y};${x}H${CC[${TC[p]}]}█"
      elif (( e < 600 )); then buf+="${ESC}[${y};${x}H${CC[${TC[p]}]}•"
      else buf+="${ESC}[${y};${x}H${CC[$(( 250 - e / 200 ))]}·"; fi
      OX[p]=$x; OY[p]=$y
    done
    printf '%s%s' "$buf" "$R"; sleep 0.03
  done
  sleep 0.4
  for ((p=0; p<NP; p++)); do at ${OY[p]} ${OX[p]}; printf ' '; done
  for s in "${!SX[@]}"; do star $s 0 "·"; done; sleep 0.25
  exit 0
}

# 0. Night falls.
for s in "${!SX[@]}"; do star $s $(( RANDOM % 3 )) "·"; (( s % 4 == 0 )) && frame 0.012; done

# 1. Stardust: points of light fly in from all over the sky and assemble into CWY:LS.
EASE=(0 150 284 403 508 600 680 748 806 854 894 925 950 969 982 991 996 999 1000 1000)
NE=${#EASE[@]}
TX=(); TYP=(); TC=()
for ((i=0; i<6; i++)); do
  row="${ART[$i]}"; len=${#row}
  for ((j=0; j<len; j++)); do
    [ "${row:j:1}" = "█" ] || continue
    TX+=($(( X0 + j ))); TYP+=($(( Y0 + i ))); TC+=(${PAL[$(( j * ${#PAL[@]} / len ))]})
  done
done
NP=${#TX[@]}
PSX=(); PSY=(); PD=(); OX=(); OY=()
for ((p=0; p<NP; p++)); do PSX[p]=$(( RANDOM % COLS + 1 )); PSY[p]=$(( RANDOM % ROWS + 1 )); PD[p]=$(( RANDOM % 8 )); OX[p]=0; OY[p]=0; done
for ((f=0; f<NE+8; f++)); do
  buf=""
  for ((p=0; p<NP; p++)); do
    k=$(( f - PD[p] )); (( k < 0 )) && continue; (( k >= NE )) && k=$(( NE - 1 ))
    e=${EASE[k]}
    x=$(( PSX[p] + (TX[p] - PSX[p]) * e / 1000 )); y=$(( PSY[p] + (TYP[p] - PSY[p]) * e / 1000 ))
    (( OX[p] > 0 && (OX[p] != x || OY[p] != y) )) && buf+="${ESC}[${OY[p]};${OX[p]}H "
    if (( e >= 1000 )); then buf+="${ESC}[${y};${x}H${CC[${TC[p]}]}█"
    elif (( e > 820 )); then buf+="${ESC}[${y};${x}H${CC[${TC[p]}]}•"
    else buf+="${ESC}[${y};${x}H${CC[$(( 244 + e / 170 ))]}·"; fi
    OX[p]=$x; OY[p]=$y
  done
  printf '%s%s' "$buf" "$R"
  frame 0.03
  [ "$FAST" = 1 ] && break
done
# The flash: all white for a moment, then the colours and the shadow settle in.
for ((i=0; i<6; i++)); do clear_row $(( Y0 + i )); at $(( Y0 + i )) $X0; printf '%s%s%s' "${CC[231]}" "${ART[$i]}" "$R"; done
frame 0.07
for ((i=0; i<6; i++)); do at $(( Y0 + i )) $X0; printf '%s' "${PAINTED[$i]}"; done
frame 0.12

# 2. A shine sweeps across the letters.
for ((h=0; h<=W+4; h+=3)); do
  for ((i=0; i<6; i++)); do at $(( Y0 + i )) $X0; paint "${ART[$i]}" $(( h - i )); done
  frame 0.016
done
for ((i=0; i<6; i++)); do at $(( Y0 + i )) $X0; printf '%s' "${PAINTED[$i]}"; done

# 3. The horizon grows from the middle; the caption fades in.
MID=$(( X0 + W / 2 ))
for ((r=0; r<=W/2; r+=2)); do
  at $HR $(( MID - r )); printf '%s%s%s' "${CC[238]}" "$(printf '%*s' $(( r * 2 + 1 )) '' | tr ' ' '─')" "$R"
  frame 0.01
done
for lv in 236 240 244 248 252; do
  if [ "$MODE" = full ]; then
    center $CAP1 "M O D   L O A D E R" "${CC[$lv]}"
    center $CAP2 "$VERSION_UI  ·  developer preview" "${CC[$(( lv - 8 < 232 ? 232 : lv - 8 ))]}"
  else
    center $CAP1 "M O D   L O A D E R   ·   $VERSION_UI" "${CC[$lv]}"
  fi
  frame 0.05
done

# ---------------------------------------------------------------- panels, pills and keycaps
rep() { local out="" i; for ((i=0; i<$2; i++)); do out+="$1"; done; printf '%s' "$out"; }
BORDER=240
panel() {  # top row, height, title, [title colour]: a rounded panel with the title set into the top border
  local top=$1 h=$2 title=" $3 " tc=${4:-248} i
  at $top $PX
  printf '%s╭─%s%s%s%s%s%s╮%s' "${CC[$BORDER]}" "$BOLD" "${CC[$tc]}" "$title" "$R" "${CC[$BORDER]}" "$(rep ─ $(( PW - 3 - ${#title} )))" "$R"
  for ((i=1; i<h-1; i++)); do
    at $(( top + i )) $PX; printf '%s│%s%*s%s│%s' "${CC[$BORDER]}" "$R" $(( PW - 2 )) '' "${CC[$BORDER]}" "$R"
  done
  at $(( top + h - 1 )) $PX; printf '%s╰%s╯%s' "${CC[$BORDER]}" "$(rep ─ $(( PW - 2 )))" "$R"
}
inner() { at "$1" $(( PX + 2 )); printf '%*s' $IW ''; at "$1" $(( PX + 2 )); }   # clear an inner row, cursor at its start
pill() { printf '%s%s%s %s %s' "${BGC[$1]}" "${CC[234]}" "$BOLD" "$2" "$R"; }   # colour, text
keycap() { printf '%s%s%s %s %s%s %s%s' "${BGC[238]}" "${CC[255]}" "$BOLD" "$1" "$R" "${CC[248]}" "$2" "$R"; }
keys() {  # row, then pairs of key and label
  KR=$1; KARGS=("$@")
  local row=$1; shift; local out="" w=0
  while [ $# -ge 2 ]; do out+="$(keycap "$1" "$2")    "; w=$(( w + ${#1} + ${#2} + 7 )); shift 2; done
  w=$(( w - 4 ))                                    # no gap after the last pair
  at $row $(( (COLS - w) / 2 + 1 )); printf '%s' "$out"
}
wrap2() {  # text, width -> L1, L2 (word wrap into two lines, the second shortened with … if needed)
  local t="$1" w=$2 cut; L1="$t"; L2=""
  [ ${#t} -le $w ] && return
  cut=$w; while [ $cut -gt 0 ] && [ "${t:cut:1}" != " " ]; do cut=$(( cut - 1 )); done
  [ $cut -eq 0 ] && cut=$w
  L1="${t:0:cut}"; L2="${t:cut+1}"
  [ ${#L2} -gt $w ] && L2="${L2:0:$(( w - 1 ))}…"
}

if [ "$FIRST_RUN" = 1 ]; then
# 4. THANK YOU, big, wiped in from the left in the dusk colours.
bigrows "THANK  YOU"
BX=$(( (COLS - ${#B0}) / 2 + 1 ))
for ((c=1; c<=${#B0}; c+=2)); do
  at $TY $BX;           gradient "$B0" "${PAL[*]}" $c
  at $(( TY + 1 )) $BX; gradient "$B1" "${PAL[*]}" $c
  at $(( TY + 2 )) $BX; gradient "$B2" "${PAL[*]}" $c
  frame 0.018
done
at $TY $BX; gradient "$B0" "${PAL[*]}"; at $(( TY + 1 )) $BX; gradient "$B1" "${PAL[*]}"; at $(( TY + 2 )) $BX; gradient "$B2" "${PAL[*]}"
T2="for choosing CWY:LS Mod Loader"
for lv in 240 244 248 252; do
  at $(( TY + 4 )) $(( (COLS - ${#T2}) / 2 + 1 ))
  nc=218; [ $lv -lt 248 ] && nc=$lv
  printf '%sfor choosing %s%sCWY:LS Mod Loader%s' "${CC[$lv]}" "$BOLD" "${CC[$nc]}" "$R"
  frame 0.05
done
frame 1.3

# 5. What it can do: a panel with one feature at a time, its name in big letters.
for ((r=MSG; r<=Y0+BLOCK-1; r++)); do clear_line $r; done
ACC=(218 216 183 117 121)
WORDS=(MUSIC SCENERY STORY CODE SAFE)
TITLES=("Music and sound" "Scenery and colour" "Lines and languages" "Code mods" "Your saves stay yours")
TEXTS=(
"Swap any game sound, or add your own songs to the music player."
"Recolour the room: a warm film look, a rainy evening, or your own."
"New lines and short scenes for Satone, or a whole new language."
"A documented C# API, built to keep working when the game updates."
"Nothing goes into your save files, and uninstalling leaves no trace."
)
NCARD=${#TITLES[@]}
BIGW=0; for w in "${WORDS[@]}"; do bigrows "$w"; [ ${#B0} -gt $BIGW ] && BIGW=${#B0}; done
TXC=$(( PX + 3 + BIGW + 3 ))                      # text column
TW=$(( PX + PW - 3 - TXC ))                       # text width
wrap() {  # text, width, lines -> WL[0..lines-1]
  local t="$1" w=$2 n=$3 i cut; WL=()
  for ((i=0; i<n; i++)); do
    if [ ${#t} -le $w ]; then WL[i]="$t"; t=""; continue; fi
    cut=$w; while [ $cut -gt 0 ] && [ "${t:cut:1}" != " " ]; do cut=$(( cut - 1 )); done
    [ $cut -eq 0 ] && cut=$w
    WL[i]="${t:0:cut}"; t="${t:cut+1}"
  done
  [ -n "$t" ] && WL[n-1]="${WL[n-1]:0:$(( w - 1 ))}…"
}
segments() {  # the page indicator, set into the panel's bottom border
  local k=$1 i seg="" w=$(( NCARD * 4 - 1 ))
  for ((i=0; i<NCARD; i++)); do
    if [ $i -eq $k ]; then seg+="${CC[${ACC[$i]}]}━━━${R}"; else seg+="${CC[238]}───${R}"; fi
    [ $i -lt $(( NCARD - 1 )) ] && seg+="${CC[$BORDER]}─${R}"
  done
  at $(( FP + 6 )) $(( PX + (PW - w) / 2 )); printf '%s' "$seg"
}
card() {  # feature $1 at brightness step $2 (0..3, for the fade)
  local k=$1 step=$2 i
  local tl=(240 245 250 255) bl=(240 244 248 252)
  local acc=${ACC[$k]}
  [ $step -eq 0 ] && acc=238
  [ $step -eq 1 ] && acc=242
  bigrows "${WORDS[$k]}"; wrap "${TEXTS[$k]}" $TW 3
  for ((i=0; i<4; i++)); do inner $(( FP + 2 + i )); done
  at $(( FP + 2 )) $(( PX + 3 )); printf '%s%s%s%s' "$BOLD" "${CC[$acc]}" "$B0" "$R"
  at $(( FP + 3 )) $(( PX + 3 )); printf '%s%s%s%s' "$BOLD" "${CC[$acc]}" "$B1" "$R"
  at $(( FP + 4 )) $(( PX + 3 )); printf '%s%s%s%s' "$BOLD" "${CC[$acc]}" "$B2" "$R"
  at $(( FP + 2 )) $TXC; printf '%s%s%s%s' "$BOLD" "${CC[${tl[$step]}]}" "${TITLES[$k]}" "$R"
  for ((i=0; i<3; i++)); do at $(( FP + 3 + i )) $TXC; printf '%s%s%s' "${CC[${bl[$step]}]}" "${WL[i]}" "$R"; done
}
show_card() { local s; for s in 0 1 2 3; do card $1 $s; [ "$FAST" = 1 ] || sleep 0.045; done; segments $1; }

panel $FP 7 "WHAT YOU CAN DO"
keys $KEYS1 "←→" "browse" "enter" "continue"
FAST=0; k=0; show_card 0; idle=0
while true; do
  sleep 0.08
  twinkle
  if poll_key; then
    case "$KEY" in
      right) k=$(( (k + 1) % NCARD )); show_card $k; idle=0 ;;
      left) k=$(( (k + NCARD - 1) % NCARD )); show_card $k; idle=0 ;;
      enter) break ;;
      q|ctrlc) quit_key ;;
    esac
  fi
  idle=$(( idle + 1 ))
  if [ $idle -ge 40 ]; then        # about 3 s on a card, then the next one
    if [ $k -eq $(( NCARD - 1 )) ]; then clear_line $KEYS1; keys $KEYS1 "enter" "begin" "←→" "browse again"; idle=-999
    else k=$(( k + 1 )); show_card $k; idle=0; fi
  fi
done
fi

# ---------------------------------------------------------------- 6. finding the game
for ((r=MSG; r<=Y0+BLOCK-1; r++)); do clear_line $r; done
if [ -z "$GAME_ROOT" ]; then screen_notfound; fi
SHORT_PATH="${GAME_ROOT/#$HOME/~}"
[ ${#SHORT_PATH} -gt $(( IW - 12 )) ] && SHORT_PATH="…${SHORT_PATH: -$(( IW - 13 ))}"
if is_demo "$GAME_ROOT"; then screen_demo; fi
refresh_state
panel $SP 6 "YOUR SETUP"
FAST=0
for ((f=0; f<12; f++)); do
  inner $(( SP + 1 )); printf '%s%-12s%s%s%s%s  %slooking in your Steam libraries…%s' "${CC[247]}" "Game" "$R" "${CC[218]}" "${SPINNER[$(( f % 10 ))]}" "$R" "${CC[248]}" "$R"
  twinkle; frame 0.06
done
draw_setup

# ---------------------------------------------------------------- 7. the menu
sel=0
menu_meta
panel $MP $(( ${#MENU[@]} + 2 )) "WHAT WOULD YOU LIKE TO DO"
for i in "${!MENU[@]}"; do draw_item $i; frame 0.04; done
keys $KEYS2 "↑↓" "move" "enter" "choose" "q" "quit"
while true; do
  sleep 0.08; twinkle
  poll_key || continue
  case "$KEY" in
    up) sel=$(( (sel + ${#MENU[@]} - 1) % ${#MENU[@]} )); draw_menu ;;
    down) sel=$(( (sel + 1) % ${#MENU[@]} )); draw_menu ;;
    [1-6]) sel=$(( KEY - 1 )); draw_menu ;;
    q|ctrlc) quit_key ;;
    esc) hint "Choose Quit to leave (or press q twice)" ;;
    enter)
      case $sel in
        0) act_install; draw_main ;;
        1) act_trace; draw_main ;;
        2) act_open; draw_main ;;
        3) act_uninstall && draw_main ;;
        4) act_uninstall_all && draw_main ;;
        5) farewell ;;
      esac ;;
  esac
done
