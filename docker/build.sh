#!/usr/bin/env bash
# Builds the CrowDB.Server container image (docs/005 §7).
#
#   docker/build.sh [image:tag]      # default: crowdb-server:local
#
# The build context is the repo root; .dockerignore keeps it small.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
image="${1:-crowdb-server:local}"

docker build -f "$repo_root/docker/Dockerfile" -t "$image" "$repo_root"

cat <<EOF

Built $image.

Run it with a key — outside Development the server refuses to start without
one — and a data volume:

  export KEY=\$(openssl rand -hex 32)
  docker run --rm -p 5217:8080 -e CrowDB__ApiKey="\$KEY" -v crowdb-data:/data $image

Then every request carries the key:

  curl -H "X-Api-Key: \$KEY" http://localhost:5217/docs
EOF
