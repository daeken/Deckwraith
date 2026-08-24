#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 3 ]]; then
  echo "Usage: stage-release-artifacts.sh <electron-output> <staging-output> <osx|linux|win>" >&2
  exit 2
fi

deckwraith_source="$1"
deckwraith_output="$2"
deckwraith_target="$3"

mkdir -p "$deckwraith_output"
find "$deckwraith_source" -maxdepth 1 -type f \( \
  -name '*.AppImage' -o \
  -name '*.dmg' -o \
  -name 'Deckwraith Setup *.exe' -o \
  -name '*.zip' -o \
  -name '*.tar.gz' -o \
  -name '*.blockmap' -o \
  -name 'latest*.yml' \
\) -exec cp -p '{}' "$deckwraith_output/" \;

case "$deckwraith_target" in
  osx)
    compgen -G "$deckwraith_output/*.dmg" >/dev/null
    compgen -G "$deckwraith_output/*-mac.zip" >/dev/null
    ;;
  linux)
    compgen -G "$deckwraith_output/*.AppImage" >/dev/null
    compgen -G "$deckwraith_output/*.tar.gz" >/dev/null
    ;;
  win)
    compgen -G "$deckwraith_output/Deckwraith Setup *.exe" >/dev/null
    compgen -G "$deckwraith_output/*-win.zip" >/dev/null
    ;;
  *)
    echo "Unsupported release target: $deckwraith_target" >&2
    exit 2
    ;;
esac

find "$deckwraith_output" -maxdepth 1 -type f -print | sort
