# Server-side Device Search Implementation Plan

> **For agentic workers:** Use superpowers:subagent-driven-development for the independent backend and UI tasks; controller owns integration and final verification.

**Goal:** One browser FindDevices request returns final server-filtered and paginated devices with matching events.
**Architecture:** DeviceSearch calls domain subgraph APIs for IDs and page details; Fusion resolves Device properties from Device Directory. Existing domain databases and catalog queries remain in place.
**Tech Stack:** .NET 10, Hot Chocolate/Fusion 16.6.6, Angular 22, xUnit/Vitest.
**Spec:** docs/superpowers/specs/2026-09-20-device-search.md

## Global Constraints

- Exact public contract and semantics are in the spec.
- Preserve existing uncommitted work in this checkout; no staging, commits, reset, stash, or new worktree.
- Authentication and tenant context must remain caller-scoped on every internal call.
- No browser set evaluation or pagination of matching IDs.
- Never present incomplete/truncated required-source results as complete.
- DeviceSearch calls domain APIs directly; gateway performs entity enrichment.

### Task 1: Backend search with regression tests

Files: create src/DeviceSearch/{Program.cs,DeviceSearch.csproj,GraphQL/*,Search/*,Transport/*,Dockerfile}; tests/DeviceSearch.Tests/*.
Interface: implement findDevices and types exactly as spec. HTTP client uses SUBGRAPH_PATCH_URL, SUBGRAPH_VULNERABILITY_URL, SUBGRAPH_SOFTWAREINSTALL_URL and a bounded per-call timeout.
- [x] Write failing tests for AND/OR precedence, pagination after intersection, empty/unknown keys, complete discovery beyond a domain page, tenant/auth forwarding, denied/down sources, and details scoped to final page.
- [x] Run dotnet test tests/DeviceSearch.Tests and confirm missing implementation failures.
- [x] Implement search, validation, transport, event mapping, GraphQL endpoint/export, and Dockerfile.
- [x] Run focused tests and record results. Review service code before integration.

### Task 2: Finder UI and mocks

Files: ui/src/app/features/device-finder/*, ui/src/app/finder/expression*, ui/src/app/graphql/{types,operations}.ts, ui/src/testing/{mock-gateway,record-fixtures}.mjs, relevant fixtures and docs.
Interface: send filters (existing lowercase fields), first=25, offset=page*25. Read result items/events directly; group events into display cells only.
- [x] Write failing UI test proving one FindDevices request and rendering of server-selected rows/count; no set/detail operations.
- [x] Replace browser evaluation and slicing with result rendering and unified failure/retry UI.
- [x] Update mock and fixture support with server-side mock evaluation, preserving catalog functionality.
- [x] Run UI tests/build; record results.

### Task 3: Integration and final verification

Files: SoR.sln, docker-compose.yml, src/Gateway/Transport/SubgraphClientNames.cs, scripts/{export-schemas,compose-schema,up,e2e}.sh as needed, schemas/device-search*, contracts/device-search*, tests/Gateway.Tests/*, docs/demo.md, docs/version-facts.md, contracts/http-and-env.md, README.md.
- [x] Add service to solution, Compose, gateway client list, and offline export/composition.
- [x] Add gateway regression verifying one findDevices query enriches returned Device IDs; update schema/transport inventory expectations.
- [x] Export schemas, compose archive, run focused backend/gateway and UI verification.
- [x] Exercise live patch AND CVE search and verify results/page count plus browser request flow when possible.
- [x] Run schema drift check and independent final review; resolve findings.

## Execution notes

Working in-place follows the user's request to replace current uncommitted behavior. Offset pagination preserves existing URLs; snapshots and hostname sorting remain outside this change. Record validation evidence here as work completes.


## Verification completed

- Independent review closed two findings: duplicate overlapping software events (dedup by source + event ID), and eager domain detail reads during discovery (selection-aware guards in all three domains).
- Exported schema and contract composition both pass; `scripts/check-schema-drift.sh` reports no drift.
- Full solution build: `dotnet build SoR.sln -c Release --no-restore --nologo -m:1`, 0 warnings/errors. Initial parallel build encountered MSBuild child-node termination; serial retry passed without source changes.
- `dotnet test SoR.sln -c Release --no-build --filter 'Category!=Integration' --nologo -m:1`: 279 tests across 9 projects passed.
- Domain implementation validation: full Patch54 and Vulnerability61 tests passed; SoftwareInstall47 non-integration tests passed, including 8 selection regressions. DeviceSearch33 tests passed.
- UI125 tests and production build passed; six new fixture responses recorded from the live gateway using `--search-only`.
- `scripts/e2e.sh`: 28/28 passed in65s, including final search pagination, permission/tenant checks, and required-source failure inside OR.
- Two focused live Gateway FindDevices integration tests passed, comparing server AND/OR counts and offset pages to independent domain results and checking caller scope.
- Browser verified AND7 rows, OR471 results, next page26–50/471, no warning/error console messages. A Find click incremented the UI proxy POST count from4 to5: one search request.
- Rebuilt local containers are healthy. Changes remain uncommitted in the user's working checkout.
