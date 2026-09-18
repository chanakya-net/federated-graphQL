# Phase 1 — Scaffold, shared libraries, contracts

**Sequential. Starts after Phase 0's gate. Everything in Stage 2 depends on this phase.**
**Effort:** 1–2 days.

## 1. Purpose

Produce the skeleton every lane builds inside, the two shared libraries every subgraph references, and the frozen contracts that let eight lanes work without talking to each other:

- Repository layout, solution with **every** project pre-created (empty where the lane fills it in), central package pins from `docs/version-facts.md`.
- `Shared.Seeding` — the canonical device catalog and deterministic RNG helpers.
- `Shared.Auth` — JWT validation, service-access policy, caller context, dev token factory.
- `contracts/` — GraphQL SDL per subgraph, HTTP/env contract, `tokens.json` schema, error contract, seeding contract.
- Script and CI skeletons.

## 2. Inputs

- `docs/version-facts.md` (all sections).
- Plan v3 §4–§7.

## 3. Repository layout (create all of it)

```
SoR/
  README.md
  .env                         # dev-only values, committed (P2C fills the rest)
  .gitignore                   # dotnet + node + docker ignores
  .dockerignore                # P2C owns, create empty placeholder
  .editorconfig
  global.json
  Directory.Build.props
  Directory.Packages.props
  .config/dotnet-tools.json    # from Phase 0
  SoR.sln
  docs/
    version-facts.md           # from Phase 0
  contracts/                   # this phase, frozen at the end
  schemas/                     # exported SDL, filled by subgraph lanes (P4 owns the script)
    .gitkeep
  gateway/                     # composed archive, P4 owns
    .gitkeep
  scripts/
    build.sh
    test.sh
  infra/                       # P2C owns; create folder only
    .gitkeep
  src/
    Shared.Seeding/
    Shared.Auth/
    DeviceDirectory/           # skeleton: Program.cs that starts and serves /health; P2B fills
    Patch/                     # skeleton; P3A fills
    Vulnerability/             # skeleton; P3B fills
    SoftwareInstall/           # skeleton; P3C fills
    Gateway/                   # skeleton; P4 fills
    TokenGenerator/            # skeleton; P2A fills
  tests/
    fixtures/                  # from Phase 0
    Shared.Seeding.Tests/
    Shared.Auth.Tests/
    DeviceDirectory.Tests/     # skeleton with one placeholder test
    Patch.Tests/
    Vulnerability.Tests/
    SoftwareInstall.Tests/
    Gateway.Tests/
    TokenGenerator.Tests/
  ui/                          # P5 owns; create folder with .gitkeep
  spike/                       # from Phase 0, not in the solution
```

Namespaces: `SoR.Shared.Seeding`, `SoR.Shared.Auth`, `SoR.DeviceDirectory`, `SoR.Patch`, `SoR.Vulnerability`, `SoR.SoftwareInstall`, `SoR.Gateway`, `SoR.TokenGenerator`. Assembly names equal the project folder names (`DeviceDirectory.dll` etc.) so the Dockerfile template in P2C works unchanged.

Skeleton web projects: each `Program.cs` builds a `WebApplication`, maps `/health` returning 200, and runs. They must build and start; that is all. The lane replaces them.

### 3.1 `global.json`

```json
{ "sdk": { "version": "<from version-facts §1>", "rollForward": "latestFeature" } }
```

### 3.2 `Directory.Build.props`

```xml
<Project>
  <PropertyGroup>
    <TargetFramework><!-- from version-facts §1 --></TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <LangVersion>latest</LangVersion>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <WarningsNotAsErrors>CS1591</WarningsNotAsErrors>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
    <InvariantGlobalization>true</InvariantGlobalization>
    <RootNamespace>SoR.$(MSBuildProjectName)</RootNamespace>
  </PropertyGroup>
</Project>
```

`InvariantGlobalization` matches the alpine runtime image (no ICU). Nothing in this POC needs culture data.

### 3.3 `Directory.Packages.props`

Versions come from `docs/version-facts.md §1`. Every package any lane will need is pinned **here, now**, so lanes do not race on this file.

```xml
<Project>
  <ItemGroup>
    <!-- Hot Chocolate -->
    <PackageVersion Include="HotChocolate.AspNetCore" Version="HC" />
    <PackageVersion Include="HotChocolate.AspNetCore.Authorization" Version="HC" />
    <PackageVersion Include="HotChocolate.AspNetCore.CommandLine" Version="HC" />
    <PackageVersion Include="<source-schema package id from version-facts §2>" Version="HC" />
    <PackageVersion Include="<gateway package id from version-facts §1>" Version="HC" />
    <!-- Auth -->
    <PackageVersion Include="Microsoft.AspNetCore.Authentication.JwtBearer" Version="X" />
    <PackageVersion Include="Microsoft.IdentityModel.JsonWebTokens" Version="X" />
    <!-- Data -->
    <PackageVersion Include="Microsoft.EntityFrameworkCore" Version="X" />
    <PackageVersion Include="Microsoft.EntityFrameworkCore.Design" Version="X" />
    <PackageVersion Include="Npgsql.EntityFrameworkCore.PostgreSQL" Version="X" />
    <PackageVersion Include="Npgsql" Version="X" />
    <PackageVersion Include="Microsoft.Extensions.Diagnostics.HealthChecks.EntityFrameworkCore" Version="X" />
    <PackageVersion Include="MongoDB.Driver" Version="X" />
    <PackageVersion Include="Azure.Storage.Blobs" Version="X" />
    <!-- Seeding -->
    <PackageVersion Include="Bogus" Version="X" />
    <!-- Tests -->
    <PackageVersion Include="Microsoft.NET.Test.Sdk" Version="X" />
    <PackageVersion Include="xunit" Version="X" />
    <PackageVersion Include="xunit.runner.visualstudio" Version="X" />
    <PackageVersion Include="Microsoft.AspNetCore.Mvc.Testing" Version="X" />
    <PackageVersion Include="Testcontainers" Version="X" />
    <PackageVersion Include="Testcontainers.PostgreSql" Version="X" />
    <PackageVersion Include="Testcontainers.MongoDb" Version="X" />
    <PackageVersion Include="Testcontainers.Azurite" Version="X" />
  </ItemGroup>
</Project>
```

Do **not** add FluentAssertions (its licence changed in v8). Use plain xunit asserts.

### 3.4 `.env` (committed, dev-only)

```
DEV_JWT_SIGNING_KEY=sor-poc-dev-only-signing-key-do-not-use-in-prod-0123456789abcdef
POSTGRES_PASSWORD=postgres
DEVDIR_DB_PASSWORD=devdir_pw
VULN_DB_PASSWORD=vuln_pw
GATEWAY_PORT=5000
UI_PORT=4200
SUBGRAPH_TIMEOUT_SECONDS=5
```

The key is 64 ASCII characters. HS256 in current `Microsoft.IdentityModel` needs at least 256 bits; anything shorter fails at token creation with `IDX10720`.

### 3.5 `scripts/build.sh`, `scripts/test.sh`

```bash
#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
dotnet build SoR.sln -c Release
```

```bash
#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
FILTER="${1:-}"
if [ "$FILTER" = "unit" ]; then dotnet test SoR.sln -c Release --no-build --filter "Category!=Integration"; else dotnet test SoR.sln -c Release --no-build; fi
```

Integration tests carry `[Trait("Category","Integration")]`. Bash 3.2 compatible (macOS default): no associative arrays anywhere in `scripts/`.

### 3.6 CI skeleton `.github/workflows/ci.yml`

Jobs: `build` (restore, build, unit tests), `integration` (needs Docker; runs `scripts/test.sh`), `schema-drift` (placeholder that P4 fills: runs `scripts/check-schema-drift.sh`). Keep it minimal; it is optional for the POC but cheap to have.

## 4. `src/Shared.Seeding`

References: `Bogus`. No ASP.NET dependency.

```csharp
namespace SoR.Shared.Seeding;

public static class SeedConstants
{
    public const int TotalDevices = 12_000;
    public const int TenantADeviceCount = 7_000;          // indexes 0..6999 -> TenantA, 7000..11999 -> TenantB
    public const string TenantA = "TenantA";
    public const string TenantB = "TenantB";

    /// Every seeded timestamp is relative to this instant, never to "now".
    public static readonly DateTimeOffset Epoch = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

    /// Domain events fall in [Epoch - EventWindow, Epoch].
    public static readonly TimeSpan EventWindow = TimeSpan.FromDays(365);
}

public sealed record SeedDevice(
    int Index, string Id, string TenantId, string Hostname, string Os, string IpAddress, DateTimeOffset LastSeenAt);

public static class DeviceCatalog
{
    private const string Prefix = "dev-";

    private static readonly string[] OsCatalog =
    [
        "Windows 11 23H2", "Windows 10 22H2", "Windows Server 2022",
        "Ubuntu 22.04", "Ubuntu 24.04", "RHEL 9", "macOS 15",
    ];

    public static string DeviceId(int index)
    {
        if (index is < 0 or >= SeedConstants.TotalDevices) throw new ArgumentOutOfRangeException(nameof(index));
        return $"{Prefix}{index:D5}";
    }

    public static bool TryGetIndex(string deviceId, out int index)
    {
        index = -1;
        if (deviceId is null || !deviceId.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        if (!int.TryParse(deviceId.AsSpan(Prefix.Length), out var i)) return false;
        if (i is < 0 or >= SeedConstants.TotalDevices) return false;
        index = i; return true;
    }

    public static string TenantOf(int index) =>
        index < SeedConstants.TenantADeviceCount ? SeedConstants.TenantA : SeedConstants.TenantB;

    /// Deterministic per index. Bogus only decorates; nothing federation depends on comes from it.
    public static SeedDevice Build(int index)
    {
        var id = DeviceId(index);
        var f = new Bogus.Faker("en") { Random = new Bogus.Randomizer(index) };
        var os = OsCatalog[f.Random.Int(0, OsCatalog.Length - 1)];
        var host = $"{f.Hacker.Noun().ToLowerInvariant().Replace(' ', '-')}-{f.Random.AlphaNumeric(4).ToLowerInvariant()}-{index % 1000:D3}";
        var lastSeen = SeedConstants.Epoch - TimeSpan.FromMinutes(f.Random.Int(1, 30 * 24 * 60));
        return new SeedDevice(index, id, TenantOf(index), host, os, f.Internet.Ip(), lastSeen);
    }

    public static IEnumerable<SeedDevice> All() =>
        Enumerable.Range(0, SeedConstants.TotalDevices).Select(Build);

    public static IEnumerable<SeedDevice> ForTenant(string tenantId) =>
        All().Where(d => d.TenantId == tenantId);
}

public static class DeterministicRandom
{
    /// Stable, cross-platform hash. Do NOT use string.GetHashCode (randomised per process).
    public static int StableHash(string s)
    {
        unchecked { var h = 23; foreach (var c in s) h = h * 31 + c; return h; }
    }

    public static int Seed(int deviceIndex, string domain) => unchecked((deviceIndex * 397) ^ StableHash(domain));

    /// Seeded System.Random is stable across .NET versions for the same seed.
    public static Random For(int deviceIndex, string domain) => new(Seed(deviceIndex, domain));

    /// Uniform instant inside the event window, from a caller-owned Random.
    public static DateTimeOffset InstantInWindow(Random rng)
    {
        var minutes = (int)SeedConstants.EventWindow.TotalMinutes;
        return SeedConstants.Epoch - TimeSpan.FromMinutes(rng.Next(0, minutes + 1));
    }
}
```

### 4.1 `tests/Shared.Seeding.Tests`

| Test | Assertion |
|---|---|
| `DeviceId_is_zero_padded` | `DeviceId(0) == "dev-00000"`, `DeviceId(11999) == "dev-11999"` |
| `DeviceId_rejects_out_of_range` | throws for `-1` and `12000` |
| `TryGetIndex_roundtrips` | for 0, 6999, 7000, 11999 |
| `Tenant_split_is_7000_5000` | `All().Count(d => d.TenantId == TenantA) == 7000`, TenantB `== 5000` |
| `TenantOf_boundary` | `TenantOf(6999) == TenantA`, `TenantOf(7000) == TenantB` |
| `Build_is_deterministic` | `Build(42) == Build(42)`; `All().Select(d => d.Hostname).Aggregate(hash) == (same on second run)` |
| `Build_matches_golden` | first five devices equal `tests/Shared.Seeding.Tests/golden-devices.json` — generate once in this phase, commit, never regenerate silently |
| `LastSeen_is_before_epoch` | all devices |
| `DeterministicRandom_same_seed_same_sequence` | `For(7,"patch").Next() == For(7,"patch").Next()`, and differs from `For(7,"vulnerability")` |
| `InstantInWindow_is_inside_window` | 10 000 samples |

## 5. `src/Shared.Auth`

References: `Microsoft.AspNetCore.Authentication.JwtBearer`, `Microsoft.IdentityModel.JsonWebTokens`, framework reference `Microsoft.AspNetCore.App`.

```csharp
namespace SoR.Shared.Auth;

public static class DevAuth
{
    public const string SigningKeyEnv = "DEV_JWT_SIGNING_KEY";
    public const string Issuer = "sor-poc";
    public const string Audience = "sor-poc";
    public const string TenantClaim = "tenantId";
    public const string ServicesClaim = "services";
    public const string SubjectClaim = "sub";
    public const string NameClaim = "name";
    public const string ServiceAccessPolicy = "ServiceAccess";

    public static class Services
    {
        public const string Patch = "patch";
        public const string Vulnerability = "vulnerability";
        public const string SoftwareInstall = "softwareinstall";
        public static readonly string[] All = [Patch, Vulnerability, SoftwareInstall];
    }

    /// Placeholder used only when the env var is absent (schema export, design-time). Health check reports it.
    public const string MissingKeyPlaceholder = "MISSING-DEV_JWT_SIGNING_KEY-placeholder-0123456789abcdef0123456789";
}
```

```csharp
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.IdentityModel.Tokens;

namespace SoR.Shared.Auth;

public static class DevAuthServiceCollectionExtensions
{
    /// JWT Bearer validation with the shared dev key. Never throws at startup; a missing key
    /// is surfaced by DevAuthHealthCheck so `schema export` keeps working without config.
    public static IServiceCollection AddDevJwtAuthentication(this IServiceCollection services, IConfiguration config)
    {
        var key = config[DevAuth.SigningKeyEnv];
        var effective = string.IsNullOrWhiteSpace(key) ? DevAuth.MissingKeyPlaceholder : key;

        services.AddSingleton(new DevAuthState(KeyConfigured: !string.IsNullOrWhiteSpace(key)));
        services.AddHttpContextAccessor();
        services.AddScoped<ICallerContext, HttpCallerContext>();
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(o =>
            {
                o.MapInboundClaims = false;
                o.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = DevAuth.Issuer,
                    ValidAudience = DevAuth.Audience,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(effective)),
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateIssuerSigningKey = true,
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromMinutes(1),
                };
            });
        services.AddHealthChecks().AddCheck<DevAuthHealthCheck>("auth-config");
        return services;
    }

    /// Registers the "ServiceAccess" policy for one domain service name (e.g. "patch").
    public static IServiceCollection AddServiceAccessPolicy(this IServiceCollection services, string serviceName)
    {
        services.AddAuthorization(o => o.AddPolicy(DevAuth.ServiceAccessPolicy,
            p => p.RequireAuthenticatedUser().RequireClaim(DevAuth.ServicesClaim, serviceName)));
        return services;
    }
}

public sealed record DevAuthState(bool KeyConfigured);

public sealed class DevAuthHealthCheck(DevAuthState state) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default) =>
        Task.FromResult(state.KeyConfigured
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy($"{DevAuth.SigningKeyEnv} is not set"));
}
```

```csharp
using Microsoft.AspNetCore.Http;

namespace SoR.Shared.Auth;

public interface ICallerContext
{
    bool IsAuthenticated { get; }
    string UserId { get; }                 // "sub"
    string TenantId { get; }               // throws UnauthorizedAccessException if missing
    IReadOnlySet<string> Services { get; }
    bool HasService(string serviceName);
}

internal sealed class HttpCallerContext(IHttpContextAccessor accessor) : ICallerContext
{
    private System.Security.Claims.ClaimsPrincipal User =>
        accessor.HttpContext?.User ?? new System.Security.Claims.ClaimsPrincipal();

    public bool IsAuthenticated => User.Identity?.IsAuthenticated == true;
    public string UserId => User.FindFirst(DevAuth.SubjectClaim)?.Value ?? string.Empty;
    public string TenantId => User.FindFirst(DevAuth.TenantClaim)?.Value
        ?? throw new UnauthorizedAccessException("tenantId claim missing");
    public IReadOnlySet<string> Services =>
        User.FindAll(DevAuth.ServicesClaim).Select(c => c.Value).ToHashSet(StringComparer.Ordinal);
    public bool HasService(string serviceName) => Services.Contains(serviceName);
}
```

```csharp
using System.Text;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace SoR.Shared.Auth;

/// Single implementation used by TokenGenerator and by every test project.
public static class DevTokenFactory
{
    public static string Create(string signingKey, string sub, string name, string tenantId,
        IEnumerable<string> services, DateTimeOffset expires, DateTimeOffset? issuedAt = null)
    {
        if (Encoding.UTF8.GetByteCount(signingKey) < 32)
            throw new ArgumentException("signing key must be at least 32 bytes (256 bits) for HS256", nameof(signingKey));

        var now = (issuedAt ?? DateTimeOffset.UtcNow).UtcDateTime;
        var handler = new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false };
        return handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = DevAuth.Issuer,
            Audience = DevAuth.Audience,
            IssuedAt = now,
            NotBefore = now,
            Expires = expires.UtcDateTime,
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)), SecurityAlgorithms.HmacSha256),
            Claims = new Dictionary<string, object>
            {
                [DevAuth.SubjectClaim] = sub,
                [DevAuth.NameClaim] = name,
                [DevAuth.TenantClaim] = tenantId,
                [DevAuth.ServicesClaim] = services.Distinct(StringComparer.Ordinal).ToArray(),   // JSON array
            },
        });
    }
}
```

Usage pattern every subgraph follows (documented here, implemented by the lanes):

```csharp
builder.Services.AddDevJwtAuthentication(builder.Configuration);
builder.Services.AddServiceAccessPolicy(DevAuth.Services.Patch);      // domain subgraphs only
builder.Services.AddGraphQLServer().AddAuthorization() /* ... */;
// ...
app.UseAuthentication();
app.UseAuthorization();
app.MapHealthChecks("/health");
app.MapGraphQL();
await app.RunWithGraphQLCommandsAsync(args);
```

### 5.1 `tests/Shared.Auth.Tests`

Use a minimal `WebApplicationFactory`-free setup: build a `ServiceCollection`, call `AddDevJwtAuthentication` with an in-memory configuration, resolve `IOptionsMonitor<JwtBearerOptions>` and validate tokens with `JsonWebTokenHandler.ValidateTokenAsync` using the configured `TokenValidationParameters`.

| Test | Assertion |
|---|---|
| `Token_validates_with_configured_parameters` | valid token → `IsValid == true`, claims `sub`, `tenantId` present |
| `Services_claim_is_multi_valued` | token with `["patch","vulnerability"]` → two claims of type `services` on the principal |
| `Wrong_key_fails` | token signed with another 64-char key → `IsValid == false` |
| `Expired_fails` | `expires = now - 1 day` → `IsValid == false` |
| `Short_key_rejected` | `DevTokenFactory.Create` with 16-char key throws `ArgumentException` |
| `Policy_allows_matching_service` | `IAuthorizationService.AuthorizeAsync(principal, "ServiceAccess")` succeeds for `services=[patch]` when policy registered for `patch` |
| `Policy_denies_missing_service` | fails for `services=[vulnerability]` |
| `CallerContext_reads_claims` | `HttpCallerContext` over a fake `IHttpContextAccessor` returns tenant, user, services |
| `CallerContext_throws_without_tenant` | `TenantId` throws `UnauthorizedAccessException` |
| `Missing_key_is_unhealthy` | `DevAuthHealthCheck` reports Unhealthy when env absent, Healthy when set |

## 6. `contracts/` (frozen at end of phase, tag `contracts-v1`)

### 6.1 `contracts/README.md`

State: contracts are the shape every lane builds to. Subgraph lanes prove conformance with in-process schema tests (not textual diff). P4 composes the gateway from these files before the real exports exist. P5 builds the UI against them. Changes follow `phases/00-execution-plan.md §7`.

### 6.2 `contracts/device-directory.graphqls`

```graphql
"""Owned by the Device Directory subgraph. Public entity owner for Device."""
schema { query: Query }

type Query {
  """The device with this id in the caller's tenant. Null (never an error) if it does not exist there."""
  device(id: ID!): Device @lookup
  """Devices in the caller's tenant. `search` matches id, hostname or os (case-insensitive substring)."""
  devices(search: String, first: Int! = 25, offset: Int! = 0): DeviceSearchResult!
}

type Device @key(fields: "id") {
  id: ID!
  hostname: String!
  os: String!
  ipAddress: String!
  lastSeenAt: DateTime!
  tenantId: String!
}

type DeviceSearchResult {
  items: [Device!]!
  totalCount: Int!
}

scalar DateTime
```

Rules: `first` is clamped to 1..100; results ordered by `hostname`, then `id`. Whether `@key` appears in the real export depends on version-facts §2; the contract records intent.

### 6.3 `contracts/patch.graphqls`

```graphql
"""Owned by the Patch subgraph. Extends Device. Service name: "patch"."""
schema { query: Query }

type Query {
  """Internal lookup for the gateway only. Returns a stub even if this subgraph has no data for the id."""
  deviceById(id: ID!): Device @lookup @internal
  """Patch catalog. Requires services contains "patch"."""
  patches(first: Int! = 25, offset: Int! = 0): [Patch!]!
}

type Device @key(fields: "id") {
  id: ID!
  """NULLABLE on purpose (plan §4.2). Requires services contains "patch". Empty list = no events; null + error = degraded or denied."""
  patchEvents(since: DateTime, until: DateTime): [PatchEvent!]
}

type PatchEvent {
  id: ID!
  deviceId: ID!
  occurredAt: DateTime!
  status: PatchStatus!
  patch: Patch!
}

enum PatchStatus { APPLIED FAILED PENDING }

type Patch {
  id: ID!
  kbId: String!
  title: String!
  severity: PatchSeverity!
  vendor: String!
  releasedAt: DateTime!
}

enum PatchSeverity { CRITICAL HIGH MEDIUM LOW }

scalar DateTime
```

### 6.4 `contracts/vulnerability.graphqls`

```graphql
"""Owned by the Vulnerability subgraph. Extends Device. Service name: "vulnerability"."""
schema { query: Query }

type Query {
  deviceById(id: ID!): Device @lookup @internal
  """CVE catalog. Requires services contains "vulnerability"."""
  cves(first: Int! = 25, offset: Int! = 0): [Cve!]!
}

type Device @key(fields: "id") {
  id: ID!
  """NULLABLE on purpose. Requires services contains "vulnerability"."""
  vulnerabilityEvents(since: DateTime, until: DateTime): [VulnerabilityEvent!]
}

type VulnerabilityEvent {
  id: ID!
  deviceId: ID!
  findingId: ID!
  occurredAt: DateTime!
  kind: VulnerabilityEventKind!
  """State of the underlying finding as of the seed epoch."""
  findingState: FindingState!
  cve: Cve!
}

enum VulnerabilityEventKind { DETECTED REMEDIATED }
enum FindingState { OPEN REMEDIATED }

type Cve {
  id: ID!
  title: String!
  cvssScore: Float!
  severity: CveSeverity!
  publishedAt: DateTime!
}

enum CveSeverity { CRITICAL HIGH MEDIUM LOW }

scalar DateTime
```

### 6.5 `contracts/software-install.graphqls`

```graphql
"""Owned by the SoftwareInstall subgraph. Extends Device. Service name: "softwareinstall"."""
schema { query: Query }

type Query {
  deviceById(id: ID!): Device @lookup @internal
}

type Device @key(fields: "id") {
  id: ID!
  """NULLABLE on purpose. Requires services contains "softwareinstall"."""
  installEvents(since: DateTime, until: DateTime): [InstallEvent!]
}

type InstallEvent {
  id: ID!
  deviceId: ID!
  occurredAt: DateTime!
  action: InstallAction!
  result: InstallResult!
  software: Software!
}

enum InstallAction { INSTALL UNINSTALL UPGRADE }
enum InstallResult { SUCCESS FAILED }

type Software {
  name: String!
  version: String!
  publisher: String!
}

scalar DateTime
```

Common rules for the three extension fields: `since`/`until` are inclusive bounds on `occurredAt`, both optional; results ordered by `occurredAt` descending; hard cap 1 000 events per call; tenant scoping comes from the `tenantId` claim and never from an argument; a device with no data returns `[]`.

### 6.6 `contracts/http-and-env.md`

| Service | Compose name | Container port | Host port | Endpoints | Required env |
|---|---|---|---|---|---|
| Device Directory | `device-directory` | 8080 | — | `/graphql`, `/health` | `DEV_JWT_SIGNING_KEY`, `ConnectionStrings__DeviceDirectory` |
| Patch | `patch` | 8080 | — | `/graphql`, `/health` | `DEV_JWT_SIGNING_KEY`, `Mongo__ConnectionString`, `Mongo__Database` |
| Vulnerability | `vulnerability` | 8080 | — | `/graphql`, `/health` | `DEV_JWT_SIGNING_KEY`, `ConnectionStrings__Vulnerability` |
| SoftwareInstall | `software-install` | 8080 | — | `/graphql`, `/health` | `DEV_JWT_SIGNING_KEY`, `Blob__ConnectionString`, `Blob__Container` |
| Gateway | `fusion-gateway` | 8080 | `${GATEWAY_PORT}` (5000) | `/graphql`, `/nitro`, `/health` | `DEV_JWT_SIGNING_KEY`, `SUBGRAPH_DEVICEDIRECTORY_URL`, `SUBGRAPH_PATCH_URL`, `SUBGRAPH_VULNERABILITY_URL`, `SUBGRAPH_SOFTWAREINSTALL_URL`, `SUBGRAPH_TIMEOUT_SECONDS` |
| UI | `angular-ui` | 80 | `${UI_PORT}` (4200) | `/`, `/graphql` (proxy), `/tokens.json` | — |
| Token generator | `token-generator` | — | — | — | `DEV_JWT_SIGNING_KEY`, `USERS_FILE`, `TOKENS_OUTPUT` |

Rules:
- `/health` returns 200 **only** when the service can serve queries: DB reachable, seed complete, signing key configured. Compose healthchecks use it.
- All subgraph URLs inside compose: `http://<compose name>:8080/graphql`.
- Source-schema names (composition and gateway HttpClient names): `DeviceDirectory`, `Patch`, `Vulnerability`, `SoftwareInstall`.
- Every image: alpine runtime, non-root, `EXPOSE 8080`, `ASPNETCORE_URLS=http://+:8080`, `wget` available for healthchecks.
- Every subgraph supports `dotnet run -- schema export --output <file>` **without any backing service running** and without `DEV_JWT_SIGNING_KEY` set.
- Seeding runs in an `IHostedService`; `Program.cs` never touches a database before `app.Run()`.

### 6.7 `contracts/tokens.json.md`

`tokens.json` is a JSON **array**. Each element:

```json
{
  "sub": "bob",
  "name": "Bob (Tenant A)",
  "tenantId": "TenantA",
  "services": ["patch", "vulnerability"],
  "token": "eyJhbGciOiJIUzI1NiIs..."
}
```

Order is the order of `users.json`. The UI shows `name`, `tenantId` and `services`, and sends `token`. The five seeded users and their services are in plan §4.4; `users.json` is owned by P2A and must contain exactly those five.

JWT payload contract: `iss = aud = "sor-poc"`, `alg = HS256`, claims `sub`, `name`, `tenantId` (string), `services` (JSON array of strings), `iat`, `nbf`, `exp` (≥ 5 years out).

### 6.8 `contracts/errors.md`

Copy the observed shapes from `tests/fixtures/` and state the rules the UI relies on:

- A section of the timeline is **degraded** when its field is `null` **and** an `errors[]` entry has a `path` whose first two elements are `["device", "<field>"]`. Match by prefix.
- It is **denied** when that error's `extensions.code == "AUTH_NOT_AUTHORIZED"`.
- It is **unavailable** for any other code, or no code.
- `device == null` with no errors means "not found in this tenant".
- `device == null` with errors means Device Directory is down (whole query failed, by design).
- Empty list `[]` means "no events", never degraded.

### 6.9 `contracts/seeding.md`

- Every subgraph seeds exactly `DeviceCatalog.All()`. No subgraph invents device ids.
- Tenant of a device is `SeedDevice.TenantId`. Every stored row/document/blob carries it.
- Domain RNG: `DeterministicRandom.For(device.Index, "<domain>")` with domain strings `"patch"`, `"vulnerability"`, `"softwareinstall"` for per-device data and `"<domain>-catalog"` for catalogs.
- Timestamps via `DeterministicRandom.InstantInWindow(rng)`; nothing uses the wall clock.
- Event ids: `{deviceId}-p{n:D3}` (patch), `{deviceId}-f{n:D3}` (finding) with `:detected` / `:remediated` suffixes for events, `{deviceId}-i{n:D3}` (install).
- Idempotent: a marker (`seed_state` table / collection / `_seed/complete.json` blob) is written last; on restart, if the marker exists the seed is skipped. A partial previous seed is discarded and redone.
- Expected counts (used by e2e sanity checks): patch events 5–30 per device, findings 2–15 per device, install events 3–20 per device.

## 7. Definition of Done

- [ ] `dotnet build SoR.sln` exits 0 with zero warnings.
- [ ] `dotnet test SoR.sln` exits 0; `Shared.Seeding.Tests` and `Shared.Auth.Tests` cover every row in §4.1 and §5.1.
- [ ] Every project in §3 exists, is in `SoR.sln`, and every skeleton web project starts and answers `/health`.
- [ ] `Directory.Packages.props` pins every package in §3.3 with versions from version-facts.
- [ ] `tests/Shared.Seeding.Tests/golden-devices.json` committed.
- [ ] `contracts/` contains all nine files from §6, and `git tag contracts-v1` exists.
- [ ] `README.md` explains: prerequisites (Docker, .NET SDK, Node), `scripts/build.sh`, `scripts/test.sh`, where to find the plan and the phase docs.
- [ ] Any deviation from this document recorded in `docs/version-facts.md §8`.
