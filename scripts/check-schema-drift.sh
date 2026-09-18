#!/usr/bin/env bash
# usage: scripts/check-schema-drift.sh
# Re-exports and re-composes (scripts/compose-schema.sh), then fails if schemas/ or gateway/gateway.far changed,
# i.e. if what is in the working tree (in CI: what is committed) is stale. On drift the fresh files are left in
# place: review and commit them.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT

[ -f gateway/gateway.far ] || { echo "DRIFT: gateway/gateway.far is missing; run scripts/compose-schema.sh"; exit 1; }
cp -R schemas "$tmp/schemas.before"
cp gateway/gateway.far "$tmp/before.far"

scripts/compose-schema.sh

drift=0
if ! diff -r "$tmp/schemas.before" schemas >/dev/null; then
  echo "DRIFT: exported schemas differ from the committed schemas/:"
  diff -r "$tmp/schemas.before" schemas || true
  drift=1
fi

# Nitro writes byte-identical archives for identical inputs in identical order (docs/version-facts.md §3), so a
# byte comparison is exact. On a difference, show which archive entries changed (the .far is a zip).
if ! cmp -s "$tmp/before.far" gateway/gateway.far; then
  echo "DRIFT: gateway/gateway.far differs from the composed archive"
  mkdir "$tmp/a" "$tmp/b"
  unzip -q "$tmp/before.far" -d "$tmp/a"
  unzip -q gateway/gateway.far -d "$tmp/b"
  diff -r "$tmp/a" "$tmp/b" || true
  drift=1
fi

if [ "$drift" = 1 ]; then
  echo "Fix: commit the regenerated schemas/ and gateway/gateway.far (they are now up to date in the working tree)."
  exit 1
fi
echo "no drift"
