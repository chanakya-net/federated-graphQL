#!/usr/bin/env bash
# usage: scripts/wait-healthy.sh <service>...
# Waits until every named compose service is healthy (or running, if it has no healthcheck; or
# exited 0, if it is a one-shot without a healthcheck). Fails fast on an unknown service or a
# crashed container. Total budget: WAIT_TIMEOUT seconds (default 420). bash 3.2 compatible.
set -euo pipefail
cd "$(dirname "$0")/.."

if [ "$#" -eq 0 ]; then
  echo "usage: $0 <service>..." >&2
  exit 2
fi

known=" $(docker compose config --services | tr '\n' ' ') "
for svc in "$@"; do
  case "$known" in
    *" $svc "*) ;;
    *) echo "unknown service: $svc (known:$known)" >&2; exit 2 ;;
  esac
done

# The service's own container: `ps -a` so a stopped/crashed one is still found (plain `ps` lists
# running containers only), skipping one-off `docker compose run` containers, which `-a` includes.
service_cid() {
  local id
  for id in $(docker compose ps -a -q "$1" 2>/dev/null || true); do
    if [ "$(docker inspect -f '{{index .Config.Labels "com.docker.compose.oneoff"}}' "$id" 2>/dev/null)" = "False" ]; then
      echo "$id"
      return 0
    fi
  done
  return 0
}

timeout="${WAIT_TIMEOUT:-420}"
start=$(date +%s)

for svc in "$@"; do
  last=""
  while true; do
    cid=$(service_cid "$svc")
    status="missing"
    if [ -n "$cid" ]; then
      # State first: an exited container keeps its last health status, which would hide the exit.
      state=$(docker inspect -f '{{.State.Status}}' "$cid" 2>/dev/null || echo missing)
      health=$(docker inspect -f '{{if .State.Health}}{{.State.Health.Status}}{{end}}' "$cid" 2>/dev/null || true)
      case "$state" in
        running) status="${health:-running}" ;;
        exited|dead)
          code=$(docker inspect -f '{{.State.ExitCode}}' "$cid" 2>/dev/null || echo "?")
          if [ -z "$health" ] && [ "$code" = "0" ]; then status="completed"; else status="exited($code)"; fi
          ;;
        *) status="$state" ;; # created, restarting, paused, removing
      esac
    fi
    case "$status" in
      healthy|running|completed) echo "ok      $svc ($status)"; break ;;
      exited*)
        echo "FAILED  $svc $status" >&2
        docker compose logs --tail 50 "$svc" >&2 || true
        exit 1
        ;;
    esac
    if [ "$status" != "$last" ]; then echo "waiting $svc ($status)"; last="$status"; fi
    if [ $(( $(date +%s) - start )) -ge "$timeout" ]; then
      echo "TIMEOUT after ${timeout}s waiting for $svc ($status)" >&2
      docker compose logs --tail 50 "$svc" >&2 || true
      exit 1
    fi
    sleep 3
  done
done
