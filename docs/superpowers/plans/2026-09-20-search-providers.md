# Registered device-search providers

Approved scope: refactor existing Patch, Vulnerability and Software Install support. Add no domain or subgraph. Preserve server-side complete-ID AND/OR search, paging, tenant/permission isolation, limits and atomic errors. Preserve existing dirty checkout; no commits or resets.

## Contract

- Provider owns category, required permission, metadata, key normalization/validation, discovery, detail and catalog mapping.
- Registry resolves category; rejects duplicates at startup and unknown categories as BAD_USER_INPUT. Engine and HTTP transport contain no domain switch.
- Shared HTTP transport retains request-local caller JWT, fixed configured URLs, deadlines, response limits and strict discovery/detail validation.
- `searchCapabilities: [SearchCapability!]!` returns category, name, icon, color, placeholder, filterKind (`catalog`), available (caller permission, not a health probe).
- `searchCatalog(category: String!, search: String, first: Int! = 25): [SearchCatalogItem!]` returns key, label, detail (all nonnull strings). It is a bounded typeahead, no offset. Provider may expand software catalog records into any-version and exact-version options, returning at most first options. Required permission checked before outbound request.
- `findDevices` stays unchanged except DeviceSearchEvent.occurredAt becomes nullable so provider summaries need not invent historical timestamps. Current providers still return actual event timestamps.
- UI loads capabilities and uses common catalog operation, generic metadata for chips and columns, string category keys. Keep legacy URL aliases patch/cve/sw; accept canonical category tokens so extension does not require a new allowlist. Unknown providers must surface a server error rather than silently drop filters.
- Only the three existing providers registered. Adding a future provider needs its adapter, registration/configuration, rebuild and deployment; no engine edits for the same catalog control shape.

## Tasks

1. Backend: extract provider contract/registry/three providers/shared transport; capabilities and catalog roots; focused tests for permissions, routing, duplicate/unknown registrations, normalization, nullable timestamp and bounded catalogs.
2. UI: metadata-driven catalogs/chips/table; generic URL/category support; adapt fixtures/mock/recorder and tests; preserve one findDevices request per Find/page.
3. Integration: regenerate and compose schema, update docs, run backend/UI/gateway checks and live smoke tests; independent review and address findings.

## Progress

- Planning, implementation and verification complete.

- Backend complete: three registered adapters, generic engine/transport, metadata/catalog roots, nullable
  summary timestamp; 52 focused tests passed. Test-only summary adapter exercises extension behavior.
- UI complete: metadata-driven controls and generic categories, legacy URL compatibility, per-user labels,
  nullable timestamps; 130 tests and production build passed. Live common API fixtures recorded.
- Integration complete: composed schema, no drift, 44 gateway tests, 3 live stack checks, 28 E2E scenarios.
- Independent review clean after fixing mock software option ordering; mock now consumes recorded options.
- Changes remain in the existing checkout without commits. No new production domain or subgraph added.
