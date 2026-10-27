#!/bin/sh
exec pwsh -NoProfile -NonInteractive -File "$(dirname "$0")/cswap.ps1" "$@"
