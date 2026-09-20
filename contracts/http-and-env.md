# HTTP and environment contract

| Service | Compose name | Container port | Host port | Endpoints | Required env |
|---|---|---|---|---|---|
| Device Directory | `device-directory` | 8080 | — | `/graphql`, `/health` | `DEV_JWT_SIGNING_KEY`, `ConnectionStrings__DeviceDirectory` |
| Patch | `patch` | 8080 | — | `/graphql`, `/health` | `DEV_JWT_SIGNING_KEY`, `Mongo__ConnectionString`, `Mongo__Database` |
| Vulnerability | `vulnerability` | 8080 | — | `/graphql`, `/health` | `DEV_JWT_SIGNING_KEY`, `ConnectionStrings__Vulnerability` |
| SoftwareInstall | `software-install` | 8080 | — | `/graphql`, `/health` | `DEV_JWT_SIGNING_KEY`, `Blob__ConnectionString`, `Blob__Container` |
| Device Search | `device-search` | 8080 | — | `/graphql`, `/health` | `DEV_JWT_SIGNING_KEY`, `SUBGRAPH_PATCH_URL`, `SUBGRAPH_VULNERABILITY_URL`, `SUBGRAPH_SOFTWAREINSTALL_URL`, `SUBGRAPH_TIMEOUT_SECONDS` |
| Gateway | `fusion-gateway` | 8080 | `${GATEWAY_PORT}` (5050) | `POST /graphql`, `GET /graphql/` (Nitro UI), `GET /timeline-sources`, `/health` | `DEV_JWT_SIGNING_KEY`, `SUBGRAPH_DEVICEDIRECTORY_URL`, `SUBGRAPH_PATCH_URL`, `SUBGRAPH_VULNERABILITY_URL`, `SUBGRAPH_SOFTWAREINSTALL_URL`, `SUBGRAPH_DEVICESEARCH_URL`, `SUBGRAPH_TIMEOUT_SECONDS`, `SUBGRAPH_DEVICESEARCH_TIMEOUT_SECONDS`, `GATEWAY_ARCHIVE`, `TIMELINE_SOURCES_CATALOG` |
| UI | `angular-ui` | 80 | `${UI_PORT}` (4200) | `/`, `/graphql` (proxy), `/timeline-sources` (proxy), `/tokens.json` | — |
| Token generator | `token-generator` | — | — | — | `DEV_JWT_SIGNING_KEY`, `USERS_FILE`, `TOKENS_OUTPUT` |

Values for the dev stack live in the committed `.env`.

## Rules

- `/health` returns 200 **only** when the service can serve queries: DB reachable, seed complete, signing
  key configured (`Shared.Auth` registers the `auth-config` check). Compose healthchecks use it.
- All subgraph URLs inside compose: `http://<compose name>:8080/graphql`. The same URLs are baked into
  `contracts/*-settings.json` as defaults; the gateway overrides them from `SUBGRAPH_<NAME>_URL`.
- Source-schema names (composition, settings files, gateway `HttpClient` names):
  `DeviceDirectory`, `Patch`, `Vulnerability`, `SoftwareInstall`, `DeviceSearch`. Each subgraph registers with
  `builder.AddGraphQL("<Name>")` using exactly this string (`docs/version-facts.md` §2).
- Every image: alpine runtime, non-root, `EXPOSE 8080`, `ASPNETCORE_URLS=http://+:8080`, `wget` available
  for healthchecks. Assembly name = project folder name (`Patch.dll`), so the P2C Dockerfile template
  works unchanged.
- Every subgraph supports `dotnet run -- schema export --output <file>` **without any backing service
  running** and without `DEV_JWT_SIGNING_KEY` set (requires `await app.RunWithGraphQLCommandsAsync(args)`).
- Seeding runs in an `IHostedService`; `Program.cs` never touches a database before `app.Run()`.
- **Gateway edge auth:** an unauthenticated `POST /graphql` gets `401` with an empty body and never
  reaches a subgraph. `GET /graphql/` (the embedded, offline Nitro UI) loads without a token; `GET /graphql`
  redirects there with `301`. There is no separate `/nitro` path. The gateway reads no `tenantId` or
  `services` claim.
- **Gateway → subgraph:** the caller's `Authorization` header is forwarded unchanged on every subgraph
  call; each subgraph `HttpClient` defaults to `SUBGRAPH_TIMEOUT_SECONDS` (5 s), with optional per-source overrides.
- The host port is `5050`, not `5000`: macOS reserves 5000 for AirPlay Receiver.

## Device Search

DeviceSearch has no database or seed dependency. Its health endpoint checks signing-key configuration;
required-source availability is evaluated per query. Its configured domain URLs are direct subgraph URLs,
not the public gateway. It forwards the caller JWT on each request and validates all selected service claims
before issuing any calls. Domain requests use `SUBGRAPH_TIMEOUT_SECONDS` (default 5 seconds).

The gateway's DeviceSearch client uses `SUBGRAPH_DEVICESEARCH_TIMEOUT_SECONDS` (Compose supplies 30 seconds,
overridable through its `DEVICE_SEARCH_TIMEOUT_SECONDS` interpolation variable), because one
search coordinates multiple domain requests. DeviceSearch bounds its own search work to 25 seconds.
Gateway client registration reads source-schema names from the FAR instead of a C# allowlist. Every source
requires `SUBGRAPH_<UPPERCASE_SOURCE_NAME>_URL`; an unconfigured source is rejected at startup. Optional
`SUBGRAPH_<UPPERCASE_SOURCE_NAME>_TIMEOUT_SECONDS` overrides the general timeout. Configure the search
source's longer timeout through `SUBGRAPH_DEVICESEARCH_TIMEOUT_SECONDS`.

## Timeline source discovery

`GET /timeline-sources` is public schema/presentation metadata (no tenant events or permissions) and sends
`Cache-Control: no-store`. The UI loads it on navigation and Retry, builds one generic event selection per
advertised field, and uses the source labels/colors/statuses for display. Domain services still decide access.
The gateway validates the catalog's SHA256 against its FAR at startup; deploy both together and restart the
gateway. See [timeline sources](../docs/timeline-sources.md) for the versioned contract and adding a source.
