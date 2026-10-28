#!/bin/sh
exec pwsh -NoProfile -NonInteractive -File "$(dirname "$0")/gh.ps1" "$@"
