# SoR — Federated GraphQL POC

A proof of concept for a federated GraphQL graph on Hot Chocolate / Fusion v2 (.NET 10): three domain
subgraphs (Patch on MongoDB, Vulnerability on PostgreSQL, SoftwareInstall on Azurite) extend a `Device`
owned by a Device Directory subgraph, behind one Fusion gateway, with per-tenant isolation and per-user
service access enforced in every subgraph, and an Angular UI that tells "service down" apart from "no
access". Two ways in: a device's merged **timeline** (Device Directory first, then the three domains), and
**Find devices** (DeviceSearch evaluates an AND / OR expression of patches, CVEs and software on the
server, selects the page, and retrieves matching events; Fusion completes its Device references through
Device Directory before returning the final result).

## Where to read

| Document | What |
|---|---|
| [`federated-graphql-poc-plan.md`](federated-graphql-poc-plan.md) | The plan (v3): what and why |
| [`phases/00-execution-plan.md`](phases/00-execution-plan.md) | Phases, dependency graph, lanes, file ownership, operating rules |
| `phases/phase-*.md` | One implementation spec per phase |
| [`docs/version-facts.md`](docs/version-facts.md) | Pinned versions, verified commands, deviations. **Wins over the phase docs** |
| [`contracts/`](contracts/README.md) | Frozen SDL, HTTP/env, token, error and seeding contracts (tag `contracts-v1`) |
| [`spike/`](spike/README.md) | Phase 0 throwaway spike (not in the solution) |
| [`docs/demo.md`](docs/demo.md) | 10-minute demo runbook: commands, expected screens, recovery per step |
| [`docs/e2e-report.md`](docs/e2e-report.md) | Phase 6 end-to-end report: fresh-clone timing, scripted scenarios, manual UI checklist |

## Prerequisites

- **.NET SDK 10.0.400** or a later 10.0 feature band (`global.json`, `rollForward: latestFeature`).
- **Docker** (Docker Desktop or equivalent): the compose stack and the Testcontainers integration tests.
- **Node.js** (current LTS) for the Angular UI in `ui/`.
- `dotnet tool restore` installs the pinned Nitro CLI (`dotnet nitro`) used for offline schema composition.

## Build and test

```bash
scripts/build.sh          # dotnet build SoR.sln -c Release (warnings are errors)
scripts/test.sh unit      # unit tests only, no Docker needed
scripts/test.sh           # everything, including [Trait("Category","Integration")] tests (Docker)
```

`scripts/test.sh` runs with `--no-build`, so run `scripts/build.sh` first.

Device Directory (P2B): `dotnet test tests/DeviceDirectory.Tests -c Release` — needs Docker (Testcontainers `postgres:17-alpine`); add `--filter "Category!=Integration"` for the unit tests only.

Patch (P3A): `dotnet test tests/Patch.Tests -c Release` — needs Docker (Testcontainers `mongo:8`); add `--filter "Category!=Integration"` for the unit tests only (no Docker).

Vulnerability (P3B): `dotnet test tests/Vulnerability.Tests -c Release` — needs Docker (Testcontainers `postgres:17-alpine`, seeded once per run as `vuln_user`); add `--filter "Category!=Integration"` for the unit tests only.

SoftwareInstall (P3C): `dotnet test tests/SoftwareInstall.Tests -c Release` — needs Docker (Testcontainers `azurite:latest`, the compose image; a full 12 000-blob seed takes about 15 s); add `--filter "Category!=Integration"` for the unit tests only.

Gateway (P4): `dotnet test tests/Gateway.Tests -c Release --filter "Category!=Integration"` — no Docker (the real gateway against in-process fake subgraphs); without the filter, `StackTests` also run against the compose stack (`scripts/up.sh` minus the UI, `scripts/demo-outage.sh patch stop|pause`), which takes minutes on a cold start.

UI (P5): `cd ui && npm ci && npm test` — no Docker (Vitest + jsdom, recorded gateway responses); `npm run mock` + `npm start` serves the UI on http://localhost:4300 against a mock gateway. See [`ui/README.md`](ui/README.md).

## Layout

```
src/Shared.Seeding       canonical 12 000-device catalog + deterministic RNG (every subgraph seeds from it)
src/Shared.Auth          dev JWT validation, ServiceAccess policy, ICallerContext, DevTokenFactory
src/DeviceDirectory      Device owner subgraph (PostgreSQL)        — Phase 2B
src/Patch                Patch subgraph (MongoDB)                  — Phase 3A
src/Vulnerability        Vulnerability subgraph (PostgreSQL)       — Phase 3B
src/SoftwareInstall      SoftwareInstall subgraph (Azurite blobs)  — Phase 3C
src/DeviceSearch         cross-domain search coordinator (no database)
src/Gateway              Fusion v2 gateway                         — Phase 4
src/TokenGenerator       mints one JWT per dummy user              — Phase 2A
tests/*.Tests            one xunit project per src project; tests/fixtures holds Phase 0 gateway responses
contracts/               frozen contracts (Phase 1)
schemas/, gateway/       exported SDL and the composed gateway.far (Phase 4 scripts; contracts-v2/v3 = reverse lookups)
infra/, ui/              compose infrastructure (Phase 2C), Angular UI (Phase 5)
```

The services include their domain implementations; DeviceSearch coordinates reverse lookups through their APIs.

## Running the stack

One command, no pre-steps: `docker compose up --build`. The scripts wrap it:

```bash
scripts/up.sh                          # build + start everything, wait until healthy (cold start: several minutes)
scripts/up.sh postgres device-directory fusion-gateway token-generator angular-ui   # a subset (+ its dependencies)
scripts/wait-healthy.sh patch vulnerability         # wait for services (WAIT_TIMEOUT, default 420 s)
docker compose run --rm -T token-generator --user alice   # print one user's JWT (e.g. for Nitro)
scripts/demo-outage.sh patch stop      # outage demos: stop = connection error, pause = 5 s timeout
scripts/demo-outage.sh patch restore   # unpause/start and wait until healthy
scripts/reset.sh                       # docker compose down -v (asks first): wipes all seeded data
```

| What | Where |
|---|---|
| UI | http://localhost:4200 (`UI_PORT`) |
| Gateway | `POST http://localhost:5050/graphql` (`GATEWAY_PORT`; macOS reserves 5000). Needs `Authorization: Bearer <jwt>` |
| Nitro UI | `GET http://localhost:5050/graphql/` (put the JWT in the connection's headers) |
| Everything else | internal network only (`internal: true`, no host ports, no internet) |

Compose project `sor-poc`; all values come from the committed `.env` (dev-only). Every .NET service
image is rendered from `infra/docker/Dockerfile.template` into `src/<Name>/Dockerfile` (build
context = repo root). Postgres roles and schemas come from `infra/postgres/init/`, which runs only on
an empty `pgdata` volume. A healthy service is one whose `/health` answers 200 (seeding done).

### Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| service stays `starting` then `unhealthy` after ~2.5 min | seeding exceeded `start_period` | raise `start_period`; check `docker compose logs <svc>` for seed progress |
| `wget: can't connect` in healthcheck | app listening on a different port | `ASPNETCORE_URLS=http://+:8080` must be set; no `launchSettings.json` port override in Release |
| `IDX10720` at startup | signing key shorter than 32 bytes | fix `.env` |
| `password authentication failed for user devdir_user` | `pgdata` volume created before init script existed | `docker compose down -v` once |
| Azurite `400 InvalidHeaderValue` | client SDK newer than emulator API | `--skipApiVersionCheck` is set; update the image |
| gateway 401 on everything | header forwarding not configured or key mismatch | compare `DEV_JWT_SIGNING_KEY` across services in `docker compose config` |
| `required variable ... is missing a value` | `.env` missing or incomplete | restore the committed `.env` |
| `failed to read dockerfile: open Dockerfile: no such file or directory` | a lane has not added its `src/<Name>/Dockerfile` (or `ui/Dockerfile`) yet | start a subset: `scripts/up.sh <services...>` |
| `bind: address already in use` on 5050 / 4200 | host port taken | set `GATEWAY_PORT` / `UI_PORT` in the shell or `.env` |
| `token-generator` exits non-zero, `Permission denied` on `/tokens` | image runs as non-root and the fresh `tokens` volume is root-owned | create `/tokens` owned by `app` in the image before `USER app`, then `docker volume rm sor-poc_tokens` |
| `wait-healthy.sh` reports `FAILED <svc> exited(N)` | the container crashed or was stopped | `docker compose logs <svc>`; `scripts/demo-outage.sh <svc> restore` after a demo |

### Schema composition

The gateway serves the composed archive `gateway/gateway.far`, built offline from the four exported subgraph
schemas and copied into the gateway image as is (the image build never composes):

```bash
scripts/compose-schema.sh                  # export schemas/*.graphqls from the subgraph code, then compose gateway/gateway.far
scripts/compose-schema.sh --no-export      # compose the committed schemas/ only
scripts/check-schema-drift.sh              # re-export + compose; exit 1 if schemas/ or gateway.far changed (CI job schema-drift)
```

**After any subgraph schema change, run `scripts/compose-schema.sh` and commit `schemas/` and
`gateway/gateway.far`.** `schemas/<name>-settings.json` holds each source schema's name and in-compose URL; export
keeps it as committed. The gateway overrides the URLs from `SUBGRAPH_<NAME>_URL` and refuses to start if the
archive is missing or unreadable.

### Get a token

Stdout is the JWT only (users in `src/TokenGenerator/users.json`); `--help` lists all options:

```bash
set -a; . ./.env; set +a; TOKEN=$(dotnet run --project src/TokenGenerator -- --user bob)   # or: --tenant TenantB --services patch,softwareinstall
TOKEN=$(docker compose run --rm -T token-generator --user alice)                            # same, inside the stack
```

## Validate

`scripts/e2e.sh` checks the running stack end to end. Start it first with `scripts/up.sh`. The script needs
bash 3.2+, curl, jq and Docker Compose:

```bash
scripts/e2e.sh     # about 1 min; prints PASS/FAIL per scenario, ends with "e2e: 26/26 passed"
```

It covers federation (the query plan fans out after Device Directory), each demo user's access, tenant isolation,
401s, each domain service stopped (fails fast) and paused (bounded by the 5 s timeout), Device Directory down,
tenant-scoped search, the hidden `deviceById` lookup, `since`/`until` pushdown, and the reverse lookups (Patch
first, then one batched Device Directory completion; denial; tenant scoping; a patch AND a CVE through the
per-item device sets). It stops at the first failure and
exits 1 (2 if the stack is not up). It always restores every service it stopped or paused. Ports and the
timeout come from the shell, else `.env`. Results and the manual UI checklist are in
[`docs/e2e-report.md`](docs/e2e-report.md).

## Find devices (reverse lookups with AND / OR)

The UI's second page (`/find`, toolbar "Find devices") turns the graph around: build an expression from
patches, CVEs and software picked from their catalogs, joined by **AND** / **OR** (AND binds first, so
`A AND B OR C` is `(A AND B) OR C`), press **Find**, and get one row per matching device of your tenant with
what matched in each source, linking to the device's timeline. The pickers search the catalogs
(`patches(search:)`, `cves(search:)`, `software(search:)`); the expression is evaluated in two steps because
the gateway can join entities but cannot intersect result sets across subgraphs:

```graphql
# 1. per category in the expression: the device-id set of every item (ids only, sorted)
query { devicesWithPatches(patchIds: ["patch-0128", "patch-0282"], first: 1) { matches { patchId deviceIds } } }
# the browser evaluates AND / OR over those sets, sorts and pages the ids, then
# 2. per category: the events of exactly the visible devices, whose stubs the gateway completes via Device Directory
query { devicesWithPatches(patchIds: ["patch-0128"], deviceIds: ["dev-00001", "dev-00013"], first: 25) {
  totalCount items { device { id hostname os } events { occurredAt status patch { kbId } } } } }
```

`devicesWithCves(cveIds:)` and `devicesWithSoftware(software: [{ name: "Git", version: null }])` work the same.
The domain subgraph answers with `Device` stubs (`id` only); the gateway completes `hostname`, `os`, ... with
**one** variable-batched `device(id)` call to Device Directory (`docs/version-facts.md` §8, 2026-09-20).
Access and tenant rules are the timeline's: a user without the service gets `null` plus `AUTH_NOT_AUTHORIZED`
for that category only (its filters count as matching nothing and the UI says so), a stopped subgraph gets
"unavailable", and with Device Directory down the sets still work but no page can be completed. Selections are
capped at 50 keys per query and the expression at 20 filters. Patch and Vulnerability answer by index
(`{tenantId, patchId, deviceId}` in MongoDB, `(tenant_id, cve_id, device_id)` in PostgreSQL); SoftwareInstall
keeps a reverse index blob (`_index/software.json`) that the seeder writes and an older container rebuilds from
its device blobs at startup.

## Demo

Follow [`docs/demo.md`](docs/demo.md): about 10 minutes, with the stack started beforehand. It goes alice (one
query, three backends, Nitro query plan), bob (Software Install denied by that service), dave (another
tenant's device is simply not found), a domain service stopped and then paused, and optionally the Device
Directory single point of failure.

## Server-side cross-domain search

The Find devices page sends one `findDevices(filters, first, offset)` operation. DeviceSearch retrieves
complete matching IDs from the selected domain APIs, evaluates AND/OR (AND binds tighter), sorts by device ID,
and paginates the combined result. It requests only that page's matching event details. Fusion enriches the
returned Device references from Device Directory before responding. Catalog picker requests stay independent.

The search service registers providers for Patch, Vulnerability and Software Install. Each owns its filter
keys, permission, domain queries and result mapping; the shared engine owns combination and pagination.
The finder builds its pickers from `searchCapabilities` and loads options through `searchCatalog`.
Capability availability reflects the caller's permissions, not a live health check. Catalogs are bounded
typeahead results (at most `first` options), including software any-version and exact-version choices.

To support another domain later, implement and register a provider and configure its endpoint, then rebuild
and deploy. The engine and finder need no domain-specific edits when it uses the existing catalog control.
A different input control still needs UI support. Federation registration remains a separate step; this
registry does not automatically discover arbitrary subgraphs. No additional domain is included here.
See [the provider extension guide](docs/search-providers.md) for the adapter contract and registration steps.

A required source denial, outage, malformed page, or work-limit failure returns a search error; it never
turns into a successful empty or partial result. Matching means historical event/finding presence, preserving
the existing filters. Offset pages are fresh reads; they are not a snapshot across services or requests.

Run `dotnet test tests/DeviceSearch.Tests -c Release` for orchestration/transport regressions, and
`dotnet test tests/Gateway.Tests -c Release --filter Category!=Integration` for gateway composition and
entity enrichment. See [the search contract](contracts/device-search.graphqls) and [demo](docs/demo.md).
