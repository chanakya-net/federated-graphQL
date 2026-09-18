#!/usr/bin/env bash
# usage: scripts/reset.sh [-y|--yes]
# Stops the stack and DELETES its volumes (Postgres/Mongo/Azurite data, tokens.json). The next
# `scripts/up.sh` re-runs the Postgres init script and reseeds every subgraph from scratch.
set -euo pipefail
cd "$(dirname "$0")/.."

case "${1:-}" in
  -y|--yes) ;;
  "")
    if [ ! -t 0 ]; then
      echo "stdin is not a terminal; re-run with --yes to confirm" >&2
      exit 1
    fi
    printf 'This runs "docker compose down -v" and destroys all seeded data. Continue? [y/N] '
    read -r answer
    case "$answer" in
      y|Y|yes|YES) ;;
      *) echo "aborted"; exit 1 ;;
    esac
    ;;
  *) echo "usage: $0 [-y|--yes]" >&2; exit 2 ;;
esac

docker compose down -v --remove-orphans
