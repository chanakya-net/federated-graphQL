# Phase 0 spike (throwaway, kept for reference)

Smallest possible Fusion v2 federation: two subgraphs and a gateway, in-memory data, no databases.
It exists to prove the assumptions in `phases/phase-0-spike.md`; the answers live in
[`../docs/version-facts.md`](../docs/version-facts.md). Not part of the solution; do not reference it
from `src/`.

| Path | What |
|---|---|
| `Owner/` | Owns `Device { id, hostname, tenantId }`; public `device(id)` lookup, tenant-scoped |
| `Extender/` | Extends `Device` with nullable `notes: [String!]` behind the `ServiceAccess` policy (`services` claim must contain `extender`); internal `deviceById` lookup. Also a spike-only non-null `noteCount: Int!` for experiment 9 |
| `Gateway/` | Fusion gateway: edge JWT check, `Authorization` forwarding, 5 s per-subgraph timeout, URLs from `SUBGRAPH_<NAME>_URL` |
| `Token/` | Prints a JWT: `dotnet run --project spike/Token -- <tenantId> [service,...]` |
| `Shared/` | `SpikeAuth` constants + JWT bearer registration, linked into the projects |
| `schemas/` | Exported SDL + settings files (URLs edited to the compose service names) |
| `Gateway/gateway.far` | Composed archive |
| `run-experiments.sh` | Runs experiments 1–11, writes `tests/fixtures/*.json` and `results/` |

```bash
spike/run-experiments.sh                  # build, start, run everything; exit 0 = all pass
docker compose -f spike/docker-compose.yml down
```

Re-export and recompose after changing a spike subgraph:

```bash
(cd spike/Owner && dotnet run -- schema export --output ../schemas/owner.graphqls)
(cd spike/Extender && dotnet run -- schema export --output ../schemas/extender.graphqls)
(cd spike && rm -f Gateway/gateway.far && dotnet nitro fusion compose \
  -f schemas/owner.graphqls -f schemas/extender.graphqls -a Gateway/gateway.far)
```

The gateway is published on host port **5050** (`SPIKE_GATEWAY_PORT`), because macOS reserves 5000
for AirPlay Receiver.
