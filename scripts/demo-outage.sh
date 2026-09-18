#!/usr/bin/env bash
# usage: scripts/demo-outage.sh <patch|vulnerability|software-install|device-directory> <stop|pause|restore>
#   stop     container gone, DNS name stops resolving -> the gateway fails fast (connection error)
#   pause    container frozen, TCP hangs -> the gateway's per-subgraph timeout (5 s) fires
#   restore  unpause/start again and wait until healthy
# Only the three domain subgraphs degrade gracefully; device-directory is the deliberate single
# point of failure (plan §7).
set -euo pipefail
cd "$(dirname "$0")/.."

usage() { echo "usage: $0 <patch|vulnerability|software-install|device-directory> <stop|pause|restore>" >&2; exit 2; }
[ "$#" -eq 2 ] || usage
svc="$1"; action="$2"

case "$svc" in
  patch|vulnerability|software-install|device-directory) ;;
  *) echo "unknown service: $svc" >&2; usage ;;
esac

case "$action" in
  stop)    docker compose stop "$svc" ;;
  pause)   docker compose pause "$svc" ;;
  restore)
    docker compose unpause "$svc" >/dev/null 2>&1 || true # not paused: nothing to do
    docker compose start "$svc"
    scripts/wait-healthy.sh "$svc"
    ;;
  *) echo "unknown action: $action" >&2; usage ;;
esac
