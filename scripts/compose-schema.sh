#!/usr/bin/env bash
# usage: scripts/compose-schema.sh [--from-contracts] [--no-export]
#   (none)            re-export the five subgraph schemas into schemas/, then compose them
#   --no-export       compose the committed schemas/ as they are
#   --from-contracts  compose contracts/*.graphqls instead (Phase 4 part 1; no subgraph code involved)
# Writes gateway/gateway.far, offline (no Nitro login, no network). After any subgraph schema change, run this
# and commit schemas/ and gateway/gateway.far; scripts/check-schema-drift.sh (CI) fails until you do.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

SRC="schemas"; EXPORT=1
for a in "$@"; do
  case "$a" in
    --from-contracts) SRC="contracts"; EXPORT=0 ;;
    --no-export) EXPORT=0 ;;
    *) echo "unknown option: $a" >&2; echo "usage: $0 [--from-contracts] [--no-export]" >&2; exit 2 ;;
  esac
done

if [ "$EXPORT" = 1 ]; then
  scripts/export-schemas.sh
fi

dotnet tool restore >/dev/null   # pinned Nitro CLI (.config/dotnet-tools.json)
mkdir -p gateway

# Composing into an existing archive merges into it (a removed subgraph would linger), so start from nothing.
# Each <name>.graphqls is paired with <name>-settings.json in the same folder. Sorted settings paths give a stable
# order while allowing a compatible source to be added without editing this script.
rm -f gateway/gateway.far
inputs=()
for settings in "$SRC"/*-settings.json; do
  schema="${settings%-settings.json}.graphqls"
  [ -f "$schema" ] || { echo "missing schema paired with $settings: $schema" >&2; exit 1; }
  inputs+=( -f "$schema" )
done
[ "${#inputs[@]}" -gt 0 ] || { echo "no schema settings found in $SRC" >&2; exit 1; }
dotnet nitro fusion compose "${inputs[@]}" -a gateway/gateway.far

dotnet run --project src/Gateway/Gateway.csproj -c Release --no-launch-profile -- \
  timeline-catalog generate \
  --archive "$ROOT/gateway/gateway.far" \
  --output "$ROOT/gateway/timeline-sources.json" \
  --source-root "$ROOT/src" \
  --schemas "$ROOT/$SRC"

echo "composed gateway/gateway.far and gateway/timeline-sources.json from $SRC/"
