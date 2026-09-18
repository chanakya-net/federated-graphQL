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
