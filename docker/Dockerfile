# CrowDB.Server container image (docs/005 §7).
#
#   docker/build.sh [image:tag]      # builds with the repo root as context
#
# Two stages: the SDK builds and publishes, the runtime image carries only the
# published app — no SDK, no sources, non-root.
#
# Configuration at run time:
#   * CrowDB__ApiKey        required outside Development (docs/005 §3) —
#                           the server refuses to start without it
#   * CrowDB__DataDirectory  where crowdb.log lives; /data by default
#   * PORT                   the port inside the container (default 8080);
#                           publish it with -p 5217:8080 etc.

# ---- build -----------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first, from the project files alone, so edits to sources don't
# invalidate the NuGet layer. global.json pins the SDK (rollForward allows the
# image's newer 10.0.x).
COPY global.json ./
COPY src/CrowDB.Engine/CrowDB.Engine.csproj src/CrowDB.Engine/
COPY src/CrowDB.Server/CrowDB.Server.csproj src/CrowDB.Server/
RUN dotnet restore src/CrowDB.Server/CrowDB.Server.csproj

COPY src/ src/
RUN dotnet publish src/CrowDB.Server/CrowDB.Server.csproj \
    -c Release \
    --no-restore \
    -o /out \
    /p:UseAppHost=false

# ---- runtime ---------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /out ./

# The entrypoint picks the listening address — `--urls http://+:${PORT:-8080}`
# on the command line, which no appsettings.json value can outrank (an env var
# can; see the script). It also forwards SIGTERM and propagates the exit code
# of a refused start.
COPY docker/entrypoint.sh ./
RUN chmod +x entrypoint.sh

# The data directory is an absolute path, so the content root can't shift it.
ENV CrowDB__DataDirectory=/data

# The engine's only writable spot. Back it with a Railway Volume (or a Docker
# -v mount when running locally). The image ships an "app" user; give the
# directory to it and run as it — a database process has no business being root.
# Railway mounts volumes root-owned; the service sets RAILWAY_RUN_UID=0, or the
# store cannot create crowdb.log inside /data (docs/005 §6).
RUN mkdir -p /data && chown app:app /data
USER app

EXPOSE 8080
ENTRYPOINT ["./entrypoint.sh"]
