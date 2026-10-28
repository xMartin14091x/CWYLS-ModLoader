#!/usr/bin/env bash
# Build the CWY:LS Mod Loader release files into dist/:
#
#   CWYLS-ModLoader-<version>-mac.zip       install.command + stock BepInEx (macOS universal) + the loader
#   CWYLS-ModLoader-<version>-windows.zip   install.bat + stock BepInEx (win x64) + the loader
#   CWYLS-ModLoader-<version>-source.zip    every file git would publish (the .gitignore decides)
#   SHA256SUMS
#
#   tools/release/make-release.sh               build + test + seamcheck first (./build.sh --no-deploy)
#   tools/release/make-release.sh --skip-build  package what is already built
#
# No sample mods, no packs and no Thai content go into the player zips. BepInEx is shipped as the
# unmodified upstream zip, checked against BEPINEX_SHA256 below; nothing here touches the game.
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
REPO="$(cd "$HERE/../.." && pwd)"
DIST="$REPO/dist"
VENDOR="$HERE/vendor"

BEPINEX_VERSION="5.4.23.6"
BEPINEX_MAC="BepInEx_macos_universal_$BEPINEX_VERSION.zip"
BEPINEX_WIN="BepInEx_win_x64_$BEPINEX_VERSION.zip"
BEPINEX_MAC_SHA256="2e711e21f6570da964fce4654b1976500dbe043f61778bce0008500078564638"
BEPINEX_WIN_SHA256="6c57a714a892efb4e9e1fef63d608a3391f30ec984031c465404e4ee381a0755"

PLUGINS="CwyLs.ModLoader.Core CwyLs.ModLoader CwyLs.ModLoader.Bulbul CwyLs.ModLoader.Bulbul.Data CwyLs.ModLoader.Trace CwyLs.ModLoader.Smoke CwyLs.ModLoader.Api"

BUILD=1
while [ $# -gt 0 ]; do
  case "$1" in
    --skip-build) BUILD=0 ;;
    -h|--help) sed -n '2,14p' "$0"; exit 0 ;;
    *) echo "unknown option: $1" >&2; exit 2 ;;
  esac
  shift
done

prop() { sed -n "s:.*<$1>\(.*\)</$1>.*:\1:p" "$REPO/Directory.Build.props" | head -1; }
VERSION="$(prop CwyLsVersion)-$(prop CwyLsVersionSuffix)"
NAME="CWYLS-ModLoader-$VERSION"
echo "==> $NAME"

if [ "$BUILD" -eq 1 ]; then
  "$REPO/build.sh" --no-deploy
fi

# ---- stock BepInEx, verified
fetch() {
  local file="$1" sha="$2"
  mkdir -p "$VENDOR"
  if [ ! -f "$VENDOR/$file" ]; then
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
