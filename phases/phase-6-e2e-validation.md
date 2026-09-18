# Phase 6 — End-to-end validation, demo script, sign-off

**Sequential. Starts when every other phase is merged and `scripts/check-schema-drift.sh` passes.**
**Effort:** 1–2 days. **Owns:** `scripts/e2e.sh`, `docs/demo.md`, `docs/e2e-report.md`.

## 1. Purpose

Prove the plan's promises on a clean machine with one command: federation, tenant isolation, per-user service access, graceful degradation (stop and pause), the deliberate Device Directory single point of failure, and the UI states. Leave behind a repeatable script, a demo runbook, and a signed report.

## 2. Inputs

- Everything merged. `docs/version-facts.md`, `contracts/errors.md`, `tests/fixtures/`.
- Plan §5 acceptance tests, §7, §9 Phase 4 checklist.

## 3. Fresh-clone test (first, by hand)

On a machine or directory that has never built the project:

```bash
git clone <repo> sor-fresh && cd sor-fresh
scripts/up.sh          # single command; note the wall-clock time to "all healthy"
```

Pass: every service healthy within 5 minutes; `http://localhost:4200` shows the user switch with five users; no manual step was needed. Record the time in the report.

## 4. `scripts/e2e.sh`

Requirements: `bash` 3.2+, `curl`, `jq`, `docker compose`. Exits non-zero on the first failing scenario; prints `PASS <name>` / `FAIL <name>` per scenario; leaves the stack in the state it found it (restores any stopped/paused service).

```bash
#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
GW="http://localhost:${GATEWAY_PORT:-5000}/graphql"
UI="http://localhost:${UI_PORT:-4200}"
TOKENS=$(curl -sf "$UI/tokens.json")
tok() { echo "$TOKENS" | jq -r --arg u "$1" '.[] | select(.sub==$u) | .token'; }
Q='query($id:ID!){ device(id:$id){ id hostname tenantId patchEvents{ id } vulnerabilityEvents{ id } installEvents{ id } } }'
gql() {  # gql <token|-> <deviceId>  -> prints body; sets HTTP_CODE and SECS
  local auth=(); [ "$1" != "-" ] && auth=(-H "Authorization: Bearer $1")
  local out; out=$(curl -s -w '\n%{http_code} %{time_total}' "$GW" "${auth[@]}" -H 'Content-Type: application/json' \
      -d "$(jq -n --arg q "$Q" --arg id "$2" '{query:$q, variables:{id:$id}}')")
  HTTP_CODE=$(echo "$out" | tail -1 | cut -d' ' -f1); SECS=$(echo "$out" | tail -1 | cut -d' ' -f2); echo "$out" | sed '$d'
}
check() { if echo "$2" | jq -e "$3" >/dev/null; then echo "PASS $1"; else echo "FAIL $1"; echo "$2" | jq . ; exit 1; fi; }
restore_all() { for s in patch vulnerability software-install device-directory; do scripts/demo-outage.sh "$s" restore >/dev/null 2>&1 || true; done; }
trap restore_all EXIT
```

Scenarios (each a block after the helpers):

| # | Name | Steps | jq assertion |
|---|---|---|---|
| 1 | `alice_full_timeline` | `R=$(gql "$(tok alice)" dev-00001)` | `.data.device.id=="dev-00001" and .data.device.patchEvents!=null and .data.device.vulnerabilityEvents!=null and .data.device.installEvents!=null and ((.errors//[])\|length)==0` |
| 2 | `alice_counts_in_seed_range` | same `R` | `(.data.device.patchEvents\|length)>=5 and (.data.device.vulnerabilityEvents\|length)>=2 and (.data.device.installEvents\|length)>=3` |
| 3 | `bob_partial_access` | `R=$(gql "$(tok bob)" dev-00001)` | `.data.device.installEvents==null and .data.device.patchEvents!=null and .data.device.vulnerabilityEvents!=null and ([.errors[]\|select(.path[0]=="device" and .path[1]=="installEvents" and .extensions.code=="AUTH_NOT_AUTHORIZED")]\|length)==1` |
| 4 | `carol_single_service` | carol, dev-00002 | `.data.device.installEvents!=null and .data.device.patchEvents==null and .data.device.vulnerabilityEvents==null and ([.errors[]\|select(.extensions.code=="AUTH_NOT_AUTHORIZED")]\|length)==2` |
| 5 | `dave_cross_tenant_null_no_error` | dave, dev-00001 | `.data.device==null and ((.errors//[])\|length)==0` |
| 6 | `dave_own_tenant_ok` | dave, dev-07000 | `.data.device.tenantId=="TenantB" and .data.device.patchEvents!=null` |
| 7 | `erin_patch_only` | erin, dev-07000 | `.data.device.patchEvents!=null and .data.device.vulnerabilityEvents==null and .data.device.installEvents==null` |
| 8 | `unauthenticated_401` | `gql - dev-00001; [ "$HTTP_CODE" = 401 ]` | shell test, not jq |
| 9 | `patch_stopped_degrades_only_patch` | `scripts/demo-outage.sh patch stop`; alice dev-00001 | `.data.device.patchEvents==null and ([.errors[]\|select(.path[0]=="device" and .path[1]=="patchEvents" and .extensions.code!="AUTH_NOT_AUTHORIZED")]\|length)>=1 and .data.device.vulnerabilityEvents!=null and .data.device.installEvents!=null` |
| 10 | `patch_stopped_is_fast` | same call | `SECS < 3` (shell: `awk`) |
| 11 | `patch_restored` | `scripts/demo-outage.sh patch restore`; alice | as scenario 1 |
| 12 | `patch_paused_times_out_cleanly` | `scripts/demo-outage.sh patch pause`; alice | scenario 9 assertion **and** `SECS` between `${SUBGRAPH_TIMEOUT_SECONDS:-5}` and `+3` |
| 13 | `patch_unpaused` | restore; alice | as scenario 1 |
| 14 | `vulnerability_stopped` | stop/restore vulnerability | mirror of 9/11 for `vulnerabilityEvents` |
| 15 | `software_install_stopped` | stop/restore software-install | mirror for `installEvents` |
| 16 | `bob_with_software_install_down` | stop software-install; bob | `.data.device.installEvents==null and ([.errors[]\|select(.path[1]=="installEvents")]\|length)>=1` — record which code appears (denial is decided by the subgraph; when it is down expect a transport code) and make sure the UI text matches |
| 17 | `device_directory_down_fails_whole_query` | stop device-directory; alice | `.data.device==null and ((.errors//[])\|length)>=1`; restore |
| 18 | `search_scoped_to_tenant` | `devices(search:"dev-00", first: 5)` as dave | every `items[].tenantId=="TenantB"` |
| 19 | `lookup_hidden_on_gateway` | introspection `{ __type(name:"Query"){ fields { name } } }` | `[.data.__type.fields[].name] \| index("deviceById") == null` |
| 20 | `since_until_pushdown` | alice, `patchEvents(since: <epoch-30d>, until: <epoch>)` | all `occurredAt` within bounds |

Print a summary line `e2e: 20/20 passed` at the end.

## 5. Manual UI checklist (`docs/e2e-report.md` section)

Perform in a browser at `http://localhost:4200`; tick each:

- [ ] User switch lists five users with tenant and services; selection persists across reload.
- [ ] As alice: search "dev-000" shows TenantA devices only; open `dev-00001`; three sections render; merged timeline sorted newest first; type / date / status / text filters work.
- [ ] As bob: Software Install section shows the **no-access** state (lock icon, "You don't have access to Software Install data."); other two render.
- [ ] As carol: only Software Install renders; two no-access sections.
- [ ] As dave: `dev-00001` shows "Device not found in TenantB"; search shows TenantB devices; `dev-07000` works.
- [ ] `scripts/demo-outage.sh patch stop`: as alice the Patch section shows the **unavailable** banner (warning icon, "Patch service is currently unavailable — patch history is not shown."); other sections intact; `restore` brings it back on refresh.
- [ ] `scripts/demo-outage.sh patch pause`: same banner within ~6 s, page never hangs.
- [ ] `scripts/demo-outage.sh device-directory stop`: global error state, not a blank page; restore.
- [ ] Nitro UI at the gateway loads; with alice's token pasted as `Authorization` header the tracer query works and the query plan view shows the fan-out.

## 6. `docs/demo.md` — 10-minute runbook

1. `scripts/up.sh` (pre-warmed before the audience arrives; cold start is 2–5 min).
2. Open UI, pick alice, search, open a device: "one query, three backends, three database technologies". Show the Nitro query plan.
3. Switch to bob: "same query, Software Install denied by that subgraph, not by the gateway or the UI". Show the raw response in Nitro: `AUTH_NOT_AUTHORIZED` at the field.
4. Switch to dave, open `dev-00001`: "device does not exist for Tenant B; no leak, no error".
5. `scripts/demo-outage.sh vulnerability stop`, refresh as alice: "one subgraph down, the timeline keeps two thirds, banner says which third is missing".
6. `scripts/demo-outage.sh vulnerability pause`: "hung, not dead; the timeout bounds the damage".
7. `restore`. Optional: `device-directory stop` to show the documented single point of failure.
8. Close with `docker compose ps`: eleven containers, one command.

Include the exact commands and the expected screen for each step, and a "if it goes wrong" line per step (usually: refresh; check `docker compose ps`).

## 7. `docs/e2e-report.md` template

```markdown
# E2E report — <date>
Commit: <sha>   Machine: <os / docker version>   Cold start to healthy: <mm:ss>
## Scripted scenarios
<paste of scripts/e2e.sh output>
## Manual UI checklist
<the §5 list, ticked, with notes>
## Deviations observed
<anything that differs from the plan or contracts, with a link to the version-facts §8 entry>
## Known issues / follow-ups
<bullets>
## Sign-off
- [ ] All 20 scripted scenarios pass on a fresh clone
- [ ] Manual checklist complete
- [ ] `scripts/check-schema-drift.sh` passes on the signed commit
Signed: <name>, <date>
```

## 8. Definition of Done

- [ ] `scripts/e2e.sh` passes 20/20 on a fresh clone; output pasted into the report.
- [ ] Manual UI checklist complete with no unexplained failures.
- [ ] `docs/demo.md` walked through once end to end by someone other than its author.
- [ ] `docs/e2e-report.md` committed with sign-off.
- [ ] README updated: "Validate" section pointing at `scripts/e2e.sh` and "Demo" section pointing at `docs/demo.md`.
- [ ] Every open `⚠️` across `phases/` has a resolution recorded in `docs/version-facts.md`.
