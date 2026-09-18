# Phase 5 — Angular UI

**Lane F. Starts after Phase 1. Parallel with every other Stage 2 lane. Integrates against the live stack in Stage 4.**
**Effort:** 3–4 days. **Owns:** `ui/`.

## 1. Purpose

A user picks a demo user, searches devices, and sees one merged, filterable timeline sourced from three subgraphs through the gateway. Each of the three sections independently shows **ok**, **no access**, or **unavailable**, decided from the GraphQL response, never from client-side assumptions.

## 2. Inputs

- `contracts/*.graphqls` (query shape), `contracts/tokens.json.md`, `contracts/errors.md`.
- `tests/fixtures/*.json` from Phase 0 (copy into `ui/src/testing/fixtures/`).
- P2C compose entry for `angular-ui` (port 80, `/tokens.json` from the `tokens` volume, `/graphql` proxied to `fusion-gateway:8080`).

Until Stage 4 the gateway may not exist. Build against fixtures and a local mock (§9); switch to the proxy in Stage 4.

## 3. Stack and scaffold

```bash
cd ui
npx @angular/cli@latest new sor-ui --directory . --standalone --routing --style scss --ssr false --skip-git
ng add @angular/material            # pick a prebuilt theme (azure-blue), typography yes, animations yes
npm i apollo-angular @apollo/client graphql
```

Pin the resulting versions in `package.json` (no `^`), record them in `docs/version-facts.md §1`. Angular current LTS, Angular Material, Apollo Angular. Note the Apollo Client major: **v3** exposes GraphQL errors as `result.errors`; **v4** as `result.error` (a `CombinedGraphQLErrors` with `.errors`). §6 handles both.

Layout:

```
ui/
  Dockerfile
  nginx.conf
  proxy.conf.json                 # dev: /graphql -> http://localhost:5000, /tokens.json -> ./src/testing/tokens.dev.json via mock
  src/
    app/
      app.config.ts               # providers: router, http (withInterceptors), Apollo
      app.routes.ts               # '' -> DeviceSearchPage, 'devices/:id' -> DeviceTimelinePage
      core/
        session.service.ts        # users signal, selected user signal, token()
        auth.interceptor.ts
        graphql.provider.ts       # provideApollo with errorPolicy 'all'
        demo-user.ts              # DemoUser type = contracts/tokens.json.md
      graphql/
        operations.ts             # gql documents
        types.ts                  # hand-written TS types for the two queries (codegen optional)
      timeline/
        section-state.ts          # pure: sectionState(field, data, errors) -> SectionState
        timeline-merge.ts         # pure: merge + sort + filter
        timeline.models.ts        # TimelineEvent, TimelineFilter
      features/
        user-switch/user-switch.component.ts
        device-search/device-search.page.ts
        device-timeline/device-timeline.page.ts
        device-timeline/section-banner.component.ts
        device-timeline/timeline-filters.component.ts
        device-timeline/timeline-list.component.ts
    testing/
      fixtures/                   # copied from tests/fixtures
      tokens.dev.json             # five fake users for local dev (tokens can be any string; the mock ignores them)
      mock-gateway.mjs            # §9
```

## 4. Session and auth

`DemoUser`: `{ sub: string; name: string; tenantId: string; services: string[]; token: string }`.

`SessionService`:
- `load()`: `GET /tokens.json` → `users.set(...)`; restore `localStorage['sor.user']` by `sub`, else first user. Called from an `APP_INITIALIZER` / `provideAppInitializer`.
- `select(sub)`: set selected, persist, then `apollo.client.clearStore()` and navigate to the current URL again so every query re-runs with the new token.
- `token()`: selected user's token or `null`.

`authInterceptor`: for requests whose URL starts with `/graphql`, add `Authorization: Bearer <token>` when a token exists. Nothing else is touched.

## 5. GraphQL operations (`operations.ts`)

```ts
export const DEVICE_SEARCH = gql`
  query DeviceSearch($search: String, $first: Int!, $offset: Int!) {
    devices(search: $search, first: $first, offset: $offset) {
      totalCount
      items { id hostname os ipAddress lastSeenAt tenantId }
    }
  }`;

export const DEVICE_TIMELINE = gql`
  query DeviceTimeline($id: ID!, $since: DateTime, $until: DateTime) {
    device(id: $id) {
      id hostname os ipAddress lastSeenAt tenantId
      patchEvents(since: $since, until: $until) {
        id occurredAt status patch { id kbId title severity vendor }
      }
      vulnerabilityEvents(since: $since, until: $until) {
        id occurredAt kind findingState findingId cve { id title cvssScore severity }
      }
      installEvents(since: $since, until: $until) {
        id occurredAt action result software { name version publisher }
      }
    }
  }`;
```

The timeline query **always** requests all three sections, whatever the selected user's `services` says. Server-side denial is what the demo proves. A client-side hint (greying the section header from the decoded claim) is optional polish; the response stays the source of truth.

`provideApollo`:

```ts
provideApollo(() => {
  const httpLink = inject(HttpLink);
  return {
    link: httpLink.create({ uri: '/graphql' }),
    cache: new InMemoryCache(),
    defaultOptions: {
      watchQuery: { errorPolicy: 'all', fetchPolicy: 'network-only' },
      query:      { errorPolicy: 'all', fetchPolicy: 'network-only' },
    },
  };
})
```

`errorPolicy: 'all'` is mandatory. The default `'none'` discards `data` whenever `errors[]` is non-empty, which would make every degraded or denied response look like a total failure.

## 6. Section state (`section-state.ts`, pure, unit-tested against the fixtures)

```ts
export type SectionKey = 'patchEvents' | 'vulnerabilityEvents' | 'installEvents';
export type SectionState<T> =
  | { kind: 'ok'; events: T[] }
  | { kind: 'no-access' }
  | { kind: 'unavailable'; message: string };

export interface GqlError { message: string; path?: (string | number)[]; extensions?: { code?: string } }

/** Normalises Apollo v3 (`errors`) and v4 (`error.errors`) into a flat list. */
export function extractErrors(result: { errors?: readonly GqlError[]; error?: { errors?: readonly GqlError[] } }): GqlError[] {
  return [...(result.errors ?? []), ...(result.error?.errors ?? [])];
}

export function sectionState<T>(field: SectionKey, device: Record<SectionKey, T[] | null> | null, errors: GqlError[]): SectionState<T> {
  const value = device?.[field];
  if (value != null) return { kind: 'ok', events: value };                      // [] is ok: no events
  const err = errors.find(e => Array.isArray(e.path) && e.path[0] === 'device' && e.path[1] === field);   // PREFIX match
  if (err?.extensions?.code === 'AUTH_NOT_AUTHORIZED') return { kind: 'no-access' };
  if (err) return { kind: 'unavailable', message: err.message };
  return { kind: 'unavailable', message: 'No data and no error returned for this section' };   // should not happen; be honest
}
```

Global states, decided before sections: `device == null && errors.length == 0` → "Device `<id>` not found in `<tenant>`"; `device == null && errors.length > 0` → "Device directory unavailable" (whole query failed, by design); network/HTTP error (401 after a token change, gateway down) → global error card with retry.

Banner copy (exact, from plan §7):

| Section | no-access | unavailable |
|---|---|---|
| Patch | You don't have access to Patch data. | Patch service is currently unavailable — patch history is not shown. |
| Vulnerability | You don't have access to Vulnerability data. | Vulnerability service is currently unavailable — vulnerability history is not shown. |
| Software Install | You don't have access to Software Install data. | Software Install service is currently unavailable — install history is not shown. |

No-access uses a lock icon and neutral colour; unavailable uses a warning icon and the theme's warn colour. They must be visibly different at a glance.

## 7. Timeline merge and filters (`timeline-merge.ts`, pure)

```ts
export type Source = 'patch' | 'vulnerability' | 'softwareinstall';
export interface TimelineEvent {
  id: string; source: Source; occurredAt: string; title: string; subtitle: string;
  status: string;            // APPLIED|FAILED|PENDING | OPEN|REMEDIATED | SUCCESS|FAILED
  severity?: string;         // patch/cve severity when present
  raw: unknown;
}
export interface TimelineFilter {
  sources: Set<Source>;      // default all three
  since?: string; until?: string;      // ISO; also sent to the server as $since/$until
  statuses: Set<string>;     // empty = all
  text: string;              // case-insensitive over title + subtitle
}
```

Mapping: patch → `title = patch.title`, `subtitle = "<kbId> · <vendor>"`, `status = status`; vulnerability → `title = "<kind> <cve.id>"`, `subtitle = cve.title`, `status = findingState`, `severity = cve.severity`; install → `title = "<action> <software.name> <software.version>"`, `subtitle = software.publisher`, `status = result`.

`merge(sections)` concatenates the `ok` sections and sorts by `occurredAt` descending, tie-break by `id`. `applyFilter(events, filter)` is pure. Date-range changes re-run the query (`$since/$until`); all other filters are client-side.

## 8. Components

- **UserSwitchComponent** (toolbar): `mat-select` of users; option shows `name`, a tenant chip, and one chip per service. On change → `session.select(sub)`.
- **DeviceSearchPage**: `mat-form-field` with debounce 300 ms; runs `DEVICE_SEARCH` with `first: 25`, `offset` paging (`mat-paginator`); table columns id, hostname, os, ipAddress, lastSeenAt; row click → `/devices/:id`. Empty search lists the first 25.
- **DeviceTimelinePage**: header card (six device fields); `TimelineFiltersComponent`; three `SectionBannerComponent`s (only rendered for non-ok sections, with the section's name and event count when ok); `TimelineListComponent` with the merged events (icon by source, `occurredAt` in local time with the ISO in a tooltip, title, subtitle, status chip, severity chip). Loading state: skeleton rows; never a blank page.
- Every `apollo.watchQuery(...)` subscription is converted with `toSignal` and unsubscribed on destroy.

## 9. Local development without the stack (`src/testing/mock-gateway.mjs`)

A ~80-line Node script (no dependencies) that serves `POST /graphql` and `GET /tokens.json` on port 5001:

- Parses the operation name. `DeviceSearch` → 25 generated devices for the tenant in the token (decode the JWT payload without verifying; fake tokens in `tokens.dev.json` are real-looking base64 JSON). `DeviceTimeline` → the fixture matching a query flag: `?mode=full|denied|outage-stop|outage-pause|cross-tenant` set via an env var or a header the dev sets in `proxy.conf.json`.
- Returns the fixture JSON verbatim so the UI exercises the exact shapes recorded by Phase 0.

`proxy.conf.json` (dev): `/graphql` → `http://localhost:5001` (mock) or `http://localhost:5000` (real gateway); `/tokens.json` → same. `ng serve --proxy-config proxy.conf.json`.

## 10. Nginx and image

`nginx.conf`:

```nginx
server {
  listen 80;
  root /usr/share/nginx/html;
  index index.html;

  location = /tokens.json {
    alias /tokens/tokens.json;
    default_type application/json;
    add_header Cache-Control "no-store";
  }
  location /graphql {
    proxy_pass http://fusion-gateway:8080/graphql;
    proxy_http_version 1.1;
    proxy_set_header Host $host;
    proxy_read_timeout 60s;
  }
  location / {
    try_files $uri $uri/ /index.html;
  }
}
```

`Dockerfile` (context = `ui/`):

```dockerfile
FROM node:22-alpine AS build
WORKDIR /app
COPY package*.json ./
RUN npm ci
COPY . .
RUN npm run build -- --configuration production

FROM nginx:alpine
COPY nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /app/dist/sor-ui/browser /usr/share/nginx/html
EXPOSE 80
```

Adjust the `dist/.../browser` path to what the Angular version emits. `nginx:alpine` has `wget` for the compose healthcheck.

## 11. Tests

Unit (`ng test`, headless Chrome):

| Test | Assertion |
|---|---|
| `sectionState: ok with events` | fixture full → `ok`, events length > 0 |
| `sectionState: ok with empty list` | `[]` → `ok`, 0 events (not degraded) |
| `sectionState: denied` | `denied.json` → `no-access` for the denied field, `ok` for others |
| `sectionState: outage stop` | `outage-stop.json` → `unavailable` with the error message |
| `sectionState: outage pause` | `outage-pause.json` → `unavailable` |
| `sectionState: prefix match` | error path `["device","patchEvents",0,"patch"]` still maps to `patchEvents` |
| `sectionState: null without error` | → `unavailable` with the fallback message |
| `extractErrors` | v3 shape and v4 shape both flatten |
| `merge sorts descending, stable` | |
| `applyFilter` | each filter dimension individually and combined |
| `authInterceptor` | adds header only to `/graphql`, only when a token exists |
| `SessionService` | restores selection from localStorage; `select` clears the Apollo store |

Component tests: `SectionBannerComponent` renders the exact copy and icon per state; `DeviceTimelinePage` with the mock Apollo (`ApolloTestingModule`) shows two sections + one lock banner for the denied fixture.

E2E (optional, Playwright, run against the live stack in Stage 4): switch to bob → lock banner on Software Install; `scripts/demo-outage.sh patch stop` → warning banner on Patch.

## 12. Stage 4 integration (after P4 part 2)

1. `scripts/up.sh`; point `proxy.conf.json` at `http://localhost:5000`; `ng serve` and walk the manual checklist in `phase-6-e2e-validation.md §5`.
2. Fix mismatches in the UI, or file a contract issue if the response truly differs from `contracts/`.
3. `docker compose build angular-ui && docker compose up -d angular-ui`; repeat the checklist on `http://localhost:4200`.
4. Record the Apollo Client major actually used and any shape differences in `docs/version-facts.md §8`.

## 13. Definition of Done

- [ ] `npm run build` and `ng test` pass; unit tests cover every row in §11.
- [ ] Timeline query uses `errorPolicy: 'all'`; a test proves partial data reaches the component.
- [ ] Three section states implemented with the exact copy in §6 and visibly distinct styling.
- [ ] Global states for not-found, directory-down and network error implemented; no blank pages.
- [ ] User switch reloads data with the new token; selection persists.
- [ ] Filters: source multi-select, date range (server-side), status, free text.
- [ ] Mock gateway lets a developer run the UI with no Docker.
- [ ] Image builds; `/tokens.json` and `/graphql` proxying verified inside compose.
- [ ] Stage 4 checklist walked on the live stack.
- [ ] README: `ui/` dev workflow; deviations recorded.
