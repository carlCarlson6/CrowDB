# 007 — The key everywhere: browser-held credentials & the portal's deployment

*2026-10-18 · A deployment/portal concern, not a roadmap step — no engine concept changes and `CrowDB.Engine` stays web-free. Status: **as built** — API tests first (`ApiKeyAuthTests`/`OpenApiDocsTests` updated), then the server, the portal, and the Railway preparation.*

**Concept of this change:** authentication becomes **unconditional at the HTTP edge** — no environment starts without a key, so "open server" stops existing as a shape — and the console learns to **hold the credential in the visitor's browser** (a modal writes it to `localStorage`; every request carries `X-Api-Key`) while the portal's server stays a **pass-through proxy** that knows the API's URL but never the key.

## 1. Why

docs/005 left one anonymous shape: Development without a key served everyone — which is exactly what the local portal relied on. Two things dissolve that arrangement:

1. The portal is going to be deployed — a public debug console must not be a public database.
2. One rule beats two: while *any* environment can run keyless, the "forgot the key" failure mode survives, just dressed as Development.

So the server requires the key everywhere, and the portal asks each visitor for it.

## 2. No environment runs without a key (amends docs/005 §3)

- Option A — keep Development open (the docs/005 shape). Zero local friction; two behaviors to reason about; the portal needs no credential.
- Option B — key optional everywhere, enforced when set. Silently-open deployments remain possible.
- **Option C — refuse to start without one, in every environment (chosen).** One behavior for every host; a missing key is loud (a startup error naming `CrowDB:ApiKey`) instead of a quiet open door. The cost: `dotnet run` needs the key exported first — deliberately no default key in the repo. The local key is a value the developer picks (`CROWDB__APIKEY=local-dev-key dotnet run --project src/CrowDB.Server`), not one committed for convenience; a checked-in default is a credential someone eventually deploys.

**As built:** `Program.cs` throws when `CrowDB:ApiKey` is null/blank and `ApiKeyAuthentication.UseApiKey` lost its no-op branch, so the middleware always enforces. The startup log has one variant left ("API key authentication is enabled (X-Api-Key)."), and the OpenAPI document always declares the scheme and the requirement — the keyless document shape is gone for the same reason the keyless server is.

## 3. Where the browser keeps the key

- Option A — no storage: type it into every request. Unusable.
- **Option B — the modal + `localStorage` (chosen).** The first visit asks; saving persists across reloads and tabs of that browser. Readable by any script on the portal's origin (an XSS on the console leaks the key) — acceptable for a single-operator debug console whose entire purpose is sending the key, and the alternative below trades one owner's exposure for everyone's.
- Option C — `sessionStorage`: the same exposure, gone at tab close; earns something only on a shared machine.
- Option D — the portal's server holds the key (its own env var), the browser never sees it. Better secret hygiene, but then anyone who can reach the portal gets the whole database with no credential of their own — the portal URL *becomes* the key — and a future "one key per operator" has nowhere to grow from. Rejected.

**As built:** the storage key is `crowdb:api-key`; the console asks before its first request; on `401` it reopens the dialog saying the server rejected the key; an "API key" button in the topbar changes it; auth failures don't masquerade as "API offline"; when storage is unavailable (private windows) the key lasts for the tab. The key rides same-origin `/api/*` requests only, and there is no cookie or session — every request carries it, like the curl examples.

## 4. The portal's server: a proxy route, not a rewrite (as built)

The same-origin rewrite in `next.config.ts` had to learn a deployment's API URL. A rewrite cannot: next.config rewrites are **compiled into the build's routes** — `CROWDB_API_URL` set at `next start` time was ignored and the server kept proxying to the build-time destination (verified with a marker server — `Failed to proxy http://localhost:5217/docs` — and verified again after the fix). Deployment configuration baked into a build needs a rebuild per environment, which is the thing runtime variables exist to avoid.

**As built:** `portal/app/api/[...path]/route.ts` — a route handler exported for the API's four methods (GET, POST, PUT, DELETE) that, per request: rebuilds the path from its decoded segments (an id may contain any character), forwards only the two headers the API reads (`content-type`, `x-api-key`), forwards the body for PUT/POST, streams status/body/content-type back, and answers `502` — details in the portal's server log — when the API is unreachable. `CROWDB_API_URL` is read per request, defaulting to `http://localhost:5217`; `next.config.ts` is back to its two feature flags.

## 5. Deploying the portal on Railway

- **Service:** a new service in the existing project, same GitHub repo, **root directory `/portal`**, builder **Railpack** (Railway's default; it detects Next.js: `npm ci`, `next build`, `next start`, and the injected `PORT` is honored). No deployment files in the repo — the platform's detected plan is the plan.
- **API URL:** `CROWDB_API_URL=http://${{CrowDB.RAILWAY_PRIVATE_DOMAIN}}:8080` — **private networking**, not the public domain: the portal-to-API hop stays inside the project (Wireguard-encrypted), works regardless of the API's public URL, and the API needs no public round trip. The service name inside `${{...}}` is case-sensitive; the port is the API's target port (8080) — `PORT` is injected at deploy time and is not a referenceable variable (the API's variable list shows none). Use the reference, not a literal domain: the API's private domain is `crowdb-copy.railway.internal` today only because it was created before the service was renamed.
- **The key is not portal configuration.** The modal collects it per visitor; the portal's environment holds only the API URL. Rotating the API's key breaks nothing in the portal — the next request answers `401` and the dialog returns.
- Railway terminates TLS at its edge, and the console and its `/api` calls are same-origin — CORS never enters the picture.
- Optional hardening/ergonomics: `watchPatterns` (`portal/**` for this service, `src/**`/`docker/**` for the API) so one push doesn't rebuild both.

Steps: create the service from the repo → root directory `/portal` → set `CROWDB_API_URL` as above → generate a domain. The first deploy needs the modal/proxy code pushed; until then the service would build the pre-key portal.

## 6. The contract (test map)

`tests/CrowDB.Server.Tests` went from 48 to 50 cases:

| Case | Proves |
|---|---|
| no usable key (`null`, `""`, `"   "`) × Development/Production → refuses to start, error names `CrowDB:ApiKey` | fail closed everywhere; Development is not an exemption (replaces docs/005's "Development, no key → served") |
| configured key in Development, no header → 401 on all six routes | the default test environment is gated like a deployment |
| wrong key (prefix, suffix, empty) → 401 | only the exact key passes |
| right key → POST/GET/PUT/DELETE/compact all work | the deployment shape serves normally |
| unauthenticated PUT → 401, then authorized GET → 404, list empty | the gate runs before the store |
| `/`, `/scalar`, `/openapi` → 200 without a key; `/scalar-extra`, `/docs` → 401 | the reference is public, the data is not |
| every server's document declares the X-Api-Key scheme and requirement | the keyless document shape is gone |

Verified by hand (the repo carries no JS test harness, and none is added for this — golden rule 7): the proxy route against a marker server proves **runtime** `CROWDB_API_URL` (the exact case a rewrite fails); then the whole flow through `next start` — no key → `401`, wrong key → `401`, public `/api/openapi/v1.json` → `200`, and PUT/GET/POST/DELETE/compact → `204/200/201/204/204` with the key, document bytes intact. The modal itself is browser behavior: the page renders the dialog on first load (no key in storage), and everything behind it is proven end-to-end — its click-through is the first manual check in `npm run dev`.

## 7. What it changes

- **New:** `portal/app/api/[...path]/route.ts`, this doc, README sections.
- **Changed:** `src/CrowDB.Server/Program.cs` (key mandatory, transformer unconditional), `src/CrowDB.Server/ApiKeyAuthentication.cs` (no no-op mode), `portal/next.config.ts` (rewrite removed), `portal/app/page.tsx` (key modal, key-aware fetch wrapper), `portal/app/api-docs/page.tsx` (comments), both suites' harness (`CrowDbServer` default key, `WithKey()` helper), `docker/Dockerfile` + `docker/build.sh` comments, `docs/005` (amendment notes), `AGENTS.md`, `portal/README.md`.
- **Unchanged:** `CrowDB.Engine` (still no reference to any of this), the on-disk format, the HTTP routes.

## 8. Decisions you will meet

- **One shared secret, no rotation, scopes, or audit trail** — docs/005 §8 still stands; this change only removes the anonymous neighborhood.
- **`localStorage` is credentials where any script on the origin can read them.** Fine for a debug console behind TLS; not a pattern for a product-facing app.
- **The portal's HTML is public.** Anyone can load the console and the reference; without the key every data request answers `401`. The API's own reference stays keyless, deliberately.
- **No session, no cookie** — the browser attaches the key per request, exactly like every curl example.
- **The proxy is not a security boundary.** It forwards the credential without interpreting it; the gate is the API's middleware.

## 9. Done when

- Both suites green for this change: the server suite is 49/50 — the one red is step-4's migration test (`MigrationApiTests`), unrelated; the engine suite is unchanged by this work (its 35 reds are docs/004's contract, awaiting the engine). ✓
- `npm run build` green; runtime `CROWDB_API_URL` proven with a marker server; full CRUD verified through `next start` with a real key. The modal's click-through is the one manual check left. ✓
- `dotnet run` without a key refuses with a message naming `CrowDB:ApiKey`; with one, serves normally. ✓
- `README.md` / `AGENTS.md` / `portal/README.md` updated; docs/005 amended in place.

**Out of scope:** per-user/multiple keys, rotation, rate limiting, audit logging, sessions/cookies, a JS test framework, custom domains/DNS, any portal auth beyond the shared key, watch-pattern configuration (noted, not applied).
