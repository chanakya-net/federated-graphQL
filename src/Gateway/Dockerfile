# syntax=docker/dockerfile:1
# Rendered from infra/docker/Dockerfile.template. Build context is always the repo root:
#   docker build -f src/Gateway/Dockerfile .   (or `docker compose build <service>`)
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props global.json ./
COPY src/Shared.Seeding/Shared.Seeding.csproj src/Shared.Seeding/
COPY src/Shared.Auth/Shared.Auth.csproj src/Shared.Auth/
COPY src/Gateway/Gateway.csproj src/Gateway/
RUN dotnet restore src/Gateway/Gateway.csproj
COPY src/Shared.Seeding src/Shared.Seeding
COPY src/Shared.Auth src/Shared.Auth
COPY src/Gateway src/Gateway
RUN dotnet publish src/Gateway/Gateway.csproj -c Release -o /app --no-restore

# -alpine: busybox wget for the compose healthcheck (no .NET image has curl).
FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine
WORKDIR /app
COPY --from=build /app .
# The committed, composed archive (scripts/compose-schema.sh). The build never composes: a stale archive is
# caught by scripts/check-schema-drift.sh, not here.
COPY gateway/gateway.far /app/gateway.far
COPY gateway/timeline-sources.json /app/timeline-sources.json
ENV GATEWAY_ARCHIVE=/app/gateway.far
ENV TIMELINE_SOURCES_CATALOG=/app/timeline-sources.json
ENV ASPNETCORE_URLS=http://+:8080 DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=true
EXPOSE 8080
# Non-root user (UID 1654) shipped with the official .NET 8+ images.
USER app
ENTRYPOINT ["dotnet", "Gateway.dll"]
