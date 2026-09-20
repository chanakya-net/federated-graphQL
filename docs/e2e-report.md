# E2E report — 2026-09-18

## Metadata-driven timeline update — 2026-09-20

The timeline now discovers compatible sources from the generated, FAR-paired catalog. The gateway validates
the catalog against its loaded schema, and the UI builds source queries, filters, and detail rows at runtime.
See [Timeline source deployment](timeline-sources.md) for onboarding and the coordinated gateway restart.

Verified in the current checkout and rebuilt local Compose stack:

- Full .NET solution: **444 tests passed**. The final focused Gateway suite additionally passed **61/61**,
  including the last shared-type validation regression added during the full run.
- UI: **77 tests passed**, production build succeeded. Coverage includes an unfamiliar fourth source,
  metadata refresh after addition/removal, empty catalogs, partial errors, and duplicate detail labels.
- Temporary Compliance subgraph composition test: discovered and queried through the real gateway with
  authorization forwarded; no production Compliance registration or UI source branch was added.
- Schema/FAR/catalog regeneration: **no drift**. Both independent review passes closed with no findings.
- `scripts/e2e.sh`: **29/29 passed in 65 seconds**, including the generic contract, inclusive date bounds,
  tenant isolation, source-specific authorization, and normalized timeline degradation during an outage.
- Browser: Alice **39 events**, generic Software Install detail rows, Bob **19 events** with only Software
  Install denied; no console warnings/errors. Local gateway and UI rebuilt; outage services restored.
- Re-recorded normalized fixtures and mock smoke: Alice counts **5/20/14**, Bob denial on the advertised
  Software Install alias, August range **0/3/2** with every returned timestamp inside the bounds.

## Server-side search update — 2026-09-20

The finder now uses one `FindDevices` operation. DeviceSearch calls domain APIs directly, combines full
ID sets, sorts and paginates on the server, and requests page-scoped events. ID-only discovery skips event
storage reads in Patch, Vulnerability, and SoftwareInstall. Fusion completes Device properties from Device Directory.

Verified on the rebuilt local Compose stack:

- `scripts/e2e.sh`: **28/28 passed**, 65 seconds. Includes required-source failure (even for OR), tenant and permission checks, and final search pagination.
- **2 focused live gateway integration tests passed**: exact server AND/OR counts and page IDs checked against independent domain results, plus caller permissions and TenantB isolation.
- Patch `patch-0128` AND CVE `CVE-2026-10166`: **7 matches**. OR: **471 matches**, 25 rows/page.
- Browser: complete device names/events, OR second page **26–50 of471**, one proxy POST for Find (observed count4→5), no console warnings/errors.
- **279 backend unit tests**, **125 UI tests**, production UI build, full solution build, schema composition and drift checks passed. Full Patch54/Vulnerability61 suites and SoftwareInstall47 unit tests cover the domain discovery changes.
- The first parallel solution build hit an MSBuild child-node termination; `-m:1` completed with0 warnings/errors without source changes.

Pagination uses fresh offset reads; it does not promise a snapshot across requests. Explicit search limits
fail with errors rather than incomplete results. Earlier sections below describe the prior phase checkpoints.


Commit: `7083819` plus the Phase 6 changes committed with this report (`scripts/e2e.sh`, `docs/`, README, and a
one-line gateway option in `src/Gateway/Program.cs`).
Machine: macOS 27.0 (arm64), OrbStack with Docker Engine 29.4.0 (VM: 2 CPUs, 11.7 GiB), Docker Compose v5.1.2,
`/bin/bash` 3.2.57, jq 1.7.1, curl 8.7.1.
Cold start to healthy: **1:40** (build without layer cache 67 s + `scripts/up.sh` 33 s on fresh volumes).

## Fresh-clone test (phase-6 §3)

This machine already runs the stack (compose project `sor-poc`). So the fresh clone ran under its own project name
and ports, which gives new containers and empty volumes, so all four data stores seed from scratch:

```bash
git clone <repo> sor-fresh && cd sor-fresh      # + the uncommitted Phase 6 changes applied as a patch
export COMPOSE_PROJECT_NAME=sor-fresh GATEWAY_PORT=5051 UI_PORT=4201
docker compose build --no-cache                 # timing only: forces a cold image build
scripts/up.sh                                   # the single command
scripts/e2e.sh
```

| Run | Code | Image build (`--no-cache`) | `scripts/up.sh` to all healthy | Total | e2e |
|---|---|---|---|---|---|
| 1 | `7083819` + `scripts/e2e.sh` (20 scenarios) | 99 s | 28 s | 2:07 | 20/20 |
| 2 | `7083819` + all Phase 6 changes | 67 s | 33 s | **1:40** | **21/21** |

- Pass: every service was healthy well within 5 minutes, and nothing was run by hand between `up.sh` and `e2e.sh`.
- `http://localhost:4201/tokens.json` (served by the UI's Nginx) listed the five users: alice, bob and carol in
  TenantA; dave and erin in TenantB, each with the services in `contracts/tokens.json.md`. The user switch itself was
  checked in the browser on the main stack (below).
- Not measured: pulling base images. `--no-cache` disables the layer cache, but the base images (.NET SDK and
  runtime, Node, Nginx, Postgres, Mongo, Azurite) were already on this machine. A machine that has never pulled
  them also has to pull them: about 2.8 GB unpacked on disk here (the .NET SDK 974 MB and mongo 830 MB are most
  of it). The compressed download is smaller.
- Both scratch stacks were removed afterwards (`docker compose down -v --rmi local`).

## Scripted scenarios

`scripts/e2e.sh` output, fresh clone, run 2:

```text
e2e: gateway http://localhost:5051/graphql, ui http://localhost:4201, subgraph timeout 5s
PASS alice_full_timeline
PASS alice_counts_in_seed_range  (patch 5, vulnerability 14, install 20)
PASS bob_partial_access
PASS carol_single_service
PASS dave_cross_tenant_null_no_error
PASS dave_own_tenant_ok
PASS erin_patch_only
PASS unauthenticated_401  (no token and a tampered token)
PASS patch_stopped_degrades_only_patch  (code: none)
PASS patch_stopped_is_fast  (0.014821s)
PASS patch_restored
PASS patch_paused_times_out_cleanly  (5.013595s)
PASS patch_unpaused
PASS vulnerability_stopped  (degraded, then restored)
PASS software_install_stopped  (degraded, then restored)
PASS bob_with_software_install_down  (code: none)
PASS device_directory_down_fails_whole_query  (HTTP 200 while down, full timeline after restore)
PASS search_scoped_to_tenant
PASS lookup_hidden_on_gateway  (Query fields: cves, device, devices, patches; deviceById -> HTTP 400)
PASS since_until_pushdown  (2026-03-05T00:00:00Z .. 2026-08-02T00:00:00Z: patchEvents 3, vulnerabilityEvents 9, installEvents 8)
PASS query_plan_fans_out  (DeviceDirectory 2 ms, Patch 3 ms, SoftwareInstall 3 ms, Vulnerability 3 ms; total 6 ms)
e2e: 21/21 passed in 52s
```

The same 21/21 result (52 s) on the long-running `sor-poc` stack, which the script left fully healthy.

Checks on the script itself:

- **Failure path.** With the expected timeout forced wrong (`SUBGRAPH_TIMEOUT_SECONDS=2` in the shell, gateway
  still at 5 s), the run printed `FAIL patch_paused_times_out_cleanly` along with the response. It exited 1,
  and the EXIT trap unpaused `patch`, which came back healthy.
- **No gateway.** Pointing at a port with no gateway fails scenario 1 with `HTTP 000` and exits 1. With no UI,
  the preflight exits 2.
- **Assertion helpers.** `full`, `ok`, `down`, `denied_at` and the plan filter were each evaluated against the
  recorded gateway responses in `ui/src/testing/fixtures/gateway/` and against mutated copies. Every case
  returned the expected true or false.

Several spec assertions were tightened because they passed on empty lists. See
[version-facts §8](version-facts.md#8-deviations-append-only-all-phases), P6 rows.

## Manual UI checklist

Done in the in-app browser against `http://localhost:4200` (main stack, same code as run 2).

- [x] User switch lists five users with tenant and services; selection persists across reload. *Alice (Tenant A)
  TenantA patch vulnerability softwareinstall* … *Erin (Tenant B) TenantB patch*. After picking bob and
  reloading, bob was still selected (`localStorage['sor.user'] = "bob"`).
- [x] As alice, search `dev-000` returns TenantA devices only: "100 devices", all `dev-000xx`, header "in TenantA".
  Opening `dev-00001` shows three sections (Patch 5, Vulnerability 14, Software Install 20) and "39 of 39 events,
  newest first", with dates descending from Aug 25, 2026 to Sep 1, 2025. Filters:
  - Type: Patch off gives 34 of 39, only vulnerability and install rows.
  - Text: `log4j` gives 4 of 39, case-insensitive.
  - Status: SUCCESS gives 17 of 39.
  - Date range: 3/1/2026 – 6/30/2026 sends a new server query and shows Patch 2, Vulnerability 9, Install 6
    (17 events).
  - Reset clears all of them.
  - Note: the typed date range was applied when focus left the field. Pressing Enter in the *To* field did not
    apply it under browser automation, where synthetic key events may not fire the native `change` event. Check
    Enter by hand once (see follow-ups).
- [x] As bob, Software Install shows the no-access state: a `lock` icon, grey card, and "You don't have access to
  Software Install data." Patch and Vulnerability render.
- [x] As carol, only Software Install renders (20 events). Patch and Vulnerability both show the lock card.
- [x] As dave, `dev-00001` shows "Device dev-00001 not found in TenantB" ("It does not exist in this tenant, or it
  belongs to another one. The gateway gives the same answer either way."). Searching `dev-0` shows "3,000
  devices", all `dev-07xxx`–`dev-09xxx`. `dev-07000` renders all three sections (6 / 17 / 8 events, TenantB).
- [x] `scripts/demo-outage.sh patch stop`: as alice, Patch shows the unavailable banner (`warning` icon, red):
  "Patch service is currently unavailable — patch history is not shown." plus "Gateway error: Unexpected
  Execution Error". The other sections are intact (34 of 34). After `restore`, a refresh brings Patch back (5
  events).
- [x] `scripts/demo-outage.sh patch pause`: placeholder rows and a progress bar while waiting, then the same banner
  at **5.1 s** after navigation. The page never hung.
- [x] `scripts/demo-outage.sh device-directory stop`: the timeline shows a global error card, "Device directory
  unavailable", with an explanation and *Retry* (not a blank page). The search page shows "Device directory
  unavailable" with *Retry*. After `restore`, *Retry* reloads the 7,000 devices.
- [ ] Nitro with alice's token: the tracer query works and the query plan view shows the fan-out. **Partly done.**
  - Done: Nitro loads at `http://localhost:5050/graphql/`.
  - Done: the data Nitro's plan view renders was checked over HTTP. Nitro sends `Fusion-Operation-Plan: 1`, and the
    gateway now returns `extensions.fusion.operationPlan`: DeviceDirectory (no dependencies), then Patch,
    Vulnerability and SoftwareInstall, each depending only on it. Scenario `query_plan_fans_out` asserts this.
  - Not done: pasting the token into Nitro's connection headers and opening the plan view. A human has to
    do that step (`docs/demo.md` "Before the audience arrives").

## Deviations observed

All recorded in [`docs/version-facts.md` §8](version-facts.md#8-deviations-append-only-all-phases), P6 rows:

1. **Gateway: Nitro's query plan view did not work.** Fusion 16.6.6 returns the plan only when
   `FusionRequestOptions.AllowOperationPlanRequests` is true, and it is false by default.
   `CollectOperationPlanTelemetry` alone, which P4 set "for the query plan view in Nitro", is not enough. Phase 6
   set the option in `src/Gateway/Program.cs` (P4-owned). Gateway unit tests pass 34/34, and a new e2e scenario
   covers it.
2. **Spec helpers.** The gateway port default is 5050 and is read from `.env`. The empty-array expansion is made
   safe for bash 3.2 under `set -u`: the spec's `"${auth[@]}"` fails there with `unbound variable`.
3. **Scenario 20 was vacuous.** `since` / `until` are ISO `DateTime`s, and `dev-00001` has no patch event in the
   last 30 days before the seed epoch. The scenario now uses a 150-day window, compares exact id sets on all three
   domains, checks a `+05:30` offset, and checks inclusive bounds.
4. **Scenario 18 was vacuous.** Dave's `dev-00` search returns nothing. The scenario now uses exact tenant counts
   (dave 3,000, alice 7,000, dave `dev-00` 0).
5. **Scenario 16 records no error code** (the outage shape). The UI's "unavailable" is the right text.
   **Scenario 17** answers HTTP 200 with `device: null` and one error.
6. **The fresh clone needs `COMPOSE_PROJECT_NAME`** on a machine that already runs the stack (`name: sor-poc` is
   fixed).
7. **Demo step 8 said "eleven containers".** The stack has ten services: nine running, plus the `token-generator`
   one-shot.

## `⚠️` audit (phase-6 DoD)

| Where | Marker | Resolution in `docs/version-facts.md` |
|---|---|---|
| phase-0 l.43 | Nitro CLI package id | §1: `ChilliCream.Nitro.CommandLine` 16.6.6, command `nitro` |
| phase-0 l.141 | namespace / package for `[Lookup]` / `[Internal]` | §2: `HotChocolate.Types.Composite` in `HotChocolate.Types`; §8 P0 row |
| phase-0 l.152 | source-schema registration call | §2: `builder.AddGraphQL("<SourceSchemaName>")`, no `.AddSourceSchema()`; §8 P0 row |
| phase-0 l.171 | lookup attribute name | §2: `[Lookup]` |
| phase-0 l.186 | source-schema package | §1: none needed |
| phase-0 l.218 | `[Lookup, Internal]` not client-callable | §2 experiment 2; e2e `lookup_hidden_on_gateway` |
| phase-0 l.254 | gateway registration | §4 snippet; §8 P0 row (`AddFileSystemConfiguration`) |
| phase-0 l.257 | error-handling mode option | §6: `ErrorHandlingMode.Propagate` / `Null`, no `Halt` |
| phase-0 l.286 | `IHttpClientFactory` by source-schema name | §5, including the `"fusion"` default-client pitfall |
| phase-0 l.294 | compose flags | §3 verified invocation; §8 P0 row |
| phase-4 l.111 | compose invocation | §3; `scripts/compose-schema.sh` (P4 rows) |

No open `⚠️` is left. (`phases/00-execution-plan.md` l.9 only explains the convention.)

## Known issues / follow-ups

- **Plan requests are open to any authenticated caller.** `AllowOperationPlanRequests = true` exposes subgraph
  names, internal URLs and variables. That is fine for this dev-only POC. Turn it off, or gate it per request,
  anywhere real.
- **Date-range Enter.** Confirm by hand that pressing Enter in the *To* date field applies the range. Blur does.
- **CI does not run `scripts/e2e.sh`.** A job would need the compose stack (about 2 minutes cold, then about 1
  minute of e2e) on a Docker-capable runner.
- **Device Directory is a single point of failure**, as designed and documented. Scenario 17 pins the behaviour.
- **`docs/demo.md` walkthrough** by someone other than its author is still to do (phase-6 DoD).

## Sign-off

- [x] All scripted scenarios pass on a fresh clone: 21/21, run 2 above. The spec's 20, plus
  `query_plan_fans_out`.
- [ ] Manual checklist complete. Everything except the Nitro token and plan-view step, which needs a human.
- [x] `scripts/check-schema-drift.sh` passes on the Phase 6 tree ("no drift"; the gateway change does not touch
  any schema). Re-run it on the signed commit.

Signed: ________________, ____-__-__

## Addendum, 2026-09-20: Find devices (reverse lookups)

Re-run on the rebuilt stack after adding the reverse lookups (`contracts-v2`): `scripts/e2e.sh` **25/25 passed in
53 s**, the four new scenarios being `find_by_patch_federates_via_device_directory` (732 TenantA devices with
patch-0128 or patch-0282, every row completed with Device Directory's fields, `dev-00001` among them),
`find_plan_completes_stubs_in_one_directory_call` (plan: Patch 22 ms, then one DeviceDirectory node 95 ms
depending on it), `find_denied_without_the_service` (carol: `null` + `AUTH_NOT_AUTHORIZED` at
`["devicesWithPatches"]`) and `find_scoped_to_tenant` (dave: 551 TenantB devices, none of TenantA).
`Gateway.Tests` `StackTests` 7/7 (incl. `Reverse_lookup_federates_patch_and_device_directory`); the three subgraph
integration suites 23 + 27 + 31 passed (Testcontainers); `scripts/check-schema-drift.sh`: no drift; UI 118/118.

Manual UI check (mock gateway, then the live stack on :4300): the Find page lists the three pickers, the Patch
picker searches its catalog as you type (debounced, "No patch matches" for an empty search), a picked item
becomes a chip, **Find devices** writes `?patch=<id>` to the URL and renders the Patch result table with one
chip per matching event; a row opens the device timeline and *back* restores the results.

## Addendum, 2026-09-20 (later): AND / OR expressions (contracts-v3)

`scripts/e2e.sh` **26/26 passed in 53 s** with `find_and_across_subgraphs_via_device_sets` (patch-0128 AND
CVE-2026-10166: the two device sets intersected client-side give 7 devices; the page fetched with `deviceIds`
returns exactly them, each with both a patch event and a CVE event, completed by Device Directory). Subgraph
integration suites 25 + 28 + 32, `Gateway.Tests` stack tests, `check-schema-drift.sh` "no drift", UI 124/124
(expression evaluator, URL round trip, page assembly for OR and AND, denial, outage, builder, paging).

## Addendum, 2026-09-20: Server search with registered providers

This supersedes the client-intersection behavior described above. The live finder now loads provider
metadata/catalogs and sends one `findDevices` request for matching and pagination. Exactly three domain
providers are registered: Patch, Vulnerability and Software Install.

Validation on the rebuilt stack:

- `scripts/e2e.sh`: **28/28 passed in 65 s**, including required-source outage failing the whole search.
- Gateway non-integration suite: **44/44**; focused live search/catalog stack tests: **3/3**.
- Backend non-integration solution run: **293 passed**; six additional provider regressions then passed in
  the final **52/52 DeviceSearch** run (299 distinct backend tests checked across these runs).
- UI: **130/130** tests and production build passed; common API fixtures recorded against the live gateway.
- Schema export/composition passed; `scripts/check-schema-drift.sh` reported no drift.
- Live browser: Patch AND Vulnerability returned **7** devices, OR returned **471**; Find increased the UI
  proxy request count from 10 to 11. Software catalog showed both any-version and exact-version options.
- Independent review completed with the mock software ordering mismatch fixed using recorded server options.

The provider extension guide is `docs/search-providers.md`. No new domain was added.
