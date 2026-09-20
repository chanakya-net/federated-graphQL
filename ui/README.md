# SoR UI — Angular timeline (Phase 5)

Pick a demo user, search devices, open one: a merged, filterable timeline from three subgraphs through
the gateway. Each section (Patch, Vulnerability, Software Install) shows **ok**, **no access** (lock,
neutral) or **unavailable** (warning, error colours), decided from the GraphQL response only
(`contracts/errors.md`). The timeline query always asks for all three sections; the subgraphs deny.

**Find devices** (`/find`) goes the other way: provider metadata supplies pickers (currently patches, CVEs and software), which use the common
`searchCatalog` query as you type; a picked item joins the expression as a chip, every chip after the first carries an
AND / OR connector you can flip, and **Find** puts the expression in the URL (`?f=patch:patch-0128&f=and:cve:…`,
plus `page=`). One `FindDevices` operation sends the ordered filters, `first=25`, and `offset=page*25`.
Device Search evaluates AND / OR (AND binds first), orders matching IDs, and returns the final page,
`totalCount`, `hasNextPage`, and matching event summaries; Fusion enriches each Device from Device Directory.
The browser groups these events into table cells only. It never fetches sets, intersects IDs, slices a
result page, or requests per-source details. Rows open the timeline. A required source that is denied,
down, or incomplete fails the whole search with a Retry card; it never counts as zero matches. Catalogs
remain independent, so a picker whose catalog is denied is locked before typing. URL filters and page,
back/forward, repeated Find, and tenant switches all trigger a fresh server search.
Provider metadata supplies names, icons, colors and placeholders. Legacy URL tokens `patch`, `cve` and
`sw` remain supported alongside canonical category strings. Unknown categories reach the server and
display its error. Summary rows omit dates when `occurredAt` is null; the timeline model is unchanged.

The events are drawn as points on one horizontal track, oldest on the left and grouped by month, each
point in its subgraph's colour (blue patch, pink vulnerability, green install; the section cards are
the legend). Clicking a point, or a row of the list below, shows that event's details under the track.

Angular 22 (standalone, zoneless, signals), Angular Material 3, Apollo Angular 14 on Apollo Client 4.
Exact versions: `package.json` (pinned) and `docs/version-facts.md` §1.

## Develop

Node.js 22.22+ / 24.15+ / 26+ (Angular CLI 22). `npm ci` once.

| Command                                    | What                                                                                                                                                           |
| ------------------------------------------ | -------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `npm run mock` + `npm start`               | No Docker: the mock gateway on :5001, the dev server on http://localhost:4300 (`proxy.conf.json`)                                                              |
| `npm run start:live`                       | Dev server on :4300 against the running stack (`scripts/up.sh`): `/graphql` → gateway :5050, `/tokens.json` → the compose UI on :4200 (`proxy.live.conf.json`) |
| `npm test`                                 | Unit and component tests once (Vitest + jsdom via `ng test`); `npm run test:watch` to watch                                                                    |
| `npm run build`                            | Production build into `dist/sor-ui/browser`                                                                                                                    |
| `npm run record-fixtures -- --search-only` | Record provider metadata/catalogs and FindDevices success/denial pages; does not stop services                                                                                   |
| `npm run record-fixtures`                  | Re-record `src/testing/fixtures/gateway/` from the running stack (stops / pauses subgraphs briefly)                                                            |
| `docker compose build angular-ui`          | The Nginx image (from the repo root)                                                                                                                           |

### Mock gateway (`src/testing/mock-gateway.mjs`)

No dependencies. Serves `/tokens.json` (`src/testing/tokens.dev.json`, five fake users with unsigned
tokens) and `POST /graphql` from the responses recorded from the real gateway, so the shapes are exact.

- `auto` (default) behaves like the stack: no token → 401; another tenant's device → `device: null`;
  a service missing from the token → that section `null` plus the recorded `AUTH_NOT_AUTHORIZED` error;
  `$since` / `$until` filter events. Devices: the ones in the recorded searches (TenantA and TenantB).
  Find devices: the catalogs are the recorded first pages, filtered by `$search`; every device of the
  tenant has the recorded timeline's events, so each selected item matches all sample devices or none.
  The mock server evaluates the expression and pages the final devices for `FindDevices`; denied
  selected sources fail the complete search. These are sample semantics, not live fleet data.
- `full`, `denied`, `denied-carol`, `outage-stop`, `outage-pause` (answers after 5 s), `cross-tenant`,
  `directory-down`: the recorded `dev-00001` response verbatim. `unauthenticated`: 401, empty body.
- Pick the mode with `MOCK_MODE=outage-stop npm run mock`, an `x-mock-mode` request header, or at
  runtime: open http://localhost:5001/__mock?mode=outage-stop.

## Layout

```
src/app/
  core/          SessionService (users from /tokens.json, selection in localStorage 'sor.user'),
                 authInterceptor (Bearer token on /graphql only), provideGraphql (errorPolicy 'all'),
                 watchQuery (Apollo watchQuery -> signal, re-run whenever the request signal changes)
  graphql/       the operations (directory search, timeline, provider capabilities/catalogs, FindDevices), hand-written
                 result types, Apollo default-option declaration
  timeline/      pure logic: section-state.ts (ok / no-access / unavailable, page-level states),
                 timeline-merge.ts (map, merge newest first, filter)
  finder/        pure logic: finder.models.ts (expression <-> URL), expression.ts (display text only),
                 field-state.ts (a root field's state)
  features/      user-switch, device-search (?q=&page= in the URL),
                 device-timeline (+ section cards, filters, the horizontal strip, event detail, list),
                 device-finder (catalog pickers, the expression bar, search error card, the combined table)
  shared/        state-card (page-level error / not-found states)
src/testing/
  fixtures/*.json          Phase 0 gateway responses (copied from tests/fixtures, spike field `notes`)
  fixtures/gateway/*.json  real gateway responses (record-fixtures.mjs); legacy lookup fixtures
                          remain for generic field-state regression tests
  mock-gateway.mjs, tokens.dev.json, record-fixtures.mjs
```

## How the states are decided

1. Transport error (HTTP 401 after a token change, 502, no connection) → a page-level card with Retry
   (401 also offers "Reload users").
2. `device == null` and no errors → "Device `<id>` not found in `<tenant>`".
3. `device == null` with errors → "Device directory unavailable" (the whole query fails by design).
4. Otherwise per section: data (even `[]`) → ok; `null` with an error whose path starts with
   `["device", <field>]` → no access if `extensions.code == "AUTH_NOT_AUTHORIZED"`, else unavailable
   (outage errors carry no `extensions` at all).

Switching the user clears the Apollo store and re-runs every query with the new token; pages depend on
the selected user, so nothing navigates. The date range is sent as `$since` / `$until` (local-day
bounds, applied when a complete range is picked or committed); source, status and text filter
client-side.

## Image

`Dockerfile` (context `ui/`): `node:24-alpine` build → `nginx:alpine`. `nginx.conf` serves the SPA,
`/tokens.json` from the `tokens` volume (`no-store`), and proxies `/graphql` (and Nitro under
`/graphql/`) to `fusion-gateway:8080`, resolved per request through Docker's DNS so Nginx starts
without the gateway and follows a recreated container. Fonts and icons are bundled: no internet needed.
