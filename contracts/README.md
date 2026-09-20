# Contracts (frozen at tag `contracts-v1`; `contracts-v2` adds the reverse lookups, `contracts-v3` their device sets)

These files are the shapes every lane builds to. They let the five subgraphs, the gateway and the UI be
built in parallel without talking to each other.

**contracts-v2 (2026-09-20, "Find devices")** is additive to v1: nothing existing changed shape. Each domain
subgraph gained a reverse lookup from its catalog to the devices of the caller's tenant
(`devicesWithPatches(patchIds)`, `devicesWithCves(cveIds)`, `devicesWithSoftware(software)`, each returning
`{ items: [{ device: Device!, events: [...]! }!]!, totalCount: Int! }`), an optional `search` argument on the
catalogs (`patches`, `cves`) and a new `software(search, first, offset)` catalog. The `device` of each item
is the entity stub (`id` only); the gateway completes it through Device Directory's `device(id)` lookup, one
batched call per page (`docs/version-facts.md` §8, 2026-09-20 rows). Device Directory's contract is unchanged.

**contracts-v3 (2026-09-20, AND / OR)** is additive to v2: each reverse lookup result gained `matches`, the
sorted device-id set of every selected item (resolved only when selected, at most 10 000 ids each), and each
lookup an optional `deviceIds: [ID!]` argument (at most 100) restricting the candidates. The original client evaluated AND / OR over these sets. These fields remain available for compatibility;
the finder now uses the server-side query described below.

**Server-side search (2026-09-20)** adds the DeviceSearch subgraph and
`findDevices(filters, first, offset)`. It owns cross-domain matching and pagination. `FindDevicesResult`
contains the combined count, next-page indicator, and final page of Device references plus normalized
matching events. Inputs use the existing ordered category/key/connector expression (AND before OR).
Discovery uses complete paginated reverse lookups, not capped `matches` sets. Required-source failures
fail the nullable search root with an error. The existing Device Directory `DeviceSearchResult` stays unchanged.

| File | What it fixes | Used by |
|---|---|---|
| `device-directory.graphqls` | Device Directory source schema (public `Device` owner) | P2B, P4, P5 |
| `patch.graphqls` | Patch source schema (`Device.patchEvents`, `patches`, `devicesWithPatches`) | P3A, P4, P5 |
| `vulnerability.graphqls` | Vulnerability source schema (`Device.vulnerabilityEvents`, `cves`, `devicesWithCves`) | P3B, P4, P5 |
| `software-install.graphqls` | SoftwareInstall source schema (`Device.installEvents`, `software`, `devicesWithSoftware`) | P3C, P4, P5 |
| `device-search.graphqls` | DeviceSearch (`findDevices`, server AND/OR and pagination, provider capabilities and catalogs) | Gateway, UI |
| `*-settings.json` | Source-schema name + in-compose URL for each subgraph (the composer requires them) | P4, subgraph lanes |
| `http-and-env.md` | Compose names, ports, endpoints, env vars, `/health` meaning | P2C, all service lanes |
| `tokens.json.md` | `tokens.json` shape and the JWT claim contract | P2A, P5 |
| `errors.md` | Response shapes for outage / denial / not found, and the UI rules built on them | P5, P6 |
| `seeding.md` | Device set, RNG, ids, idempotency, expected counts | every subgraph lane, P6 |

## How conformance is checked

- **Subgraph lanes** prove conformance with in-process schema tests against their own Hot Chocolate
  schema (field names, argument names, nullability, enum values), **not** with a textual diff. The real
  export will differ cosmetically: it adds `@authorize` directives, directive definitions, descriptions
  and ordering. That is fine.
- **P4 (part 1)** composes the gateway straight from these files before any subgraph exists. Verified in
  Phase 1 with the pinned Nitro CLI (16.6.6), no directive definitions or edits needed:

  ```bash
  dotnet tool restore
  rm -f gateway/gateway.far
  dotnet nitro fusion compose \
    -f contracts/device-directory.graphqls \
    -f contracts/patch.graphqls \
    -f contracts/vulnerability.graphqls \
    -f contracts/software-install.graphqls \
    -f contracts/device-search.graphqls \
    -a gateway/gateway.far
  ```

  Result: the public `Query` has `device`, `devices`, `patches`, `cves`, `software`, the three
  `devicesWith*` reverse lookups, `findDevices`, and **not** `deviceById`; `Device` has nine fields (six from Device
  Directory plus the three nullable extension lists).
- **P5** builds its queries and fixtures against these SDL files and `errors.md`.

## Notes on the SDL

- **No `@key`.** In Fusion v2 (HC 16.6.6) the entity key is implied by the lookup's argument:
  `device(id: ID!) @lookup` / `deviceById(id: ID!) @lookup` make `id` the key
  (`docs/version-facts.md` §2). Do not add `[Key]` / `@key`.
- **`@lookup @internal`** on `deviceById` keeps it off the composite schema; only the gateway calls it.
- **Every field guarded by a service policy is nullable**: the three extension lists, the root catalogs
  `patches` / `cves` / `software` and the three reverse lookups. A denial or outage on a non-null field
  propagates to the nearest nullable parent (for a root field that is `data` itself). Never tighten these
  to non-null. Inside a reverse lookup the item's `device` **is** non-null on purpose: if Device Directory
  cannot complete a stub the whole (nullable) lookup fails with an error at its path, the same "directory
  down" shape the timeline has, instead of half-completed rows.
- **Reverse lookups cap the selection at 50 keys** per query; more is a field error with
  `extensions.code = "SELECTION_TOO_LARGE"`. Blank keys are ignored, duplicates collapse.
- `scalar DateTime` is Hot Chocolate's built-in `DateTime` (ISO-8601 with offset).

## Changing a contract

Contracts are frozen. Follow `phases/00-execution-plan.md` §7: an issue naming the affected lanes, one PR
that edits `contracts/` and notifies those lanes, and a new tag (`contracts-v2`) on merge. Almost every
"I need to change the contract" is solvable inside the lane.
