# Seeding contract

All seed code uses `SoR.Shared.Seeding` (`src/Shared.Seeding`). Nothing that federation depends on is
random.

- **Device set.** Every subgraph seeds exactly `DeviceCatalog.All()`: 12 000 devices, ids
  `dev-00000` … `dev-11999` (`DeviceCatalog.DeviceId(index)`). No subgraph invents device ids.
- **Tenant.** `SeedDevice.TenantId`: indexes 0–6999 → `TenantA` (7 000), 7000–11999 → `TenantB` (5 000).
  Every stored row / document / blob carries it.
- **Decoration** (hostname, OS, IP, last seen) comes from Bogus with a per-index seed and is pinned by
  `tests/Shared.Seeding.Tests/golden-devices.json`. Only Device Directory stores it.
- **Domain RNG.** `DeterministicRandom.For(device.Index, "<domain>")` with domain strings `"patch"`,
  `"vulnerability"`, `"softwareinstall"` for per-device data, and `"<domain>-catalog"` (e.g.
  `"patch-catalog"`) for catalogs. Draw from one `Random` per device, in a fixed order.
- **Timestamps.** `DeterministicRandom.InstantInWindow(rng)`, i.e. inside
  `[SeedConstants.Epoch - 365 days, SeedConstants.Epoch]` with `Epoch = 2026-09-01T00:00:00Z`. Nothing
  uses the wall clock (`DateTime.UtcNow`, `DateTimeOffset.Now`), `Guid.NewGuid()` or an unseeded `Random`.
- **Event ids.**
  - Patch: `{deviceId}-p{n:D3}` (e.g. `dev-00042-p007`).
  - Vulnerability: finding `{deviceId}-f{n:D3}`; its events `{findingId}:detected` and
    `{findingId}:remediated`.
  - SoftwareInstall: `{deviceId}-i{n:D3}`.
- **Idempotency.** A completion marker (`seed_state` table / collection, `_seed/complete.json` blob) is
  written **last**. On start: marker present → skip; marker absent → discard any partial data and seed
  again. `/health` stays unhealthy until the marker exists.
- **Expected counts** (e2e sanity checks): patch events 5–30 per device, findings 2–15 per device,
  install events 3–20 per device.
- **Performance.** `DeviceCatalog.All()` builds all 12 000 devices in well under a second; bulk-insert
  rather than one round trip per row, and use bounded concurrency for blob PUTs (plan §6).
