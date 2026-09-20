#!/usr/bin/env bash
# usage: scripts/export-schemas.sh
# Exports each subgraph's SDL to schemas/<name>.graphqls (docs/version-facts.md §3). Needs no database and no
# signing key. The paired schemas/<name>-settings.json is kept as committed: export rewrites only its "name".
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

# Every schema settings file names its source project. The gateway's .NET helper parses JSON, including compact
# settings files, so adding a source does not require another hard-coded list here.
projects="$(dotnet run --project src/Gateway/Gateway.csproj -c Release --no-launch-profile -- \
  timeline-catalog source-projects --settings "$ROOT/schemas")"
while IFS=$'\t' read -r name proj; do
  [ -n "$proj" ] && [ -f "src/$proj/$proj.csproj" ] || {
    echo "invalid source project for schemas/$name-settings.json: '$proj'" >&2; exit 1;
  }
  echo "==> exporting $name (src/$proj)"
  # --no-launch-profile: Production environment, as in the containers and the subgraphs' export tests.
  ( cd "src/$proj" && dotnet run -c Release --no-launch-profile -- schema export --output "$ROOT/schemas/$name.graphqls" )
done <<< "$projects"
