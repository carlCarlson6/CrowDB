import type { NextConfig } from "next";

// The portal reaches CrowDB.Server through app/api/[...path]/route.ts, which
// reads CROWDB_API_URL per request (docs/007). It is deliberately not a
// rewrite here: next.config rewrites are compiled into the build's routes —
// swapping a deployment's API URL would need a rebuild, not a restart.

const nextConfig: NextConfig = {
  cacheComponents: true,
  partialPrefetching: true,
};

export default nextConfig;
