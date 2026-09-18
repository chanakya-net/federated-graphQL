# Version facts (single source of truth — produced by Phase 0)

Produced by the Phase 0 spike on 2026-09-18 (`spike/`, branch `phase/0-spike`). Every fact below was
checked against a running stack or the Hot Chocolate 16.6.6 source (tag `16.6.6` of
`ChilliCream/graphql-platform`). **If a phase document disagrees with this file, this file wins.**

**Decision: Fusion v2 (Hot Chocolate 16.6.6). GO for Phase 1.** All four assumptions held on the
first attempt: offline composition works, outages and denials stay at the field, `AUTH_NOT_AUTHORIZED`
survives the trip through the gateway, and the `Authorization` header can be forwarded. The Fusion v1
fallback was never needed.

Reproduce everything below with:

```bash
spike/run-experiments.sh      # builds + starts spike/docker-compose.yml, runs experiments 1-11, exit 0 = all pass
```

## §1 Pinned versions

| Item | Value |
|---|---|
| .NET SDK (`global.json`) | `10.0.400`, `"rollForward": "latestFeature"` (dev machine: 10.0.400; `mcr.microsoft.com/dotnet/sdk:10.0` was 10.0.401 on 2026-09-18) |
| Target framework | `net10.0` (Hot Chocolate 16.6.6 targets net8.0 / net9.0 / net10.0) |
| Runtime image | `mcr.microsoft.com/dotnet/aspnet:10.0-alpine` (ASP.NET Core 10.0.12, Alpine 3.24.2; busybox `wget` present, no `curl`) |
| `HotChocolate.AspNetCore` | `16.6.6` |
| `HotChocolate.AspNetCore.Authorization` | `16.6.6` |
| `HotChocolate.AspNetCore.CommandLine` | `16.6.6` |
| Source-schema package | **none needed.** `[Lookup]` / `[Internal]` ship in `HotChocolate.Types` 16.6.6, which `HotChocolate.AspNetCore` brings in transitively. (`HotChocolate.Fusion.SourceSchema` exists only up to 15.1.x and is **not** used.) |
| Gateway package | `HotChocolate.Fusion.AspNetCore` `16.6.6` |
| Composition CLI | `ChilliCream.Nitro.CommandLine` `16.6.6`, command `nitro`, pinned in `.config/dotnet-tools.json` (`rollForward: false`) |
| Fusion generation | **v2** (composite schema spec: `@lookup`, `@internal`, `.far` archive, `nitro fusion compose`) |
| `Microsoft.AspNetCore.Authentication.JwtBearer` | `10.0.12` |
| `Microsoft.IdentityModel.JsonWebTokens` | `8.19.2` (the exact version JwtBearer 10.0.12 depends on; avoids a mixed IdentityModel graph) |

Copy-paste block for the root `Directory.Packages.props` (identical to `spike/Directory.Packages.props`):

```xml
<PackageVersion Include="HotChocolate.AspNetCore" Version="16.6.6" />
<PackageVersion Include="HotChocolate.AspNetCore.Authorization" Version="16.6.6" />
<PackageVersion Include="HotChocolate.AspNetCore.CommandLine" Version="16.6.6" />
<PackageVersion Include="HotChocolate.Fusion.AspNetCore" Version="16.6.6" />
<PackageVersion Include="Microsoft.AspNetCore.Authentication.JwtBearer" Version="10.0.12" />
<PackageVersion Include="Microsoft.IdentityModel.JsonWebTokens" Version="8.19.2" />
```

**UI (P5, `ui/package.json`, all pinned exactly).** Angular `22.1.7` (`@angular/cli` / `@angular/build`
`22.1.8`), Angular Material / CDK `22.1.7`, `apollo-angular` `14.2.0`, `@apollo/client` **`4.3.0` (v4)**,
`graphql` `16.14.2`, `rxjs` `7.8.2`, TypeScript `6.0.3`, Vitest `4.1.11` + jsdom `28.1.0` (the `ng test`
runner), `@fontsource/roboto` `5.3.0`, `material-icons` `1.13.14`. Build image `node:24-alpine` (Node
24.21.0, npm 11.19.0 on 2026-09-18), runtime `nginx:alpine`. Dev machine: Node 26.8.2, npm 11.19.1.
Angular CLI 22 requires Node `^22.22.3 || ^24.15.0 || >=26.0.0`.

## §2 Packages and namespaces

- **Lookup attributes:** `[Lookup]` and `[Internal]` live in namespace `HotChocolate.Types.Composite`
  (package `HotChocolate.Types`, transitive via `HotChocolate.AspNetCore`). They work on plain
  reflection-based query classes; the source generator (`HotChocolate.Types.Analyzers`) is **not** required.
- **`[ID]`** lives in `HotChocolate.Types.Relay`. On a `string` it only changes the GraphQL type to
  `ID!`; the value is **not** re-encoded (global object identification is not enabled), so
  `"dev-00001"` round-trips unchanged through gateway and subgraphs (experiment 1).
- **`[Authorize]`** is `HotChocolate.Authorization.AuthorizeAttribute` (package
  `HotChocolate.AspNetCore.Authorization`), plus `.AddAuthorization()` on the GraphQL builder.
- **Registration call on the subgraph builder:** no `.AddSourceSchema()` or similar. Use
  `builder.AddGraphQL("<SourceSchemaName>")` (the `IHostApplicationBuilder` overload). The name passed
  here becomes `"name"` in the exported `*-settings.json`, i.e. the source-schema name the gateway uses.
  Everything else is ordinary Hot Chocolate: `.AddAuthorization().AddQueryType<Query>().AddTypeExtension<X>()`.
- **Type extensions:** `[ExtendObjectType<Device>]` on a class with `[Parent] Device device` resolvers
  works unchanged in 16.6.6.
- **Key fields:** implicit from the lookup's arguments. `deviceById(id: ID!)` makes `id` the key
  (`@fusion__lookup(key: "id" ...)` in the composite schema). No `[Key]` attribute, no `@key` in SDL.
  Both subgraphs declaring `Device.id` composes without `@shareable`.
- **Does `deviceById` stay hidden on the gateway? Yes** (experiment 2). Introspection of the gateway's
  `Query` returns only `device` and `devices`; calling `deviceById` directly fails validation with
  ``The field `deviceById` does not exist on the type `Query`.`` (HTTP 400).
- **`@authorize` in the composite schema:** composition copies `@authorize` directives into the gateway
  schema, but the Fusion gateway does **not** enforce them (there is no authorization code in
  `HotChocolate.Fusion.Execution`, and the gateway registers no policies). The denial seen through the
  gateway is the subgraph's own error: querying the extender directly with the same token returns the
  identical error at `["deviceById","notes"]`; the gateway rewrites the path to `["device","notes"]`.
- **Default security (new in HC 16):** `AddGraphQL(...)` and `AddGraphQLGateway(...)` both disable
  introspection when the environment is not `Development`. Containers run as `Production`, so
  subgraph introspection is off (harmless: the gateway uses the archive, never introspects) and the
  gateway must opt back in with `.DisableIntrospection(false)` (see §4).

## §3 Commands (copy-paste ready, verified)

- **Export** (no database needed; builds the DI container only):
  ```bash
  cd src/<Project> && dotnet run -- schema export --output ../../schemas/<name>.graphqls
  ```
  Requires `await app.RunWithGraphQLCommandsAsync(args);` in `Program.cs`. Writes **two** files:
  `<name>.graphqls` and `<name>-settings.json` (settings file name = SDL base name + `-settings.json`).
  Output is byte-identical across runs.
- **Settings file behaviour:** on first export the settings file gets the hard-coded URL
  `http://localhost:5000/graphql`. **If the settings file already exists, export rewrites only
  `"name"` and keeps everything else.** So: export once, set the in-cluster URL once, commit
  `schemas/<name>-settings.json`, and later exports leave it alone. Shape (as exported, URL edited):
  ```json
  {
    "name": "DeviceDirectory",
    "transports": {
      "http": {
        "url": "http://device-directory:8080/graphql",
        "capabilities": {
          "batching": { "variableBatching": true, "requestBatching": true, "aliasBatching": true },
          "onError": "propagate"
        }
      }
    }
  }
  ```
- **Compose** (verified with outbound network denied via `sandbox-exec`, no login, no API key):
  ```bash
  rm -f gateway/gateway.far
  dotnet nitro fusion compose \
    -f schemas/device-directory.graphqls \
    -f schemas/patch.graphqls \
    -f schemas/vulnerability.graphqls \
    -f schemas/software-install.graphqls \
    -a gateway/gateway.far
  ```
  - `-f` / `--source-schema-file` accepts several values: repeating `-f` per file and a single `-f`
    followed by a shell-expanded glob (`-f schemas/*.graphqls`) both work. The CLI does **not** expand
    a quoted glob itself (`-f 'schemas/*.graphqls'` → "does not exist"), and `-f schemas` (a directory
    holding several schemas) fails with "Post-merge validation failed".
  - **Input order matters for the bytes:** the archive lists source schemas in the order given
    (`archive-metadata.json`, `gateway-settings.json`). Pass the files in a fixed, explicit order (as
    above) so the drift check is stable across shells and locales.
  - Each `x.graphqls` is automatically paired with `x-settings.json` in the same directory.
  - **Composing into an existing archive merges into it**: source schemas not passed on the command
    line stay in the archive. Always `rm -f` the archive first (or use `--remove-source-schema`),
    otherwise a renamed or removed subgraph lingers.
  - Output is **byte-identical** for identical inputs in identical order (checked by composing twice
    and into an existing archive; same SHA-1), so `git diff --exit-code gateway/gateway.far` is a valid
    drift check.
  - The `.far` is a zip: `archive-metadata.json`, `composition-settings.json`,
    `gateway/2.0.0/gateway.graphqls`, `gateway/2.0.0/gateway-settings.json`,
    `source-schemas/<Name>/schema.graphqls` + `schema-settings.json`.
- **Per-source-schema settings:** the `*-settings.json` files above, paired by file name. Their URLs are
  baked into the archive **but the gateway overrides them from environment variables** (§4, experiment 11),
  so the archive URL is only a default.
- **Source schema names used:** `DeviceDirectory`, `Patch`, `Vulnerability`, `SoftwareInstall` (the
  string passed to `builder.AddGraphQL("...")` in each subgraph; must match the gateway's client names).

## §4 Gateway registration

- **Registration API** (`spike/Gateway/Program.cs`):
  ```csharp
  var gateway = builder
      .AddGraphQLGateway()
      .AddFileSystemConfiguration(Path.Combine(AppContext.BaseDirectory, "gateway.far"))
      .DisableIntrospection(false)             // HC16 default security turns it off outside Development
      .ModifyRequestOptions(o =>
      {
          o.CollectOperationPlanTelemetry = true;
          o.DefaultErrorHandlingMode = HotChocolate.Language.ErrorHandlingMode.Propagate;
      });

  foreach (var name in new[] { "DeviceDirectory", "Patch", "Vulnerability", "SoftwareInstall" })
  {
      var key = $"SUBGRAPH_{name.ToUpperInvariant()}_URL";   // SUBGRAPH_DEVICEDIRECTORY_URL, ... (matches P2C)
      var url = builder.Configuration[key] ?? throw new InvalidOperationException($"{key} is not set.");
      builder.Services.AddHttpClient(name, c => c.Timeout = timeout)
          .AddHttpMessageHandler<ForwardAuthorizationHandler>();
      gateway.AddHttpClientConfiguration(name, new Uri(url));   // HttpClient name == source-schema name
  }
  // ...
  app.MapGraphQL();
  ```
- **How the archive is loaded:** `AddFileSystemConfiguration(path)` (not `AddFileConfiguration`). P4's
  `GATEWAY_ARCHIVE` env var can feed `path`; the spike hard-codes `AppContext.BaseDirectory/gateway.far`. The csproj copies `gateway.far` to
  the output/publish directory (`<None Update="gateway.far" CopyToOutputDirectory="PreserveNewest"
  CopyToPublishDirectory="PreserveNewest" />`), so the Docker image only needs the published output.
- **Subgraph URLs:** `SUBGRAPH_<NAME>_URL` env vars, applied with `AddHttpClientConfiguration`. Code-level
  client configurations are registered after the archive's and replace them by name (verified in
  experiment 11: with the extender healthy but `SUBGRAPH_EXTENDER_URL=http://nowhere.invalid:8080/graphql`,
  `notes` fails). **Every source schema must get an `AddHttpClientConfiguration` call** (see §5). The
  spike throws at startup when a URL is missing; P4's placeholder URL for a missing variable is equally
  fine, as long as the call is made for every name.
- **Nitro UI:** served by `MapGraphQL()` on **GET `/graphql/`**; GET `/graphql` answers `301` →
  `/graphql/`. The IDE and its assets are embedded in the package (relative `./assets/...` URLs,
  no CDN), so it works offline. The edge-auth middleware only rejects unauthenticated **POST**s, so the
  UI page loads without a token; the IDE's own POSTs (introspection, queries) need an
  `Authorization: Bearer <jwt>` header set in the Nitro connection settings. A separate `/nitro` path
  (plan §4.3) is not needed.
- **Health endpoint:** plain ASP.NET Core health checks, `builder.Services.AddHealthChecks();
  app.MapHealthChecks("/health");`, probed by
  `wget -q -O /dev/null http://localhost:8080/health` on the `-alpine` image. The gateway is healthy
  without any subgraph running (it loads the archive, not live schemas).
- **Edge auth:** `app.UseAuthentication()` + a small middleware returning `401` for an unauthenticated
  `POST /graphql` (experiment 7: 401, empty body, no `WWW-Authenticate` header, no subgraph saw the
  request). No `tenantId` / `services` logic at the gateway.

## §5 Header forwarding

- **Mechanism that worked:** `IHttpClientFactory` named clients + a `DelegatingHandler` reading
  `IHttpContextAccessor`. Experiment 3: the extender logs `Authorization header present: True` for every
  gateway call; gateway logs show `System.Net.Http.HttpClient.Extender.*` / `HttpClient.Owner.*`, i.e.
  the per-subgraph named clients are the ones in use.
- **How Fusion v2 picks the `HttpClient`:** `IHttpClientFactory.CreateClient(configuration.HttpClientName)`.
  - A source schema configured **only by the archive** uses the client name `"fusion"`
    (`HttpSourceSchemaClientConfiguration.DefaultClientName`) — one shared client for every subgraph —
    unless its settings file sets `transports.http.clientName`.
  - `gateway.AddHttpClientConfiguration(name, uri)` sets the client name to the source-schema name.
  - **Pitfall:** if a source schema falls back to an unregistered `"fusion"` client, you get a default
    `HttpClient`: no `Authorization` header (every subgraph answers as anonymous) and the 100 s default
    timeout. The spike therefore registers every source schema in code and deliberately does **not**
    register a `"fusion"` client, so a missing registration fails loudly instead of silently.
- **Code snippet:**
  ```csharp
  builder.Services.AddHttpContextAccessor();
  builder.Services.AddTransient<ForwardAuthorizationHandler>();

  public sealed class ForwardAuthorizationHandler(IHttpContextAccessor accessor) : DelegatingHandler
  {
      protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
      {
          var auth = accessor.HttpContext?.Request.Headers.Authorization.ToString();
          if (!string.IsNullOrEmpty(auth) && request.Headers.Authorization is null)
          {
              request.Headers.TryAddWithoutValidation("Authorization", auth);
          }
          return base.SendAsync(request, ct);
      }
  }
  ```
- **Alternative (not needed):** `AddHttpClientConfiguration(..., onBeforeSend: (ctx, node, request) => ...)`
  gives access to the outgoing `HttpRequestMessage` per execution node.

## §6 Error handling

- **Option name and value chosen:** `FusionRequestOptions.DefaultErrorHandlingMode =
  ErrorHandlingMode.Propagate` (it is also the default; set explicitly), with
  `AllowErrorHandlingModeOverride = false` for the real gateway. The enum is
  `HotChocolate.Language.ErrorHandlingMode` and has only two values in 16.6.6: `Propagate` and `Null`
  (**no `Halt`**). When override is allowed, a request may send `"onError": "NULL" | "PROPAGATE"`.
  Subgraph settings files also carry `capabilities.onError: "propagate"` (exported by default).
- **Experiment 9 result:** for the nullable extension field, `PROPAGATE` and `NULL` give identical
  responses (outage and denial). For a **non-null** guarded field (`noteCount: Int!`, spike only),
  `PROPAGATE` nulls the whole `device` (`{"data":{"device":null}}`) for both a denial and an outage,
  while `NULL` keeps `device` and nulls only the field. This is the plan §4.2 nullability rule, observed.
  `Propagate` + nullable extension fields is the combination the POC uses.
- **Timeout mechanism and value:** `HttpClient.Timeout` on each named subgraph client, **5 s**
  (`SUBGRAPH_TIMEOUT_SECONDS`). Overall request cap is `FusionRequestOptions.ExecutionTimeout`
  (default 30 s, left unchanged).
- **Behaviour on client timeout: field error; the rest of the request is NOT cancelled.** Experiment 5
  (`docker compose pause extender`): HTTP 200 after 5.26–5.32 s (three runs) with `device` intact and an error at
  `["device","notes"]`. Why it works: `OperationExecutionNode` only treats
  `OperationCanceledException` as cancellation `when (cancellationToken.IsCancellationRequested)`; an
  `HttpClient` timeout throws `TaskCanceledException` while the request token is *not* cancelled, so it
  falls into the generic `catch (Exception)` and becomes a field error. **No fix or handler wrapper needed.**
- **Stopped container:** fails fast with `HttpRequestException: Name does not resolve (extender:8080)`
  (logged by `HttpClient.Extender.LogicalHandler[104]`); response in 7–70 ms.

## §7 Fixtures

Files in `tests/fixtures/` — the four JSON bodies are the raw, unmodified gateway responses; the query
was `{ device(id:"dev-00001") { id hostname notes } }` (`notes` = the extender's nullable list field):

| File | Scenario | Content |
|---|---|---|
| `outage-stop.json` | extender stopped, full-access token | HTTP 200, `device` intact, `notes: null`, one error at `["device","notes"]`, no `extensions` |
| `outage-pause.json` | extender paused (5 s timeout), full-access token | identical to `outage-stop.json`, HTTP 200 after ≈5.3 s |
| `denied.json` | extender healthy, token without the `extender` service | HTTP 200, `device` intact, `notes: null`, error at `["device","notes"]` with `extensions.code = "AUTH_NOT_AUTHORIZED"` |
| `unauthenticated.json` | no token | **not a raw body** (the raw body is empty): a JSON record `{ httpStatus: 401, headers: {...}, body: "" }` of the raw response |
| `cross-tenant.json` | TenantB token, TenantA device | HTTP 200, `{"data":{"device":null}}`, no `errors` |

- **Path shape observed:** exactly `["device","notes"]` for both outage and denial — the field itself,
  not deeper. The UI's prefix match still applies (for list fields a deeper path may appear in other
  cases).
- **Code observed for outage (transport) errors: none.** The outage error is
  `{"message":"Unexpected Execution Error","path":["device","notes"]}` — **no `extensions` object at
  all**, for both the stopped and the hung case. The denial is
  `{"message":"The current user is not authorized to access this resource.","path":["device","notes"],"extensions":{"code":"AUTH_NOT_AUTHORIZED"}}`.
  So the two differ in `message` and in the presence of `extensions.code` (plan §7 assumed both carry a
  code). The UI rule "`AUTH_NOT_AUTHORIZED` → no access; any other error, including one with no code →
  unavailable" works as written. The UI must not require `extensions` to exist.

Experiment outcomes (`spike/results/summary.txt`, run of 2026-09-18):

| # | Experiment | Outcome |
|---|---|---|
| 1 | Happy path | PASS — `notes: ["note for dev-00001 in TenantA"]`, no errors |
| 2 | Lookup hidden | PASS — gateway `Query` fields `["device","devices"]`; direct `deviceById` → 400 "does not exist" |
| 3 | Header forwarded | PASS — extender logs `Authorization header present: True` |
| 4 | Outage, stopped | PASS — HTTP 200 in 7–70 ms, shape as above |
| 5 | Outage, hung | PASS — HTTP 200 in 5.26–5.32 s, same shape, request not cancelled |
| 6 | Denied | PASS — HTTP 200, `AUTH_NOT_AUTHORIZED` at `["device","notes"]` |
| 7 | Unauthenticated | PASS — 401, empty body, zero subgraph POSTs |
| 8 | Cross-tenant | PASS — `device: null`, no `errors` |
| 9 | Error mode | PASS — `Propagate` chosen; `Null` only differs for non-null fields |
| 10 | Nitro UI | PASS — `GET /graphql` → 301 → `/graphql/` 200 `text/html`, embedded assets 200 |
| 11 | URL override (extra) | PASS — `SUBGRAPH_<NAME>_URL` beats the archive URL |

## §8 Deviations (append-only, all phases)

| Date | Phase | Document said | Reality | Action |
|---|---|---|---|---|
| 2026-09-18 | P0 | `dotnet new tool-manifest` creates `.config/dotnet-tools.json` | The .NET 10 SDK creates `dotnet-tools.json` at the repo root | Moved it to `.config/dotnet-tools.json` (still discovered by `dotnet tool restore`) |
| 2026-09-18 | P0 | Gateway published on host port `5000` (spike compose, TB checkpoint, plan examples) | On macOS, port 5000 is held by AirPlay Receiver (`ControlCenter`), so `5000:8080` fails to bind | Spike publishes `${SPIKE_GATEWAY_PORT:-5050}:8080`. **P2C/P4/P6:** make the host port a variable (e.g. `${GATEWAY_PORT:-5050}`) and update the TB checkpoint command |
| 2026-09-18 | P0 | Source-schema package ⚠️ VERIFY (`HotChocolate.Fusion.SourceSchema` / `HotChocolate.Types.Composite`) | No extra package. Attributes are in namespace `HotChocolate.Types.Composite` in `HotChocolate.Types` 16.6.6 | Reference only `HotChocolate.AspNetCore` (+ `.Authorization`, `.CommandLine`) |
| 2026-09-18 | P0 | `builder.Services.AddGraphQLServer()` on subgraphs | Use `builder.AddGraphQL("<SourceSchemaName>")`; the name ends up in the exported settings and is the source-schema name | Spike uses `AddGraphQL("Owner")` / `AddGraphQL("Extender")` |
| 2026-09-18 | P0 | `nitro fusion compose --source-schema-file schemas/*.graphqls --archive gateway/gateway.far` | Works as written (shell-expanded), but archive bytes depend on input order, settings are paired by `<name>-settings.json`, and composing into an existing archive **merges** | Compose script lists the four files in a fixed order and deletes the archive first (§3) |
| 2026-09-18 | P0 | Subgraph URLs "settings file vs. flag ⚠️ VERIFY" | Settings file baked into the archive, overridable in code with `AddHttpClientConfiguration(name, uri)` | Gateway takes URLs from `SUBGRAPH_<NAME>_URL` (§4) |
| 2026-09-18 | P0 | Named `HttpClient` per subgraph via `AddHttpClient(name)` is enough | Archive-configured schemas all use one client named `"fusion"`; a per-name client is only used after `AddHttpClientConfiguration(name, uri)` | Register both for every subgraph; no `"fusion"` fallback (§5) |
| 2026-09-18 | P0 | Fusion error-handling mode "propagate / null / halt or similar" | `ErrorHandlingMode.Propagate` / `Null` only, via `FusionRequestOptions.DefaultErrorHandlingMode` | `Propagate` (§6) |
| 2026-09-18 | P0 | Outage error carries an `extensions.code` (plan §7 expected shape) | Outage errors have **no `extensions`**; message `"Unexpected Execution Error"` | UI treats "no code" as unavailable. P4 may add a gateway error filter that stamps a code (e.g. `SUBGRAPH_UNAVAILABLE`) — optional |
| 2026-09-18 | P0 | Nitro UI "mapped on a separate path (`/nitro`)" | `MapGraphQL()` serves Nitro on `GET /graphql/`; the POST-only edge check already leaves it reachable | No separate path needed; Nitro users paste a token into the connection's headers |
| 2026-09-18 | P0 | (not mentioned) | HC 16 default security disables introspection outside `Development`, on subgraphs and gateway | Gateway calls `.DisableIntrospection(false)` |
| 2026-09-18 | P0 | Spike Dockerfile per project | One `spike/Dockerfile` with `--build-arg PROJECT=<name>` | Spike only; real lanes follow P2C's template |
| 2026-09-18 | P0 | Extender has only the nullable `notes` field | Spike extender also has a **non-null** `noteCount: Int!` behind the same policy | Spike only, used by experiment 9 to demonstrate the nullability rule. Never copy it into a real subgraph |
| 2026-09-18 | P0 | P4: `AddFileConfiguration(...)`, `.ModifyOptions(o => o.ErrorHandlingMode = ...)` | `AddFileSystemConfiguration(path)`; `.ModifyRequestOptions(o => o.DefaultErrorHandlingMode = ErrorHandlingMode.Propagate)` | Use the §4 snippet |
| 2026-09-18 | P0 | P4 drift check: "`.far` is a zip; compare contents, not bytes (zip timestamps vary)" | Nitro 16.6.6 writes byte-identical archives for identical inputs given in the same order | Byte comparison (`git diff --exit-code gateway/gateway.far`) works with a fixed input order; content comparison also still fine |
| 2026-09-18 | P0 | `tests/fixtures/unauthenticated.json` is a raw body | The 401 body is empty, so the file records `{httpStatus, headers, body}` | Documented in §7 |
| 2026-09-18 | P1 | `.env`: `GATEWAY_PORT=5000`; `contracts/http-and-env.md`: gateway host port 5000 | Port 5000 is taken on macOS (P0 row above) | `.env` sets `GATEWAY_PORT=5050`; the contract records 5050. **P2C/P6:** default `${GATEWAY_PORT:-5050}` in scripts |
| 2026-09-18 | P1 | `contracts/http-and-env.md`: gateway endpoints include `/nitro` | Nitro is served by `MapGraphQL()` at `GET /graphql/` (§4) | Contract lists `POST /graphql`, `GET /graphql/`, `/health`; no `/nitro` |
| 2026-09-18 | P1 | Contract SDL declares `type Device @key(fields: "id")` | Fusion v2 derives the key from the lookup argument (§2); the hand-written SDL composes without `@key` | `@key` removed from all four contract files. Lanes add no `[Key]` |
| 2026-09-18 | P1 | Contract lists nine files; P4 part 1 may need to prepend directive definitions or keep settings in `gateway/source-schemas/` | The composer needs a `<name>-settings.json` next to each `<name>.graphqls` (fails with "Schema settings file ... does not exist"), but needs **no** directive definitions: the four contract SDL files compose unedited with Nitro 16.6.6 | Added `contracts/{device-directory,patch,vulnerability,software-install}-settings.json` (name + in-compose URL). `compose-schema.sh --from-contracts` can pass `contracts/*.graphqls` directly |
| 2026-09-18 | P1 | `patches(...): [Patch!]!` and `cves(...): [Cve!]!` (non-null), guarded by the service policy | A denial on a non-null field propagates to the nearest nullable parent (§6, experiment 9); for a root field that is `data` itself, which would also contradict P3B's `Denied_on_cves_root_field` (`cves == null`) | Both root catalogs are nullable lists in the contract (`[Patch!]`, `[Cve!]`). The three extension fields are unchanged |
| 2026-09-18 | P1 | `DeviceCatalog.Build` creates `new Faker("en") { Random = new Randomizer(index) }` per device | Constructing a full `Faker` costs about 0.26 ms per device (3–5 s per `All()` call, paid by every subgraph at startup and in tests) | `Build` constructs only the `Hacker` and `Internet` datasets on one per-index `Randomizer`: same draws in the same order, output identical for all 12 000 devices (checked against the `Faker` version, and pinned by `golden-devices.json`), `All()` about 40 ms |
| 2026-09-18 | P1 | `DeviceCatalog.TryGetIndex` parses any integer after `dev-` | `int.TryParse` accepts `dev-42`, `dev-+0042`, `dev- 0042`, so several strings would map to one device | Only the canonical five-digit form is accepted; test `TryGetIndex_rejects_non_canonical_ids` |
| 2026-09-18 | P1 | Solution file `SoR.sln` | `dotnet new sln` in the .NET 10 SDK creates `SoR.slnx` by default | Created with `dotnet new sln --format sln`; the solution is `SoR.sln` as every doc and script expects |
| 2026-09-18 | P1 | `Directory.Packages.props` pins with `Version="X"` | Versions chosen on 2026-09-18: EF Core / HealthChecks.EFCore / Mvc.Testing `10.0.12`, Npgsql + Npgsql.EFCore `10.0.3`, MongoDB.Driver `3.12.0`, Azure.Storage.Blobs `12.29.2`, Bogus `35.6.5`, Microsoft.NET.Test.Sdk `18.10.1`, xunit `2.9.3` (v2), xunit.runner.visualstudio `3.1.5`, Testcontainers.* `4.15.0` | Pinned; lanes append only |
| 2026-09-18 | P2C | phase-2c §4 init script leaves the roles on the default `search_path` (`"$user", public`); §7 step 3 expects `select current_schema()` as `devdir_user` → `device_directory` | With the default, `current_schema()` is `public` for both roles (checked: `set search_path = "$user", public; select current_schema()` → `public`). Also: the official image trusts the Unix socket and `127.0.0.1`, so `docker compose exec postgres psql -U devdir_user` never checks a password | Init script adds `ALTER ROLE devdir_user SET search_path = device_directory` and `ALTER ROLE vuln_user SET search_path = vulnerability`, and passes passwords as psql variables (`:'devdir_pw'`) instead of shell interpolation. Connection strings keep `Search Path=`. Verified: cross-schema `select` and `create` → `permission denied for schema ...`, `create` in `public` denied, `create schema` denied; password auth checked from another container on `sor-poc_internal` (wrong password → `password authentication failed`). **P2B/P3B:** add the two `ALTER ROLE` lines to your test copy of the script |
| 2026-09-18 | P2C | Postgres healthcheck `pg_isready -U postgres -d sor` | Over the Unix socket this reports ready while the entrypoint's temporary server is still running `docker-entrypoint-initdb.d` (checked with a 12 s init script: socket probe ready at 3 s, TCP probe only after init); the temporary server does not listen on TCP | Healthcheck is `pg_isready -h 127.0.0.1 -U postgres -d sor`, so `service_healthy` means the roles and schemas exist |
| 2026-09-18 | P2C | phase-2c §6 `wait-healthy.sh` | `docker compose ps -q` omits stopped containers, and `{{if .State.Health}}` hides `exited` for a container with a healthcheck (a stopped azurite reports `unhealthy`), so the `exited` branch never fires and the script waits until timeout. `ps -a` also lists one-off `docker compose run` containers. An unpaused container passes through `unhealthy` before `healthy` | Script uses `ps -a`, skips `oneoff=True` containers, checks `.State.Status` before health: fails fast on exited/dead (a one-shot without healthcheck that exited 0 counts as `completed`), keeps waiting on `starting`/`unhealthy`/`paused`, rejects unknown service names up front |
| 2026-09-18 | P2C | phase-2c §5/§6: template lives in the phase doc; `.dockerignore` lists `ui/dist`; `up.sh` echoes `GATEWAY_PORT:-5000` and `/nitro` | Template committed as `infra/docker/Dockerfile.template`. The `angular-ui` build uses context `./ui`, where the root `.dockerignore` does not apply | Render with `sed -e '/^##/d' -e 's/<Name>/X/g' infra/docker/Dockerfile.template > src/X/Dockerfile` (`##` lines are template notes). Root `.dockerignore` excludes all of `ui/`, `.claude`, `.codegraph`, `.github`, root `*.md` and `.env*` as well (root context ≈ 80 kB). **P5:** add `ui/.dockerignore` (`node_modules`, `dist`, `.angular`). `up.sh` reads ports from the shell, then `.env`, default 5050/4200, prints Nitro at `/graphql/`, and accepts a service subset |
| 2026-09-18 | P2C | phase-2c §3 compose text | `mongo:8` resolved to MongoDB 8.3.11 and `azurite:latest` to 3.37.0 on 2026-09-18 (both healthy with the §3 healthchecks) | Added `--wiredTigerCacheSizeGB 0.25` to mongo (default cache is about half the Docker VM's RAM). Key and passwords use `${VAR:?}` so a missing `.env` fails `docker compose config`; ports and timeout default (`${GATEWAY_PORT:-5050}`, `${UI_PORT:-4200}`, `${SUBGRAPH_TIMEOUT_SECONDS:-5}`). `token-generator` also gets `USERS_FILE=/app/users.json` (required env in `contracts/http-and-env.md`) |
| 2026-09-18 | P2C | phase-2a §6 Dockerfile has no `USER` line; `contracts/http-and-env.md` says every image is non-root | A fresh named volume mounted at a path the image does not contain is `root:root 755`, so a non-root process cannot write `tokens.json` (checked: `--user app` → `Permission denied`); if the image pre-creates the directory owned by `app`, the new volume inherits that ownership (checked) | **P2A:** if the image runs as `app`, add `RUN mkdir -p /tokens && chown app:app /tokens` before `USER app` (the `runtime:10.0-alpine` image has the `app` user). Running as root also works with the compose entry as is |
| 2026-09-18 | P2A | TokenGenerator runtime image `mcr.microsoft.com/dotnet/runtime:<ver>-alpine` ("console app") | `Shared.Auth` carries `<FrameworkReference Include="Microsoft.AspNetCore.App" />` (JwtBearer), which flows into `TokenGenerator.runtimeconfig.json`; on `runtime:10.0-alpine` the app exits 150 with "Framework 'Microsoft.AspNetCore.App' ... No frameworks were found" | Runtime stage is `aspnet:10.0-alpine` (same base as every other service, so no extra layers). `USER app` (uid 1654); the image pre-creates `/tokens` owned by `app`, so a fresh named volume inherits that owner. A `tokens` volume created earlier by a root-owned image stays root-owned: `docker compose down -v` once |
| 2026-09-18 | P2A | CLI exit codes 0 / 2 / 3 / 4; "unknown service → exit 4"; skeleton mints with `iat = nbf = now` | Cases the doc does not cover | Compose mode cannot write the output → exit 1 (temp file removed). Unknown service in ad-hoc `--services` → exit 2 (an argument, not the users file). `iat`/`nbf` are backdated 5 min so a host-minted token is not "not yet valid" in a container whose clock lags (validation skew is 1 min); `exp` = now + 10 y |
| 2026-09-18 | P2B | P2B §4: generate `Data/Migrations/` with `dotnet ef migrations add Initial` | `dotnet-ef` is not in `.config/dotnet-tools.json` (P1-owned) | Installed outside the repo (`dotnet tool install dotnet-ef --version 10.0.12 --tool-path <scratch>`), ran `<scratch>/dotnet-ef migrations add Initial --output-dir Data/Migrations` in `src/DeviceDirectory` via the design-time factory (no DB, no env). Unit test `Committed_migrations_match_the_model` fails if the model drifts. Do **not** pass `--namespace`: it drops the model snapshot into a namespace-named folder |
| 2026-09-18 | P2B | P2B §6 injects the scoped `DeviceDbContext` into query resolvers | Safe only because HC 16.6.6 gives every **query** resolver its own DI scope (`DefaultQueryDependencyInjectionScope = Resolver`). With `Request` scope, alias-batched lookups (`d0: device(..) d1: device(..)`, which the gateway sends) fail with "A second operation was started on this context instance" (verified) | `.ModifyOptions(o => o.DefaultQueryDependencyInjectionScope = DependencyInjectionScope.Resolver)` pinned explicitly; test `Aliased_lookups_and_search_in_one_request_succeed`. **P3B:** same rule for `VulnerabilityDbContext` |
| 2026-09-18 | P2B | P2B §6: `[Service]` "if the pinned version requires it" | Without `[Service]`, HC 16 infers services from DI registrations, so a schema built without the app's DI (in-process schema test) would turn `DeviceDbContext` / `ICallerContext` into GraphQL arguments | `[Service]` on every injected parameter; the schema no longer depends on what DI contains |
| 2026-09-18 | P2B | Exported SDL "matches the contract" | Export adds `@cost(weight: "10")` on the two async root fields (HC 16 cost analysis), `@authorize` on `Query`, directive definitions, and `scalar DateTime @specifiedBy(url: "https://scalars.graphql.org/chillicream/date-time.html")`. Composing it with the other three **contract** files succeeds with one warning `SPECIFIED_BY_URL_MISMATCH` (contract `scalar DateTime` has no URL); it disappears once every subgraph's real export is used | Conformance checked structurally in `SchemaTests` (contracts/README.md). The export's settings file is re-indented vs `contracts/device-directory-settings.json` but has identical content (compose URL kept) |
| 2026-09-18 | P2B | (not mentioned) | Npgsql 10 defaults to `GSS Encryption Mode=Prefer` and probes `libgssapi_krb5.so.2`, which `aspnet:10.0-alpine` lacks: every start printed `Error loading shared library libgssapi_krb5.so.2` | `DeviceDbContext.ConfigureNpgsql` sets `GssEncryptionMode = Disable` on the configured connection string (compose value unchanged). **P3B:** same fix |
| 2026-09-18 | P2B | (not mentioned) | On an empty schema, EF Core 10 / Npgsql probes the history table before creating it and logs one `fail: Microsoft.EntityFrameworkCore.Database.Command[20102] Failed executing DbCommand ... FROM device_directory.__ef_migrations_history`; the migration then succeeds | Harmless, first boot only; noted in `SeedHostedService`. `appsettings.json` lowers other EF Core / ASP.NET Core categories to `Warning` |
| 2026-09-18 | P3A | P3A §7: `public Device GetDeviceById(...)`; C# type `Patch` | With `<Nullable>enable</Nullable>` that exports `deviceById(id: ID!): Device!`, but `contracts/patch.graphqls` declares a nullable `Device` (the spike used `Device?`). A C# type named `Patch` is shadowed by the `SoR.Patch` namespace everywhere outside `SoR.Patch.GraphQL` | `GetDeviceById` returns `Device?` and always returns the stub (never null); `SchemaTests.DeviceById_is_an_internal_lookup` pins the contract type. The contract's `Patch` type is the C# record `PatchInfo` with `[GraphQLName("Patch")]` |
| 2026-09-18 | P3A | P3A §6: ping → ensure indexes → marker check → drop `patch_events` / `patches` → insert | Dropping a collection drops its indexes, so indexes created before the discard step are gone after every (re)seed | Indexes are created after the drop, before the bulk insert, and again (idempotent) on the skip path. `Partial_seed_without_marker_is_discarded_and_redone` checks `tenantId_1_deviceId_1_occurredAt_-1` exists after a reseed |
| 2026-09-18 | P3A | P3A §7: `new MongoClient(connectionString)` with driver defaults | The driver's `ServerSelectionTimeout` (30 s) equals Hot Chocolate's `ExecutionTimeout` (30 s): with `mongo` stopped, a `patchEvents` query in the container failed as a whole with **HTTP 500** after 30 s instead of a field error | `MongoOptions.ToClientSettings()` uses 3 s unless the connection string sets `serverSelectionTimeoutMS` (compose value unchanged). Now HTTP 200 after about 3 s, `deviceById` intact, `{"message":"Unexpected Execution Error","path":["deviceById","patchEvents"]}` with no `extensions` (the gateway's outage shape). The `mongo` health check has a 2 s timeout. Not covered: a *paused* mongo (the driver's socket timeout is infinite); the gateway's 5 s subgraph timeout handles that. Test `Outage_with_default_timeouts_is_a_field_error_not_a_failed_request` |
| 2026-09-18 | P3A | P3A §6: seeding takes 10–30 s. §7: sort by `occurredAt` descending | Measured: 209 907 events + 300 patches in 5 000-event unordered `InsertMany` batches took 1.3–1.6 s on a warm Docker and 2.7 s from an empty volume (OrbStack, 2 CPUs); in the container `/health` went 503 → 200 about 4 s after start. Minute-resolution timestamps can tie | Sort is `occurredAt` descending, then `_id` ascending, so the order is deterministic (the index serves the filter; at most 30 docs per device are sorted in memory). `since` / `until` are rounded to MongoDB's millisecond precision (since up, until down), so the bounds stay exactly inclusive |
| 2026-09-18 | P3B | phase-3a §7 pattern (which P3B §8 follows): `public Device GetDeviceById([ID] string id) => new(id);` | A non-nullable C# return type exports `deviceById(id: ID!): Device!`, but every contract declares `deviceById(...): Device` (nullable), so `Schema_matches_contract` fails | `public Device? GetDeviceById(...)`: nullable in the schema as the contract says, yet it always returns a stub, never null. **P3A/P3C:** same (`Device?`) |
| 2026-09-18 | P3B | phase-3b §4 `cvss_score numeric(3,1)`; contract `cvssScore: Float!` | EF maps `numeric(3,1)` to C# `decimal`, and HC 16 infers its `Decimal` scalar for `decimal`, not `Float` | `CveEntity.CvssScore` is `decimal` (`HasPrecision(3, 1)`); the GraphQL `Cve` record (`GraphQL/Types.cs`) is separate from the entity and carries `double`, so the export says `Float!`. Scores have one decimal, so the conversion is exact for display |
| 2026-09-18 | P3B | phase-3b §7: filter findings on `detected_at` / `remediated_at` "between bounds", bounds default to -infinity/+infinity | Npgsql 6+ writes a `timestamptz` parameter only from a UTC value; a `since`/`until` sent with any other offset (e.g. `+05:30`) fails the field with `Cannot write DateTimeOffset with Offset=05:30:00 to PostgreSQL type 'timestamp with time zone', only offset 0 (UTC) is supported` (verified) | `FindingRepository` converts `since`/`until` with `ToUniversalTime()` and adds each bound only when given (no ±infinity sentinels). Tests `Since_until_accept_any_utc_offset`, `Since_until_filter_includes_remediated_events_in_range` (fails if the `remediated_at` branch of the OR is removed) |
| 2026-09-18 | P3B | phase-3b §4 lists two `findings` indexes; version-facts P2B row names the context `VulnerabilityDbContext` | EF also creates `IX_findings_cve_id` for the required FK `findings.cve_id -> cves.id`. Phase-3b §3 names the context `VulnDbContext` | Kept the FK index (COPY of ~102 500 rows still seeds in 1–2 s cold). Context is `VulnDbContext`; `DefaultQueryDependencyInjectionScope = Resolver` is pinned in `VulnerabilitySchema`, and `Aliased_lookups_and_cves_in_one_request_succeed` fails with "A second operation was started on this context instance" when it is switched to `Request` (checked) |
| 2026-09-18 | P3B | version-facts P2C row: "add the two `ALTER ROLE` lines to your test copy of the script" | `tests/DeviceDirectory.Tests/Postgres/01-roles-and-schemas.sh` (P2B) still has the pre-P2C-fix text (no `ALTER ROLE`, passwords through shell interpolation) | `tests/Vulnerability.Tests/Postgres/01-roles-and-schemas.sh` is a verbatim copy of `infra/postgres/init/01-roles-and-schemas.sh` below a header comment; unit test `Embedded_init_script_matches_the_infra_script` pins the two together. The P2B copy is P2B-owned and left as is |
| 2026-09-18 | P3C | P3A §7 / P3C §8: `public Device GetDeviceById([ID] string id) => new(id);` | With `<Nullable>enable</Nullable>` a non-nullable C# return exports `deviceById(id: ID!): Device!` (checked: `Schema_matches_contract` fails with `Device! != contract Device`); the contract has nullable `Device` | `public Device? GetDeviceById(...)`, still always returning a stub. **P3A/P3B:** same |
| 2026-09-18 | P3C | P3C §7: read `container.GetBlobClient($"{tenant}/{deviceId}/installEvents.json")` with the `deviceId` argument as given | `GetBlobClient` builds the URL through `System.Uri`, which removes dot segments: a TenantA caller asking for `deviceById(id: "../TenantB/dev-07000")` gets blob name `TenantB/dev-07000/installEvents.json` (checked with Azure.Storage.Blobs 12.29.2: both `BlobClient.Name` and `Uri` point into TenantB), i.e. a cross-tenant read despite the prefix layout | `InstallEventsBlobStore.TryGetBlobName` maps only canonical device ids (`DeviceCatalog.TryGetIndex`) and alphanumeric tenant ids to a blob; anything else is "no blob" → `[]`. A document whose `tenantId`/`deviceId` differ from the requested pair fails the read. Tests `Only_canonical_ids_map_to_a_blob`, `Unknown_or_non_canonical_id_is_a_stub_with_no_events` |
| 2026-09-18 | P3C | P3C §7: `catch RequestFailedException when Status == 404: return null` | 404 is also `ContainerNotFound` (wrong `Blob__Container`, container deleted), which would turn a broken deployment into "no events" | Only `ErrorCode == BlobNotFound` maps to `[]`; every other storage failure is a field error (the UI's "unavailable") |
| 2026-09-18 | P3C | P3C §4: "enums as strings" | `JsonStringEnumConverter` writes the C# member name (`Install`), not the §4 example's `INSTALL` | `JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseUpper, allowIntegerValues: false)`: stored value = GraphQL enum value. `RespectNullableAnnotations` + `RespectRequiredConstructorParameters` make a blob with a missing or null field fail the read (field error) instead of feeding a null into a non-null GraphQL field |
| 2026-09-18 | P3C | `contracts/seeding.md`: marker absent → "discard any partial data and seed again"; P3C §6: overwrite, no delete pass | Overwriting every device blob replaces whatever a partial run wrote under those names; a stray blob under another name would survive, but the read path never resolves a non-canonical name, so it is unreachable | Followed P3C §6 (upload all 12 000 with overwrite, marker last). Test `Partial_seed_without_marker_is_redone` (marker, blobs and a torn blob removed → all restored) |
| 2026-09-18 | P3C | P3C §10: `Testcontainers.Azurite` | `AzuriteBuilder`'s default image is `azurite:3.28.0`, older than compose's `azurite:latest` (3.37.0), and it does not pass `--skipApiVersionCheck` | Tests use `new AzuriteBuilder("mcr.microsoft.com/azure-storage/azurite:latest").WithInMemoryPersistence().WithCommand("--skipApiVersionCheck")` (the command is appended to the module's). Cold seed of 12 000 blobs with 32 concurrent PUTs: 10.5–14 s in-memory (tests), 20.0 s in compose (disk-backed `azuritedata` volume), far under the 300 s `start_period` |
| 2026-09-18 | P4 | phase-4 §4/§9: unauthenticated `POST /graphql` → `401` with body `{"errors":[{"message":"Unauthorized","extensions":{"code":"AUTH_NOT_AUTHENTICATED"}}]}` | `contracts/http-and-env.md` and `contracts/errors.md` (frozen) say `401`, **empty body**, no `WWW-Authenticate`; fixture `unauthenticated.json` records exactly that | Followed the contract. `EdgeAuthMiddleware` rejects, without a valid token, any request to `/graphql` or `/graphql/` that could run an operation: every method except GET/HEAD, and GET/HEAD with a `query` parameter. `EdgeAuthTests` cover no token, wrong key, expired, not-a-JWT, `POST /graphql/`, `GET ?query=`, and assert that no (fake) subgraph saw a request |
| 2026-09-18 | P4 | phase-4 §4: "`/health` 200" once the archive is loaded; version-facts §4: the gateway is healthy without subgraphs | `AddFileSystemConfiguration(path)` never fails on a bad archive: for a missing file (in an existing directory) or a corrupt one, Fusion's warm-up waits for a usable archive **forever** (Kestrel never listens, the process does not exit; checked 40 s, then `SIGTERM` → `TaskCanceledException` in `FusionRequestExecutorManager.GetSchemaDocumentAsync`). Only a nonexistent *directory* fails, with `ArgumentException` from the file watcher | `GatewayArchive.EnsureUsableAsync` runs before the host is built: `File.Exists`, then `FusionArchive.Open(path, Read)` + `GetLatestSupportedGatewayFormatAsync` + `TryGetGatewayConfigurationAsync` (`HotChocolate.Fusion.Packaging`, transitive). A bad archive exits at once (code 134) naming the path and `scripts/compose-schema.sh`. `SchemaTests.Unusable_archive_fails_startup` (missing, not a zip, zip without a gateway configuration). `/health` stays the plain check plus `auth-config` |
| 2026-09-18 | P4 | phase-4 §3/§6: `gateway.far` reaches the app via the Dockerfile only; `SUBGRAPH_*_URL` missing → placeholder `http://localhost:0/<Name>` | `dotnet run` and the tests need the archive next to the assembly too; the Docker build stage has no `gateway/` | `Gateway.csproj` links `../../gateway/gateway.far` as `gateway.far` (copy to output/publish, `Condition="Exists(...)"`); the Dockerfile copies it into the runtime stage and sets `GATEWAY_ARCHIVE=/app/gateway.far`. Missing URL → `http://localhost:0/unconfigured/<Name>` plus one startup warning naming the variables. `SUBGRAPH_TIMEOUT_SECONDS` ≤ 0 fails startup |
| 2026-09-18 | P4 | phase-4 §5 drift check: `diff -r` of `schemas/`, unzip + `diff -r` of the archive | Byte comparison is exact (P0 row above) | `cmp` on the archive; on a difference it unzips both and prints `diff -r` of the entries. A missing `gateway/gateway.far` is drift. Checked both failure modes: a stale `schemas/patch.graphqls` and a contract-composed archive each exit 1 and leave the regenerated files in place. `compose-schema.sh` runs `dotnet tool restore` and rejects unknown options (exit 2) |
| 2026-09-18 | P4 | phase-4 §9: `Partial_failure_shape` / `Denial_shape_through_gateway` are Docker tests, or deferred to P6 | The same shapes can be reproduced without Docker: Fusion sends plain lookups (`query Op_…_1 { device(id: "dev-00001") { id hostname } }` to Device Directory, `deviceById(id: $__fusion_1_id) { patchEvents { id } }` to Patch) that a canned JSON answer satisfies | Both: `TransportTests` run the real `Program` against in-process fake subgraphs on `127.0.0.1` (exact `Authorization` value per subgraph, URL override, stopped = connection refused, hung = 1 s timeout → field error in 1.0–1.1 s, denial code kept and path rewritten `["deviceById","installEvents"]` → `["device","installEvents"]`, Device Directory down → `device: null` + error). `StackTests` (`Category=Integration`) run the §9 Docker scenarios against compose via `scripts/up.sh` (without `angular-ui`, which has no Dockerfile before P5) and `scripts/demo-outage.sh patch stop|pause|restore`; services the fixture started are stopped afterwards |
| 2026-09-18 | P4 | version-facts P0 row: optional gateway error filter stamping `SUBGRAPH_UNAVAILABLE` on outage errors | `contracts/errors.md` (frozen) documents outage errors **without** `extensions`, and the UI rule already handles "no code" | Not added; the gateway passes subgraph errors through unchanged |
| 2026-09-18 | P5 | phase-5 §3: "Angular current LTS"; `ng new --standalone --ssr false`; `ng test` in headless Chrome | Angular's LTS lines are 20 and 21; the current release is 22.1. `ng new` 22 defaults to standalone, zoneless, the 2025 file-name style, and **Vitest + jsdom** instead of Karma | Angular 22.1 (latest stable, supported by apollo-angular 14.2 and Material 22). Kept Vitest + jsdom: no browser needed, `ng test` unchanged. File names follow the phase doc (`session.service.ts`, `device-search.page.ts`) |
| 2026-09-18 | P5 | (not mentioned) | Node 25+ defines its own experimental `localStorage` global (undefined without `--localstorage-file`) that hides jsdom's: every `localStorage` call in a spec threw | `ui/src/test-setup.ts` (angular.json `test.options.setupFiles`) puts jsdom's storage back. App code already treats storage as optional (try/catch) |
| 2026-09-18 | P5 | phase-5 §3/§6: Apollo v4 exposes `result.error` (a `CombinedGraphQLErrors`) | Confirmed with `@apollo/client` 4.3.0 / `apollo-angular` 14.2.0; `ObservableQuery` emits errors as `next` values and does not terminate. Transport errors from apollo-angular's `HttpLink` are `ServerError` (with `statusCode`, e.g. 401 / 502) or a plain `Error` (status 0). Setting `errorPolicy: 'all'` in `defaultOptions` does not type-check until declared via `ApolloClient.DeclareDefaultOptions` | `timelineView()` treats `CombinedGraphQLErrors` as GraphQL errors and anything else as a transport error. `ui/src/app/graphql/apollo-default-options.d.ts` declares the defaults. **No shape differences** from `contracts/errors.md` on the live stack (Stage 4) |
| 2026-09-18 | P5 | phase-5 §2/§11: unit tests run against `tests/fixtures/*.json` | Those record the spike query (`notes` field, no `__typename`). Apollo's cache adds `__typename` to every selection set and reads results back through the cache, so fixture bodies must match the document Apollo actually sends | Copied the Phase 0 files into `ui/src/testing/fixtures/` (field-agnostic shape tests) **and** recorded the real gateway's answers to the UI's two operations, with `__typename` (Apollo's `addTypenameToDocument`), for alice/bob/carol/dave, date range, patch stop/pause, device-directory stop, bob with software-install stopped: `ui/src/testing/record-fixtures.mjs` → `fixtures/gateway/` |
| 2026-09-18 | P5 | phase-5 §4: `select(sub)` clears the Apollo store, then navigates to the current URL so queries re-run | A same-URL navigation neither recreates the component nor re-runs a `watchQuery`. Also `ApolloClient.clearStore()` defers its work to a microtask: switching the user first and clearing second let the clear cancel the **new** user's in-flight query (the page stayed on loading; caught by `DeviceTimelinePage` spec) | Pages build their query request from `session.selected()`, so a switch re-runs them without navigation. `select` awaits `clearStore()` and only then switches. `watchQuery()` tags each result with its request, so a stale result never shows |
| 2026-09-18 | P5 | phase-5 §9: dev proxy `/graphql` → `localhost:5000`; `ng serve` on the default 4200 | The gateway is on 5050 (P0/P1 rows); 4200 is the compose UI's host port | Dev server on **4300**. `proxy.conf.json` → mock (:5001); `proxy.live.conf.json` → gateway :5050 and `/tokens.json` from the compose UI (:4200), which serves the real tokens |
| 2026-09-18 | P5 | phase-5 §10: `FROM node:22-alpine`; `proxy_pass http://fusion-gateway:8080/graphql`; Google Fonts links from `ng add @angular/material` | `node:24-alpine` is the active LTS and meets the CLI's engine range. A static `proxy_pass` host is resolved once at start: Nginx fails to start without the gateway and keeps a recreated container's old IP. A regex asset location would also shadow Nitro's `/graphql/*.js`. The compose network has no internet | `node:24-alpine`. `resolver 127.0.0.11` + `set $gateway` + `location ^~ /graphql`; hashed assets `immutable`, `index.html` `no-cache`. Roboto and Material Icons bundled from npm (`@fontsource/roboto`, `material-icons`). `ui/.dockerignore` added (P2C row) |
| 2026-09-18 | P5 | Angular default budget: initial bundle warning 500 kB | Initial bundle 826 kB raw / 189 kB transferred (Apollo Client + graphql-js + Material); routes are lazy | `angular.json` production budget: warning 1 MB, error 1.5 MB |
| 2026-09-18 | P5 | phase-5 §7: date-range changes re-run the query | Material's date-range inputs update the form value on every keystroke, and `"8"` already parses as a date, so each keystroke would have queried | The range is applied on `dateChange` (calendar pick, or typed and committed by blur / Enter), only when complete and ordered, as local-day bounds (`00:00:00.000` to `23:59:59.999`) in UTC ISO |
| 2026-09-18 | P5 | plan §8: bob with SoftwareInstall stopped — "document whichever the pinned version produces" | The gateway answers `{"message":"Unexpected Execution Error","path":["device","installEvents"]}` without `extensions`: only the subgraph can deny, and it is down | The UI shows **unavailable** for bob's Software Install section while it is stopped (fixture `timeline-denied-bob-software-install-stopped.json`) |
