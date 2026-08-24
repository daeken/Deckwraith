#!/usr/bin/env bash
set -uo pipefail

if [[ $# -ne 1 ]]; then
  echo "Usage: run-ci-test.sh <test-project>" >&2
  exit 2
fi

deckwraith_repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
deckwraith_project="$1"
deckwraith_output="$(mktemp "${TMPDIR:-/tmp}/deckwraith-ci-test.XXXXXX")"
trap 'rm -f "$deckwraith_output"' EXIT

set +e
dotnet test "$deckwraith_repo/$deckwraith_project" -c Release --nologo 2>&1 |
  tee "$deckwraith_output"
deckwraith_status="${PIPESTATUS[0]}"
set -e

if [[ "$deckwraith_status" -ne 0 ]]; then
  # Public repositories still require authentication to read Actions logs.
  # Preserve a bounded diagnostic in the public job annotation instead.
  deckwraith_diagnostic="$(tail -c 20000 "$deckwraith_output")"
  deckwraith_diagnostic="${deckwraith_diagnostic//'%'/%25}"
  deckwraith_diagnostic="${deckwraith_diagnostic//$'\r'/%0D}"
  deckwraith_diagnostic="${deckwraith_diagnostic//$'\n'/%0A}"
  deckwraith_name="$(basename "$deckwraith_project" .csproj)"
  printf '::error title=%s failed::%s\n' "$deckwraith_name" "$deckwraith_diagnostic"
fi

exit "$deckwraith_status"
