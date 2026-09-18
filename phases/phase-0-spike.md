# Phase 0 — Spike: pin versions, prove partial failure, prove denial

**Sequential. Nothing else starts until this phase's gate is met.**
**Time box:** 2 days. Decision points at 4 h and 8 h (see §7).

## 1. Purpose

Every later phase rests on four assumptions that this document cannot prove on paper:

1. The pinned Hot Chocolate / Fusion version composes offline from exported SDL files, and the gateway loads the result.
2. When a domain subgraph is **down** (stopped or hung), the gateway returns the rest of the `device` object and puts an error at the extension field's path.
3. When a subgraph **denies** a field on an authorization policy, the gateway forwards `extensions.code = "AUTH_NOT_AUTHORIZED"` unchanged, at the same path shape.
4. The `Authorization` header can be forwarded from the gateway to every subgraph.

The spike builds the smallest possible federation (two subgraphs + gateway, in-memory data, no databases), runs the experiments, and writes the answers into `docs/version-facts.md`. Everything downstream reads that file instead of guessing.

## 2. Inputs

- Plan v3 §4.2 (nullability), §4.3 (gateway), §7 (resilience), §9 Phase 0.
- Internet access for NuGet and the ChilliCream docs. This is the only phase that needs it.

## 3. Deliverables

| Path | Content |
|---|---|
| `docs/version-facts.md` | The eight sections in §8 of this document, fully filled in. |
| `tests/fixtures/outage-stop.json` | Raw gateway response with the extender container stopped. |
| `tests/fixtures/outage-pause.json` | Raw gateway response with the extender container paused. |
| `tests/fixtures/denied.json` | Raw gateway response with a token lacking the extender's service. |
| `tests/fixtures/unauthenticated.json` | Raw gateway response body (and recorded HTTP status) with no token. |
| `tests/fixtures/cross-tenant.json` | Raw gateway response for a device of another tenant. |
| `spike/` | The throwaway projects, kept for reference, excluded from the solution. |

## 4. Step 1 — Pin versions (≈1 h)

1. Choose the .NET SDK: prefer the current LTS (`net10.0`). If the newest stable Hot Chocolate does not target it, use `net9.0`. Record in `docs/version-facts.md §1`.
2. Choose the Hot Chocolate major. Preference order:
   - **Fusion v2** (composite schema spec: `@lookup`, `@internal`, `.far` archives, `nitro fusion compose`). Available from Hot Chocolate 15/16.
   - **Fallback: Fusion v1** (Hot Chocolate 14, `dotnet fusion` CLI, `.fsp` / `.fgp` files). Mature, well documented, slightly less clean entity semantics.
3. Install the composition CLI as a **local** tool so the version is pinned in the repo:
   ```bash
   dotnet new tool-manifest
   dotnet tool install ChilliCream.Nitro.CommandLine        # Fusion v2   ⚠️ VERIFY exact package id
   # or, fallback:
   dotnet tool install HotChocolate.Fusion.CommandLine      # Fusion v1
   ```
4. Run the CLI once with `--help` **while offline** (turn Wi-Fi off) and confirm it does not demand a login for the compose command. Record the exact command surface in §3 of version-facts.
5. Record exact versions of every package you end up using. Later phases copy them into `Directory.Packages.props` verbatim.

## 5. Step 2 — Build the spike (≈3 h)

Layout (not part of the final solution):

```
spike/
  Owner/            # owns Device { id, hostname, tenantId }, public lookup
  Extender/         # extends Device with nullable `notes`, internal lookup, service policy
  Gateway/          # Fusion gateway, header forwarding, edge auth, timeouts
  Token/            # console app that prints a JWT
  docker-compose.yml
  schemas/          # exported SDL
  gateway.far       # composed archive (or gateway.fgp for v1)
```

Shared constants for the spike (copy into every project; Phase 1 turns these into `Shared.Auth`):

```csharp
public static class SpikeAuth
{
    public const string Issuer = "sor-poc";
    public const string Audience = "sor-poc";
    public const string TenantClaim = "tenantId";
    public const string ServicesClaim = "services";
    // HS256 needs >= 256 bits. This is 64 ASCII chars.
    public const string Key = "sor-poc-dev-only-signing-key-do-not-use-in-prod-0123456789abcdef";
}
```

### 5.1 `spike/Token` — mint a JWT

```csharp
using System.Text;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

// usage: dotnet run -- <tenantId> <comma-separated services>
var tenant = args[0];
var services = args.Length > 1 ? args[1].Split(',', StringSplitOptions.RemoveEmptyEntries) : [];
var handler = new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false };
var token = handler.CreateToken(new SecurityTokenDescriptor
{
    Issuer = SpikeAuth.Issuer,
    Audience = SpikeAuth.Audience,
    IssuedAt = DateTime.UtcNow,
    NotBefore = DateTime.UtcNow,
    Expires = DateTime.UtcNow.AddYears(5),
    SigningCredentials = new SigningCredentials(
        new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SpikeAuth.Key)), SecurityAlgorithms.HmacSha256),
    Claims = new Dictionary<string, object>
    {
        ["sub"] = "spike-user",
        [SpikeAuth.TenantClaim] = tenant,
        [SpikeAuth.ServicesClaim] = services,   // JSON array -> multiple claims on the server side
    },
});
Console.WriteLine(token);
```

Package: `Microsoft.IdentityModel.JsonWebTokens`.

### 5.2 Shared JWT registration (Owner, Extender, Gateway)

```csharp
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.Text;

static IServiceCollection AddSpikeJwt(this IServiceCollection services) =>
    services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(o =>
        {
            o.MapInboundClaims = false;   // keep "sub", "tenantId", "services" as-is
            o.TokenValidationParameters = new TokenValidationParameters
            {
                ValidIssuer = SpikeAuth.Issuer,
                ValidAudience = SpikeAuth.Audience,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SpikeAuth.Key)),
                ValidateIssuer = true, ValidateAudience = true,
                ValidateIssuerSigningKey = true, ValidateLifetime = true,
                ClockSkew = TimeSpan.FromMinutes(1),
            };
        }).Services;
```

Package: `Microsoft.AspNetCore.Authentication.JwtBearer`.

### 5.3 `spike/Owner`

```csharp
using HotChocolate.Authorization;
// ⚠️ VERIFY: namespace/package that provides [Lookup] / [Internal] for the pinned version
//   candidates: HotChocolate.Fusion.SourceSchema, HotChocolate.Types.Composite

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSpikeJwt();
builder.Services.AddAuthorization();
builder.Services.AddHttpContextAccessor();
builder.Services
    .AddGraphQLServer()
    .AddAuthorization()
    .AddQueryType<Query>();
    // ⚠️ VERIFY: any source-schema registration call the pinned version needs (e.g. .AddSourceSchema())

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.MapGraphQL();
await app.RunWithGraphQLCommandsAsync(args);   // enables: dotnet run -- schema export --output x.graphqls

public sealed record Device([property: ID] string Id, string Hostname, string TenantId);

[Authorize]   // every field needs a valid token
public sealed class Query
{
    private static readonly Device[] Devices =
    [
        new("dev-00001", "alpha", "TenantA"),
        new("dev-00002", "beta",  "TenantB"),
    ];

    [Lookup]   // ⚠️ VERIFY attribute name
    public Device? GetDevice([ID] string id, [Service] IHttpContextAccessor http)
    {
        var tenant = http.HttpContext!.User.FindFirst(SpikeAuth.TenantClaim)?.Value;
        return Devices.FirstOrDefault(d => d.Id == id && d.TenantId == tenant);   // null, never an error, for another tenant
    }

    public IEnumerable<Device> GetDevices([Service] IHttpContextAccessor http)
    {
        var tenant = http.HttpContext!.User.FindFirst(SpikeAuth.TenantClaim)?.Value;
        return Devices.Where(d => d.TenantId == tenant);
    }
}
```

Packages: `HotChocolate.AspNetCore`, `HotChocolate.AspNetCore.Authorization`, `HotChocolate.AspNetCore.CommandLine`, plus the source-schema package (⚠️ VERIFY).

### 5.4 `spike/Extender`

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSpikeJwt();
builder.Services.AddAuthorization(o => o.AddPolicy("ServiceAccess",
    p => p.RequireAuthenticatedUser().RequireClaim(SpikeAuth.ServicesClaim, "extender")));
builder.Services.AddHttpContextAccessor();
builder.Services
    .AddGraphQLServer()
    .AddAuthorization()
    .AddQueryType<Query>()
    .AddTypeExtension<DeviceExtensions>();

var app = builder.Build();
app.Use(async (ctx, next) =>   // experiment 3: prove the header arrives
{
    app.Logger.LogInformation("Authorization header present: {Present}", ctx.Request.Headers.ContainsKey("Authorization"));
    await next();
});
app.UseAuthentication();
app.UseAuthorization();
app.MapGraphQL();
await app.RunWithGraphQLCommandsAsync(args);

public sealed record Device([property: ID] string Id);

[Authorize]
public sealed class Query
{
    [Lookup, Internal]   // ⚠️ VERIFY attribute names; the lookup must NOT be client-callable through the gateway
    public Device GetDeviceById([ID] string id) => new(id);
}

[ExtendObjectType<Device>]
public sealed class DeviceExtensions
{
    // NULLABLE list on purpose. See plan §4.2.
    [Authorize(Policy = "ServiceAccess")]
    public IReadOnlyList<string>? GetNotes([Parent] Device device, [Service] IHttpContextAccessor http)
    {
        var tenant = http.HttpContext!.User.FindFirst(SpikeAuth.TenantClaim)?.Value;
        return [$"note for {device.Id} in {tenant}"];
    }
}
```

### 5.5 `spike/Gateway`

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHttpContextAccessor();
builder.Services.AddTransient<ForwardAuthorizationHandler>();

var timeout = TimeSpan.FromSeconds(5);
foreach (var name in new[] { "Owner", "Extender" })   // names must equal the source-schema names used at composition
{
    builder.Services.AddHttpClient(name, c =>
    {
        c.BaseAddress = new Uri(builder.Configuration[$"SUBGRAPH_{name.ToUpperInvariant()}_URL"]!);
        c.Timeout = timeout;
    }).AddHttpMessageHandler<ForwardAuthorizationHandler>();
}

builder.Services.AddSpikeJwt();

// ⚠️ VERIFY gateway registration for the pinned version:
//   Fusion v2:  builder.AddGraphQLGateway().AddFileConfiguration("gateway.far");
//   Fusion v1:  builder.Services.AddFusionGatewayServer().ConfigureFromFile("gateway.fgp");
// ⚠️ VERIFY error-handling mode option (Fusion v2 exposes something like ErrorHandlingMode.Propagate/Null/Halt)

var app = builder.Build();
app.UseAuthentication();
app.Use(async (ctx, next) =>   // edge check: reject unauthenticated POSTs, keep the GET UI reachable
{
    if (ctx.Request.Path.StartsWithSegments("/graphql") && HttpMethods.IsPost(ctx.Request.Method)
        && ctx.User.Identity?.IsAuthenticated != true)
    {
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }
    await next();
});
app.MapGraphQL();
app.Run();

public sealed class ForwardAuthorizationHandler(IHttpContextAccessor accessor) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var auth = accessor.HttpContext?.Request.Headers.Authorization.ToString();
        if (!string.IsNullOrEmpty(auth) && request.Headers.Authorization is null)
            request.Headers.TryAddWithoutValidation("Authorization", auth);
        return base.SendAsync(request, ct);
    }
}
```

⚠️ VERIFY that the pinned Fusion version resolves subgraph clients through `IHttpClientFactory` by source-schema name (v1 did; v2 is expected to). If it uses its own client factory abstraction, implement header forwarding through that instead and record it in version-facts §5.

### 5.6 Export and compose

```bash
cd spike/Owner    && dotnet run -- schema export --output ../schemas/owner.graphqls
cd spike/Extender && dotnet run -- schema export --output ../schemas/extender.graphqls
cd spike
# Fusion v2 — ⚠️ VERIFY flags; record the working invocation verbatim in version-facts §3
dotnet nitro fusion compose \
  --source-schema-file schemas/owner.graphqls \
  --source-schema-file schemas/extender.graphqls \
  --archive gateway.far
# If the compose step needs per-source-schema settings (name + http URL), create them, e.g.
#   schemas/owner-settings.json  { "name": "Owner", "transports": { "http": { "url": "http://owner:8080/graphql" } } }
# and record exactly how they are passed.
```

Fusion v1 fallback:

```bash
dotnet fusion subgraph config set name Owner    -w spike/Owner
dotnet fusion subgraph config set http --url http://owner:8080/graphql -w spike/Owner
dotnet fusion subgraph pack -w spike/Owner        # -> Owner.fsp
# same for Extender
dotnet fusion compose -p spike/gateway.fgp -s spike/Owner/Owner.fsp -s spike/Extender/Extender.fsp
```

Check the exported `extender.graphqls`: `notes: [String!]` must have **no trailing `!`**, and `deviceById` must carry `@lookup @internal` (v2). Check the composed public schema: `deviceById` must **not** be visible on the gateway's `Query` type (query `{ __type(name:"Query") { fields { name } } }` against the gateway).

### 5.7 `spike/docker-compose.yml`

```yaml
name: sor-spike
services:
  owner:
    build: { context: ., dockerfile: Owner/Dockerfile }
    environment: { ASPNETCORE_URLS: "http://+:8080" }
  extender:
    build: { context: ., dockerfile: Extender/Dockerfile }
    environment: { ASPNETCORE_URLS: "http://+:8080" }
  gateway:
    build: { context: ., dockerfile: Gateway/Dockerfile }
    environment:
      ASPNETCORE_URLS: "http://+:8080"
      SUBGRAPH_OWNER_URL: "http://owner:8080/graphql"
      SUBGRAPH_EXTENDER_URL: "http://extender:8080/graphql"
    ports: [ "5000:8080" ]
    depends_on: [ owner ]      # deliberately NOT extender: the gateway must start without it
```

Dockerfiles: `mcr.microsoft.com/dotnet/sdk:<ver>` build stage, `mcr.microsoft.com/dotnet/aspnet:<ver>-alpine` runtime, `EXPOSE 8080`. The gateway Dockerfile copies `gateway.far`.

## 6. Step 3 — Experiments (≈3 h)

Helper:

```bash
TOKEN_FULL=$(dotnet run --project spike/Token -- TenantA extender)
TOKEN_NONE=$(dotnet run --project spike/Token -- TenantA)
TOKEN_B=$(dotnet run --project spike/Token -- TenantB extender)
Q='{"query":"{ device(id:\"dev-00001\") { id hostname notes } }"}'
gql() { curl -s -w '\nHTTP %{http_code} %{time_total}s\n' http://localhost:5000/graphql -H "Authorization: Bearer $1" -H 'Content-Type: application/json' -d "$Q"; }
```

| # | Experiment | Command | Pass condition | Record to |
|---|---|---|---|---|
| 1 | Happy path | `gql $TOKEN_FULL` | `data.device.notes` is a one-element array, no `errors` | — |
| 2 | Lookup hidden | introspect gateway `Query` fields | `deviceById` absent, `device` present | version-facts §2 |
| 3 | Header forwarded | `docker compose logs extender \| grep "header present"` after #1 | `True` | version-facts §5 |
| 4 | Outage, stopped | `docker compose stop extender` then `gql $TOKEN_FULL` | HTTP 200; `data.device.id` and `hostname` present; `notes` is `null`; one `errors[]` entry whose `path` starts `["device","notes"]`; response time < 2 s | `tests/fixtures/outage-stop.json` |
| 5 | Outage, hung | `docker compose start extender`, wait, `docker compose pause extender`, `gql $TOKEN_FULL` | same shape as #4; response time between 5 s and 8 s (the 5 s client timeout, not the 100 s default); **the whole request must not be cancelled** | `tests/fixtures/outage-pause.json` |
| 6 | Denied | `docker compose unpause extender`, `gql $TOKEN_NONE` | HTTP 200; `data.device` populated; `notes` null; `errors[0].path` starts `["device","notes"]`; `errors[0].extensions.code == "AUTH_NOT_AUTHORIZED"` | `tests/fixtures/denied.json` |
| 7 | Unauthenticated | `curl -s -o /dev/null -w '%{http_code}' -X POST http://localhost:5000/graphql -H 'Content-Type: application/json' -d "$Q"` | `401` from the gateway; no subgraph log line for the request | `tests/fixtures/unauthenticated.json` (body + status) |
| 8 | Cross-tenant | `gql $TOKEN_B` (dev-00001 belongs to TenantA) | HTTP 200; `data.device == null`; **no** `errors` | `tests/fixtures/cross-tenant.json` |
| 9 | Error mode | flip the Fusion error-handling option (if it exists) and repeat #4 | pick the mode that keeps #4/#6 shapes; record it | version-facts §6 |
| 10 | Nitro UI | open `http://localhost:5000/graphql` in a browser | the built-in UI loads (GET is not blocked by the edge check) | version-facts §4 |

Expected shape for #4 and #6 (record the real one, not this one):

```json
{
  "errors": [
    { "message": "...", "path": ["device", "notes"], "extensions": { "code": "..." } }
  ],
  "data": { "device": { "id": "dev-00001", "hostname": "alpha", "notes": null } }
}
```

Only the `code` differs between outage and denial. If the outage error has no `path`, or the path is at the root, or `data.device` is `null`, **stop and go to §7**.

Experiment 5 is the one that finds real problems: a missing timeout makes the request hang for 100 s; a timeout that surfaces as `TaskCanceledException` may be treated by the executor as *request* cancellation and kill the whole response. If that happens, look for a Fusion transport option to distinguish the two, or wrap the handler to translate `TaskCanceledException` (with the request token **not** cancelled) into an `HttpRequestException`. Record whatever worked.

## 7. Decision points

- **4 h:** if Fusion v2 composition still does not produce a loadable archive offline, switch to the Fusion v1 fallback (§5.6) and continue the experiments there. Record the reason.
- **8 h:** if neither generation returns partial data in #4, escalate before Phase 1 starts. Plan B (design change, requires the plan owner's decision): the UI issues one query per section (`device { patchEvents }`, `device { vulnerabilityEvents }`, …) so a failure is naturally isolated per request. Federation is still real; only the request granularity changes.
- **Any time:** if the composition CLI demands an account, switch to invoking the `HotChocolate.Fusion.Composition` library from a small console project and record that as the compose command.

## 8. `docs/version-facts.md` — required sections

Write this file. Every later phase reads it. Do not leave a section empty; write "not applicable" with a reason if that is the truth.

```markdown
# Version facts (single source of truth — produced by Phase 0)

## §1 Pinned versions
- .NET SDK (global.json):
- Target framework:
- HotChocolate.AspNetCore / .Authorization / .CommandLine:
- Source-schema package (name + version):
- Gateway package (name + version):
- Composition CLI (tool id + version):
- Fusion generation used: v2 | v1 (reason if v1)
- Microsoft.AspNetCore.Authentication.JwtBearer:
- Microsoft.IdentityModel.JsonWebTokens:

## §2 Packages and namespaces
- Attributes for lookups: `[Lookup]`, `[Internal]` live in `<namespace>` (package `<id>`)
- Registration call needed on the subgraph builder (if any):
- How key fields are declared (implicit from lookup | `[Key]` | `@key` in SDL):
- Does `deviceById` stay hidden on the gateway? (experiment 2)

## §3 Commands (copy-paste ready, verified)
- Export: `cd src/<Project> && dotnet run -- schema export --output ../../schemas/<name>.graphqls`
- Compose:
- Per-source-schema settings (file shape and how it is passed), or "URLs come from gateway HttpClient config only":
- Source schema names used: DeviceDirectory, Patch, Vulnerability, SoftwareInstall

## §4 Gateway registration
- Registration API:
- How the archive is loaded (path, env var):
- Nitro UI path and whether it is affected by the edge auth middleware:
- Health endpoint approach:

## §5 Header forwarding
- Mechanism that worked (IHttpClientFactory named clients + DelegatingHandler | other):
- Code snippet:

## §6 Error handling
- Option name and value chosen:
- Timeout mechanism and value:
- Behaviour on client timeout (field error | whole request cancelled) and the fix if needed:

## §7 Fixtures
- List of files in tests/fixtures with a one-line description each
- Path shape observed for outage and denial (exact vs deeper)
- Code observed for outage (transport) errors:

## §8 Deviations (append-only, all phases)
| Date | Phase | Document said | Reality | Action |
```

## 9. Definition of Done

- [ ] `.config/dotnet-tools.json` exists with the composition CLI pinned.
- [ ] `spike/` builds and `docker compose -f spike/docker-compose.yml up --build` runs.
- [ ] Experiments 1–10 executed; results recorded in the table above (copy it into `docs/version-facts.md §7` with actual outcomes).
- [ ] Five fixture files committed, each the **raw, unmodified** gateway response.
- [ ] `docs/version-facts.md` has all eight sections filled.
- [ ] Decision recorded: Fusion v2 or v1, and why.
- [ ] Every `⚠️ VERIFY` in this file has a corresponding fact in version-facts.
- [ ] No fixture shows `data.device == null` for an outage or denial case.
