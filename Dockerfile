# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
# Restore first so the package layer is cached across source changes.
COPY Directory.Build.props ./
COPY src/ConnectionTracker.Core/ConnectionTracker.Core.csproj src/ConnectionTracker.Core/
COPY src/ConnectionTracker.Web/ConnectionTracker.Web.csproj src/ConnectionTracker.Web/
RUN dotnet restore src/ConnectionTracker.Web/ConnectionTracker.Web.csproj
COPY src/ src/
RUN dotnet publish src/ConnectionTracker.Web/ConnectionTracker.Web.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
# /keys holds the data-protection keys (a named volume inherits this ownership); /imports is the watched export folder.
RUN mkdir -p /keys /imports && chown "$APP_UID" /keys
COPY --from=build /app .
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "ConnectionTracker.Web.dll"]
