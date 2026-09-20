#!/usr/bin/env bash
# usage: scripts/export-schemas.sh
# Exports each subgraph's SDL to schemas/<name>.graphqls (docs/version-facts.md §3). Needs no database and no
# signing key. The paired schemas/<name>-settings.json is kept as committed: export rewrites only its "name".
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

# <schema file base name>:<project folder>, in the fixed composition order.
PAIRS="device-directory:DeviceDirectory patch:Patch vulnerability:Vulnerability software-install:SoftwareInstall device-search:DeviceSearch"
for pair in $PAIRS; do
  name="${pair%%:*}"; proj="${pair##*:}"
  echo "==> exporting $name (src/$proj)"
  # --no-launch-profile: Production environment, as in the containers and the subgraphs' export tests.
  ( cd "src/$proj" && dotnet run -c Release --no-launch-profile -- schema export --output "$ROOT/schemas/$name.graphqls" )
done
