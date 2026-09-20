# Error contract

Response shapes the UI relies on. Observed through the pinned Fusion v2 gateway in Phase 0
(`tests/fixtures/`, `docs/version-facts.md` §6–§7). The spike's extension field was called `notes`; in
the real graph it is `patchEvents`, `vulnerabilityEvents` or `installEvents`, with the same shapes.

## Observed shapes

**Outage** — domain subgraph stopped (fails in milliseconds) or hung (fails after the 5 s timeout).
HTTP 200. The error has **no `extensions` object at all**:

```json
{"errors":[{"message":"Unexpected Execution Error","path":["device","notes"]}],
 "data":{"device":{"id":"dev-00001","hostname":"alpha","notes":null}}}
```

**Denied** — subgraph healthy, token without that subgraph's service. HTTP 200:

```json
{"errors":[{"message":"The current user is not authorized to access this resource.","path":["device","notes"],
  "extensions":{"code":"AUTH_NOT_AUTHORIZED"}}],
 "data":{"device":{"id":"dev-00001","hostname":"alpha","notes":null}}}
```

**Not found / other tenant** — HTTP 200, no `errors`:

```json
{"data":{"device":null}}
```

**Unauthenticated** — no or invalid token: HTTP **401**, empty body, no `WWW-Authenticate` value. No
subgraph is called.

The error path observed is exactly `["device","<field>"]`; for list fields a deeper path
(`["device","<field>",3,...]`) is possible in other cases, hence the prefix rule below.

## Rules the UI relies on

For each timeline section (`patchEvents`, `vulnerabilityEvents`, `installEvents`):

- The section is **degraded** when its field is `null` **and** an `errors[]` entry has a `path` whose
  first two elements are `["device", "<field>"]`. Match by **prefix**, not equality.
- It is **denied** ("no access", lock icon) when that error's `extensions.code == "AUTH_NOT_AUTHORIZED"`.
- It is **unavailable** (warning icon) for any other code **or no code**. Do not require `extensions`
  to exist: outage errors have none.
- An empty list `[]` means "no events", never degraded.

For the device as a whole:

- `device == null` with **no** errors: "not found in this tenant" (tenant isolation leaks nothing).
- `device == null` **with** errors: Device Directory is down; the whole query failed by design. Show a
  global error, not a blank page.
- HTTP 401: the selected token is missing or invalid.

The root catalogs `patches` and `cves` follow the same field rules: `null` plus an
`AUTH_NOT_AUTHORIZED` error at `["patches"]` / `["cves"]` when denied, sibling root fields unaffected.

The Apollo timeline query must use `errorPolicy: 'all'`; the default (`'none'`) discards `data` whenever
`errors` is non-empty.

## Reverse lookups and catalogs (contracts-v2)

The same rules apply to the domain-level nullable root fields (`patches`, `cves`, `software`,
`devicesWithPatches`, `devicesWithCves`, `devicesWithSoftware`), with the path prefix `["<field>"]` instead of
`["device", <field>]`: data (even an empty list or `items: []`) is ok; `null` plus an error whose path starts
with the field is **no access** when `extensions.code == "AUTH_NOT_AUTHORIZED"` and **unavailable** otherwise.
With Device Directory down, the error sits deeper (`["devicesWithPatches", "items", 0, "device", "hostname"]`)
and the nullable lookup is `null`: the prefix match still reads it as unavailable.


## Cross-domain `findDevices`

The root is nullable. A required filter source that is denied, unavailable, malformed, or over an explicit
search work limit fails the complete search: `data.findDevices: null` and an error at `findDevices`.
Denied sources retain `AUTH_NOT_AUTHORIZED`; other search failures use explicit search error codes.
No source is substituted with an empty set, including inside OR. Successful zero matches return
`{ items: [], totalCount: 0, hasNextPage: false }`. Catalogs remain independent requests.
A Device Directory enrichment failure also surfaces at the nullable root through normal null propagation.

The finder uses the common nullable `searchCatalog` root for picker options. A denied provider returns
`null` with `AUTH_NOT_AUTHORIZED`; an unknown category or invalid catalog limit uses `BAD_USER_INPUT`.
Downstream errors use the same search source error codes as `findDevices`. `searchCapabilities` requires
authentication and lists registered providers with caller-specific `available` flags. A false flag does
not replace server-side authorization; a true flag does not promise downstream availability.
