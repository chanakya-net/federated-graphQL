# syntax=docker/dockerfile:1
# Template: phases/phase-2c-infra-and-compose.md §5 with <Name> = DeviceDirectory. Build context: repo root.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props global.json ./
COPY src/Shared.Seeding/Shared.Seeding.csproj src/Shared.Seeding/
COPY src/Shared.Auth/Shared.Auth.csproj src/Shared.Auth/
COPY src/DeviceDirectory/DeviceDirectory.csproj src/DeviceDirectory/
RUN dotnet restore src/DeviceDirectory/DeviceDirectory.csproj
COPY src/Shared.Seeding src/Shared.Seeding
COPY src/Shared.Auth src/Shared.Auth
COPY src/DeviceDirectory src/DeviceDirectory
RUN dotnet publish src/DeviceDirectory/DeviceDirectory.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080 DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=true
EXPOSE 8080
USER app
ENTRYPOINT ["dotnet", "DeviceDirectory.dll"]
