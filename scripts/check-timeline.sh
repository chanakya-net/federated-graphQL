#!/usr/bin/env bash
# Read-only integration checks for the deployed generic timeline and its UI proxy.
set -euo pipefail
cd "$(dirname "$0")/.."
GW="${TIMELINE_GATEWAY_URL:-http://localhost:5050}"
UI="${TIMELINE_UI_URL:-http://localhost:4200}"
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
curl -fsS "$GW/timeline-sources" > "$work/catalog.json"
curl -fsS "$UI/timeline-sources" > "$work/ui-catalog.json"
jq -e '.version == 1 and (.schemaHash | test("^[a-fA-F0-9]{64}$")) and (.sources | length > 0) and all(.sources[]; .contractVersion == 1 and (.field | test("^[A-Za-z_][A-Za-z0-9_]*$")))' "$work/catalog.json" >/dev/null
cmp <(jq -S . "$work/catalog.json") <(jq -S . "$work/ui-catalog.json")
curl -fsS "$UI/tokens.json" > "$work/tokens.json"
token() { jq -r --arg user "$1" '.[] | select(.sub == $user) | .token' "$work/tokens.json"; }
# The query is generated solely from advertised fields, using one stable event selection.
query=$(jq -r '"query TimelineContract($id: ID!, $since: DateTime, $until: DateTime) { device(id: $id) { id tenantId " + ([.sources[].field + "(since: $since, until: $until) { id occurredAt label title subtitle status severity details { label value mono } }"] | join(" ")) + " } }"' "$work/catalog.json")
post() {
  jq -nc --arg query "$query" --argjson variables "$2" '{query: $query, variables: $variables}' |
    curl -fsS "$GW/graphql" -H 'Content-Type: application/json' -H "Authorization: Bearer $(token "$1")" --data-binary @-
}
post alice '{"id":"dev-00001","since":null,"until":null}' > "$work/full.json"
jq -e --slurpfile catalog "$work/catalog.json" '
  ((.errors // []) | length == 0) and .data.device.id == "dev-00001" and
  (.data.device as $d | all($catalog[0].sources[]; ($d[.field] | type == "array" and length > 0) and all($d[.field][]; (.id | length > 0) and (.title | length > 0) and (.occurredAt | length > 0) and (.details | type == "array") and all(.details[]; (.label | type == "string") and (.value | type == "string") and (.mono | type == "boolean")))))
' "$work/full.json" >/dev/null
post bob '{"id":"dev-00001","since":null,"until":null}' > "$work/denied.json"
jq -e '.data.device.patchTimeline != null and .data.device.vulnerabilityTimeline != null and .data.device.softwareInstallTimeline == null and any(.errors[]; .path[0:2] == ["device","softwareInstallTimeline"] and .extensions.code == "AUTH_NOT_AUTHORIZED")' "$work/denied.json" >/dev/null
post dave '{"id":"dev-00001","since":null,"until":null}' | jq -e '.data.device == null and ((.errors // []) | length == 0)' >/dev/null
# Compare an inclusive single-instant window with the unfiltered response, across all advertised sources.
at=$(jq -r --slurpfile catalog "$work/catalog.json" '.data.device[$catalog[0].sources[0].field][0].occurredAt' "$work/full.json")
post alice "$(jq -nc --arg at "$at" '{id:"dev-00001",since:$at,until:$at}')" > "$work/range.json"
jq -e --arg at "$at" --slurpfile full "$work/full.json" --slurpfile catalog "$work/catalog.json" '
  ((.errors // []) | length == 0) and (.data.device as $d | all($catalog[0].sources[]; .field as $f | ($d[$f] | map(.id) | sort) == ($full[0].data.device[$f] | map(select(.occurredAt == $at) | .id) | sort)))
' "$work/range.json" >/dev/null
printf 'PASS dynamic timeline: catalog/proxy, generic query, details, partial denial, tenant isolation, inclusive date range\n'
