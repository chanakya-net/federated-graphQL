# Phase 4 — Fusion gateway and schema composition

**Lane B (after P2B) or its own lane. Part 1 starts after Phase 1. Part 2 starts when P2B, P3A, P3B, P3C are merged.**
**Effort:** 1–2 days. **Owns:** `src/Gateway/`, `tests/Gateway.Tests/`, `gateway/`, `scripts/export-schemas.sh`, `scripts/compose-schema.sh`, `scripts/check-schema-drift.sh`.

## 1. Purpose

The single public GraphQL endpoint. Composes the four source schemas into `gateway/gateway.far`, loads it, validates JWTs at the edge, forwards the `Authorization` header unchanged, bounds every subgraph call with a timeout, and returns partial data on subgraph failure. Also owns the scripted, repeatable composition step and the drift check.

Part 1 works from `contracts/*.graphqls` so the gateway, scripts and tests exist before any real subgraph. Part 2 swaps in the real exports.

## 2. Inputs

- `docs/version-facts.md` §3 (compose command), §4 (gateway registration), §5 (header forwarding), §6 (error mode, timeouts).
- `contracts/` (Part 1), `schemas/*.graphqls` (Part 2).
- `tests/fixtures/*.json` (expected shapes).
- P2C compose entry for `fusion-gateway` (env var names).

## 3. Project layout

```
src/Gateway/
  Gateway.csproj             # refs: gateway package (version-facts §1), HotChocolate.AspNetCore,
                             #       Microsoft.AspNetCore.Authentication.JwtBearer, Shared.Auth
  Program.cs
  Dockerfile
  Transport/
    ForwardAuthorizationHandler.cs
    SubgraphClientNames.cs   # "DeviceDirectory","Patch","Vulnerability","SoftwareInstall"
  EdgeAuthMiddleware.cs
gateway/
  gateway.far                # committed composed archive
  source-schemas/            # only if version-facts §3 requires per-source settings files
    DeviceDirectory.json  Patch.json  Vulnerability.json  SoftwareInstall.json
scripts/
  export-schemas.sh
  compose-schema.sh
  check-schema-drift.sh
```

## 4. `Program.cs`

```csharp
using SoR.Shared.Auth;

var builder = WebApplication.CreateBuilder(args);
var cfg = builder.Configuration;
var timeout = TimeSpan.FromSeconds(int.TryParse(cfg["SUBGRAPH_TIMEOUT_SECONDS"], out var s) ? s : 5);

builder.Services.AddHttpContextAccessor();
builder.Services.AddTransient<ForwardAuthorizationHandler>();
foreach (var name in SubgraphClientNames.All)
{
    var url = cfg[$"SUBGRAPH_{name.ToUpperInvariant()}_URL"] ?? $"http://localhost:0/{name}";   // placeholder keeps startup alive
    builder.Services.AddHttpClient(name, c => { c.BaseAddress = new Uri(url); c.Timeout = timeout; })
        .AddHttpMessageHandler<ForwardAuthorizationHandler>();
}

builder.Services.AddDevJwtAuthentication(cfg);      // signature + expiry only; no tenant or service logic here
builder.Services.AddHealthChecks();                 // + "auth-config" from Shared.Auth

// Gateway registration — copy the exact form from version-facts §4. Illustrative:
//   builder.AddGraphQLGateway().AddFileConfiguration(cfg["GATEWAY_ARCHIVE"] ?? "gateway.far")
//          .ModifyOptions(o => o.ErrorHandlingMode = <value from version-facts §6>);

var app = builder.Build();
app.UseAuthentication();
app.UseMiddleware<EdgeAuthMiddleware>();
app.MapHealthChecks("/health");
app.MapGraphQL();                                   // POST /graphql; GET serves the built-in UI
// If version-facts §4 says the UI must live elsewhere: app.MapNitroApp("/nitro") and disable the tool on /graphql.
app.Run();
```

`EdgeAuthMiddleware`: for `POST /graphql` (and `GET /graphql` **with** a `query` parameter), if `ctx.User.Identity?.IsAuthenticated != true` → `401` with body `{"errors":[{"message":"Unauthorized","extensions":{"code":"AUTH_NOT_AUTHENTICATED"}}]}` and `Content-Type: application/json`. Everything else passes through, so the browser UI still loads.

`ForwardAuthorizationHandler`: copy from `phase-0-spike.md §5.5`, or the mechanism version-facts §5 recorded if different. Set the header only when absent on the outgoing request.

Timeouts: `HttpClient.Timeout` per subgraph. If version-facts §6 recorded that a client timeout cancelled the whole request, apply the recorded fix (typically: catch `TaskCanceledException` in the handler when `ct.IsCancellationRequested == false` and rethrow as `HttpRequestException("subgraph timeout")`).

## 5. Scripts

All bash 3.2 compatible; run from any directory; `set -euo pipefail`.

`scripts/export-schemas.sh`:

```bash
#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"
PAIRS="device-directory:DeviceDirectory patch:Patch vulnerability:Vulnerability software-install:SoftwareInstall"
for pair in $PAIRS; do
  name="${pair%%:*}"; proj="${pair##*:}"
  echo "==> exporting $name"
  ( cd "src/$proj" && dotnet run -c Release --no-launch-profile -- schema export --output "$ROOT/schemas/$name.graphqls" )
done
```

`scripts/compose-schema.sh`:

```bash
#!/usr/bin/env bash
# usage: compose-schema.sh [--from-contracts] [--no-export]
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"; cd "$ROOT"
SRC="schemas"; EXPORT=1
for a in "$@"; do case "$a" in --from-contracts) SRC="contracts"; EXPORT=0 ;; --no-export) EXPORT=0 ;; esac; done
[ "$EXPORT" = 1 ] && scripts/export-schemas.sh
mkdir -p gateway
# ⚠️ Replace the next command with the verified invocation from version-facts §3. Illustrative Fusion v2 form:
dotnet nitro fusion compose \
  --source-schema-file "$SRC/device-directory.graphqls" \
  --source-schema-file "$SRC/patch.graphqls" \
  --source-schema-file "$SRC/vulnerability.graphqls" \
  --source-schema-file "$SRC/software-install.graphqls" \
  --archive gateway/gateway.far
echo "composed gateway/gateway.far from $SRC"
```

If version-facts §3 requires per-source settings (name + URL), keep them in `gateway/source-schemas/<Name>.json` and pass them exactly as recorded. URLs in settings must equal the compose DNS names (`http://patch:8080/graphql`), the same values the gateway's HttpClients get from env.

`scripts/check-schema-drift.sh`:

```bash
#!/usr/bin/env bash
# Fails if the committed exports or the composed archive are stale.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"; cd "$ROOT"
tmp="$(mktemp -d)"; trap 'rm -rf "$tmp"' EXIT
cp -R schemas "$tmp/schemas.before"; cp gateway/gateway.far "$tmp/before.far"
scripts/compose-schema.sh
if ! diff -r "$tmp/schemas.before" schemas >/dev/null; then echo "DRIFT: exported schemas differ from committed"; diff -r "$tmp/schemas.before" schemas || true; exit 1; fi
# .far is a zip; compare contents, not bytes (zip timestamps vary)
mkdir "$tmp/a" "$tmp/b"; unzip -q "$tmp/before.far" -d "$tmp/a"; unzip -q gateway/gateway.far -d "$tmp/b"
if ! diff -r "$tmp/a" "$tmp/b" >/dev/null; then echo "DRIFT: composed archive contents differ from committed"; exit 1; fi
echo "no drift"
```

If the archive format is not a zip (check with `file gateway/gateway.far`), compare with the tool's own inspect/print command recorded in version-facts §3 instead of `unzip`.

CI job `schema-drift` runs this script; document in README: **after any subgraph schema change, run `scripts/compose-schema.sh` and commit `schemas/` and `gateway/gateway.far`.**

## 6. Dockerfile (`src/Gateway/Dockerfile`, context = repo root)

Same as the P2C template with `<Name> = Gateway`, plus after the publish copy:

```dockerfile
COPY gateway/gateway.far /app/gateway.far
ENV GATEWAY_ARCHIVE=/app/gateway.far
```

The image build never runs composition. A stale `gateway.far` is caught by the drift check, not at build time.

## 7. Part 1 — from contracts (before real subgraphs exist)

1. Implement §3–§6.
2. `scripts/compose-schema.sh --from-contracts` → `gateway/gateway.far`. If the composer rejects hand-written SDL (missing directive definitions), prepend the directive definitions found in the spike's exports to a temporary copy of each contract; do not edit `contracts/`.
3. `dotnet run --project src/Gateway` with `SUBGRAPH_*_URL` pointing at unreachable ports → `/health` 200, `GET /graphql` serves the UI, `POST /graphql` without a token → 401, with a token → `data.device: null` plus a transport error (nothing behind it yet). That is the expected Part 1 state.
4. Tests in §9 that do not need subgraphs pass.
5. Commit the interim `gateway.far` with the message `gateway: interim archive composed from contracts`.

## 8. Part 2 — from real exports

1. Merge state: P2B, P3A, P3B, P3C all merged; each has committed its `schemas/*.graphqls`.
2. `scripts/compose-schema.sh` (full: re-export + compose). Fix any composition errors **in the subgraph that violates the contract**, not by editing schemas by hand; hand-edited exports are overwritten next run.
3. Confirm on the composed public schema (introspect the running gateway): `Query` has `device`, `devices` and **not** `deviceById`; `Device` has all nine fields (six from Device Directory plus three extension fields); the three extension fields are nullable lists.
4. `scripts/check-schema-drift.sh` → `no drift`.
5. Commit `schemas/` and `gateway/gateway.far`.
6. Run the tracer-bullet query from `00-execution-plan.md §4` against the full stack with `scripts/up.sh`; keep the response as `tests/fixtures/gateway-full-alice.json`.

## 9. Tests (`tests/Gateway.Tests`)

| Test | Needs | Assertion |
|---|---|---|
| `ForwardAuthorizationHandler_copies_header` | unit | fake `IHttpContextAccessor` with `Authorization: Bearer x`; handler adds the same header to the outgoing request; does not overwrite an existing one |
| `ForwardAuthorizationHandler_no_header_no_copy` | unit | |
| `EdgeAuth_rejects_unauthenticated_post` | WebApplicationFactory | `POST /graphql` no token → 401, JSON body with `AUTH_NOT_AUTHENTICATED` |
| `EdgeAuth_allows_get_ui` | WebApplicationFactory | `GET /graphql` with `Accept: text/html` → 200 |
| `EdgeAuth_passes_valid_token` | WebApplicationFactory | valid token → not 401 (any GraphQL response) |
| `Gateway_starts_with_committed_archive` | WebApplicationFactory | `/health` 200; introspection lists `device` and `devices`, not `deviceById` |
| `Timeout_is_configured` | unit | resolve `IHttpClientFactory`, create `Patch` client → `Timeout == 5 s` (from env override 5) |
| `Partial_failure_shape` | Docker (Integration) | with `scripts/up.sh` then `scripts/demo-outage.sh patch stop`: alice query → `data.device` non-null, `patchEvents` null, error path prefix `["device","patchEvents"]`, other sections non-null; then `restore` |
| `Denial_shape_through_gateway` | Docker (Integration) | bob → `installEvents` null with `AUTH_NOT_AUTHORIZED`; patch/vulnerability non-null |

The two Docker tests can live as `[Trait("Category","Integration")]` xunit tests that shell out to the scripts, or be deferred to `scripts/e2e.sh` in P6; if deferred, say so in the DoD.

## 10. Definition of Done

- [ ] Part 1: gateway runs from the contract-composed archive; §9 non-Docker tests pass; interim archive committed.
- [ ] Part 2: archive composed from real exports; drift check passes; `deviceById` hidden; nine `Device` fields present; extension fields nullable in the **composed** schema.
- [ ] Header forwarding verified end to end (a subgraph log line shows the header for a gateway-originated request).
- [ ] Unauthenticated `POST /graphql` → 401 at the gateway; no subgraph receives the request.
- [ ] Stop and pause of `patch` both degrade only `patchEvents`; pause resolves within `SUBGRAPH_TIMEOUT_SECONDS + 3 s`.
- [ ] README: composition workflow, drift check, "after any schema change" warning.
- [ ] Every `⚠️` in this document resolved against version-facts; deviations recorded.
