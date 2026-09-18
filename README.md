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

`docker compose up --build` (added in Phase 2C). The gateway is published on host port **5050**
(`GATEWAY_PORT` in `.env`; macOS reserves 5000), the UI on 4200.

**After any subgraph schema change, run `scripts/compose-schema.sh` and commit `schemas/` and
`gateway/gateway.far`** (added in Phase 4).
