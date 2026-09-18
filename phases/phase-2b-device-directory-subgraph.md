# Phase 2B — Device Directory subgraph (PostgreSQL)

**Lane B. Starts after Phase 1. Parallel with every other Stage 2 lane. The tracer bullet (00-execution-plan §4) needs this lane first.**
**Effort:** 1.5–2 days. **Owns:** `src/DeviceDirectory/`, `tests/DeviceDirectory.Tests/`, `schemas/device-directory.graphqls`.

## 1. Purpose

The entity owner. Serves the canonical, tenant-scoped device list, the public `device(id)` lookup the gateway uses to resolve `Device`, and `devices(search)` for the UI. Seeds the 12 000 devices from `Shared.Seeding` into its own Postgres schema.

## 2. Inputs

- `contracts/device-directory.graphqls`, `contracts/http-and-env.md`, `contracts/seeding.md`, `contracts/errors.md`.
- `docs/version-facts.md` §2 (lookup attribute, registration call), §3 (export command).
- `src/Shared.Seeding`, `src/Shared.Auth`.
- Dockerfile template from `phase-2c-infra-and-compose.md §5` (copy it; do not wait for P2C to merge).

## 3. Project layout

```
src/DeviceDirectory/
  DeviceDirectory.csproj        # refs: HotChocolate.AspNetCore, .Authorization, .CommandLine, source-schema pkg,
                                #       Microsoft.EntityFrameworkCore, Npgsql.EntityFrameworkCore.PostgreSQL,
                                #       Microsoft.EntityFrameworkCore.Design (PrivateAssets=all),
                                #       Microsoft.Extensions.Diagnostics.HealthChecks.EntityFrameworkCore,
                                #       Shared.Seeding, Shared.Auth
  Program.cs
  Dockerfile
  Data/
    DeviceDbContext.cs
    DeviceDbContextFactory.cs   # design-time factory: lets `dotnet ef` run with no DB
    Entities.cs
    Migrations/                 # generated once with `dotnet ef migrations add Initial`
  Seeding/
    SeedState.cs                # singleton flag: Completed, Error
    SeedHostedService.cs
    SeedCompletedHealthCheck.cs
  GraphQL/
    Query.cs
    DeviceType.cs               # maps DeviceEntity -> GraphQL Device
    DeviceSearchResult.cs
```

## 4. Data model

```csharp
public sealed class DeviceEntity
{
    public required string Id { get; set; }            // "dev-00042"
    public required string TenantId { get; set; }
    public required string Hostname { get; set; }
    public required string Os { get; set; }
    public required string IpAddress { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
}

public sealed class SeedStateEntity
{
    public required string Key { get; set; }           // "devices"
    public DateTimeOffset CompletedAt { get; set; }
    public int RowCount { get; set; }
}
```

`DeviceDbContext`:

```csharp
protected override void OnModelCreating(ModelBuilder b)
{
    b.HasDefaultSchema("device_directory");
    b.Entity<DeviceEntity>(e =>
    {
        e.ToTable("devices");
        e.HasKey(x => x.Id);
        e.Property(x => x.Id).HasColumnName("id").HasMaxLength(16);
        e.Property(x => x.TenantId).HasColumnName("tenant_id").HasMaxLength(32);
        e.Property(x => x.Hostname).HasColumnName("hostname").HasMaxLength(128);
        e.Property(x => x.Os).HasColumnName("os").HasMaxLength(64);
        e.Property(x => x.IpAddress).HasColumnName("ip_address").HasMaxLength(45);
        e.Property(x => x.LastSeenAt).HasColumnName("last_seen_at");
        e.HasIndex(x => new { x.TenantId, x.Hostname });
        e.HasIndex(x => new { x.TenantId, x.Os });
    });
    b.Entity<SeedStateEntity>(e =>
    {
        e.ToTable("seed_state");
        e.HasKey(x => x.Key);
        e.Property(x => x.Key).HasColumnName("key");
        e.Property(x => x.CompletedAt).HasColumnName("completed_at");
        e.Property(x => x.RowCount).HasColumnName("row_count");
    });
}
```

Registration: `UseNpgsql(cs, npg => npg.MigrationsHistoryTable("__ef_migrations_history", "device_directory"))`. The history table must live in the owned schema; the role has no rights on `public`.

Design-time factory returns a context with `UseNpgsql("Host=localhost;Database=design;Username=x;Password=x")`; `dotnet ef migrations add` never connects. Commit `Data/Migrations/`.

## 5. Seeding (`SeedHostedService : BackgroundService`)

```
ExecuteAsync:
  for attempt in 1..10:
    try:
      using scope; db = scope.ServiceProvider.GetRequiredService<DeviceDbContext>()
      await db.Database.MigrateAsync(ct)
      if await db.SeedState.AnyAsync(s => s.Key == "devices", ct): mark Completed; return
      await db.Database.ExecuteSqlRawAsync("TRUNCATE device_directory.devices", ct)   // discard partial previous seed
      db.ChangeTracker.AutoDetectChangesEnabled = false
      foreach batch of 2000 in DeviceCatalog.All():
          db.Devices.AddRange(batch.Select(Map)); await db.SaveChangesAsync(ct); db.ChangeTracker.Clear()
          log "seeded {n}/12000"
      db.SeedState.Add(new { Key="devices", CompletedAt = DateTimeOffset.UtcNow, RowCount = 12000 }); await db.SaveChangesAsync(ct)
      mark Completed; log total time; return
    catch (ex) when not ct.IsCancellationRequested:
      log warning "seed attempt {attempt} failed"; state.Error = ex.Message
      await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, 2 * attempt)), ct)
```

`CompletedAt` is the one place wall-clock time is allowed (it is bookkeeping, not data). Expected duration on Docker Desktop: under 10 s.

Health:

```csharp
builder.Services.AddHealthChecks()
    .AddDbContextCheck<DeviceDbContext>("postgres")
    .AddCheck<SeedCompletedHealthCheck>("seed");          // Unhealthy until SeedState.Completed
// DevAuthHealthCheck "auth-config" is added by AddDevJwtAuthentication
```

## 6. GraphQL

`Program.cs`:

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddDbContextPool<DeviceDbContext>(o =>
    o.UseNpgsql(builder.Configuration.GetConnectionString("DeviceDirectory")
                ?? "Host=localhost;Database=sor;Username=devdir_user;Password=devdir_pw;Search Path=device_directory",
                npg => npg.MigrationsHistoryTable("__ef_migrations_history", "device_directory")));
builder.Services.AddDevJwtAuthentication(builder.Configuration);
builder.Services.AddAuthorization();
builder.Services.AddSingleton<SeedState>();
builder.Services.AddHostedService<SeedHostedService>();
builder.Services.AddHealthChecks().AddDbContextCheck<DeviceDbContext>("postgres").AddCheck<SeedCompletedHealthCheck>("seed");
builder.Services
    .AddGraphQLServer()
    .AddAuthorization()
    .AddQueryType<Query>()
    .AddType<DeviceType>()
    .ModifyRequestOptions(o => o.IncludeExceptionDetails = builder.Environment.IsDevelopment());
    // + source-schema registration per version-facts §2, if any

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.MapHealthChecks("/health");
app.MapGraphQL();
await app.RunWithGraphQLCommandsAsync(args);
```

The fallback connection string exists only so `schema export` and design-time tools never throw on a missing config value. It is never used in compose.

`Query.cs`:

```csharp
[Authorize]   // AUTH_NOT_AUTHENTICATED on any field without a valid token
public sealed class Query
{
    [Lookup]                                                       // per version-facts §2
    public async Task<DeviceEntity?> GetDevice(
        [ID] string id, ICallerContext caller, DeviceDbContext db, CancellationToken ct)
        => await db.Devices.AsNoTracking()
               .FirstOrDefaultAsync(d => d.Id == id && d.TenantId == caller.TenantId, ct);
        // Other tenant's device -> null, no error. Do not "helpfully" throw NotFound.

    public async Task<DeviceSearchResult> GetDevices(
        string? search, int first, int offset, ICallerContext caller, DeviceDbContext db, CancellationToken ct)
    {
        first = Math.Clamp(first, 1, 100);
        offset = Math.Max(0, offset);
        var q = db.Devices.AsNoTracking().Where(d => d.TenantId == caller.TenantId);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var p = $"%{search.Trim()}%";
            q = q.Where(d => EF.Functions.ILike(d.Hostname, p) || EF.Functions.ILike(d.Id, p) || EF.Functions.ILike(d.Os, p));
        }
        var total = await q.CountAsync(ct);
        var items = await q.OrderBy(d => d.Hostname).ThenBy(d => d.Id).Skip(offset).Take(first).ToListAsync(ct);
        return new DeviceSearchResult(items, total);
    }
}

public sealed record DeviceSearchResult(IReadOnlyList<DeviceEntity> Items, int TotalCount);
```

`DeviceType : ObjectType<DeviceEntity>`: `descriptor.Name("Device")`; `Id` as `ID` type; field names `id, hostname, os, ipAddress, lastSeenAt, tenantId`; nothing else exposed. If version-facts §2 says keys must be declared explicitly, add `[Key("id")]` / `descriptor.Key("id")` accordingly.

`ICallerContext` is scoped and comes from `Shared.Auth`; Hot Chocolate resolves it from the request scope. If the pinned Hot Chocolate version requires `[Service]` on injected services, add it.

Default `first` and `offset` values are declared in the contract (`first: Int! = 25`, `offset: Int! = 0`); express them as C# parameter defaults so the export matches.

## 7. Export

```bash
cd src/DeviceDirectory && dotnet run -c Release --no-launch-profile -- schema export --output ../../schemas/device-directory.graphqls
```

Commit `schemas/device-directory.graphqls`. It must contain `device(id: ID!): Device` with `@lookup`, `devices(search: String, first: Int! = 25, offset: Int! = 0): DeviceSearchResult!`, and the `Device` type with exactly the six contract fields. Run this **without** Postgres running and **without** `DEV_JWT_SIGNING_KEY` set; both must work.

## 8. Tests (`tests/DeviceDirectory.Tests`)

Integration tests (`[Trait("Category","Integration")]`) use `Testcontainers.PostgreSql` (`postgres:17-alpine`) with the init script from P2C copied into the test project as an embedded resource so the schema and role exist, and `WebApplicationFactory<Program>` with `ConnectionStrings__DeviceDirectory` overridden. Tokens via `DevTokenFactory` with the `.env` key. Wait for `/health` to be 200 before querying.

| Test | Category | Assertion |
|---|---|---|
| `Schema_matches_contract` | unit | build the schema in-process (`AddGraphQLServer()...BuildSchemaAsync()`); `Device` has exactly the six fields with the contract types; `device` returns nullable `Device`; `devices` args have defaults 25/0 |
| `Export_runs_without_database` | unit | run `Program` with args `schema export --output <tmp>` in a process with no connection string and no key; exit 0; file contains `type Device` |
| `Seed_inserts_12000_and_is_idempotent` | integration | after health OK: `select count(*)` = 12000; restart the factory; still 12000; `seed_state` has one row |
| `Seed_split_is_7000_5000` | integration | counts per tenant |
| `Device_lookup_returns_own_tenant` | integration | alice token, `device(id:"dev-00001")` → hostname equals `DeviceCatalog.Build(1).Hostname` |
| `Device_lookup_other_tenant_is_null_without_error` | integration | dave token (TenantB), `device(id:"dev-00001")` → `data.device == null`, no `errors` |
| `Device_lookup_unknown_id_is_null` | integration | `dev-99999` → null, no errors |
| `Devices_search_scoped_to_tenant` | integration | dave: `devices(search:"dev-00")` → every item `tenantId == TenantB`; `totalCount` ≤ 5000 |
| `Devices_search_matches_hostname_and_os_case_insensitively` | integration | search on an uppercased fragment of a known hostname returns it |
| `Devices_first_is_clamped` | integration | `first: 500` returns ≤ 100 items |
| `Unauthenticated_yields_AUTH_NOT_AUTHENTICATED` | integration | no header → `errors[0].extensions.code == "AUTH_NOT_AUTHENTICATED"`, `data.device == null` |
| `Wrong_key_token_is_rejected` | integration | token signed with another key → same as unauthenticated |
| `Health_is_unhealthy_until_seed_completes` | integration | poll `/health` from factory start: first observed status is 503, later 200 |

## 9. Dockerfile

Copy the template from `phase-2c-infra-and-compose.md §5` with `<Name> = DeviceDirectory`. Verify: `docker build -f src/DeviceDirectory/Dockerfile -t sor/device-directory .` then `docker run --rm -p 8080:8080 -e DEV_JWT_SIGNING_KEY=... -e ConnectionStrings__DeviceDirectory=... sor/device-directory` against a local Postgres started with the P2C init script.

## 10. Definition of Done

- [ ] All tests in §8 pass; integration tests run against Testcontainers, not a hand-started database.
- [ ] `schemas/device-directory.graphqls` committed and produced with no database and no signing key.
- [ ] `/health` returns 503 until seeding completes, 200 afterwards, and 503 if `DEV_JWT_SIGNING_KEY` is missing.
- [ ] A second `docker compose up` does not reseed (log line "seed already present").
- [ ] Image builds from the template; container starts as non-root on 8080.
- [ ] No wall-clock time in seeded data; `LastSeenAt` values equal `DeviceCatalog.Build(i).LastSeenAt`.
- [ ] README one-liner for running this lane's tests.
- [ ] Deviations recorded in `docs/version-facts.md §8`.
