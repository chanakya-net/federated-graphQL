#!/usr/bin/env bash
# usage: scripts/up.sh [service...]
# Builds and starts the stack (default: everything) and waits until it is healthy.
# With service names, starts only those (plus their dependencies), e.g. the tracer bullet:
#   scripts/up.sh postgres device-directory fusion-gateway token-generator angular-ui
# Cold start (first build + seeding) takes several minutes; WAIT_TIMEOUT (default 420 s) bounds the wait.
set -euo pipefail
cd "$(dirname "$0")/.."

# Value of NAME from the shell, else from .env, else DEFAULT (compose uses the same precedence).
env_value() {
  local val
  val=$(printenv "$1" || true)
  if [ -z "$val" ] && [ -f .env ]; then
    val=$(sed -n "s/^$1=//p" .env | tail -n 1 | tr -d "\"'")
  fi
  printf '%s' "${val:-$2}"
}

if [ "$#" -eq 0 ]; then
  docker compose up --build -d
  scripts/wait-healthy.sh postgres mongo azurite device-directory patch vulnerability software-install fusion-gateway angular-ui
else
  docker compose up --build -d "$@"
  # token-generator is a one-shot; `up` already fails if it does not exit 0.
  wait_for=""
  for svc in "$@"; do
    [ "$svc" = "token-generator" ] || wait_for="$wait_for $svc"
  done
  if [ -n "$wait_for" ]; then
    # shellcheck disable=SC2086 # word splitting intended: service names contain no spaces
    scripts/wait-healthy.sh $wait_for
  fi
fi

gateway_port=$(env_value GATEWAY_PORT 5050)
ui_port=$(env_value UI_PORT 4200)
echo
echo "UI:        http://localhost:${ui_port}"
echo "Gateway:   http://localhost:${gateway_port}/graphql   (Nitro UI: http://localhost:${gateway_port}/graphql/)"
echo "Token:     docker compose run --rm -T token-generator --user alice"
