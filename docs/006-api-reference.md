# 006 — API reference: the OpenAPI document & the Scalar UI

*2026-10-18 · A server concern, not a roadmap step — no engine concept changes and `CrowDB.Engine` stays web-free. Status: **as built** — contract written first (`OpenApiDocsTests` plus one exemption case in `ApiKeyAuthTests`), then the server change; the server suite is green except the pre-existing red step-4 migration test.*

**Concept of this change:** **route-derived documentation** — the OpenAPI document is generated from the mapped endpoints and their metadata, and each handler's XML comment becomes the operation's summary and response text. Nobody writes the reference by hand, so it cannot drift from the routes; adding a `Map*` + comment adds a documented operation. Second concept: **a public reference for a gated API** — an amendment to `docs/005`.

## 1. Why

The API is tiny but it keeps changing (delete in step 2, `/compact` in step 3, the key in docs/005) and it is now deployed. A browsable reference makes the locked endpoints (docs/000 §7) discoverable, shows the real status codes (204/400/404/415), and lets you fire requests from the page instead of from curl. It is also the honest way to answer "what does the deployed instance actually accept?" — the server describes itself.

## 2. Where the document comes from

- Option A — hand-written Markdown. Nothing to run, but it is a second copy of the routes: it drifts silently.
- Option B — Swashbuckle/NSwag. The classic .NET toolchain; a whole generator + middleware family for something the framework now does itself.
- **Option C — `Microsoft.AspNetCore.OpenApi` (chosen).** Ships with ASP.NET Core (same support window as the framework), generates the document from the endpoints at request time, and the `MapOpenApi` endpoint serves it at the framework default route `/openapi/v1.json`.

**As built: C.** `AddOpenApi()` + `MapOpenApi()`; one document, named `v1`; no build-time generation and no versioning. The text comes from the handlers' XML comments via the package's source generator (`Microsoft.AspNetCore.OpenApi.SourceGenerators`), which reads:

- XML documentation from referenced assemblies that enable it, and
- **only `public` members of the app assembly** (its `ParseCompilation` visits public types/properties/methods) — which is why every endpoint handler is now `public static`. This is the one surprising rule of the whole feature; it is noted next to `MapCrowDbEndpoints` in `WebAppExtensions.cs`.

For the `<response>` text to land, the status codes must be *declared* first: each endpoint carries `.Produces(...)` for the codes it answers with (`200/400/404/415` as appropriate) and `.Accepts<JsonElement>("application/json")` for its body. `JsonElement` documents the body as free-form JSON — exactly what CrowDB accepts, because documents are opaque bytes to the engine. The root `/` is `.ExcludeFromDescription()` too: it redirects into the UI — a front door, not an API operation. Operation tags default to the endpoint class names (`GetDocs`, `WriteDocs`, `Compact`, `DeleteDocs`) — a code-organisation view, deliberately not themed further.

## 3. Public reference, gated API (docs/005, amended)

docs/005 decided: "configured → every request enforced". A browser cannot put `X-Api-Key` on the *page load* of the UI, so the options were:

- **Exempt the reference paths (chosen).** `/` (the front door, which redirects into the UI), `/scalar` and `/openapi` are served without the key; every data route — all of `/docs` and `/compact` — stays gated. A reference describes the API; the key protects the data.
- Docs only in Development (the pattern in Microsoft's own template). Rejected: the deployed instance — the one you want to poke — would have no reference at all.
- Gated docs. Rejected: the HTML page itself would 401, so the UI could never load in a deployment.

**As built:** `ApiKeyAuthentication` lets the reference's surface through without the key: the root `/` (the front door, which redirects into the UI) and paths starting with the `/scalar` or `/openapi` *segment* (`StartsWithSegments`, so `/scalar-extra` is not the reference); the fail-closed startup rule is untouched — outside Development the server still refuses to start without a key.

The document mirrors the deployment it is served by:

- **key configured** → `components.securitySchemes.ApiKeyAuth` (type `apiKey`, in `header`, name `X-Api-Key`) plus a global `security` requirement, so Scalar's authentication panel asks for the key and carries it into "try it" requests;
- **no key (Development)** → no security section at all.

Server-side enforcement stays docs/005's middleware (fixed-time comparison, before routing); the scheme is documentation of that gate, not a second gate.

## 4. The UI

- Option A — `Swashbuckle.AspNetCore.SwaggerUI` at `/swagger`: the classic page; a legacy-feeling extra package family.
- **Option B — `Scalar.AspNetCore` at `/scalar` (chosen).** The UI the official ASP.NET Core docs pair with `Microsoft.AspNetCore.OpenApi`; one package with no dependencies.
- Option C — render the document ourselves in the portal. That is a client project, not a server concern; the portal stays a debug console.

**As built: B.** `MapScalarApiReference()` serves `/scalar` (redirects to `/scalar/`; `/scalar/v1` names the document explicitly). The root `/` redirects to `/scalar/` as well — typing the bare host lands in the reference, the usual pattern for a self-documenting server. The page's JavaScript is **embedded in the package and served by this process** — `/scalar/scalar.js`, `/scalar/scalar.aspnetcore.js`, `/scalar/favicon.svg` — so there is no CDN and the reference works offline and inside the container. The HTML carries the document's route (relative, `openapi/v1.json`) and the client fetches it in the browser.

The document also carries a `servers` entry — the URL the browser sends the "try it" requests to. It is derived from the incoming request (scheme + host), so behind a proxy the forwarded headers must be trusted: `Program.cs` enables the forwarded-headers middleware (`X-Forwarded-Proto`, `X-Forwarded-Host`, known networks cleared) because Railway's edge terminates TLS, **overwrites** both headers, and publishes no stable proxy addresses. Without it, the `https://…/scalar` page would be handed an `http://…` server URL and the browser would block every request as mixed content — which is exactly how the first Railway test of the reference failed. Locally the headers never appear, so nothing changes.

## 5. Declaring the body switches on content-type validation

`.Accepts<JsonElement>("application/json")` is what gives the two write operations a request-body editor in the UI. In .NET 10 the same metadata is also *enforced*: a request whose `Content-Type` is not `application/json` is answered **415 before the handler reads anything**. Before this change, any content type was read raw and only non-JSON *bytes* were answered 400.

- Option A — document the body the standard way and accept the tightening (`.Accepts`). **Chosen.**
- Option B — a custom operation transformer that adds a body to the document but not the metadata. Rejected: hand-built machinery to keep *sloppier* input handling.
- Option C — no body in the document. Rejected: the write endpoints, the interesting ones, would lose their body editors.

**As built: A.** `POST /docs` and `PUT /docs/{id}` accept `application/json` only, and refuse anything else with `415` (empty body): the JSON-declared bodies are unchanged — an empty or non-JSON body with the right content type is still `400` from the handler's own check. The portal and every recipe in the docs already send `application/json`. One existing API test changed shape (`GIVEN_an_empty_body...` became `GIVEN_an_empty_json_body...`, still 400) and a new case pins `text/plain → 415`.

## 6. Packages & versions (golden rule 7)

- `Microsoft.AspNetCore.OpenApi` **10.0.13** — the document generator; same versioning and support window as the framework. 10.0.13 specifically: 10.0.10 pinned the vulnerable `Microsoft.OpenApi 2.0.0` (NuGet audit NU1903); from 10.0.12 the dependency is the patched `[2.12.0, 3.0.0)`.
- `Scalar.AspNetCore` **2.18.0** — the UI. Third-party (MIT, no dependencies, ~4 MB because the JavaScript bundle is embedded — which is the point). A renderer is not something the framework ships; Microsoft's own docs point at Scalar.
- `GenerateDocumentationFile=true` + `NoWarn CS1591` in `CrowDB.Server.csproj` — the XML file is the generator's input, not a documentation requirement on every public member.

## 7. The contract (test map)

`tests/CrowDB.Server.Tests` — 12 new cases through the real server in-process:

| Case | Proves |
|---|---|
| `OpenApiDocsTests`: document → 200 JSON with `openapi` + `paths` | the route exists and parses |
| path/method set equals `/docs` GET·POST, `/docs/{id}` GET·PUT·DELETE, `/compact` POST — and nothing else | the reference cannot drift from the routes; the root, the docs and the UI routes leak into it nowhere |
| every operation has a non-empty `summary` | the XML comments reach the document (the public-handler rule) |
| declared status codes per operation; one `<response>` description | the metadata and comment texts land where they should |
| key configured → `ApiKeyAuth` scheme (apiKey/header/`X-Api-Key`) + global requirement | the auth panel appears in deployments |
| no key → no `security`/`securitySchemes` | the document describes the server it is served by |
| `X-Forwarded-Proto`/`-Host` on the document request → the `servers` URL is the public `https` one | the reference works behind Railway's proxy (no mixed content) |
| `/` → 302 to `/scalar/` | the root is the reference's front door |
| `/scalar` → HTML that references `openapi/v1.json` | the UI page works end to end |
| the page's scripts → 200 JavaScript **from this host** | embedded bundle, no CDN |
| `ApiKeyAuthTests`: `/` (followed), `/scalar` and `/openapi/v1.json` → 200 without the key in Production; `/scalar-extra` and `/docs` → 401 | public front door and reference; gated API; segment-exact matching |
| `DocsApiTests`: `text/plain` write → 415 and nothing stored; empty `application/json` → 400 | the content-type gate and the unchanged JSON check |

## 8. What it changes

- **New:** `docs/006-api-reference.md` (this record), `tests/CrowDB.Server.Tests/OpenApiDocsTests.cs`.
- **Changed:** `CrowDB.Server.csproj` (two packages, `GenerateDocumentationFile`), `Program.cs` (`AddOpenApi`; the security-scheme transformer runs only when a key is configured; forwarded-headers middleware for proxied deployments), `WebAppExtensions.cs` (root redirects into the reference; maps the document and UI routes; keeps the handler rule next to the mapping), `ApiKeyAuthentication.cs` (the public front door + prefixes + the scheme name), the endpoint files (handlers public, XML comments, `Produces`/`Accepts` metadata), `DocsApiTests.cs` (415 case, empty-body case), `ApiKeyAuthTests.cs` (the exemption case), `README.md`, `AGENTS.md`.
- **Unchanged:** the engine (web-free as ever), the on-disk format, the routing itself, the portal, the Docker files.

## 9. Decisions you will meet

- **The reference is public; the data is not.** Anyone who can reach the port can read the API description — the root leads straight into it — and still gets an empty 401 from every data route without the key. If the deployment's very existence must be secret, put auth in front of the whole host.
- **Behind a proxy, the document follows the edge.** The `servers` URL is request-derived, so the server honors `X-Forwarded-Proto`/`-Host` (trusting the immediate peer — Railway publishes no stable proxy IPs and its edge overwrites the headers). An `https://` page handed an `http://` URL gets its browser requests blocked as mixed content; that is the failure this middleware fixes, not a CORS one.
- **415 now exists.** Write operations answer 415 for a non-JSON `Content-Type` and 400 for JSON-declared bodies that are not JSON. Both are per-handler truth, declared in the document.
- **The document's title/version are the defaults** (`CrowDB.Server | v1`, `1.0.0`). Custom info, logos and themes are later decisions.
- **401 responses are not declared per operation.** The gate is deployment infrastructure that runs before routing (docs/005); it stays prose, not OpenAPI.
- **No portal link.** The portal stays a debug console; the reference lives at the server's own `/scalar`.
- **The engine still knows nothing about any of this.** It stores bytes; HTTP metadata and XML comments are server-side concerns only.

## 10. Done when / out of scope

- Done when: the server suite is green for this change (10 new cases, one updated) — the one red step-4 migration test stays red until the engine implements docs/004 — and `README.md` / `AGENTS.md` are updated. The engine suite's state is untouched by this change.

**Out of scope:** build-time document generation and client codegen; multiple documents/API versioning; custom info/branding; Scalar options beyond the defaults; portal rendering or linking the reference; OpenAPI-driven request validation beyond what `.Accepts` already enforces; documenting 401 per operation.
