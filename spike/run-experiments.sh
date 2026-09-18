#!/usr/bin/env bash
# Phase 0 spike: runs experiments 1-11 against the spike stack and records the raw responses.
#
#   spike/run-experiments.sh            # builds + starts the stack, runs everything
#
# Fixtures (raw, unmodified gateway bodies) go to tests/fixtures/, everything else to spike/results/.
# Exits non-zero if any experiment fails its pass condition.
set -uo pipefail
cd "$(dirname "$0")"

PORT=${SPIKE_GATEWAY_PORT:-5050}
URL="http://localhost:$PORT/graphql"
FIX=../tests/fixtures
OUT=results
mkdir -p "$FIX" "$OUT"
: > "$OUT/summary.txt"

FAILED=0
pass() { echo "PASS  $*" | tee -a "$OUT/summary.txt"; }
fail() { echo "FAIL  $*" | tee -a "$OUT/summary.txt"; FAILED=1; }
note() { echo "      $*" | tee -a "$OUT/summary.txt"; }
check() { # check <label> <jq-expression> <file>
  if jq -e "$2" "$3" >/dev/null 2>&1; then pass "$1"; else fail "$1  (jq: $2)"; fi
}

wait_healthy() {
  for _ in $(seq 1 90); do
    [ "$(docker compose ps "$1" --format '{{.Health}}' 2>/dev/null)" = healthy ] && return 0
    sleep 1
  done
  echo "service $1 did not become healthy" >&2
  exit 2
}

# Count POSTs to /graphql seen by a subgraph so far (health checks are GETs and are ignored).
subgraph_posts() { docker compose logs "$1" 2>/dev/null | grep -c 'Request starting HTTP/1.1 POST http://[^ ]*/graphql' || true; }

echo "== building and starting the spike stack"
docker compose up --build -d --wait >/dev/null 2>&1 || { docker compose up --build -d; }
wait_healthy owner; wait_healthy extender; wait_healthy gateway

dotnet build Token -v q -nologo >/dev/null
tk() { dotnet Token/bin/Debug/net10.0/Token.dll "$@"; }
TOKEN_FULL=$(tk TenantA extender)  # TenantA, has the extender service
TOKEN_NONE=$(tk TenantA)           # TenantA, no services at all
TOKEN_B=$(tk TenantB extender)     # TenantB, has the extender service

Q='{"query":"{ device(id:\"dev-00001\") { id hostname notes } }"}'

# gql <token> <outfile> [body]  ->  prints "<http_code> <seconds>"
gql() {
  curl -s -o "$2" -w '%{http_code} %{time_total}' "$URL" \
    -H "Authorization: Bearer $1" -H 'Content-Type: application/json' -d "${3:-$Q}"
}
between() { awk -v t="$1" -v lo="$2" -v hi="$3" 'BEGIN { exit !(t >= lo && t <= hi) }'; }

echo "== 1 happy path"
read -r code t < <(gql "$TOKEN_FULL" "$OUT/01-happy.json")
note "HTTP $code in ${t}s: $(cat "$OUT/01-happy.json")"
check "1 happy path: notes has one element, no errors" \
  '(.data.device.notes | length) == 1 and (has("errors") | not)' "$OUT/01-happy.json"

echo "== 2 lookup hidden"
read -r code t < <(gql "$TOKEN_FULL" "$OUT/02-query-fields.json" '{"query":"{ __type(name:\"Query\") { fields { name } } }"}')
note "HTTP $code: $(jq -c '[.data.__type.fields[].name]' "$OUT/02-query-fields.json")"
check "2 introspection: device present, deviceById absent" \
  '[.data.__type.fields[].name] | (index("device") != null) and (index("deviceById") == null)' "$OUT/02-query-fields.json"
read -r code t < <(gql "$TOKEN_FULL" "$OUT/02-direct-deviceById.json" '{"query":"{ deviceById(id:\"dev-00001\") { id } }"}')
note "direct deviceById -> HTTP $code: $(jq -c '.errors[0].message' "$OUT/02-direct-deviceById.json")"
check "2 direct call to deviceById is rejected by validation" \
  '.errors[0].message | test("does not exist")' "$OUT/02-direct-deviceById.json"

echo "== 3 header forwarded"
before=$(docker compose logs extender 2>/dev/null | grep -c 'Authorization header present: True' || true)
gql "$TOKEN_FULL" /dev/null >/dev/null
sleep 1
after=$(docker compose logs extender 2>/dev/null | grep -c 'Authorization header present: True' || true)
docker compose logs --no-log-prefix extender 2>/dev/null | grep 'Authorization header present' | tail -2 > "$OUT/03-extender-log.txt"
note "extender log lines with 'present: True': before=$before after=$after"
if [ "$after" -gt "$before" ]; then pass "3 extender received the Authorization header"; else fail "3 header not seen at extender"; fi

echo "== 4 outage: extender stopped"
docker compose stop extender >/dev/null 2>&1
read -r code t < <(gql "$TOKEN_FULL" "$FIX/outage-stop.json")
note "HTTP $code in ${t}s: $(cat "$FIX/outage-stop.json")"
check "4 stop: device kept, notes null, error path starts [device,notes]" \
  '.data.device.id == "dev-00001" and .data.device.hostname == "alpha" and .data.device.notes == null
   and (.errors | length) >= 1 and (.errors[0].path[0:2] == ["device","notes"])' "$FIX/outage-stop.json"
if [ "$code" = 200 ] && between "$t" 0 2; then pass "4 stop: HTTP 200 in < 2 s (${t}s)"; else fail "4 stop: HTTP $code in ${t}s"; fi
echo "$code $t" > "$OUT/04-stop-status.txt"

echo "== 11 archive URL is overridden by SUBGRAPH_<NAME>_URL (extender still stopped is irrelevant here)"
SPIKE_EXTENDER_URL=http://nowhere.invalid:8080/graphql docker compose up -d gateway >/dev/null 2>&1
wait_healthy gateway
docker compose start extender >/dev/null 2>&1; wait_healthy extender
read -r code t < <(gql "$TOKEN_FULL" "$OUT/11-url-override.json")
note "extender healthy, gateway pointed at nowhere.invalid -> HTTP $code: $(jq -c '{notes: .data.device.notes, err: .errors[0].message}' "$OUT/11-url-override.json")"
check "11 env URL wins over archive URL (healthy extender is NOT reached)" \
  '.data.device.id == "dev-00001" and .data.device.notes == null and (.errors | length) >= 1' "$OUT/11-url-override.json"
docker compose up -d gateway >/dev/null 2>&1   # back to the real URL
wait_healthy gateway

echo "== 5 outage: extender paused (timeout path)"
docker compose pause extender >/dev/null 2>&1
read -r code t < <(gql "$TOKEN_FULL" "$FIX/outage-pause.json")
docker compose unpause extender >/dev/null 2>&1
note "HTTP $code in ${t}s: $(cat "$FIX/outage-pause.json")"
check "5 pause: device kept, notes null, error path starts [device,notes]" \
  '.data.device.id == "dev-00001" and .data.device.hostname == "alpha" and .data.device.notes == null
   and (.errors | length) >= 1 and (.errors[0].path[0:2] == ["device","notes"])' "$FIX/outage-pause.json"
if [ "$code" = 200 ] && between "$t" 5 8; then pass "5 pause: HTTP 200 between 5 s and 8 s (${t}s)"; else fail "5 pause: HTTP $code in ${t}s"; fi
echo "$code $t" > "$OUT/05-pause-status.txt"
wait_healthy extender

echo "== 6 denied (token without the extender service)"
read -r code t < <(gql "$TOKEN_NONE" "$FIX/denied.json")
note "HTTP $code in ${t}s: $(cat "$FIX/denied.json")"
check "6 denied: device kept, notes null, path [device,notes], code AUTH_NOT_AUTHORIZED" \
  '.data.device.id == "dev-00001" and .data.device.notes == null
   and (.errors[0].path[0:2] == ["device","notes"]) and .errors[0].extensions.code == "AUTH_NOT_AUTHORIZED"' "$FIX/denied.json"
[ "$code" = 200 ] && pass "6 denied: HTTP 200" || fail "6 denied: HTTP $code"

echo "== 7 unauthenticated"
o_before=$(subgraph_posts owner); e_before=$(subgraph_posts extender)
curl -s -D "$OUT/07-headers.txt" -o "$OUT/07-body.raw" -w '%{http_code}' -X POST "$URL" \
  -H 'Content-Type: application/json' -d "$Q" > "$OUT/07-status.txt"
sleep 1
o_after=$(subgraph_posts owner); e_after=$(subgraph_posts extender)
code=$(cat "$OUT/07-status.txt")
jq -n --argjson status "$code" \
      --arg wwwAuthenticate "$(grep -i '^www-authenticate:' "$OUT/07-headers.txt" | cut -d' ' -f2- | tr -d '\r')" \
      --arg contentLength "$(grep -i '^content-length:' "$OUT/07-headers.txt" | cut -d' ' -f2- | tr -d '\r')" \
      --rawfile body "$OUT/07-body.raw" \
      '{httpStatus: $status, headers: {"www-authenticate": $wwwAuthenticate, "content-length": $contentLength}, body: $body}' \
      > "$FIX/unauthenticated.json"
note "HTTP $code, body bytes=$(wc -c < "$OUT/07-body.raw" | tr -d ' '), subgraph POSTs owner $o_before->$o_after extender $e_before->$e_after"
[ "$code" = 401 ] && pass "7 unauthenticated: 401 at the gateway" || fail "7 unauthenticated: HTTP $code"
if [ "$o_before" = "$o_after" ] && [ "$e_before" = "$e_after" ]; then pass "7 no subgraph saw the request"; else fail "7 a subgraph saw the request"; fi

echo "== 8 cross-tenant"
read -r code t < <(gql "$TOKEN_B" "$FIX/cross-tenant.json")
note "HTTP $code: $(cat "$FIX/cross-tenant.json")"
check "8 cross-tenant: device null, no errors" '.data.device == null and (has("errors") | not)' "$FIX/cross-tenant.json"

echo "== 9 error-handling mode (PROPAGATE is the gateway default; NULL via per-request onError)"
QN='{"query":"{ device(id:\"dev-00001\") { id hostname notes } }","onError":"NULL"}'
QC='{"query":"{ device(id:\"dev-00001\") { id hostname noteCount } }"}'
QCN='{"query":"{ device(id:\"dev-00001\") { id hostname noteCount } }","onError":"NULL"}'
gql "$TOKEN_NONE" "$OUT/09-nullable-null.json" "$QN" >/dev/null
gql "$TOKEN_NONE" "$OUT/09-nonnull-propagate.json" "$QC" >/dev/null
gql "$TOKEN_NONE" "$OUT/09-nonnull-null.json" "$QCN" >/dev/null
docker compose stop extender >/dev/null 2>&1
gql "$TOKEN_FULL" "$OUT/09-stop-nullable-null.json" "$QN" >/dev/null
gql "$TOKEN_FULL" "$OUT/09-stop-nonnull-propagate.json" "$QC" >/dev/null
docker compose start extender >/dev/null 2>&1; wait_healthy extender
for f in 09-nullable-null 09-nonnull-propagate 09-nonnull-null 09-stop-nullable-null 09-stop-nonnull-propagate; do
  note "$f: $(jq -c . "$OUT/$f.json")"
done
check "9 nullable field + onError NULL: same shape as PROPAGATE (denied)" \
  '.data.device.id == "dev-00001" and .data.device.notes == null and .errors[0].extensions.code == "AUTH_NOT_AUTHORIZED"' "$OUT/09-nullable-null.json"
check "9 NON-null field + PROPAGATE: error nulls the whole device (why extension fields must stay nullable)" \
  '.data.device == null and (.errors | length) >= 1' "$OUT/09-nonnull-propagate.json"
check "9 outage on NON-null field + PROPAGATE: whole device null too" \
  '.data.device == null and (.errors | length) >= 1' "$OUT/09-stop-nonnull-propagate.json"

echo "== 10 Nitro UI (GET with Accept: text/html, no token)"
# GET /graphql answers 301 -> /graphql/ ; the IDE and its assets are embedded (no CDN).
curl -s -o /dev/null -w '%{http_code} %{redirect_url}' -H 'Accept: text/html' "$URL" > "$OUT/10-redirect.txt"
curl -sL -o "$OUT/10-nitro.html" -w '%{http_code} %{content_type}' -H 'Accept: text/html' "$URL" > "$OUT/10-status.txt"
asset=$(grep -o 'src="./assets/[^"]*"' "$OUT/10-nitro.html" | head -1 | sed 's#src="./##; s#"$##')
asset_code=$(curl -s -o /dev/null -w '%{http_code}' "$URL/$asset")
note "GET /graphql -> $(cat "$OUT/10-redirect.txt"); followed -> $(cat "$OUT/10-status.txt"); asset $asset -> $asset_code"
if grep -q '^200 text/html' "$OUT/10-status.txt" && [ "$asset_code" = 200 ]; then
  pass "10 Nitro UI + assets served on GET without a token"
else
  fail "10 Nitro UI not served"
fi

echo
echo "== summary"
cat "$OUT/summary.txt" | grep -E '^(PASS|FAIL)'
exit $FAILED
