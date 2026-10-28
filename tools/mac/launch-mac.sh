#!/bin/bash
# Launches the game with BepInEx injected, natively on Apple Silicon.
# SteamAppId/SteamGameId make Steamworks' RestartAppIfNecessary() return false; without them the game
# quits and Steam relaunches it WITHOUT the injector (that is what happened on the first try).
# Steam must be running (the game still initialises Steamworks).
# Usage: launch-mac.sh [--log]   (--log tails BepInEx/LogOutput.log after launch)
set -euo pipefail
GAME_ROOT="${GAME_ROOT:-$HOME/Library/Application Support/Steam/steamapps/common/Chill with You Lo-Fi Story}"
LOG="$GAME_ROOT/BepInEx/LogOutput.log"
[ -x "$GAME_ROOT/run_bepinex.sh" ] || { echo "BepInEx not installed; run install.command from the release zip first" >&2; exit 1; }
if pgrep -f "MacOS/Chill With You" >/dev/null; then
  echo "Game already running; quitting it first..."; osascript -e 'tell application "Chill With You" to quit' || true
  for _ in $(seq 1 15); do pgrep -f "MacOS/Chill With You" >/dev/null || break; sleep 1; done
fi
rm -f "$LOG"
cd "$GAME_ROOT"
( SteamAppId=3548580 SteamGameId=3548580 ./run_bepinex.sh "Chill With You.app" >/dev/null 2>&1 & )
echo "Launched. BepInEx log: $LOG"
if [ "${1:-}" = "--log" ]; then
  for _ in $(seq 1 30); do [ -f "$LOG" ] && break; sleep 1; done
  tail -f "$LOG"
fi
