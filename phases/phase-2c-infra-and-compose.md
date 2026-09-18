# Phase 2C — Infrastructure containers and Docker Compose

**Lane A (after 2A) or its own lane. Starts after Phase 1. Parallel with every other Stage 2 lane.**
**Effort:** 1 day. **Owns:** `docker-compose.yml`, `.env` (completing it), `.dockerignore`, `infra/`, `scripts/up.sh`, `scripts/wait-healthy.sh`, `scripts/demo-*.sh`, the Dockerfile template.

## 1. Purpose

Make `docker compose up --build` the single command. Define every service now, from the contracts, so subgraph lanes plug in without editing this file. Pre-empt the known gotchas: no `curl` in .NET images, seeding time vs. healthchecks, Azurite endpoints, Postgres roles.

## 2. Inputs

- `contracts/http-and-env.md` (service names, ports, env vars).
- Plan §4.1, §4.6, §7.

## 3. `docker-compose.yml`

```yaml
name: sor-poc

x-dotnet-health: &dotnet-health
  test: ["CMD-SHELL", "wget -qO- http://127.0.0.1:8080/health >/dev/null 2>&1 || exit 1"]
  interval: 5s
  timeout: 3s
  retries: 30

services:
  postgres:
    image: postgres:17-alpine
    environment:
      POSTGRES_USER: postgres
      POSTGRES_PASSWORD: ${POSTGRES_PASSWORD}
      POSTGRES_DB: sor
      DEVDIR_DB_PASSWORD: ${DEVDIR_DB_PASSWORD}
      VULN_DB_PASSWORD: ${VULN_DB_PASSWORD}
    volumes:
      - ./infra/postgres/init:/docker-entrypoint-initdb.d:ro
      - pgdata:/var/lib/postgresql/data
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U postgres -d sor"]
      interval: 5s
      timeout: 3s
      retries: 20
    networks: [internal]

  mongo:
    image: mongo:8
    volumes:
      - mongodata:/data/db
    healthcheck:
      test: ["CMD-SHELL", "mongosh --quiet --eval \"db.adminCommand('ping').ok\" | grep -q 1"]
      interval: 5s
      timeout: 5s
      retries: 20
    networks: [internal]

  azurite:
    image: mcr.microsoft.com/azure-storage/azurite:latest
    command: azurite-blob --blobHost 0.0.0.0 --blobPort 10000 --location /data --skipApiVersionCheck --silent
    volumes:
      - azuritedata:/data
    healthcheck:
      test: ["CMD-SHELL", "node -e \"require('net').connect(10000,'127.0.0.1').on('connect',()=>process.exit(0)).on('error',()=>process.exit(1))\""]
      interval: 5s
      timeout: 3s
      retries: 20
    networks: [internal]

  device-directory:
    build: { context: ., dockerfile: src/DeviceDirectory/Dockerfile }
    environment:
      ASPNETCORE_URLS: http://+:8080
      DEV_JWT_SIGNING_KEY: ${DEV_JWT_SIGNING_KEY}
      ConnectionStrings__DeviceDirectory: "Host=postgres;Database=sor;Username=devdir_user;Password=${DEVDIR_DB_PASSWORD};Search Path=device_directory"
    depends_on:
      postgres: { condition: service_healthy }
    healthcheck:
      <<: *dotnet-health
      start_period: 120s
    networks: [internal]

  patch:
    build: { context: ., dockerfile: src/Patch/Dockerfile }
    environment:
      ASPNETCORE_URLS: http://+:8080
      DEV_JWT_SIGNING_KEY: ${DEV_JWT_SIGNING_KEY}
      Mongo__ConnectionString: mongodb://mongo:27017
      Mongo__Database: patch
    depends_on:
      mongo: { condition: service_healthy }
    healthcheck:
      <<: *dotnet-health
      start_period: 180s
    networks: [internal]

  vulnerability:
    build: { context: ., dockerfile: src/Vulnerability/Dockerfile }
    environment:
      ASPNETCORE_URLS: http://+:8080
      DEV_JWT_SIGNING_KEY: ${DEV_JWT_SIGNING_KEY}
      ConnectionStrings__Vulnerability: "Host=postgres;Database=sor;Username=vuln_user;Password=${VULN_DB_PASSWORD};Search Path=vulnerability"
    depends_on:
      postgres: { condition: service_healthy }
    healthcheck:
      <<: *dotnet-health
      start_period: 180s
    networks: [internal]

  software-install:
    build: { context: ., dockerfile: src/SoftwareInstall/Dockerfile }
    environment:
      ASPNETCORE_URLS: http://+:8080
      DEV_JWT_SIGNING_KEY: ${DEV_JWT_SIGNING_KEY}
      Blob__ConnectionString: "DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;BlobEndpoint=http://azurite:10000/devstoreaccount1;"
      Blob__Container: install-events
    depends_on:
      azurite: { condition: service_healthy }
    healthcheck:
      <<: *dotnet-health
      start_period: 300s
    networks: [internal]

  fusion-gateway:
    build: { context: ., dockerfile: src/Gateway/Dockerfile }
    environment:
      ASPNETCORE_URLS: http://+:8080
      DEV_JWT_SIGNING_KEY: ${DEV_JWT_SIGNING_KEY}
      SUBGRAPH_DEVICEDIRECTORY_URL: http://device-directory:8080/graphql
      SUBGRAPH_PATCH_URL: http://patch:8080/graphql
      SUBGRAPH_VULNERABILITY_URL: http://vulnerability:8080/graphql
      SUBGRAPH_SOFTWAREINSTALL_URL: http://software-install:8080/graphql
      SUBGRAPH_TIMEOUT_SECONDS: ${SUBGRAPH_TIMEOUT_SECONDS}
    ports:
      - "${GATEWAY_PORT}:8080"
    depends_on:
      device-directory: { condition: service_healthy }
      # Domain subgraphs are deliberately NOT dependencies: the gateway must start and serve
      # degraded responses when any of them is missing or unhealthy (plan §7).
    healthcheck:
      <<: *dotnet-health
      start_period: 30s
    networks: [internal, public]

  token-generator:
    build: { context: ., dockerfile: src/TokenGenerator/Dockerfile }
    restart: "no"
    environment:
      DEV_JWT_SIGNING_KEY: ${DEV_JWT_SIGNING_KEY}
      TOKENS_OUTPUT: /tokens/tokens.json
    volumes:
      - tokens:/tokens
    networks: [internal]

  angular-ui:
    build: { context: ./ui, dockerfile: Dockerfile }
    ports:
      - "${UI_PORT}:80"
    depends_on:
      token-generator: { condition: service_completed_successfully }
      fusion-gateway: { condition: service_healthy }
    volumes:
      - tokens:/tokens:ro
    healthcheck:
      test: ["CMD-SHELL", "wget -qO- http://127.0.0.1/ >/dev/null 2>&1 || exit 1"]
      interval: 5s
      timeout: 3s
      retries: 20
    networks: [internal, public]

volumes:
  pgdata:
  mongodata:
  azuritedata:
  tokens:

networks:
  internal:
    internal: true
  public:
```

Notes:
- The `internal: true` network has no route to the host or the internet. Only the gateway and UI also sit on `public`, and only they publish ports.
- `start_period` covers first-boot seeding. Failures during it do not count against `retries`.
- The Azurite connection string uses the well-known emulator account key and the compose DNS name. `UseDevelopmentStorage=true` does not work across containers.
- `docker compose stop patch` / `docker compose pause patch` are the outage demos (plan §7). `docker compose start` / `unpause` recover.

## 4. `infra/postgres/init/01-roles-and-schemas.sh`

Init scripts run only when `pgdata` is empty. Shell form so passwords can come from env.

```bash
#!/usr/bin/env bash
set -euo pipefail
psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" <<-SQL
  CREATE ROLE devdir_user LOGIN PASSWORD '${DEVDIR_DB_PASSWORD}';
  CREATE ROLE vuln_user   LOGIN PASSWORD '${VULN_DB_PASSWORD}';
  CREATE SCHEMA device_directory AUTHORIZATION devdir_user;
  CREATE SCHEMA vulnerability    AUTHORIZATION vuln_user;
  REVOKE CREATE ON SCHEMA public FROM PUBLIC;
SQL
```

Each role owns exactly one schema and has no `USAGE` on the other. A subgraph physically cannot read the other's tables. Migrations run as the owning role, so EF can create tables and its history table inside the schema.

## 5. Dockerfile template (each subgraph lane copies into `src/<Name>/Dockerfile`)

```dockerfile
# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:<ver> AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props global.json ./
COPY src/Shared.Seeding/Shared.Seeding.csproj src/Shared.Seeding/
COPY src/Shared.Auth/Shared.Auth.csproj src/Shared.Auth/
COPY src/<Name>/<Name>.csproj src/<Name>/
RUN dotnet restore src/<Name>/<Name>.csproj
COPY src/Shared.Seeding src/Shared.Seeding
COPY src/Shared.Auth src/Shared.Auth
COPY src/<Name> src/<Name>
RUN dotnet publish src/<Name>/<Name>.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:<ver>-alpine
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080 DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=true
EXPOSE 8080
USER app
ENTRYPOINT ["dotnet", "<Name>.dll"]
```

Alpine has busybox `wget`, which the shared healthcheck uses. `USER app` exists in official images since .NET 8. Build context is always the repo root.

`.dockerignore` at the root:

```
**/bin
**/obj
**/node_modules
ui/dist
spike
.git
docs
phases
tests
```

## 6. Scripts

`scripts/up.sh`:

```bash
#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
docker compose up --build -d
scripts/wait-healthy.sh postgres mongo azurite device-directory patch vulnerability software-install fusion-gateway angular-ui
echo "UI:      http://localhost:${UI_PORT:-4200}"
echo "Gateway: http://localhost:${GATEWAY_PORT:-5000}/graphql   (Nitro UI: /nitro)"
```

`scripts/wait-healthy.sh` (bash 3.2 compatible):

```bash
#!/usr/bin/env bash
# usage: wait-healthy.sh <service>...   waits up to WAIT_TIMEOUT (default 420) seconds
set -euo pipefail
cd "$(dirname "$0")/.."
timeout="${WAIT_TIMEOUT:-420}"
start=$(date +%s)
for svc in "$@"; do
  while true; do
    cid=$(docker compose ps -q "$svc" 2>/dev/null || true)
    status="missing"
    if [ -n "$cid" ]; then status=$(docker inspect -f '{{if .State.Health}}{{.State.Health.Status}}{{else}}{{.State.Status}}{{end}}' "$cid"); fi
    case "$status" in
      healthy|running) echo "ok      $svc"; break ;;
      exited) echo "exited  $svc"; docker compose logs --tail 50 "$svc"; exit 1 ;;
    esac
    if [ $(( $(date +%s) - start )) -ge "$timeout" ]; then echo "timeout waiting for $svc ($status)"; docker compose logs --tail 50 "$svc"; exit 1; fi
    sleep 3
  done
done
```

`scripts/demo-outage.sh`:

```bash
#!/usr/bin/env bash
# usage: demo-outage.sh <patch|vulnerability|software-install|device-directory> <stop|pause|restore>
set -euo pipefail
cd "$(dirname "$0")/.."
svc="$1"; action="$2"
case "$action" in
  stop)    docker compose stop "$svc" ;;
  pause)   docker compose pause "$svc" ;;
  restore) docker compose unpause "$svc" 2>/dev/null || true; docker compose start "$svc"; scripts/wait-healthy.sh "$svc" ;;
  *) echo "unknown action $action"; exit 2 ;;
esac
```

`scripts/reset.sh`: `docker compose down -v` with a confirmation prompt (destroys seeded data).

## 7. Validation while the subgraphs do not exist yet

The compose file references Dockerfiles other lanes have not written. To validate this phase alone:

1. `docker compose config` — must render without error (all env vars resolve from `.env`).
2. `docker compose up -d postgres mongo azurite && scripts/wait-healthy.sh postgres mongo azurite` — all three healthy.
3. `docker compose exec postgres psql -U devdir_user -d sor -c "select current_schema()"` → `device_directory`; `... -c "select * from vulnerability.anything"` → permission denied (schema `USAGE` missing).
4. `docker compose exec azurite node -e "..."` healthcheck command by hand → exit 0.
5. Build the skeleton projects from Phase 1 with the template Dockerfile for at least one service (`docker compose build device-directory`) to prove the template compiles. The skeleton's `/health` returns 200 so the healthcheck passes.

## 8. Troubleshooting notes (put into README)

| Symptom | Cause | Fix |
|---|---|---|
| service stays `starting` then `unhealthy` after ~2.5 min | seeding exceeded `start_period` | raise `start_period`; check `docker compose logs <svc>` for seed progress |
| `wget: can't connect` in healthcheck | app listening on a different port | `ASPNETCORE_URLS=http://+:8080` must be set; no `launchSettings.json` port override in Release |
| `IDX10720` at startup | signing key shorter than 32 bytes | fix `.env` |
| `password authentication failed for user devdir_user` | `pgdata` volume created before init script existed | `docker compose down -v` once |
| Azurite `400 InvalidHeaderValue` | client SDK newer than emulator API | `--skipApiVersionCheck` is set; update the image |
| gateway 401 on everything | header forwarding not configured or key mismatch | compare `DEV_JWT_SIGNING_KEY` across services in `docker compose config` |

## 9. Definition of Done

- [ ] `docker compose config` renders; every `${VAR}` resolves from `.env`.
- [ ] Infra trio comes up healthy; role isolation verified (§7 step 3).
- [ ] At least one skeleton service builds with the template Dockerfile and reports healthy.
- [ ] `scripts/up.sh`, `wait-healthy.sh`, `demo-outage.sh`, `reset.sh` are executable, bash 3.2 clean (`bash -n` and no `declare -A`).
- [ ] `.dockerignore` present; a `docker compose build` of the skeleton does not send `ui/node_modules` or `spike` in the context.
- [ ] README has the run instructions and the troubleshooting table.
- [ ] Deviations recorded in `docs/version-facts.md §8`.
