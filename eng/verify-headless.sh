#!/usr/bin/env bash
set -euo pipefail

deckwraith_repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
deckwraith_rid="${1:-linux-x64}"
deckwraith_mode="${2:-}"
deckwraith_output="$(mktemp -d "${TMPDIR:-/tmp}/deckwraith-headless.XXXXXX")"
trap 'rm -rf "$deckwraith_output"' EXIT

if [[ -n "$deckwraith_mode" && "$deckwraith_mode" != "--publish-only" ]]; then
  echo "Usage: verify-headless.sh [runtime-id] [--publish-only]" >&2
  exit 2
fi

deckwraith_tests=(
  "$deckwraith_repo/tests/Deckwraith.Core.Tests/Deckwraith.Core.Tests.csproj"
  "$deckwraith_repo/tests/Deckwraith.Persistence.Tests/Deckwraith.Persistence.Tests.csproj"
  "$deckwraith_repo/tests/Deckwraith.IntegrationTests/Deckwraith.IntegrationTests.csproj"
  "$deckwraith_repo/tests/Deckwraith.Notebooks.Tests/Deckwraith.Notebooks.Tests.csproj"
  "$deckwraith_repo/tests/Deckwraith.Kernels.ContractTests/Deckwraith.Kernels.ContractTests.csproj"
  "$deckwraith_repo/tests/Deckwraith.PowerShell.Tests/Deckwraith.PowerShell.Tests.csproj"
  "$deckwraith_repo/tests/Deckwraith.Mcp.Tests/Deckwraith.Mcp.Tests.csproj"
  "$deckwraith_repo/tests/Deckwraith.Continuity.Tests/Deckwraith.Continuity.Tests.csproj"
  "$deckwraith_repo/tests/Deckwraith.Providers.ContractTests/Deckwraith.Providers.ContractTests.csproj"
  "$deckwraith_repo/tests/Deckwraith.Hosting.Tests/Deckwraith.Hosting.Tests.csproj"
)

if [[ "$deckwraith_mode" != "--publish-only" ]]; then
  for deckwraith_test in "${deckwraith_tests[@]}"; do
    dotnet test "$deckwraith_test" -c Release --nologo
  done
fi

dotnet publish \
  "$deckwraith_repo/src/Deckwraith.Headless/Deckwraith.Headless.csproj" \
  -c Release \
  -r "$deckwraith_rid" \
  --self-contained false \
  -o "$deckwraith_output"

if rg -i 'ElectronNET|Chromium|CefSharp' "$deckwraith_output/Deckwraith.Headless.deps.json"; then
  echo "Desktop-only dependencies entered the headless publish graph." >&2
  exit 1
fi

if find "$deckwraith_output" -type f -print | rg -i '/(ElectronNET|Chromium|CefSharp)[^/]*$'; then
  echo "Desktop-only assemblies entered the headless publish output." >&2
  exit 1
fi

test -x "$deckwraith_output/Deckwraith.Headless" || test -f "$deckwraith_output/Deckwraith.Headless.exe"
