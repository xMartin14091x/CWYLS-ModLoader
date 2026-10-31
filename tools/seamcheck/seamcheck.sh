#!/usr/bin/env bash
# seamcheck - resolve every patch target the CWY plugins register against a game's Managed/
# folder, without running the game. Details: tools/seamcheck/README.md.
#
#   tools/seamcheck/seamcheck.sh                        the local macOS install (default)
#   tools/seamcheck/seamcheck.sh --platform win         a copy of a Windows install's Managed/ in _ref/win-1.18.0/Managed
#   tools/seamcheck/seamcheck.sh --platform all         both, plus the per-platform differences
#   tools/seamcheck/seamcheck.sh --managed <dir> [--platform mac|win]
#   tools/seamcheck/seamcheck.sh --json out.json        also write the JSON report
#   tools/seamcheck/seamcheck.sh --verbose              debug diagnostics on stderr
#
# Exit code: 0 every target resolved, 1 a registered target is missing or a trace static-constructor
# hazard has no exclusion (TRACE HAZARD line), 2 tool error (build failure, failed control,
# unreadable table).
#
# Builds the tool - and with it every plugin project under src/ - into $SEAMCHECK_ARTIFACTS
# (default: $TMPDIR/cwy-seamcheck), never into src/*/bin or obj. Game assemblies are only read
# through a metadata-only load; nothing is written to or launched from the game folder.
set -uo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
REPO="$(cd "$HERE/../.." && pwd)"
export DOTNET_ROOT="${DOTNET_ROOT:-/opt/homebrew/opt/dotnet/libexec}"
DOTNET="dotnet"
if [ -x "$DOTNET_ROOT/dotnet" ]; then
  DOTNET="$DOTNET_ROOT/dotnet"
fi

ARTIFACTS="${SEAMCHECK_ARTIFACTS:-${TMPDIR:-/tmp}/cwy-seamcheck}"
mkdir -p "$ARTIFACTS" || { echo "seamcheck: cannot create $ARTIFACTS" >&2; exit 2; }
BUILD_LOG="$ARTIFACTS/build.log"

if ! "$DOTNET" build "$HERE/Seamcheck/Seamcheck.csproj" -c Release --artifacts-path "$ARTIFACTS" -nologo \
    > "$BUILD_LOG" 2>&1; then
  echo "seamcheck: build failed (full log: $BUILD_LOG)" >&2
  grep -E "error|Error" "$BUILD_LOG" | sort -u | tail -20 >&2
  exit 2
fi

TOOL="$ARTIFACTS/bin/Seamcheck/release/seamcheck.dll"
if [ ! -f "$TOOL" ]; then
  echo "seamcheck: build reported success but $TOOL is missing" >&2
  exit 2
fi

exec "$DOTNET" "$TOOL" --repo "$REPO" "$@"
