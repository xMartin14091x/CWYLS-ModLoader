#!/usr/bin/env bash
# One-time move of an install made before the rename to CWY:LS Mod Loader (2026-10-27).
#
#   tools/mac/migrate-old-install.sh "<game folder>"
#
# Nothing is deleted:
#   BepInEx/plugins/CWY                       -> BepInEx/_old/<stamp>/plugins-CWY   (else the loader loads twice)
#   BepInEx/config/com.*.cwy.patcher*.cfg and com.*.cwyls.modloader*.cfg (the two earlier plugin ids)
#                                             -> copied to cwyls.modloader*.cfg when that
#                                                does not exist yet; the original goes to BepInEx/_old/<stamp>/
#   CWYPacks                                  -> "CWYLS Mods" (renamed when "CWYLS Mods" does not exist yet)
# Safe to run again: with nothing old left it prints nothing and changes nothing.
set -euo pipefail

GAME_ROOT="${1:?usage: migrate-old-install.sh <game folder>}"
STAMP="$(date +%Y%m%d-%H%M%S)"
OLD="$GAME_ROOT/BepInEx/_old/$STAMP"

if [ -d "$GAME_ROOT/BepInEx/plugins/CWY" ]; then
  mkdir -p "$OLD"
  mv "$GAME_ROOT/BepInEx/plugins/CWY" "$OLD/plugins-CWY"
  echo "    moved old plugins/CWY -> BepInEx/_old/$STAMP/plugins-CWY"
fi

CONFIG="$GAME_ROOT/BepInEx/config"
if [ -d "$CONFIG" ]; then
  for old_cfg in "$CONFIG"/com.*.cwy.patcher*.cfg "$CONFIG"/com.*.cwyls.modloader*.cfg; do
    [ -f "$old_cfg" ] || continue
    name="$(basename "$old_cfg")"
    case "$name" in
      *cwy.patcher*) new_cfg="$CONFIG/cwyls.modloader${name#*cwy.patcher}" ;;
      *) new_cfg="$CONFIG/cwyls.modloader${name#*cwyls.modloader}" ;;
    esac
    if [ ! -f "$new_cfg" ]; then
      cp "$old_cfg" "$new_cfg"
      echo "    settings kept: $name -> $(basename "$new_cfg")"
    fi
    mkdir -p "$OLD/config"
    mv "$old_cfg" "$OLD/config/$name"
  done
fi

if [ -d "$GAME_ROOT/CWYPacks" ]; then
  if [ -e "$GAME_ROOT/CWYLS Mods" ]; then
    echo "    note: both CWYPacks and \"CWYLS Mods\" exist; only \"CWYLS Mods\" is read. Move your mods over by hand." >&2
  else
    mv "$GAME_ROOT/CWYPacks" "$GAME_ROOT/CWYLS Mods"
    echo "    renamed CWYPacks -> \"CWYLS Mods\""
  fi
fi
