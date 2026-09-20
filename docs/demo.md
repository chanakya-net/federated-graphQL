# Demo runbook (12 minutes)

What the audience sees: one GraphQL query, answered by four services on three database technologies; access
decided by each domain service, not by the gateway or the UI; tenant isolation that leaks nothing; and a page
that keeps working when one domain service is down or hung.

Every command runs from the repository root. Screen copy below is what the UI shows on the seeded data
(`dev-00001` is in TenantA: 5 patch, 14 vulnerability and 20 install events).

## Before the audience arrives (5 minutes)

```bash
scripts/up.sh      # cold start 2-5 min (first build + seeding); about 30 s when the images already exist
scripts/e2e.sh     # optional smoke test, about 1 min; restores everything it stops, ends with "e2e: 26/26 passed"
```

Open two browser tabs:

1. **UI**: http://localhost:4200
2. **Nitro**: http://localhost:5050/graphql/. Mint a token and add it to the Nitro document's connection
   settings as the HTTP header `Authorization: Bearer <jwt>`:
   ```bash
   docker compose run --rm -T token-generator --user alice
   ```
   Paste this tracer query into a Nitro document:
   ```graphql
   query Tracer {
     device(id: "dev-00001") {
       id
       hostname
       tenantId
       patchEvents { id occurredAt status }
       vulnerabilityEvents { id occurredAt kind }
       installEvents { id occurredAt action }
     }
   }
   ```

If it goes wrong: `docker compose ps` should list nine services `(healthy)`, plus `token-generator`, which
exits after it writes the tokens. For anything else, see README "Troubleshooting".

## 1. One query, three backends (2 min)

In the UI, pick **Alice (Tenant A)** in the *Demo user* menu. The menu shows each user's tenant and services.
Search `dev-000`: you get 100 devices, all in TenantA. Open **dev-00001**.

Expected: three section cards, **Patch 5 events**, **Vulnerability 14 events**, **Software Install 20
events**, one horizontal timeline of 39 points in the subgraph colours (blue patch, pink vulnerability,
green install; click a point for the event's details) and one merged list, "39 of 39 events, newest
first". Filter by type, date range, status or text to show that it is one timeline.

> "One query, three backends, three database technologies: MongoDB, PostgreSQL and Azure Blob storage."

In Nitro, run `Tracer` and open the operation plan for the response. It shows **DeviceDirectory** first, then
**Patch**, **Vulnerability** and **SoftwareInstall**, each depending only on that first step, so they run in
parallel.

If it goes wrong: the page says "Device directory unavailable" → `scripts/demo-outage.sh device-directory restore`,
then *Retry*. Nitro answers 401 → the `Authorization` header is missing or has no `Bearer ` prefix. No plan
in the response → the gateway image is older than Phase 6; run `scripts/up.sh` to rebuild it.

## 2. Access is decided by the domain service (2 min)

Switch the *Demo user* to **Bob (Tenant A)**. The page reloads the same device.

Expected: Patch (5) and Vulnerability (14) render. **Software Install** turns into a grey card with a
**lock**: "You don't have access to Software Install data." The list shows 19 of 19 events.

> "Same query. The Software Install service itself refused; the gateway and the UI make no access decisions."

Show the raw answer. Either swap Nitro's token for bob's (`--user bob`), or run:

```bash
TOKEN=$(docker compose run --rm -T token-generator --user bob)
curl -s http://localhost:5050/graphql -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d '{"query":"{ device(id:\"dev-00001\") { id installEvents { id } } }"}' | jq .
```

Expected: `installEvents: null`, and an error at `["device","installEvents"]` with
`extensions.code: "AUTH_NOT_AUTHORIZED"`.

If it goes wrong: every section is locked → the wrong user is selected (check the menu), or the token in
Nitro belongs to someone else.

## 3. Tenant isolation leaks nothing (1 min)

Stay on `dev-00001` and switch to **Dave (Tenant B)**.

Expected: a card, "Device dev-00001 not found in TenantB. It does not exist in this tenant, or it belongs to
another one. The gateway gives the same answer either way." *Back to devices* shows TenantB devices only.
Search `dev-07000` and open it: all three sections render for Dave's own device.

> "The device exists, just not for Dave. The answer is `{"data":{"device":null}}`: no error, no hint."

If it goes wrong: refresh the page. The user choice is kept across reloads.

## 4. One domain service down (2 min)

```bash
scripts/demo-outage.sh vulnerability stop
```

Switch back to **Alice (Tenant A)**, open `dev-00001` and refresh.

Expected, within milliseconds: a red **warning** banner, "Vulnerability service is currently unavailable —
vulnerability history is not shown.", with "Gateway error: Unexpected Execution Error" below it. Patch (5)
and Software Install (20) are intact: 25 of 25 events.

> "One service down, the timeline keeps two thirds, and the banner says which third is missing. It is not a
> lock: the user can tell *down* from *no access*."

If it goes wrong: all three sections still render → the refresh happened before `stop` returned; refresh again.

## 5. Hung, not dead (1 min)

A stopped container cannot be paused, so bring it back first:

```bash
scripts/demo-outage.sh vulnerability restore && scripts/demo-outage.sh vulnerability pause
```

Refresh as Alice. Expected: grey placeholder rows and a progress bar for about 5 s, then the same warning
banner. The page never hangs.

> "Hung, not dead. The 5 s per-service timeout bounds the damage; the other two sections come back as normal."

If it goes wrong: the page waits much longer than 6 s → check `SUBGRAPH_TIMEOUT_SECONDS` in `.env` (5).

## 6. Restore, and the known single point of failure (1-2 min)

```bash
scripts/demo-outage.sh vulnerability restore     # unpauses and waits until healthy (about 5-10 s)
```

Refresh: all three sections are back.

Optional: show the documented limit.

```bash
scripts/demo-outage.sh device-directory stop
```

Refresh. Expected: a card, "Device directory unavailable. Every device comes from the Device Directory
subgraph. Without it the whole query fails (by design), so no section can be shown." It has a *Retry*
button. The device search page shows "Device directory unavailable" too.

```bash
scripts/demo-outage.sh device-directory restore
```

Click *Retry*: the page recovers.

> "Device Directory owns the `Device` everything else attaches to. That is a documented single point of failure,
> not a surprise."

If it goes wrong: `restore` prints `FAILED` → `docker compose logs device-directory`, then run `restore` again.

## 7. Find devices: the graph turned around, with AND / OR (2 min)

Click **Find devices** in the toolbar, as **Alice (Tenant A)**. Type `KB5000128` in the *Patch* box and pick
it, type `CVE-2026-10166` in *Vulnerability* and pick it. The expression bar reads `KB5000128 AND
CVE-2026-10166`. Press **Find devices**.

Expected: one table with the server's matching count. Every AND row has both a patch chip
(`KB5000128`) and a CVE chip (`CVE-2026-10166`). Change **AND** to **OR** and Find again: rows can
have either or both sources, with “—” in a nonmatching source's cell. Click a row to open its timeline;
back restores the filter expression and page from the URL.

> "The browser sends the complete expression once. Device Search asks Patch and Vulnerability for matching
> IDs, combines them on the server, and chooses the page. It fetches matching events only for that page;
> the gateway completes device properties from Device Directory. The browser displays the final result."

In the browser Network panel, Find sends one `FindDevices` operation. Catalog picker queries are separate.
The picker definitions come from `SearchCapabilities`; each picker uses `SearchCatalog` with its category.
Exactly three providers are registered: Patch, Vulnerability and Software Install. For Bob, Software Install
is unavailable in capability metadata and its catalog is denied server-side. Metadata availability is a
permission indication, not a source health probe.
Changing pages sends the same expression with a new offset; no device-ID sets or per-source detail queries
are sent by the browser.

Switch to **Bob (Tenant A)**: the Software Install picker is denied. A restored URL containing a software
filter produces a search error, not a partial answer. Switch to **Carol** for the opposite permissions.

Optional: `scripts/demo-outage.sh patch stop`, then Find with the Patch/CVE expression. The whole search
shows an error with Retry; it must not claim zero matches or show only the CVE results, even for OR.
Run `scripts/demo-outage.sh patch restore`, then Retry.

Device Directory being down prevents final device enrichment, so the search shows an error as well.
Catalogs may still work because they are independent requests. Restore the service and Retry.

## 8. Close (30 s)

```bash
docker compose ps -a
```

Expected: eleven services from one command (`docker compose up --build`): ten `Up … (healthy)`, and
`token-generator` `Exited (0)` because it only writes the tokens.

> "Eleven containers, one command, no cloud account, works offline."

## After the demo

```bash
scripts/e2e.sh     # proves the stack is back to normal; restores anything it stops
```

Leave the stack running, or `docker compose down` (keeps the seeded data). `scripts/reset.sh` wipes the data;
the next start seeds it again.
