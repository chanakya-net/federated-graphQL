# Contracts (frozen at tag `contracts-v1`)

These files are the shapes every lane builds to. They let the four subgraphs, the gateway and the UI be
built in parallel without talking to each other.

| File | What it fixes | Used by |
|---|---|---|
| `device-directory.graphqls` | Device Directory source schema (public `Device` owner) | P2B, P4, P5 |
| `patch.graphqls` | Patch source schema (`Device.patchEvents`, `patches`) | P3A, P4, P5 |
| `vulnerability.graphqls` | Vulnerability source schema (`Device.vulnerabilityEvents`, `cves`) | P3B, P4, P5 |
| `software-install.graphqls` | SoftwareInstall source schema (`Device.installEvents`) | P3C, P4, P5 |
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
    -a gateway/gateway.far
  ```

  Result: the public `Query` has `device`, `devices`, `patches`, `cves` and **not** `deviceById`;
  `Device` has nine fields (six from Device Directory plus the three nullable extension lists).
- **P5** builds its queries and fixtures against these SDL files and `errors.md`.

## Notes on the SDL

- **No `@key`.** In Fusion v2 (HC 16.6.6) the entity key is implied by the lookup's argument:
  `device(id: ID!) @lookup` / `deviceById(id: ID!) @lookup` make `id` the key
  (`docs/version-facts.md` §2). Do not add `[Key]` / `@key`.
- **`@lookup @internal`** on `deviceById` keeps it off the composite schema; only the gateway calls it.
- **Every field guarded by a service policy is nullable**: the three extension lists *and* the root
  catalogs `patches` / `cves`. A denial or outage on a non-null field propagates to the nearest nullable
  parent (for a root field that is `data` itself). Never tighten these to non-null.
- `scalar DateTime` is Hot Chocolate's built-in `DateTime` (ISO-8601 with offset).

## Changing a contract

Contracts are frozen. Follow `phases/00-execution-plan.md` §7: an issue naming the affected lanes, one PR
that edits `contracts/` and notifies those lanes, and a new tag (`contracts-v2`) on merge. Almost every
"I need to change the contract" is solvable inside the lane.
