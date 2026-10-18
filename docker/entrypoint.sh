#!/bin/sh
# The container's entrypoint (docs/005 §6) — it runs the server as a child
# process, which fixes two PID-1 warts:
#
#   * A refused start (no CrowDB__ApiKey) must exit the container with the
#     real exit code. As PID 1, .NET's fatal-exception path calls abort(), and
#     the kernel ignores default-terminate signals for init — the process
#     would be gone but the container would look "running" forever, and
#     `docker run` would never return. As a child, the exit code propagates.
#   * `docker stop` sends SIGTERM to the entrypoint; forward it so the app
#     shuts down gracefully instead of waiting for the ten-second SIGKILL.
#
# It also picks the listening address, and it must do it from the command line:
# a `Urls` key in appsettings.json outranks the ASPNETCORE_URLS environment
# variable in minimal hosting, which is how the first Railway deploy ended up
# on localhost:5217 — loopback-only, unreachable by the edge proxy, HTTP 502.
# Command-line arguments outrank every configuration source. PaaS platforms
# inject PORT; 8080 is the default for local docker runs and the base image.
set -u

dotnet CrowDB.Server.dll --urls "http://+:${PORT:-8080}" "$@" &
pid=$!

trap 'kill -TERM "$pid" 2>/dev/null || true' TERM INT

code=0
wait "$pid" || code=$?
exit "$code"
