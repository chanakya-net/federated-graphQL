#!/usr/bin/env bash
# usage: scripts/e2e.sh
# End-to-end validation (phases/phase-6-e2e-validation.md §4) against the running compose stack: start it first
# with scripts/up.sh. Prints PASS/FAIL per scenario, stops at the first failure, and always restores the services
# it stopped or paused (and only those), so the stack ends in the state it was found in.
# Needs bash 3.2+, curl, jq and docker compose. Ports and the subgraph timeout come from the shell, else .env.
set -euo pipefail
cd "$(dirname "$0")/.."

# Value of NAME from the shell, else from .env, else DEFAULT (same precedence as compose and scripts/up.sh).
env_value() {
  local val
  val=$(printenv "$1" || true)
  if [ -z "$val" ] && [ -f .env ]; then
    val=$(sed -n "s/^$1=//p" .env | tail -n 1 | tr -d "\"'")
  fi
  printf '%s' "${val:-$2}"
}

GW="http://localhost:$(env_value GATEWAY_PORT 5050)/graphql"
UI="http://localhost:$(env_value UI_PORT 4200)"
TIMEOUT_S=$(env_value SUBGRAPH_TIMEOUT_SECONDS 5)
EPOCH="2026-09-01T00:00:00Z" # SeedConstants.Epoch (contracts/seeding.md); every seeded event is in the year before it
SERVICES="postgres mongo azurite device-directory patch vulnerability software-install device-search fusion-gateway angular-ui"
TOTAL=29 # Existing APIs/search plus the metadata-driven normalized timeline contract

for tool in curl jq docker; do
  command -v "$tool" >/dev/null || { echo "e2e: '$tool' is required" >&2; exit 2; }
done

# ---------------------------------------------------------------------------------------------------------------
# Outages: remember what this run broke, restore exactly that on any exit.

touched=""
passed=0
current=""
started="" # set once the preflight passed

outage() { # outage <service> <stop|pause|restore>
  case "$2" in
    stop|pause) case " $touched " in *" $1 "*) ;; *) touched="$touched $1" ;; esac ;;
  esac
  local log
  log=$(scripts/demo-outage.sh "$1" "$2" 2>&1) || { printf 'FAIL %s\n  scripts/demo-outage.sh %s %s failed:\n%s\n' "$current" "$1" "$2" "$log"; exit 1; }
  if [ "$2" = restore ]; then
    touched=$(printf '%s' " $touched " | sed "s/ $1 / /; s/^ *//; s/ *$//")
  fi
}

on_exit() {
  local rc=$? svc
  for svc in $touched; do
    scripts/demo-outage.sh "$svc" restore >/dev/null 2>&1 ||
      echo "WARN could not restore $svc: run scripts/demo-outage.sh $svc restore" >&2
  done
  if [ "$rc" -ne 0 ] && [ -n "$started" ]; then echo "e2e: $passed/$TOTAL passed"; fi
  exit "$rc"
}
trap on_exit EXIT
trap 'exit 130' INT TERM

# ---------------------------------------------------------------------------------------------------------------
# GraphQL calls and assertions.

BODY="" HTTP_CODE="" SECS=""

# gql <token|-> <query> [variables-json] [extra-header]: sets BODY, HTTP_CODE (000 = no connection) and SECS.
# "-" sends no token.
gql() {
  local auth=() extra=() out
  if [ "$1" != "-" ]; then auth=(-H "Authorization: Bearer $1"); fi
  if [ -n "${4:-}" ]; then extra=(-H "$4"); fi
  out=$(curl -s --max-time 30 -w '\n%{http_code} %{time_total}' "$GW" ${auth[@]+"${auth[@]}"} ${extra[@]+"${extra[@]}"} \
    -H 'Content-Type: application/json' \
    -d "$(jq -nc --arg q "$2" --argjson v "${3:-null}" '{query: $q, variables: $v}')") || true
  BODY=$(printf '%s\n' "$out" | sed '$d')
  HTTP_CODE=$(printf '%s\n' "$out" | tail -n 1 | cut -d' ' -f1)
  SECS=$(printf '%s\n' "$out" | tail -n 1 | cut -d' ' -f2)
}

TOKENS=""
tok() { printf '%s' "$TOKENS" | jq -r --arg u "$1" '.[] | select(.sub == $u) | .token'; }

Q_DEVICE='query($id: ID!) {
  device(id: $id) { id hostname tenantId patchEvents { id } vulnerabilityEvents { id } installEvents { id } }
}'
device() { gql "$(tok "$1")" "$Q_DEVICE" "$(jq -nc --arg id "$2" '{id: $id}')"; } # device <user> <deviceId>

# Error matching follows contracts/errors.md: by path prefix ["device", <field>]; a denial carries
# AUTH_NOT_AUTHORIZED, an outage any other code or none at all (no `extensions`).
JQ_DEFS='
def errs: (.errors // []);
def errs_at($f): [errs[] | select((.path // [])[0:2] == ["device", $f])];
def denied_at($f): [errs_at($f)[] | select(.extensions.code? == "AUTH_NOT_AUTHORIZED")];
def outage_at($f): [errs_at($f)[] | select(.extensions.code? != "AUTH_NOT_AUTHORIZED")];
def ok($f): (.data.device[$f] | type) == "array" and (errs_at($f) | length) == 0;
def full: .data.device.id == "dev-00001" and ok("patchEvents") and ok("vulnerabilityEvents")
  and ok("installEvents") and (errs | length) == 0;
def down($f): .data.device[$f] == null and (outage_at($f) | length) >= 1 and (denied_at($f) | length) == 0;
def ts: sub("\\.[0-9]+"; "") | fromdateiso8601;
'

begin() { current="$1"; }
pass() { passed=$((passed + 1)); echo "PASS $current${1:+  ($1)}"; current=""; }
fail() {
  echo "FAIL $current"
  echo "  $1"
  echo "  HTTP $HTTP_CODE in ${SECS}s"
  if [ -n "$BODY" ]; then printf '%s\n' "$BODY" | jq . 2>/dev/null || printf '%s\n' "$BODY"; fi
  exit 1
}
# expect <jq-filter> [jq args...]: the filter (with JQ_DEFS) must be true for BODY.
expect() {
  local filter="$1" err; shift
  if ! err=$(printf '%s\n' "$BODY" | jq -e "$@" "$JQ_DEFS $filter" 2>&1 >/dev/null); then
    fail "expected: $filter${err:+ ($err)}"
  fi
}
expect_http() { [ "$HTTP_CODE" = "$1" ] || fail "expected HTTP $1"; }
expect_secs() { # expect_secs <min> <max>: min <= SECS < max
  awk -v s="$SECS" -v lo="$1" -v hi="$2" 'BEGIN { exit !(s >= lo && s < hi) }' ||
    fail "expected a response time in [$1, $2) s"
}
# jqb <jq-filter> [jq args...]: print a value computed from BODY (with JQ_DEFS).
jqb() { local filter="$1"; shift; printf '%s\n' "$BODY" | jq -r "$@" "$JQ_DEFS $filter"; }

# ---------------------------------------------------------------------------------------------------------------
# Preflight (not a scenario): the whole stack is up and the UI serves a token for each of the five users.

echo "e2e: gateway $GW, ui $UI, subgraph timeout ${TIMEOUT_S}s"
# shellcheck disable=SC2086 # word splitting intended: service names contain no spaces
WAIT_TIMEOUT="${E2E_WAIT_TIMEOUT:-60}" scripts/wait-healthy.sh $SERVICES >/dev/null ||
  { echo "e2e: the stack is not healthy; start it with scripts/up.sh" >&2; exit 2; }
TOKENS=$(curl -sf --max-time 10 "$UI/tokens.json") ||
  { echo "e2e: cannot read $UI/tokens.json" >&2; exit 2; }
for u in alice bob carol dave erin; do
  [ -n "$(tok "$u")" ] || { echo "e2e: tokens.json has no token for $u" >&2; exit 2; }
done
started=$(date +%s)

# ---------------------------------------------------------------------------------------------------------------
# Federation, access and tenant isolation.

begin dynamic_timeline_contract
TIMELINE_GATEWAY_URL="${GW%/graphql}" TIMELINE_UI_URL="$UI" scripts/check-timeline.sh || fail "dynamic timeline contract failed"
pass

begin alice_full_timeline
device alice dev-00001
expect_http 200
expect 'full and .data.device.tenantId == "TenantA"'
pass

begin alice_counts_in_seed_range
# contracts/seeding.md: patch events 5-30, findings 2-15 (one or two events each), install events 3-20 per device,
# and every event belongs to the device it hangs off.
expect '(.data.device.patchEvents | length) as $p | $p >= 5 and $p <= 30'
expect '(.data.device.vulnerabilityEvents | length) as $v | $v >= 2 and $v <= 30'
expect '(.data.device.installEvents | length) as $i | $i >= 3 and $i <= 20'
expect '[.data.device[("patchEvents", "vulnerabilityEvents", "installEvents")][].id | startswith("dev-00001-")] | all'
pass "$(jqb '.data.device | "patch \(.patchEvents | length), vulnerability \(.vulnerabilityEvents | length), install \(.installEvents | length)"')"

begin bob_partial_access
device bob dev-00001
expect_http 200
expect 'ok("patchEvents") and ok("vulnerabilityEvents") and .data.device.installEvents == null'
expect '(denied_at("installEvents") | length) == 1 and (errs | length) == 1'
pass

begin carol_single_service
device carol dev-00002
expect_http 200
expect 'ok("installEvents") and .data.device.patchEvents == null and .data.device.vulnerabilityEvents == null'
expect '(denied_at("patchEvents") | length) == 1 and (denied_at("vulnerabilityEvents") | length) == 1 and (errs | length) == 2'
pass

begin dave_cross_tenant_null_no_error
device dave dev-00001
expect_http 200
expect '.data.device == null and (errs | length) == 0'
pass

begin dave_own_tenant_ok
device dave dev-07000
expect_http 200
expect '.data.device.tenantId == "TenantB" and ok("patchEvents") and ok("vulnerabilityEvents") and ok("installEvents")'
expect '(errs | length) == 0'
pass

begin erin_patch_only
device erin dev-07000
expect_http 200
expect 'ok("patchEvents") and .data.device.vulnerabilityEvents == null and .data.device.installEvents == null'
expect '(denied_at("vulnerabilityEvents") | length) == 1 and (denied_at("installEvents") | length) == 1'
pass

begin unauthenticated_401
gql - "$Q_DEVICE" '{"id": "dev-00001"}'
expect_http 401
[ -z "$BODY" ] || fail "expected an empty 401 body (contracts/errors.md)"
alice_token=$(tok alice)
gql "${alice_token%.*}.not-the-signature" "$Q_DEVICE" '{"id": "dev-00001"}'
expect_http 401
pass "no token and a tampered token"

# ---------------------------------------------------------------------------------------------------------------
# Graceful degradation: stopped (connection error, fast) and paused (subgraph timeout) domain subgraphs.

begin patch_stopped_degrades_only_patch
outage patch stop
device alice dev-00001
expect_http 200
expect 'down("patchEvents") and ok("vulnerabilityEvents") and ok("installEvents")'
# The new generic UI contract must preserve the same independent degradation as the domain APIs.
gql "$(tok alice)" '{ device(id: "dev-00001") { id patchTimeline { id } vulnerabilityTimeline { id } softwareInstallTimeline { id } } }'
expect_http 200
expect 'down("patchTimeline") and ok("vulnerabilityTimeline") and ok("softwareInstallTimeline")'
pass "legacy and normalized fields both preserve healthy sources"

begin patch_stopped_is_fast
expect_secs 0 3
pass "${SECS}s"

begin patch_restored
outage patch restore
device alice dev-00001
expect_http 200
expect full
pass

begin patch_paused_times_out_cleanly
outage patch pause
device alice dev-00001
expect_http 200
expect 'down("patchEvents") and ok("vulnerabilityEvents") and ok("installEvents")'
expect_secs "$TIMEOUT_S" "$((TIMEOUT_S + 3))"
pass "${SECS}s"

begin patch_unpaused
outage patch restore
device alice dev-00001
expect_http 200
expect full
pass

begin vulnerability_stopped
outage vulnerability stop
device alice dev-00001
expect_http 200
expect 'down("vulnerabilityEvents") and ok("patchEvents") and ok("installEvents")'
outage vulnerability restore
device alice dev-00001
expect_http 200
expect full
pass "degraded, then restored"

begin software_install_stopped
outage software-install stop
device alice dev-00001
expect_http 200
expect 'down("installEvents") and ok("patchEvents") and ok("vulnerabilityEvents")'
outage software-install restore
device alice dev-00001
expect_http 200
expect full
pass "degraded, then restored"

begin bob_with_software_install_down
# Only the subgraph can deny, and it is down: bob gets the outage shape, which the UI shows as "unavailable"
# (docs/version-facts.md §8, P5 row).
outage software-install stop
device bob dev-00001
expect_http 200
expect '.data.device.installEvents == null and (errs_at("installEvents") | length) >= 1'
expect 'ok("patchEvents") and ok("vulnerabilityEvents")'
code=$(jqb '[errs_at("installEvents")[] | .extensions.code? // "none"] | unique | join(", ")')
outage software-install restore
pass "code: $code"

begin device_directory_down_fails_whole_query
# The documented single point of failure (plan §7): no device, so nothing to extend.
outage device-directory stop
device alice dev-00001
expect '.data.device == null and (errs | length) >= 1'
dd_http=$HTTP_CODE
outage device-directory restore
device alice dev-00001
expect_http 200
expect full
pass "HTTP $dd_http while down, full timeline after restore"

# ---------------------------------------------------------------------------------------------------------------
# Search, schema surface and argument pushdown.

begin search_scoped_to_tenant
# "dev-0" matches dev-00000..dev-09999: 7 000 TenantA and 3 000 TenantB ids (contracts/seeding.md).
Q_SEARCH='query($s: String) { devices(search: $s, first: 5) { totalCount items { id tenantId } } }'
gql "$(tok dave)" "$Q_SEARCH" '{"s": "dev-0"}'
expect_http 200
expect '.data.devices.totalCount == 3000 and (.data.devices.items | length) == 5'
expect '[.data.devices.items[].tenantId == "TenantB"] | all'
gql "$(tok dave)" "$Q_SEARCH" '{"s": "dev-00"}' # TenantA ids only
expect_http 200
expect '.data.devices.totalCount == 0 and .data.devices.items == [] and (errs | length) == 0'
gql "$(tok alice)" "$Q_SEARCH" '{"s": "dev-0"}'
expect_http 200
expect '.data.devices.totalCount == 7000 and ([.data.devices.items[].tenantId == "TenantA"] | all)'
pass

begin lookup_hidden_on_gateway
gql "$(tok alice)" '{ __type(name: "Query") { fields { name } } }'
expect_http 200
expect '[.data.__type.fields[].name] | (index("deviceById") == null) and (index("device") != null)'
fields=$(jqb '[.data.__type.fields[].name] | join(", ")')
gql "$(tok alice)" '{ deviceById(id: "dev-00001") { id } }' # a validation error, never reaches a subgraph
expect '.data == null and ([errs[].message | test("deviceById")] | any)'
pass "Query fields: $fields; deviceById -> HTTP $HTTP_CODE"

begin since_until_pushdown
# Every domain filters on the server. Window [Epoch - 180 d, Epoch - 30 d]: the filtered answer must be exactly the
# unfiltered events inside the window (inclusive), the same when the bounds carry a +05:30 offset, and a window of
# one instant must still return the event at that instant.
Q_EVENTS='query($id: ID!, $since: DateTime, $until: DateTime) { device(id: $id) {
  patchEvents(since: $since, until: $until) { id occurredAt }
  vulnerabilityEvents(since: $since, until: $until) { id occurredAt }
  installEvents(since: $since, until: $until) { id occurredAt } } }'
since=$(jq -rn --arg e "$EPOCH" '$e | fromdateiso8601 - 180 * 86400 | todate')
until=$(jq -rn --arg e "$EPOCH" '$e | fromdateiso8601 - 30 * 86400 | todate')
ist() { jq -rn --arg t "$1" '$t | fromdateiso8601 + 19800 | strftime("%Y-%m-%dT%H:%M:%S") + "+05:30"'; }
range() { jq -nc --arg s "$1" --arg u "$2" '{id: "dev-00001", since: $s, until: $u}'; }

gql "$(tok alice)" "$Q_EVENTS" '{"id": "dev-00001"}'
expect_http 200
expect 'ok("patchEvents") and ok("vulnerabilityEvents") and ok("installEvents") and (errs | length) == 0'
all=$BODY
counts=""
for f in patchEvents vulnerabilityEvents installEvents; do
  for window in "$since $until" "$(ist "$since") $(ist "$until")"; do
    # shellcheck disable=SC2086 # word splitting intended: two bounds
    gql "$(tok alice)" "$Q_EVENTS" "$(range $window)"
    expect_http 200
    expect '(errs | length) == 0 and ([.data.device[$f][].occurredAt | ts | . >= ($s | ts) and . <= ($u | ts)] | all)' \
      --arg f "$f" --arg s "$since" --arg u "$until"
    expect '([.data.device[$f][].id] | sort) as $got
      | ([$all.data.device[$f][] | select((.occurredAt | ts) as $t | $t >= ($s | ts) and $t <= ($u | ts)) | .id] | sort) as $want
      | ($want | length) > 0 and $got == $want' \
      --arg f "$f" --arg s "$since" --arg u "$until" --argjson all "$all"
  done
  n=$(jqb '.data.device[$f] | length' --arg f "$f")
  # The oldest event in the window, alone: since == until == its timestamp.
  id=$(jqb '.data.device[$f][-1].id' --arg f "$f")
  at=$(jqb '.data.device[$f][-1].occurredAt' --arg f "$f")
  gql "$(tok alice)" "$Q_EVENTS" "$(range "$at" "$at")"
  expect_http 200
  expect '([.data.device[$f][].id] | index($id) != null) and ([.data.device[$f][].occurredAt | ts == ($at | ts)] | all)' \
    --arg f "$f" --arg id "$id" --arg at "$at"
  counts="$counts${counts:+, }$f $n"
done
pass "$since .. $until: $counts"

begin query_plan_fans_out
# Plan claim #1: the gateway loads the device from Device Directory first, then calls the three domain subgraphs,
# each depending only on that first step (so they run in parallel). Nitro's plan view reads the same data.
gql "$(tok alice)" "$Q_DEVICE" '{"id": "dev-00001"}' 'Fusion-Operation-Plan: 1'
expect_http 200
expect full
expect '.extensions.fusion.operationPlan.nodes as $n
  | ($n | map(select(.schema == "DeviceDirectory"))) as $dd
  | ($n | map(select(.schema != "DeviceDirectory"))) as $domains
  | ($n | length) == 4 and ([$n[].status == "Success"] | all)
    and ($dd | length) == 1 and ($dd[0].dependencies // []) == []
    and ([$domains[].schema] | sort) == ["Patch", "SoftwareInstall", "Vulnerability"]
    and ([$domains[].dependencies == [$dd[0].id]] | all)'
pass "$(jqb '.extensions.fusion.operationPlan | (.nodes | map("\(.schema) \(.duration | floor) ms") | join(", ")) + "; total \(.duration | floor) ms"')"

# ---------------------------------------------------------------------------------------------------------------
# Find devices (reverse lookups): the domain subgraph answers with Device stubs, the gateway completes them
# through Device Directory in one batched lookup; access and tenant rules are the same as for the timeline.

Q_FIND='query($ids: [ID!]!, $first: Int!) {
  devicesWithPatches(patchIds: $ids, first: $first) {
    totalCount items { device { id hostname os tenantId } events { id status patch { id kbId } } }
  }
}'
find_patches() { gql "$(tok "$1")" "$Q_FIND" "$(jq -nc --argjson ids "$2" --argjson first "$3" '{ids: $ids, first: $first}')"; }

begin find_by_patch_federates_via_device_directory
# dev-00001 has events for patch-0128 and patch-0282 (docs/demo.md), so it must be in the answer, completed with
# Device Directory's fields, and every listed event must reference a selected patch.
find_patches alice '["patch-0128","patch-0282"]' 100
expect_http 200
expect '(errs | length) == 0 and .data.devicesWithPatches.totalCount > 0'
expect '[.data.devicesWithPatches.items[] | .device.tenantId == "TenantA" and (.device.hostname | length) > 0] | all'
expect '[.data.devicesWithPatches.items[].device.id] | index("dev-00001") != null'
expect '[.data.devicesWithPatches.items[].events[] | .patch.id | IN("patch-0128", "patch-0282")] | all'
expect '[.data.devicesWithPatches.items[].device.id] as $ids | $ids == ($ids | sort)'
pass "$(jqb '"\(.data.devicesWithPatches.totalCount) devices, \(.data.devicesWithPatches.items | length) on the page"')"

begin find_plan_completes_stubs_in_one_directory_call
# The plan: Patch first, then one Device Directory node that depends on it (the batched `device(id)` lookup).
gql "$(tok alice)" "$Q_FIND" '{"ids": ["patch-0128"], "first": 25}' 'Fusion-Operation-Plan: 1'
expect_http 200
expect '.extensions.fusion.operationPlan.nodes as $n
  | ($n | map(select(.schema == "Patch"))) as $p
  | ($n | map(select(.schema == "DeviceDirectory"))) as $dd
  | ($n | length) == 2 and ([$n[].status == "Success"] | all)
    and ($p | length) == 1 and ($p[0].dependencies // []) == []
    and ($dd | length) == 1 and ($dd[0].dependencies == [$p[0].id])'
pass "$(jqb '.extensions.fusion.operationPlan | (.nodes | map("\(.schema) \(.duration | floor) ms") | join(", "))')"

begin find_denied_without_the_service
find_patches carol '["patch-0128"]' 25
expect_http 200
expect '.data.devicesWithPatches == null and ([errs[] | select(.path == ["devicesWithPatches"]) | .extensions.code == "AUTH_NOT_AUTHORIZED"] | any)'
pass

begin find_scoped_to_tenant
find_patches dave '["patch-0128","patch-0282"]' 100
expect_http 200
expect '(errs | length) == 0 and .data.devicesWithPatches.totalCount > 0'
expect '[.data.devicesWithPatches.items[] | .device.tenantId == "TenantB"] | all'
expect '[.data.devicesWithPatches.items[].device.id] | index("dev-00001") == null'
pass "$(jqb '"\(.data.devicesWithPatches.totalCount) TenantB devices"')"

begin find_and_across_subgraphs_on_server
Q_SEARCH='query($filters: [DeviceSearchFilterInput!]!, $first: Int!, $offset: Int!) {
  findDevices(filters: $filters, first: $first, offset: $offset) {
    totalCount hasNextPage
    items { device { id hostname tenantId } events { source itemKey status } }
  }
}'
search_vars='{"filters":[{"category":"patch","key":"patch-0128"},{"category":"vulnerability","key":"CVE-2026-10166","connector":"and"}],"first":25,"offset":0}'
gql "$(tok alice)" "$Q_SEARCH" "$search_vars" 'Fusion-Operation-Plan: 1'
expect_http 200
expect '(errs | length) == 0 and .data.findDevices.totalCount > 1'
expect '[.data.findDevices.items[] | (.device.hostname | length) > 0 and .device.tenantId == "TenantA"
  and ([.events[].source] | unique | sort) == ["patch", "vulnerability"]] | all'
expect '[.data.findDevices.items[].events[].itemKey | IN("patch-0128", "CVE-2026-10166")] | all'
expect '.extensions.fusion.operationPlan.nodes | map(.schema) | sort == ["DeviceDirectory", "DeviceSearch"]'
search_total=$(jqb '.data.findDevices.totalCount')
search_second=$(jqb '.data.findDevices.items[1].device.id')
gql "$(tok alice)" "$Q_SEARCH" "$(printf '%s' "$search_vars" | jq '.first=1 | .offset=1')"
expect_http 200
expect '(errs | length) == 0 and .data.findDevices.totalCount == $total
  and (.data.findDevices.items | length) == 1 and .data.findDevices.items[0].device.id == $second' \
  --argjson total "$search_total" --arg second "$search_second"
pass "$search_total devices; server page and gateway enrichment verified"

begin find_server_permissions_and_tenant
gql "$(tok carol)" "$Q_SEARCH" "$search_vars"
expect_http 200
expect '.data.findDevices == null and ([errs[] | select(.path == ["findDevices"]) | .extensions.code == "AUTH_NOT_AUTHORIZED"] | any)'
gql "$(tok dave)" "$Q_SEARCH" '{"filters":[{"category":"patch","key":"patch-0128"}],"first":25,"offset":0}'
expect_http 200
expect '(errs | length) == 0 and .data.findDevices.totalCount > 0'
expect '[.data.findDevices.items[] | .device.tenantId == "TenantB" and .device.id != "dev-00001"] | all'
pass

begin find_server_required_source_failure_is_not_partial
outage patch stop
gql "$(tok alice)" "$Q_SEARCH" "$(printf '%s' "$search_vars" | jq '.filters[1].connector="or"')"
expect_http 200
expect '.data.findDevices == null and ([errs[] | select((.path // [])[0] == "findDevices")] | length) > 0'
outage patch restore
pass

echo "e2e: $passed/$TOTAL passed in $(( $(date +%s) - started ))s"
[ "$passed" -eq "$TOTAL" ]
