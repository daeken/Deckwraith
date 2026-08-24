#!/usr/bin/env bash
set -euo pipefail

deckwraith_repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
deckwraith_version="${1:-1.0.0}"
deckwraith_target="${DECKWRAITH_ELECTRON_TARGET:-}"

if [[ -z "$deckwraith_target" ]]; then
  case "$(uname -s)" in
    Darwin) deckwraith_target="osx" ;;
    Linux) deckwraith_target="linux" ;;
    MINGW*|MSYS*|CYGWIN*) deckwraith_target="win" ;;
    *) echo "Unsupported desktop packaging host: $(uname -s)" >&2; exit 2 ;;
  esac
fi

dotnet tool restore --tool-manifest "$deckwraith_repo/.config/dotnet-tools.json"
(
  cd "$deckwraith_repo/src/Deckwraith.Desktop"
  deckwraith_electron_args=(--allow-roll-forward -- build \
    /target "$deckwraith_target" \
    /Version "$deckwraith_version" \
    /package-json electron.package.json)
  if [[ "$deckwraith_target" == "win" && "$(uname -s)" =~ ^(MINGW|MSYS|CYGWIN) ]]; then
    MSYS2_ARG_CONV_EXCL='*' dotnet tool run electronize "${deckwraith_electron_args[@]}"
  else
    dotnet tool run electronize "${deckwraith_electron_args[@]}"
  fi
)
