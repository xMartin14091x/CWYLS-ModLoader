#!/usr/bin/env bash
# Deploy the content packs kept in packs/ to the game's CWYLS Mods folder.
#
#   ./deploy-packs.sh              deploy every packs/<id>/ that has a manifest.json
#   GAME_ROOT=... ./deploy-packs.sh   deploy somewhere else
#
# Each pack folder is replaced, not merged: a file deleted from the repo pack must not survive in
# the deployed copy. This script never launches the game and writes nothing outside CWYLS Mods.
# The game reads packs once at startup, so restart it after deploying.
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
SOURCE="$HERE/packs"

while [ $# -gt 0 ]; do
  case "$1" in
    -h|--help) sed -n '2,10p' "$0"; exit 0 ;;
    *) echo "unknown option: $1" >&2; exit 2 ;;
  esac
done

case "$(uname -s)" in
  Darwin)
    GAME_ROOT="${GAME_ROOT:-$HOME/Library/Application Support/Steam/steamapps/common/Chill with You Lo-Fi Story}"
    ;;
  *)
    # Same gap as build.sh: deploying to a Windows install is not automated yet.
    if [ -z "${GAME_ROOT:-}" ]; then
      echo "set GAME_ROOT: automatic detection is only implemented for macOS" >&2
      exit 1
    fi
    ;;
esac

if [ ! -d "$GAME_ROOT" ]; then
  echo "game not found at: $GAME_ROOT" >&2
  echo "set GAME_ROOT to the folder containing the game" >&2
  exit 1
fi

if [ ! -d "$SOURCE" ]; then
  echo "no packs folder at: $SOURCE" >&2
  exit 1
fi

DEST="$GAME_ROOT/CWYLS Mods"
mkdir -p "$DEST"

echo "==> deploy packs -> $DEST"

count=0
for dir in "$SOURCE"/*/; do
  [ -d "$dir" ] || continue
  [ -f "$dir/manifest.json" ] || { echo "    skipped $(basename "$dir") (no manifest.json)"; continue; }

  id="$(basename "$dir")"
  rm -rf "$DEST/$id"
  mkdir -p "$DEST/$id"
  cp -R "$dir." "$DEST/$id/"
  echo "    $id ($(find "$DEST/$id" -type f | wc -l | tr -d ' ') file(s))"
  count=$((count + 1))
done

if [ "$count" -eq 0 ]; then
  echo "no pack folder in $SOURCE contains a manifest.json" >&2
  exit 1
fi

# BepInEx and the loader both read these; quarantine flags only cause trouble.
xattr -dr com.apple.quarantine "$DEST" 2>/dev/null || true

echo
echo "Deployed $count pack(s). Restart the game to pick them up."
