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
# Each <name>.graphqls is paired with <name>-settings.json in the same folder. The archive lists source schemas
# in argument order, so the order is fixed here to keep the bytes stable (docs/version-facts.md §3).
rm -f gateway/gateway.far
dotnet nitro fusion compose \
  -f "$SRC/device-directory.graphqls" \
  -f "$SRC/patch.graphqls" \
  -f "$SRC/vulnerability.graphqls" \
  -f "$SRC/software-install.graphqls" \
  -f "$SRC/device-search.graphqls" \
  -a gateway/gateway.far

echo "composed gateway/gateway.far from $SRC/"
