# Phase 3A — Patch subgraph (MongoDB)

**Lane C. Starts after Phase 1. Parallel with every other Stage 2 lane.**
**Effort:** 1.5–2 days. **Owns:** `src/Patch/`, `tests/Patch.Tests/`, `schemas/patch.graphqls`.

## 1. Purpose

Extend `Device` with `patchEvents`, guarded by the `patch` service policy and scoped to the caller's tenant. Seed a patch catalog and per-device patch events into MongoDB, deterministically, from the shared device catalog.

## 2. Inputs

- `contracts/patch.graphqls`, `contracts/http-and-env.md`, `contracts/seeding.md`, `contracts/errors.md`.
- `docs/version-facts.md` §2 (`[Lookup]`, `[Internal]`, registration), §3 (export).
- `src/Shared.Seeding`, `src/Shared.Auth`.
- Dockerfile template from `phase-2c-infra-and-compose.md §5`.

## 3. Project layout

```
src/Patch/
  Patch.csproj              # refs: HotChocolate.AspNetCore, .Authorization, .CommandLine, source-schema pkg,
                            #       MongoDB.Driver, Shared.Seeding, Shared.Auth
  Program.cs
  Dockerfile
  Data/
    MongoOptions.cs         # ConnectionString, Database (bound from "Mongo" section)
    PatchDocument.cs
    PatchEventDocument.cs
    SeedStateDocument.cs
    PatchStore.cs           # IPatchStore: GetEventsAsync(tenant, deviceId, since, until), GetPatchesAsync, catalog lookup
  Seeding/
    PatchSeedData.cs        # pure functions: BuildCatalog(), BuildEventsFor(SeedDevice, catalogCount)
    SeedState.cs
    SeedHostedService.cs
    SeedCompletedHealthCheck.cs
  GraphQL/
    Device.cs               # record Device(string Id)
    DeviceExtensions.cs     # patchEvents
    Query.cs                # deviceById (internal lookup), patches
    Types.cs                # PatchEvent, Patch, enums
```

## 4. Documents

```csharp
public sealed class PatchDocument
{
    [BsonId] public required string Id { get; set; }             // "patch-0007"
    public required string KbId { get; set; }                    // "KB5000007"
    public required string Title { get; set; }
    public required string Severity { get; set; }                // CRITICAL|HIGH|MEDIUM|LOW
    public required string Vendor { get; set; }
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)] public DateTime ReleasedAt { get; set; }
}

public sealed class PatchEventDocument
{
    [BsonId] public required string Id { get; set; }             // "dev-00042-p003"
    public required string TenantId { get; set; }
    public required string DeviceId { get; set; }
    public required string PatchId { get; set; }
    public required string Status { get; set; }                  // APPLIED|FAILED|PENDING
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)] public DateTime OccurredAt { get; set; }
}

public sealed class SeedStateDocument { [BsonId] public required string Key { get; set; } public DateTime CompletedAt { get; set; } public long Count { get; set; } }
```

Store `DateTime` in UTC (the driver's default `DateTimeOffset` serializer is awkward). Convert to `DateTimeOffset` at the GraphQL boundary.

Collections: `patches`, `patch_events`, `seed_state`. Indexes (create in the seed service, idempotent):

- `patch_events`: `{ tenantId: 1, deviceId: 1, occurredAt: -1 }` (the only query path), `{ tenantId: 1 }`.
- `patches`: none beyond `_id`.

## 5. Seed data (pure, testable, in `PatchSeedData`)

Catalog (300 patches), `rng = DeterministicRandom.For(0, "patch-catalog")`:

```
for i in 0..299:
  id = $"patch-{i:D4}", kbId = $"KB{5000000 + i}"
  vendor  = pick(rng, ["Microsoft","Canonical","Red Hat","Apple","Adobe","Oracle"])
  severity = weighted(rng, CRITICAL 15%, HIGH 35%, MEDIUM 35%, LOW 15%)
  title   = $"{vendor} security update {kbId}"   (or a Bogus sentence seeded from rng.Next())
  releasedAt = Epoch - days(rng.Next(0, 730))
```

Events per device, `rng = DeterministicRandom.For(device.Index, "patch")`:

```
count = rng.Next(5, 31)                                   // 5..30 inclusive
for n in 0..count-1:
  patchIndex = rng.Next(0, 300)
  occurredAt = DeterministicRandom.InstantInWindow(rng)
  status = weighted(rng, APPLIED 80%, FAILED 12%, PENDING 8%)
  id = $"{device.Id}-p{n:D3}"
```

Expected totals: ~210 000 events. Hard rule: no `DateTime.UtcNow`, no `Guid.NewGuid()`, no unseeded `Random`.

## 6. Seeding (`SeedHostedService`)

```
for attempt in 1..10 (backoff min(30, 2*attempt) s):
  ping database
  ensure indexes
  if seed_state has "patch": Completed; return
  drop patch_events and patches (discard partial)
  insert catalog (300 docs, one InsertMany)
  for each batch of 5000 events across DeviceCatalog.All(): InsertManyAsync(batch, new InsertManyOptions { IsOrdered = false })
       log "seeded {n} events for {devices} devices"
  insert seed_state { _id: "patch", completedAt: now, count }
  Completed
```

Expected duration: 10–30 s on Docker Desktop. `start_period` in compose is 180 s.

Health: `AddCheck<SeedCompletedHealthCheck>("seed")` plus a Mongo ping check (`IMongoDatabase.RunCommandAsync(new BsonDocument("ping", 1))`).

## 7. GraphQL

`Program.cs` follows the Shared.Auth usage pattern with `AddServiceAccessPolicy(DevAuth.Services.Patch)`:

```csharp
builder.Services.Configure<MongoOptions>(builder.Configuration.GetSection("Mongo"));
builder.Services.AddSingleton<IMongoClient>(sp => new MongoClient(
    sp.GetRequiredService<IOptions<MongoOptions>>().Value.ConnectionString ?? "mongodb://localhost:27017"));
builder.Services.AddSingleton<IPatchStore, PatchStore>();
builder.Services.AddDevJwtAuthentication(builder.Configuration);
builder.Services.AddServiceAccessPolicy(DevAuth.Services.Patch);
builder.Services.AddSingleton<SeedState>();
builder.Services.AddHostedService<SeedHostedService>();
builder.Services.AddHealthChecks().AddCheck<MongoPingHealthCheck>("mongo").AddCheck<SeedCompletedHealthCheck>("seed");
builder.Services.AddGraphQLServer().AddAuthorization().AddQueryType<Query>().AddTypeExtension<DeviceExtensions>().AddType<PatchEventType>()/*...*/;
```

`MongoClient` construction does not connect; `schema export` works with no Mongo.

```csharp
public sealed record Device([property: ID] string Id);

[Authorize]                                  // valid token required for everything
public sealed class Query
{
    [Lookup, Internal]                       // per version-facts §2. NOT client-callable via the gateway.
    public Device GetDeviceById([ID] string id) => new(id);
    // Always a stub. Tenant scoping happens in the field resolvers, keyed on the claim.
    // Returning null here would make patchEvents null WITHOUT an error and break the UI contract.

    [Authorize(Policy = DevAuth.ServiceAccessPolicy)]
    public Task<IReadOnlyList<Patch>> GetPatches(int first, int offset, IPatchStore store, CancellationToken ct)
        => store.GetPatchesAsync(Math.Clamp(first, 1, 100), Math.Max(0, offset), ct);
}

[ExtendObjectType<Device>]
public sealed class DeviceExtensions
{
    // NULLABLE list on purpose (plan §4.2). Do not change to non-null. Schema test enforces this.
    [Authorize(Policy = DevAuth.ServiceAccessPolicy)]
    public async Task<IReadOnlyList<PatchEvent>?> GetPatchEvents(
        [Parent] Device device, DateTimeOffset? since, DateTimeOffset? until,
        ICallerContext caller, IPatchStore store, CancellationToken ct)
        => await store.GetEventsAsync(caller.TenantId, device.Id, since, until, ct);
}
```

`PatchStore.GetEventsAsync`: filter `TenantId == tenant && DeviceId == deviceId`, plus `OccurredAt >= since` / `<= until` when given (convert to UTC `DateTime`), sort `OccurredAt` descending, limit 1000, then join the catalog from an in-memory dictionary (`patches` loaded once after seed completes, 300 entries; reload lazily if empty). Return `[]` when nothing matches. Cross-tenant callers therefore get `[]`, never another tenant's rows and never an error.

GraphQL types mirror the contract exactly: `PatchEvent { id deviceId occurredAt status patch }`, `Patch { id kbId title severity vendor releasedAt }`, enums `PatchStatus`, `PatchSeverity`. Use C# enums with the same member names so Hot Chocolate emits the contract names.

## 8. Export

```bash
cd src/Patch && dotnet run -c Release --no-launch-profile -- schema export --output ../../schemas/patch.graphqls
```

Must work with no Mongo and no signing key. Check the output: `patchEvents(since: DateTime, until: DateTime): [PatchEvent!]` (no trailing `!`), `deviceById` carries `@lookup @internal`.

## 9. Tests (`tests/Patch.Tests`)

Unit tests run with no Docker. Integration tests use `Testcontainers.MongoDb` (`mongo:8`) + `WebApplicationFactory<Program>` with `Mongo__ConnectionString` overridden; wait for `/health` 200 first.

| Test | Category | Assertion |
|---|---|---|
| `Schema_matches_contract` | unit | `Device.patchEvents` type is a **nullable** list of non-null `PatchEvent`; args `since`/`until` nullable `DateTime`; `deviceById` exists and has the internal/lookup directives; `patches` exists |
| `PatchEvents_field_is_nullable_list` | unit | explicit, separate test with a comment: "guards plan §4.2; do not delete" |
| `SeedData_is_deterministic` | unit | `BuildEventsFor(Build(42))` twice → identical sequences; count in 5..30; all `occurredAt` in window |
| `SeedData_catalog_has_300_and_stable_ids` | unit | ids `patch-0000..patch-0299` |
| `Export_runs_without_database` | unit | as in P2B |
| `Seed_is_idempotent_and_indexed` | integration | second start does not change counts; index `tenantId_1_deviceId_1_occurredAt_-1` exists |
| `Events_for_own_tenant_device` | integration | alice, `deviceById(id:"dev-00001") { patchEvents { id status patch { kbId } } }` → non-empty, ids prefixed `dev-00001-p`, count equals seed function output |
| `Events_cross_tenant_are_empty_not_error` | integration | dave (TenantB) for `dev-00001` → `patchEvents == []`, no errors |
| `Denied_without_patch_service` | integration | carol (`softwareinstall` only): `deviceById` non-null, `patchEvents == null`, one error, `path == ["deviceById","patchEvents"]`, `extensions.code == "AUTH_NOT_AUTHORIZED"` |
| `Denied_on_patches_root_field` | integration | carol → `patches == null`, `AUTH_NOT_AUTHORIZED` |
| `Unauthenticated_is_AUTH_NOT_AUTHENTICATED` | integration | |
| `Since_until_filter_inclusive` | integration | pick two event timestamps from seed data; query with `since` = earlier, `until` = later → both included, nothing outside |
| `Order_is_descending_and_capped` | integration | timestamps non-increasing; ≤ 1000 |

## 10. Dockerfile

Template from P2C with `<Name> = Patch`. Verify with a local `mongo:8` container.

## 11. Definition of Done

- [ ] All §9 tests pass.
- [ ] `schemas/patch.graphqls` committed; produced with no Mongo and no key; `patchEvents` nullable; lookup internal.
- [ ] `/health` 503 until seeded, 200 after; second start skips seeding.
- [ ] Seeding finishes under 180 s on a cold Docker Desktop; log the duration.
- [ ] Denial and outage behaviour verified against the subgraph directly (`AUTH_NOT_AUTHORIZED` at the field).
- [ ] Image builds from the template; non-root; 8080.
- [ ] README one-liner for this lane's tests.
- [ ] Deviations recorded in `docs/version-facts.md §8`.
