#!/bin/bash
# Colorized live view of the CWY console log. Options pass through: --quiet, --last N, --no-follow
exec python3 "$(cd "$(dirname "$0")" && pwd)/watch-log.py" "$@"
