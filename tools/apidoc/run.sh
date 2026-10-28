#!/usr/bin/env bash
# Regenerate the API reference in docs/api/ from the framework's XML documentation.
#
#   tools/apidoc/run.sh                 build CwyLs.ModLoader (+ Core) with XML docs, then write docs/api/
#   tools/apidoc/run.sh --check         the same, but only report whether docs/api/ is stale (exit 1)
#   tools/apidoc/run.sh --from DIR      skip the build: DIR holds CwyLs.ModLoader.dll/.xml and
#                                       CwyLs.ModLoader.Core.dll/.xml from an earlier documented build
#
# The build goes to a scratch artifacts folder (CWY_APIDOC_ARTIFACTS, default $TMPDIR/cwy-apidoc),
# never into src/*/bin or obj, and no project file is changed: the XML docs are switched on from
# the command line. Nothing here touches the game.
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
REPO="$(cd "$HERE/../.." && pwd)"
export DOTNET_ROOT="${DOTNET_ROOT:-/opt/homebrew/opt/dotnet/libexec}"
export PATH="$DOTNET_ROOT:$PATH"

CHECK=()
FROM=""
while [ $# -gt 0 ]; do
  case "$1" in
    --check) CHECK=(--check) ;;
    --from) shift; FROM="${1:?--from needs a folder}" ;;
    -h|--help) sed -n '2,11p' "$0"; exit 0 ;;
    *) echo "unknown option: $1" >&2; exit 2 ;;
  esac
  shift
done

if [ -z "$FROM" ]; then
  ART="${CWY_APIDOC_ARTIFACTS:-${TMPDIR:-/tmp}/cwy-apidoc}"
  echo "==> build CwyLs.ModLoader + CwyLs.ModLoader.Core with XML docs -> $ART"
  dotnet build "$REPO/src/CwyLs.ModLoader/CwyLs.ModLoader.csproj" -c Release \
    --artifacts-path "$ART" -p:GenerateDocumentationFile=true -p:NoWarn=1591 -nologo -v:q
  PATCHER="$ART/bin/CwyLs.ModLoader/release/CwyLs.ModLoader.dll"
  CORE="$ART/bin/CwyLs.ModLoader.Core/release/CwyLs.ModLoader.Core.dll"
else
  PATCHER="$FROM/CwyLs.ModLoader.dll"
  CORE="$FROM/CwyLs.ModLoader.Core.dll"
fi

echo "==> apidoc -> $REPO/docs/api"
python3 "$HERE/apidoc.py" ${CHECK[@]+"${CHECK[@]}"} --out "$REPO/docs/api" "$PATCHER" "$CORE"
