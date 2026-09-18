# HTTP and environment contract

| Service | Compose name | Container port | Host port | Endpoints | Required env |
|---|---|---|---|---|---|
| Device Directory | `device-directory` | 8080 | — | `/graphql`, `/health` | `DEV_JWT_SIGNING_KEY`, `ConnectionStrings__DeviceDirectory` |
| Patch | `patch` | 8080 | — | `/graphql`, `/health` | `DEV_JWT_SIGNING_KEY`, `Mongo__ConnectionString`, `Mongo__Database` |
| Vulnerability | `vulnerability` | 8080 | — | `/graphql`, `/health` | `DEV_JWT_SIGNING_KEY`, `ConnectionStrings__Vulnerability` |
| SoftwareInstall | `software-install` | 8080 | — | `/graphql`, `/health` | `DEV_JWT_SIGNING_KEY`, `Blob__ConnectionString`, `Blob__Container` |
| Gateway | `fusion-gateway` | 8080 | `${GATEWAY_PORT}` (5050) | `POST /graphql`, `GET /graphql/` (Nitro UI), `/health` | `DEV_JWT_SIGNING_KEY`, `SUBGRAPH_DEVICEDIRECTORY_URL`, `SUBGRAPH_PATCH_URL`, `SUBGRAPH_VULNERABILITY_URL`, `SUBGRAPH_SOFTWAREINSTALL_URL`, `SUBGRAPH_TIMEOUT_SECONDS` |
| UI | `angular-ui` | 80 | `${UI_PORT}` (4200) | `/`, `/graphql` (proxy), `/tokens.json` | — |
| Token generator | `token-generator` | — | — | — | `DEV_JWT_SIGNING_KEY`, `USERS_FILE`, `TOKENS_OUTPUT` |

Values for the dev stack live in the committed `.env`.

## Rules

- `/health` returns 200 **only** when the service can serve queries: DB reachable, seed complete, signing
  key configured (`Shared.Auth` registers the `auth-config` check). Compose healthchecks use it.
- All subgraph URLs inside compose: `http://<compose name>:8080/graphql`. The same URLs are baked into
  `contracts/*-settings.json` as defaults; the gateway overrides them from `SUBGRAPH_<NAME>_URL`.
- Source-schema names (composition, settings files, gateway `HttpClient` names):
  `DeviceDirectory`, `Patch`, `Vulnerability`, `SoftwareInstall`. Each subgraph registers with
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
  call; each subgraph `HttpClient` has `Timeout = SUBGRAPH_TIMEOUT_SECONDS` (5 s).
- The host port is `5050`, not `5000`: macOS reserves 5000 for AirPlay Receiver.
