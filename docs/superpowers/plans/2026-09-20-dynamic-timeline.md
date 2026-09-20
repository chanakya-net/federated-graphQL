# Dynamic device timeline implementation plan

> Execute with superpowers:subagent-driven-development; independent file ownership supports parallel work, then integrate and review.

**Goal:** Add compatible timeline sources without UI code changes or deployment.
**Architecture:** Normalize at domain boundary; generate validated capability catalog with FAR; UI builds queries and presentation from catalog. Configured gateway clients preserve auth forwarding.
**Tech Stack:** .NET / Hot Chocolate Fusion 16.6.6, Angular, Apollo, Vitest.
**Spec:** docs/superpowers/specs/2026-09-20-dynamic-timeline-design.md

## Global constraints
- Preserve source authorization, tenant boundaries, independent partial errors, ISO dates, and same /graphql URL.
- Source descriptor and event shapes are exactly those in spec; legacy domain fields remain for existing consumers.
- Use current checkout on codex/dynamic-device-timeline so user sees changes. Do not commit or publish without integration review.

## Task 1: Normalized domain fields
Files: src/Shared timeline types, three domain GraphQL/DeviceExtensions.cs, each domain timeline.json, domain schema/auth tests.
- [x] Add failing endpoint/schema assertions selecting normalized field, complete details, dates, denied and cross-tenant behavior.
- [x] Implement shared shareable output contract and adapters reusing domain storage/resolvers.
- [x] Write subgraph metadata for existing sources and run targeted domain tests.

## Task 2: Catalog and gateway registration
Files: composition/export scripts, src/Gateway, gateway project packaging, tests/Gateway.Tests, docker compose configuration, docs/timeline-sources.md.
- [x] Add failing tests for fourth configured client retaining auth/timeout; invalid descriptor/schema and mismatched artifact rejection.
- [x] Generate catalog from exported schemas and per-subgraph descriptors; discover composition inputs from schema settings rather than repeated lists.
- [x] Serve startup-validated catalog and use configuration-driven clients. Update deployment packaging/configuration.
- [x] Verify no source silently uses an unconfigured anonymous client; document restart update contract.

## Task 3: Generic UI
Files: ui/src/app timeline/graphql/core/features/device-timeline, tests/fixtures/mock gateway/recorder, proxies.
- [x] Add failing synthetic fourth-source test exercising generated query, cards, filters, details, date changes and partial errors.
- [x] Fetch/validate source catalog and generate typed GraphQL AST query; adapt query watcher to dynamic documents.
- [x] Replace domain mappings/static sources/statuses/detail switches with shared generic records; remove obsolete queries/types with reference checks.
- [x] Update fixtures/mock/recorder and tests, run full UI tests/build.

## Task 4: Integration and cleanup
- [x] Compose fresh schemas/FAR/catalog and verify schema drift check.
- [x] Run .NET suite and UI suite/build; test actual gateway normalized data and metadata.
- [x] Review whole diff for stale code, broken docs/scripts, auth regressions and deployment mismatch.
- [x] Resolve findings, verify final current checkout, summarize evidence and remaining limits.

Verification and review outcomes: `docs/e2e-report.md`, metadata-driven timeline update.
