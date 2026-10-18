import type { NextRequest } from "next/server";

// The portal's API proxy (docs/007): the browser calls same-origin /api/*,
// this handler forwards the request to CrowDB.Server and streams the answer
// back. One design point, one reason: a next.config rewrite is compiled into
// the build's routes — a runtime CROWDB_API_URL there is ignored — while this
// handler reads the variable per request, so one image serves every
// deployment (with the variable unset it reaches the local server on :5217).
//
// X-Api-Key travels through untouched: the console stores the key in the
// visitor's localStorage and sends it on each request; this proxy passes it on
// and never keeps it.

/** The API's base URL: the deployment's CROWDB_API_URL, or the local server. */
function apiBase(): string {
  return (process.env.CROWDB_API_URL ?? "http://localhost:5217").replace(/\/+$/, "");
}

async function forward(request: NextRequest, path: string[]): Promise<Response> {
  // Rebuild the path from its decoded segments: an id may contain anything,
  // and the API wants each as one escaped segment — exactly how the console
  // sent it. Query strings pass through for the day endpoints gain parameters.
  const url = `${apiBase()}/${path.map(encodeURIComponent).join("/")}${request.nextUrl.search}`;

  // Only the headers this API reads travel on: the body's content type and the
  // key being checked. Everything else — cookies, accepts, the portal's own
  // origin — is dropped; this proxies one specific API.
  const headers = new Headers();
  for (const name of ["content-type", "x-api-key"]) {
    const value = request.headers.get(name);
    if (value !== null) headers.set(name, value);
  }

  // GET and DELETE carry nothing; PUT and POST forward the document bytes
  // (still opaque to everyone in between).
  const body = request.method === "GET" || request.method === "DELETE" ? undefined : await request.arrayBuffer();

  let upstream: Response;
  try {
    upstream = await fetch(url, { method: request.method, headers, body, cache: "no-store" });
  } catch (error) {
    // The API is down or CROWDB_API_URL is wrong: answer like a gateway, not
    // like a crashed portal, and leave the detail in the server's log.
    console.error(`api proxy: cannot reach ${url}:`, error);
    return new Response("The portal cannot reach the CrowDB API — check CROWDB_API_URL.", {
      status: 502,
    });
  }

  // The API answers JSON, plain text, or nothing (204), and the console reads
  // the content type; the body streams through, no buffering.
  const responseHeaders = new Headers();
  const contentType = upstream.headers.get("content-type");
  if (contentType !== null) responseHeaders.set("content-type", contentType);

  return new Response(upstream.body, { status: upstream.status, headers: responseHeaders });
}

/** The catch-all segment arrives as the decoded path pieces, in a promise. */
type RouteContext = { params: Promise<{ path: string[] }> };

// One export per method the API has: GET/PUT/DELETE /docs/{id}, GET/POST
// /docs, POST /compact. HEAD and OPTIONS have no API counterpart; Next
// answers OPTIONS itself.

export async function GET(request: NextRequest, context: RouteContext): Promise<Response> {
  return forward(request, (await context.params).path);
}

export async function POST(request: NextRequest, context: RouteContext): Promise<Response> {
  return forward(request, (await context.params).path);
}

export async function PUT(request: NextRequest, context: RouteContext): Promise<Response> {
  return forward(request, (await context.params).path);
}

export async function DELETE(request: NextRequest, context: RouteContext): Promise<Response> {
  return forward(request, (await context.params).path);
}
