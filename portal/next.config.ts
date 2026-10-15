import type { NextConfig } from "next";

const nextConfig: NextConfig = {
  cacheComponents: true,
  partialPrefetching: true,

  // The portal runs on :3000 and talks to CrowDB.Server on :5217 through this
  // same-origin rewrite: no CORS setup, and no API URL in client code.
  // If the server port changes, change this destination too.
  async rewrites() {
    return [
      {
        source: "/api/:path*",
        destination: "http://localhost:5217/:path*",
      },
    ];
  },
};

export default nextConfig;
