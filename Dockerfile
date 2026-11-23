# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:11.0 AS build
WORKDIR /src

# Restore first so the dependency layer is cached between code changes.
COPY global.json Directory.Build.props Directory.Packages.props VelocityGuard.slnx ./
COPY src/VelocityGuard.Core/VelocityGuard.Core.csproj src/VelocityGuard.Core/
COPY src/VelocityGuard.Redis/VelocityGuard.Redis.csproj src/VelocityGuard.Redis/
COPY src/VelocityGuard.Api/VelocityGuard.Api.csproj src/VelocityGuard.Api/
RUN dotnet restore src/VelocityGuard.Api/VelocityGuard.Api.csproj

COPY src/ src/
RUN dotnet publish src/VelocityGuard.Api/VelocityGuard.Api.csproj -c Release -o /app --no-restore -p:UseAppHost=false

# Chiseled: no shell, no package manager, runs as the non-root "app" user.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled AS runtime
WORKDIR /app
COPY --from=build /app .

# REST (HTTP/1.1) on 8080, gRPC (HTTP/2 cleartext) on 8081; see Kestrel:Endpoints in appsettings.json.
EXPOSE 8080 8081
USER $APP_UID
ENTRYPOINT ["dotnet", "VelocityGuard.Api.dll"]
