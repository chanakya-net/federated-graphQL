# syntax=docker/dockerfile:1
# Build context: repo root (docker build -f src/TokenGenerator/Dockerfile .).
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props global.json ./
COPY src/Shared.Auth/Shared.Auth.csproj src/Shared.Auth/
COPY src/TokenGenerator/TokenGenerator.csproj src/TokenGenerator/
RUN dotnet restore src/TokenGenerator/TokenGenerator.csproj
COPY src/Shared.Auth src/Shared.Auth
COPY src/TokenGenerator src/TokenGenerator
RUN dotnet publish src/TokenGenerator/TokenGenerator.csproj -c Release -o /app --no-restore

# aspnet, not runtime: Shared.Auth has a FrameworkReference to Microsoft.AspNetCore.App (JwtBearer), which flows
# into TokenGenerator.runtimeconfig.json; on runtime:10.0-alpine the app fails with "Framework
# 'Microsoft.AspNetCore.App' ... No frameworks were found" (docs/version-facts.md §8). Same base as every other service.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine
WORKDIR /app
COPY --from=build /app .
# Non-root. A fresh named volume mounted at /tokens copies this directory's owner (app), so the app can write into it.
RUN mkdir /tokens && chown app:app /tokens
ENV USERS_FILE=/app/users.json TOKENS_OUTPUT=/tokens/tokens.json
USER app
ENTRYPOINT ["dotnet", "TokenGenerator.dll"]
