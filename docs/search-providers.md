# Extending device search

DeviceSearch registers three providers: Patch, Vulnerability and Software Install. The browser sends its
filter expression to `findDevices`; the server discovers complete matching IDs, combines them, sorts and
pages the final set, then retrieves details for that page. Fusion completes the Device fields.

`ISearchProvider` in `src/DeviceSearch/Providers/ISearchProvider.cs` is the extension contract. Its adapter
supplies the category, required permission, picker metadata, key normalization, configured endpoint and
GraphQL request shapes, and maps domain responses into common search summaries/catalog options.
`CatalogSearchProvider` shares the existing domains' reverse-lookup envelope. A provider with a different
query shape can implement `ISearchProvider` directly and alias its response fields to the shared envelope.
Discovery responses need `result { totalCount items { device { id } } }`; detail responses add `events`.
Catalog responses use the `result` alias for a list. The adapter maps each domain detail to a summary;
`occurredAt` may be null when a timestamp does not apply. Canonical device IDs must match Device Directory.

The registry resolves categories and rejects duplicate registration during startup. The engine uses each
provider's required permission before any source calls. The shared transport forwards the caller JWT on
each request to the configured endpoint, checks complete pages and details, and enforces time/size limits.
The registry and adapters are stateless; tenant and caller information remain request-local.

To add support later:

1. Implement the provider contract (or derive from the common catalog base) for that domain's existing API.
2. Register it as `ISearchProvider` in `src/DeviceSearch/Program.cs`, and configure its endpoint. Use a stable
   lowercase category token with letters, numbers, hyphens or underscores; keep legacy URL aliases reserved.
3. Return capability metadata with `filterKind: "catalog"` and normalized catalog options. The generic
   finder renders its picker, chips and result column from this metadata. A different filter control needs
   corresponding UI implementation.
4. Test discovery completeness, canonical identity, key normalization, permission denial, tenant scoping,
   catalog bounds and detail mapping. The provider's service must independently enforce authorization.
5. Rebuild and deploy DeviceSearch. Register/compose the underlying subgraph separately if its public
   fields should also be available through Fusion. This is explicit registration, not runtime discovery.

`searchCapabilities.available` reflects the current caller's permission, not health. `searchCatalog` is a
bounded typeahead query (`first` 1–100), not a paginated inventory API. Software expands source records into
any-version and exact-version options, deduplicates keys, and truncates to the requested option limit.
Saved `patch`, `cve` and `sw` URLs still work; canonical provider categories also round-trip in URLs.
Unknown categories remain in the request so the server can reject them rather than silently weaken a filter.

No additional domain is included in this refactor. The existing historical event/finding match semantics,
AND-before-OR precedence, atomic failures and fresh offset-page semantics are preserved.
