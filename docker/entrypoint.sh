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
set -u

dotnet CrowDB.Server.dll "$@" &
pid=$!

trap 'kill -TERM "$pid" 2>/dev/null || true' TERM INT

code=0
wait "$pid" || code=$?
exit "$code"
