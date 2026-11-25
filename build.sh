#!/usr/bin/env bash
# Build the CWY:LS plugin solution and deploy the plugin DLLs to the local BepInEx install.
#
#   ./build.sh                 build Release, run tests, deploy to the macOS game folder
#   ./build.sh --no-deploy     build + test only
#   ./build.sh --no-test       skip the unit tests
#   ./build.sh --deploy-packs  also deploy packs/ to <game root>/CWYLS Mods (./deploy-packs.sh)
#   ./build.sh -c Debug        pick a configuration
#
# This script never launches the game and writes nothing into the game except
# BepInEx/plugins/CwyLsModLoader (DLLs, Pack Maker's maker/) and, with --deploy-packs, CWYLS Mods (plus
# the one-time move of an install from before the rename: tools/mac/migrate-old-install.sh). Installing
# BepInEx itself is tools/mac/install-bepinex-mac.sh.
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd -P)"
export DOTNET_ROOT="${DOTNET_ROOT:-/opt/homebrew/opt/dotnet/libexec}"
export PATH="$DOTNET_ROOT:$PATH"

CONFIG="Release"
DEPLOY=1
DEPLOY_PACKS=0
RUN_TESTS=1

while [ $# -gt 0 ]; do
  case "$1" in
    --no-deploy) DEPLOY=0 ;;
    --no-test|--no-tests) RUN_TESTS=0 ;;
    --deploy-packs) DEPLOY_PACKS=1 ;;
    -c|--configuration) shift; CONFIG="${1:?missing configuration}" ;;
    -h|--help) sed -n '2,12p' "$0"; exit 0 ;;
    *) echo "unknown option: $1" >&2; exit 2 ;;
  esac
  shift
done

# --deploy-packs is part of deploying, so asking for both at once is a mistake worth naming
# rather than silently honouring one of them.
if [ "$DEPLOY" -eq 0 ] && [ "$DEPLOY_PACKS" -eq 1 ]; then
  echo "--no-deploy and --deploy-packs contradict each other; run ./deploy-packs.sh on its own" >&2
  exit 2
fi

# Plugin assemblies to ship. Everything else they reference (UnityEngine, Assembly-CSharp,
# BepInEx, 0Harmony, ...) is Private=false and must be resolved at runtime from the game.
PLUGINS="CwyLs.ModLoader.Core CwyLs.ModLoader CwyLs.ModLoader.Bulbul CwyLs.ModLoader.Bulbul.Data CwyLs.ModLoader.Trace CwyLs.ModLoader.Smoke CwyLs.ModLoader.Api"

echo "==> build ($CONFIG)"
dotnet build "$HERE/CwyLs.ModLoader.sln" -c "$CONFIG"

# Code mods in mods-src/ build like third-party mods: against the framework DLLs just built, each
# into its own packs/<id>/ folder (installed only by --deploy-packs).
for proj in "$HERE"/mods-src/*/*.csproj; do
  [ -f "$proj" ] || continue
  echo
  echo "==> build mod $(basename "$(dirname "$proj")") ($CONFIG)"
  dotnet build "$proj" -c "$CONFIG" -p:CwyConfiguration="$CONFIG"
done

if [ "$RUN_TESTS" -eq 1 ]; then
  echo
  echo "==> test"
  dotnet test "$HERE/src/CwyLs.ModLoader.Tests/CwyLs.ModLoader.Tests.csproj" -c "$CONFIG" --no-build

  # Preview clip ownership in the real MakerEditorPlay.cs, compiled against engine stubs (25 scenarios, including subtitle-only playback).
  # It stays out of the xUnit assembly (its fake UnityEngine types), so it runs here; a failure stops the build.
  echo
  echo "==> preview adapter harness"
  dotnet run --project "$HERE/src/CwyLs.ModLoader.Tests/PreviewAdapterHarness/AdapterHarness.csproj" -c "$CONFIG"

  # Parser parity: the unit tests regenerate the golden console corpus; the viewer must accept
  # every line of it. One fixture, two parsers, no drift.
  echo
  echo "==> console grammar parity (watch-log.py --strict)"
  python3 "$HERE/tools/mac/watch-log.py" --strict --no-follow \
    "$HERE/tools/mac/tests/console-golden.txt" > /dev/null
  echo "    tools/mac/tests/console-golden.txt: every line parses"

  # Offline seam check: every registered patch target must resolve in the installed game's
  # Managed/ DLLs (and in the Windows reference copy when present). A missing seam stops here.
  echo
  echo "==> seamcheck"
  SC_PLATFORM=mac; [ -d "$HERE/_ref/win-1.18.0/Managed" ] && SC_PLATFORM=all
  if [ -n "${SEAMCHECK_ARTIFACTS:-}" ]; then
    SC_ARTIFACTS="$SEAMCHECK_ARTIFACTS"
  else
    if command -v shasum >/dev/null 2>&1; then
      HERE_HASH="$(printf '%s' "$HERE" | shasum -a 256 | cut -c1-12)"
    else
      HERE_HASH="$(printf '%s' "$HERE" | cksum | cut -d' ' -f1)"
    fi
    SC_ARTIFACTS="${TMPDIR:-/tmp}/cwy-seamcheck-$HERE_HASH"
  fi
  if [ -n "${GameManagedPath:-}" ]; then
    # Built against a copy of another game build's Managed folder: check the seams against that copy.
    echo "    against GameManagedPath=$GameManagedPath (platform ${SEAMCHECK_PLATFORM:-win})"
    SEAMCHECK_ARTIFACTS="$SC_ARTIFACTS" "$HERE/tools/seamcheck/seamcheck.sh" --managed "$GameManagedPath" --platform "${SEAMCHECK_PLATFORM:-win}"
  else
    SEAMCHECK_ARTIFACTS="$SC_ARTIFACTS" "$HERE/tools/seamcheck/seamcheck.sh" --platform "$SC_PLATFORM"
  fi
  dotnet test "$HERE/tools/seamcheck/Seamcheck.Tests/Seamcheck.Tests.csproj" -c "$CONFIG" --artifacts-path "$SC_ARTIFACTS"
fi

if [ "$DEPLOY" -eq 0 ]; then
  echo
  echo "==> deploy skipped (--no-deploy)"
  exit 0
fi

case "$(uname -s)" in
  Darwin)
    GAME_ROOT="${GAME_ROOT:-$HOME/Library/Application Support/Steam/steamapps/common/Chill with You Lo-Fi Story}"
    ;;
  *)
    # TODO: Windows deploy. A Windows install runs stock BepInEx 5.4.23.5 and takes the same DLLs
    # under <game root>\BepInEx\plugins\CwyLsModLoader. The DLLs themselves are already
    # platform-neutral, so this is a copy target, not a build change.
    echo "deploy is only implemented for macOS on this branch (see TODO in build.sh)" >&2
    exit 1
    ;;
esac

if [ ! -d "$GAME_ROOT" ]; then
  echo "game not found at: $GAME_ROOT" >&2
  echo "set GAME_ROOT, or run with --no-deploy" >&2
  exit 1
fi

# An install from before the rename (plugins/CWY, CWYPacks, old .cfg names) is moved over first;
# nothing is deleted.
"$HERE/tools/mac/migrate-old-install.sh" "$GAME_ROOT"

DEST="$GAME_ROOT/BepInEx/plugins/CwyLsModLoader"
mkdir -p "$DEST"

echo
echo "==> deploy -> $DEST"
for name in $PLUGINS; do
  src="$HERE/src/$name/bin/$CONFIG/netstandard2.1/$name.dll"
  [ -f "$src" ] || { echo "missing build output: $src" >&2; exit 1; }
  cp "$src" "$DEST/$name.dll"
  echo "    $name.dll"
done

# Pack Maker's web app: the API plugin serves it at /maker/ from plugins/CwyLsModLoader/maker/. Only the app
# ships: the excludes are the same as tools/release/make-release.sh (keep the two lists in sync), so a dev deploy
# serves exactly what a release does. --delete (and --delete-excluded, for files an older deploy copied before they
# were excluded) clears that one folder, which holds nothing but this copy.
WEB_SRC="$HERE/src/CwyLs.ModLoader.Maker.Web"
if [ -d "$WEB_SRC" ]; then
  mkdir -p "$DEST/maker"
  rsync -a --delete --delete-excluded --exclude 'tools/' --exclude 'tests/' --exclude '_*' --exclude '.*' \
    --exclude 'package.json' --exclude 'generate_api.mjs' --exclude '*.md' "$WEB_SRC/" "$DEST/maker/"
  echo "    maker/ ($(find "$DEST/maker" -type f | wc -l | tr -d ' ') files)"
else
  echo "    maker/ skipped: $WEB_SRC does not exist yet"
fi

# BepInEx will not load a quarantined assembly on macOS.
xattr -dr com.apple.quarantine "$DEST" 2>/dev/null || true

echo
echo "Deployed $(echo $PLUGINS | wc -w | tr -d ' ') assemblies."

if [ "$DEPLOY_PACKS" -eq 1 ]; then
  # Same GAME_ROOT the plugins went to, passed on explicitly so the two halves of one deploy can
  # never disagree about which install they are writing to.
  echo
  GAME_ROOT="$GAME_ROOT" "$HERE/deploy-packs.sh"
fi

echo
echo "Verify in game with tools/mac/launch-mac.sh --log"
