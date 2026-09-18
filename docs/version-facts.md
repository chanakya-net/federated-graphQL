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
