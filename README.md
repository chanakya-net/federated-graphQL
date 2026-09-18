# SoR — Federated GraphQL POC

A proof of concept for a federated GraphQL graph on Hot Chocolate / Fusion v2 (.NET 10): three domain
subgraphs (Patch on MongoDB, Vulnerability on PostgreSQL, SoftwareInstall on Azurite) extend a `Device`
owned by a Device Directory subgraph, behind one Fusion gateway, with per-tenant isolation and per-user
service access enforced in every subgraph, and an Angular timeline UI that tells "service down" apart
from "no access".

## Where to read

| Document | What |
|---|---|
| [`federated-graphql-poc-plan.md`](federated-graphql-poc-plan.md) | The plan (v3): what and why |
| [`phases/00-execution-plan.md`](phases/00-execution-plan.md) | Phases, dependency graph, lanes, file ownership, operating rules |
| `phases/phase-*.md` | One implementation spec per phase |
| [`docs/version-facts.md`](docs/version-facts.md) | Pinned versions, verified commands, deviations. **Wins over the phase docs** |
| [`contracts/`](contracts/README.md) | Frozen SDL, HTTP/env, token, error and seeding contracts (tag `contracts-v1`) |
| [`spike/`](spike/README.md) | Phase 0 throwaway spike (not in the solution) |

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

## Layout

```
src/Shared.Seeding       canonical 12 000-device catalog + deterministic RNG (every subgraph seeds from it)
src/Shared.Auth          dev JWT validation, ServiceAccess policy, ICallerContext, DevTokenFactory
src/DeviceDirectory      Device owner subgraph (PostgreSQL)        — Phase 2B
src/Patch                Patch subgraph (MongoDB)                  — Phase 3A
src/Vulnerability        Vulnerability subgraph (PostgreSQL)       — Phase 3B
src/SoftwareInstall      SoftwareInstall subgraph (Azurite blobs)  — Phase 3C
src/Gateway              Fusion v2 gateway                         — Phase 4
src/TokenGenerator       mints one JWT per dummy user              — Phase 2A
tests/*.Tests            one xunit project per src project; tests/fixtures holds Phase 0 gateway responses
contracts/               frozen contracts (Phase 1)
schemas/, gateway/       exported SDL and the composed gateway.far (Phase 4 scripts)
infra/, ui/              compose infrastructure (Phase 2C), Angular UI (Phase 5)
```

The subgraph, gateway and token generator projects are Phase 1 skeletons (they start and answer
`/health`); each lane replaces its own skeleton.

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
