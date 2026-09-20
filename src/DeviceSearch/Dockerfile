# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props global.json ./
COPY src/Shared.Auth/Shared.Auth.csproj src/Shared.Auth/
COPY src/DeviceSearch/DeviceSearch.csproj src/DeviceSearch/
RUN dotnet restore src/DeviceSearch/DeviceSearch.csproj
COPY src/Shared.Auth src/Shared.Auth
COPY src/DeviceSearch src/DeviceSearch
RUN dotnet publish src/DeviceSearch/DeviceSearch.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080 DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=true
EXPOSE 8080
USER app
ENTRYPOINT ["dotnet", "DeviceSearch.dll"]
