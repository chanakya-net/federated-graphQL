# One Dockerfile for all spike services; the project is chosen with --build-arg PROJECT=<name>.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG PROJECT
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props ./
COPY Shared/ Shared/
COPY ${PROJECT}/${PROJECT}.csproj ${PROJECT}/
RUN dotnet restore ${PROJECT}/${PROJECT}.csproj
COPY ${PROJECT}/ ${PROJECT}/
RUN dotnet publish ${PROJECT}/${PROJECT}.csproj -c Release --no-restore -o /app

# -alpine: busybox wget is available for the healthcheck (the Debian aspnet image has no curl/wget).
FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine
ARG PROJECT
ENV APP_DLL=${PROJECT}.dll
WORKDIR /app
COPY --from=build /app .
EXPOSE 8080
ENTRYPOINT ["sh", "-c", "exec dotnet \"$APP_DLL\""]
