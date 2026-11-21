#!/usr/bin/env bash
# Build the CWY:LS Mod Loader release files into dist/:
#
#   CWYLS-ModLoader-<version>-mac.zip       install.command + stock BepInEx (macOS universal) + the loader
#   CWYLS-ModLoader-<version>-windows.zip   install.bat + stock BepInEx (win x64) + the loader
#   CWYLS-ModLoader-<version>-source.zip    every file git would publish (the .gitignore decides)
#   SHA256SUMS
#
#   tools/release/make-release.sh               build + test + seamcheck first (./build.sh --no-deploy), page tests,
#                                               smokes and generated-docs checks; refuses a tree that is not clean
#   tools/release/make-release.sh --dry-run --output-dir /tmp/cwyls-dry --skip-build --skip-page-tests
#     local dry run only: package existing build outputs without the build or page-test gates
#
# No sample mods, no packs and no Thai content go into the player zips. BepInEx is shipped as the
# unmodified upstream zip, checked against BEPINEX_SHA256 below; nothing here touches the game.
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd -P)"
REPO="$(cd "$HERE/../.." && pwd -P)"
DIST="$REPO/dist"
VENDOR="$HERE/vendor"

BEPINEX_VERSION="5.4.23.6"
BEPINEX_MAC="BepInEx_macos_universal_$BEPINEX_VERSION.zip"
BEPINEX_WIN="BepInEx_win_x64_$BEPINEX_VERSION.zip"
BEPINEX_MAC_SHA256="2e711e21f6570da964fce4654b1976500dbe043f61778bce0008500078564638"
BEPINEX_WIN_SHA256="6c57a714a892efb4e9e1fef63d608a3391f30ec984031c465404e4ee381a0755"

PLUGINS="CwyLs.ModLoader.Core CwyLs.ModLoader CwyLs.ModLoader.Bulbul CwyLs.ModLoader.Bulbul.Data CwyLs.ModLoader.Trace CwyLs.ModLoader.Smoke CwyLs.ModLoader.Api"

BUILD=1
PAGE_TESTS=1
DRY_RUN=0
OUTPUT_DIR=""
while [ $# -gt 0 ]; do
  case "$1" in
    --skip-build) BUILD=0 ;;
    --skip-page-tests) PAGE_TESTS=0 ;;
    --dry-run) DRY_RUN=1 ;;
    --output-dir) shift; OUTPUT_DIR="${1:?missing output directory}" ;;
    -h|--help) sed -n '2,16p' "$0"; exit 0 ;;
    *) echo "unknown option: $1" >&2; exit 2 ;;
  esac
  shift
done
if [ "$DRY_RUN" -eq 0 ] && { [ "$BUILD" -eq 0 ] || [ "$PAGE_TESTS" -eq 0 ] || [ -n "$OUTPUT_DIR" ]; }; then
  echo "--skip-build, --skip-page-tests and --output-dir are local dry-run options only" >&2; exit 2
fi
if [ "$DRY_RUN" -eq 1 ]; then
  [ -n "$OUTPUT_DIR" ] || { echo "--dry-run requires --output-dir" >&2; exit 2; }
  DIST="$OUTPUT_DIR"
  echo "==> LOCAL DRY RUN: not a qualified release"
fi

# A release is built from one commit: no modified tracked files and no untracked files git would publish
# (the source zip takes every file git would publish, so a stray file would ship). A dry run only warns.
dirty="$(cd "$REPO" && git status --porcelain --untracked-files=normal 2>/dev/null)" || dirty="not a git checkout"
if [ -n "$dirty" ]; then
  if [ "$DRY_RUN" -eq 1 ]; then
    echo "==> warning: the working tree is not clean; a release would refuse:" >&2
    printf '%s\n' "$dirty" | head -10 >&2
  else
    echo "the working tree is not clean; commit or remove these first:" >&2
    printf '%s\n' "$dirty" | head -20 >&2
    exit 3
  fi
fi

# The source zip is public: no file git would publish may carry this machine's home folder or user name
# (a test once defaulted its screenshot folder to the maintainer's home). Generic examples such as /Users/someone pass.
# The git author's real name (and its no-space form, as export file names use) is private too (audit F2, 2026-11-17).
leak_patterns=(-e "$HOME/" -e "$(id -un)")
git_name="$(git -C "$REPO" config user.name 2>/dev/null || true)"
if [ -n "$git_name" ]; then
  leak_patterns+=(-e "$git_name" -e "$(printf '%s' "$git_name" | tr -d ' ' | tr '[:upper:]' '[:lower:]')")
fi
leaks="$(cd "$REPO" && git ls-files -co --exclude-standard -z | xargs -0 grep -lsiF "${leak_patterns[@]}" 2>/dev/null || true)"
if [ -n "$leaks" ]; then
  echo "these files name this machine's home folder or user; replace the path before releasing:" >&2
  printf '%s\n' "$leaks" | head -20 >&2
  exit 4
fi

prop() { sed -n "s:.*<$1>\(.*\)</$1>.*:\1:p" "$REPO/Directory.Build.props" | head -1; }
VERSION="$(prop CwyLsVersion)-$(prop CwyLsVersionSuffix)"
NAME="CWYLS-ModLoader-$VERSION"
echo "==> $NAME"

if [ "$BUILD" -eq 1 ]; then
  "$REPO/build.sh" --no-deploy
fi
if [ "$PAGE_TESTS" -eq 1 ]; then
  echo "==> Pack Maker page tests and smoke tests"
  (
    cd "$REPO/src/CwyLs.ModLoader.Maker.Web"
    page_tests=()
    while IFS= read -r f; do page_tests+=("$f"); done < <(find tests -type f -name '*.test.mjs' | LC_ALL=C sort)
    [ "${#page_tests[@]}" -gt 0 ] || { echo "no Pack Maker page tests found" >&2; exit 1; }
    node --test "${page_tests[@]}"
    node tests/e2e/smoke.mjs
    node tests/e2e/smoke-fake.mjs
    node generate_api.mjs --check
  )
fi
if [ "$BUILD" -eq 1 ]; then
  # The API reference in docs/api/ is generated from the XML docs: a stale page means the docs ship wrong.
  echo "==> API reference is current"
  "$REPO/tools/apidoc/run.sh" --check
fi

# ---- stock BepInEx, verified
fetch() {
  local file="$1" sha="$2"
  mkdir -p "$VENDOR"
  if [ ! -f "$VENDOR/$file" ]; then
    [ "$DRY_RUN" -eq 0 ] || { echo "missing local vendor archive for dry run: $VENDOR/$file" >&2; exit 1; }
    echo "==> download $file"
    curl -fsSL -o "$VENDOR/$file" "https://github.com/BepInEx/BepInEx/releases/download/v$BEPINEX_VERSION/$file"
  fi
  local got
  got="$(shasum -a 256 "$VENDOR/$file" | cut -d' ' -f1)"
  if [ "$got" != "$sha" ]; then
    echo "SHA-256 mismatch for $file: $got (expected $sha)" >&2
    exit 1
  fi
}
fetch "$BEPINEX_MAC" "$BEPINEX_MAC_SHA256"
fetch "$BEPINEX_WIN" "$BEPINEX_WIN_SHA256"

# ---- staging
STAGE="$(mktemp -d "${TMPDIR:-/tmp}/cwyls-release.XXXXXX")"
mkdir -p "$DIST"

common_docs() {
  local dest="$1"
  cp "$REPO/LICENSE.md" "$REPO/NOTICE.md" "$REPO/THIRD-PARTY-NOTICES.md" "$dest/"
  sed "s/__VERSION__/$VERSION/g" "$HERE/README-player.txt" > "$dest/README.txt"
}

loader_dlls() {
  local dest="$1/CwyLsModLoader"
  mkdir -p "$dest"
  local name src
  for name in $PLUGINS; do
    src="$REPO/src/$name/bin/Release/netstandard2.1/$name.dll"
    [ -f "$src" ] || { echo "missing build output: $src (run without --skip-build)" >&2; exit 1; }
    if strings -a "$src" | grep -q "/Users/\|C:\\\\Users\\\\"; then
      echo "a local path is embedded in $name.dll; refusing to package it" >&2
      exit 1
    fi
    cp "$src" "$dest/"
  done
  local web="$REPO/src/CwyLs.ModLoader.Maker.Web"
  [ -d "$web" ] || { echo "missing Pack Maker web app: $web" >&2; exit 1; }
  mkdir -p "$dest/maker"
  rsync -a --delete \
    --exclude 'tools/' \
    --exclude 'tests/' \
    --exclude '_*' \
    --exclude '.*' \
    --exclude 'package.json' \
    --exclude 'generate_api.mjs' \
    --exclude '*.md' \
    "$web/" "$dest/maker/"
}

# macOS
MAC="$STAGE/$NAME-mac"
mkdir -p "$MAC/files"
sed "s/__VERSION__/$VERSION/g" "$HERE/mac/install.command" > "$MAC/install.command"
chmod +x "$MAC/install.command"
cp "$VENDOR/$BEPINEX_MAC" "$MAC/files/"
printf '%s  %s\n' "$BEPINEX_MAC_SHA256" "$BEPINEX_MAC" > "$MAC/files/BepInEx-SHA256.txt"
loader_dlls "$MAC/files"
common_docs "$MAC"

# Windows (CRLF for the .bat; the .ps1 is UTF-8 with a BOM, which sed keeps)
WIN="$STAGE/$NAME-windows"
mkdir -p "$WIN/files"
cp "$HERE/win/install.bat" "$WIN/install.bat"
sed "s/__VERSION__/$VERSION/g" "$HERE/win/install.ps1" > "$WIN/files/install.ps1"
cp "$VENDOR/$BEPINEX_WIN" "$WIN/files/"
printf '%s  %s\n' "$BEPINEX_WIN_SHA256" "$BEPINEX_WIN" > "$WIN/files/BepInEx-SHA256.txt"
loader_dlls "$WIN/files"
common_docs "$WIN"
# Windows Notepad copes with LF today, but CRLF is what a Windows user expects in a .txt.
for f in "$WIN/README.txt"; do perl -pi -e 's/\r?\n/\r\n/' "$f"; done

# Source: exactly what git would publish.
SRC="$STAGE/$NAME-source"
mkdir -p "$SRC"
( cd "$REPO" && git ls-files -co --exclude-standard -z ) | rsync -a --from0 --files-from=- "$REPO/" "$SRC/"

# ---- zips (fixed timestamps so a rebuild of the same tree gives the same bytes)
( cd "$STAGE" && find . -exec touch -h -t 202610270000 {} + )
for dir in "$NAME-mac" "$NAME-windows" "$NAME-source"; do
  rm -f "$DIST/$dir.zip"
  ( cd "$STAGE" && find "$dir" -print | LC_ALL=C sort | zip -q -X -@ "$DIST/$dir.zip" )
  echo "    dist/$dir.zip ($(du -h "$DIST/$dir.zip" | cut -f1))"
done

( cd "$DIST" && shasum -a 256 "$NAME-mac.zip" "$NAME-windows.zip" "$NAME-source.zip" > SHA256SUMS )
echo "    dist/SHA256SUMS"
echo "    staged in $STAGE (temporary)"
