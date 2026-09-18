# Phase 3C — SoftwareInstall subgraph (Azure Blob via Azurite)

**Lane E. Starts after Phase 1. Parallel with every other Stage 2 lane.**
**Effort:** 1.5–2 days. **Owns:** `src/SoftwareInstall/`, `tests/SoftwareInstall.Tests/`, `schemas/software-install.graphqls`.

## 1. Purpose

Extend `Device` with `installEvents`, guarded by the `softwareinstall` service policy and tenant-scoped **structurally**: one blob per device at `{tenantId}/{deviceId}/installEvents.json`, so the tenant claim selects the prefix and a cross-tenant read finds nothing.

## 2. Inputs

- `contracts/software-install.graphqls`, `contracts/http-and-env.md`, `contracts/seeding.md`, `contracts/errors.md`.
- `docs/version-facts.md` §2, §3.
- `src/Shared.Seeding`, `src/Shared.Auth`. Dockerfile template from P2C.

## 3. Project layout

```
src/SoftwareInstall/
  SoftwareInstall.csproj    # refs: HotChocolate.*, Azure.Storage.Blobs, Shared.Seeding, Shared.Auth
  Program.cs
  Dockerfile
  Storage/
    BlobOptions.cs          # ConnectionString, Container (section "Blob")
    InstallEventsBlobStore.cs   # IInstallEventsStore: ReadAsync(tenant, deviceId) -> DeviceInstallDocument?; WriteAsync(doc)
    DeviceInstallDocument.cs    # JSON model
  Seeding/
    InstallSeedData.cs      # pure: BuildCatalog(), BuildEventsFor(SeedDevice)
    SeedHostedService.cs
    SeedState.cs, SeedCompletedHealthCheck.cs
  GraphQL/
    Device.cs, DeviceExtensions.cs, Query.cs, Types.cs
```

## 4. Blob layout and JSON

Container: `install-events` (from `Blob__Container`). Blob name: `{tenantId}/{deviceId}/installEvents.json`. Seed marker: `_seed/complete.json` (the underscore prefix can never collide with a tenant id).

```json
{
  "schemaVersion": 1,
  "tenantId": "TenantA",
  "deviceId": "dev-00042",
  "events": [
    {
      "id": "dev-00042-i003",
      "occurredAt": "2026-07-14T09:12:00+00:00",
      "action": "INSTALL",
      "result": "SUCCESS",
      "software": { "name": "7-Zip", "version": "24.08", "publisher": "Igor Pavlov" }
    }
  ]
}
```

`System.Text.Json`, camelCase, enums as strings, `DateTimeOffset` ISO 8601. Events inside the blob are stored ascending by `occurredAt`; the resolver returns descending.

## 5. Seed data (`InstallSeedData`, pure)

Catalog (150 products), `rng = DeterministicRandom.For(0, "softwareinstall-catalog")`: `name` from a fixed list of ~40 realistic product names combined with a suffix index when exhausted, `publisher` from a fixed list, 3–6 versions per product generated as `{major}.{minor}.{patch}` with increasing numbers.

Events per device, `rng = DeterministicRandom.For(device.Index, "softwareinstall")`:

```
count = rng.Next(3, 21)                                  // 3..20
for n in 0..count-1:
  product = catalog[rng.Next(0, 150)]
  action = weighted(rng, INSTALL 55%, UPGRADE 30%, UNINSTALL 15%)
  version = product.Versions[rng.Next(0, product.Versions.Count)]
  result = rng.NextDouble() < 0.92 ? SUCCESS : FAILED
  occurredAt = DeterministicRandom.InstantInWindow(rng)
  id = $"{device.Id}-i{n:D3}"
sort by occurredAt ascending before writing
```

## 6. Seeding

```
create BlobServiceClient from connection string (construction never connects)
retry loop as in P2B:
  container.CreateIfNotExistsAsync()
  if blob "_seed/complete.json" exists: Completed; return
  Parallel.ForEachAsync(DeviceCatalog.All(), new ParallelOptions { MaxDegreeOfParallelism = 32 }, async (device, ct) =>
      upload JSON to $"{device.TenantId}/{device.Id}/installEvents.json" with overwrite: true
      (Interlocked counter; log every 1000))
  upload "_seed/complete.json" { completedAt, deviceCount: 12000 }
  Completed
```

12 000 PUTs against Azurite: expect 30–90 s. Compose `start_period` is 300 s. Overwrite semantics make a re-run after a partial seed safe without a delete pass.

Health: `SeedCompletedHealthCheck` + a check that `container.ExistsAsync()` succeeds.

## 7. Read path (`InstallEventsBlobStore.ReadAsync`)

```
blob = container.GetBlobClient($"{tenant}/{deviceId}/installEvents.json")
try: content = await blob.DownloadContentAsync(ct); deserialize
catch RequestFailedException when Status == 404: return null   // no blob = no events for this tenant/device
```

Resolver: `null` document → `[]`; otherwise filter `since`/`until` inclusive, order descending, take 1000. A TenantB caller asking for a TenantA device reads `TenantB/dev-00001/...`, which does not exist → `[]`. That is the structural isolation; there is no tenant column to forget.

## 8. GraphQL

Same pattern as P3A with `AddServiceAccessPolicy(DevAuth.Services.SoftwareInstall)`:

- `Query`: `[Authorize]` on class; `deviceById` `[Lookup, Internal]` stub. No other root fields (contract has none).
- `DeviceExtensions.GetInstallEvents(...)`: `[Authorize(Policy = ServiceAccess)]`, returns `IReadOnlyList<InstallEvent>?` — **nullable**.
- Types exactly as the contract: `InstallEvent { id deviceId occurredAt action result software }`, `Software { name version publisher }`, enums `InstallAction`, `InstallResult`.

## 9. Export

```bash
cd src/SoftwareInstall && dotnet run -c Release --no-launch-profile -- schema export --output ../../schemas/software-install.graphqls
```

No Azurite, no key. Check nullability and directives.

## 10. Tests (`tests/SoftwareInstall.Tests`)

Integration: `Testcontainers.Azurite` + `WebApplicationFactory<Program>` with `Blob__ConnectionString` set to the container's connection string (Testcontainers exposes it) and `Blob__Container=install-events-test`.

| Test | Category | Assertion |
|---|---|---|
| `Schema_matches_contract` / `InstallEvents_field_is_nullable_list` | unit | |
| `SeedData_is_deterministic_and_sorted` | unit | device 42 twice → identical; ascending in blob order; count 3..20 |
| `Document_roundtrips_json` | unit | serialize → deserialize equals; enums as strings; `occurredAt` keeps offset |
| `Export_runs_without_storage` | unit | |
| `Seed_writes_one_blob_per_device_and_marker` | integration | blob count with prefix `TenantA/` = 7000, `TenantB/` = 5000, marker exists; second start skips (log line) |
| `Events_for_own_tenant_device` | integration | alice: `installEvents` for `dev-00001` non-empty; descending |
| `Events_cross_tenant_are_empty_not_error` | integration | dave → `[]`, no errors (blob `TenantB/dev-00001/...` absent) |
| `Missing_blob_yields_empty_list` | integration | delete `TenantA/dev-00005/...` then query → `[]` |
| `Denied_without_softwareinstall_service` | integration | bob → `installEvents == null`, `AUTH_NOT_AUTHORIZED` at `["deviceById","installEvents"]`, `deviceById` non-null |
| `Since_until_filter_inclusive` | integration | |
| `Unauthenticated_is_AUTH_NOT_AUTHENTICATED` | integration | |

## 11. Dockerfile

Template from P2C with `<Name> = SoftwareInstall`.

## 12. Definition of Done

- [ ] All §10 tests pass.
- [ ] `schemas/software-install.graphqls` committed; produced with no storage and no key; `installEvents` nullable; lookup internal.
- [ ] Seeding finishes under 300 s cold with 32-way concurrency; duration logged; a second start skips.
- [ ] `/health` semantics as in P2B.
- [ ] Explicit `BlobEndpoint` connection string works from inside compose (no `UseDevelopmentStorage`).
- [ ] Image builds; non-root; 8080.
- [ ] README one-liner; deviations recorded.
