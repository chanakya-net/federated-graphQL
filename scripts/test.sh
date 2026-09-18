#!/usr/bin/env bash
# usage: scripts/test.sh [unit]
#   unit  -> skip tests tagged [Trait("Category","Integration")] (no Docker needed)
#   (none) -> run everything; integration tests use Testcontainers and need Docker
# Run scripts/build.sh first: tests run with --no-build against the Release output.
set -euo pipefail
cd "$(dirname "$0")/.."
FILTER="${1:-}"
if [ "$FILTER" = "unit" ]; then
  dotnet test SoR.sln -c Release --no-build --filter "Category!=Integration"
else
  dotnet test SoR.sln -c Release --no-build
fi
