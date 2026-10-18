# 005 — Deployment: API key authentication & the container image

*2026-10-18 · A deployment concern, not a roadmap step — no engine concept changes and `CrowDB.Engine` stays web-free. Status: **as built** — contract written first (`ApiKeyAuthTests`), then the server change; the image is built by `docker/build.sh`.*

**Concept of this change:** **authentication at the HTTP edge** — whether a request may touch the database is decided before routing, so the engine never sees an unauthenticated call — plus **packaging**: the same server shipped as a container image whose configuration (key, data directory, port) arrives at run time.

## 1. Why

The engine keeps evolving; the server should be deployable without being open to whoever can reach the port. That is two separable promises:

1. **A credential on every request.** One API key is the smallest thing that turns "anyone" into "anyone holding the key".
2. **A repeatable artifact.** A container image: multi-stage build (no SDK ships), non-root, data on a volume, secrets supplied at run time.

Everything here lives in `CrowDB.Server` and the repo root; the engine, the endpoints, the portal, and the on-disk format are untouched.

## 2. Where the gate lives

- **Option A — middleware before routing (chosen).** One `app.Use` in front of the whole pipeline: it covers every endpoint, present and future, and needs no change to the `Map*` files. An unauthenticated request never gets far enough to discover whether an id exists (401, not 404).
- Option B — an endpoint filter per route group. Ties the check to routing, so every future endpoint must remember to join the protected group.
- Option C — ASP.NET Core's authentication framework (`AddAuthentication` + custom scheme + `RequireAuthorization`). The standard machinery, but scheme, options, handler and authorization policies are more ceremony than a single shared secret needs (golden rule 2).

**As built: A.** `ApiKeyAuthentication.UseApiKey` is registered in `Program.cs` before `MapCrowDbEndpoints`; a missing or wrong key answers `401` with an empty body, so an unauthenticated response carries nothing — not even which ids exist.

## 3. When the key is required

- Option A — key configured → enforced; unset → open in any environment, with a warning. Simplest; but a production deployment that forgot the key silently serves everyone.
- Option B — always required. Strictest; but local `dotnet run`, the portal, and every existing test stop working until a key is configured.
- **Option C — configured → enforced everywhere; unset → Development open (with a warning), anything else refuses to start (chosen).** Local workflow unchanged, deployment fail-closed, and a configured key is still enforced in Development — which is what makes it testable without a production-shaped host.

**As built: C.** `CrowDB:ApiKey` is read from configuration (environment variable `CrowDB__ApiKey`, user-secrets, or `appsettings`); empty or whitespace counts as unset. Outside Development a missing key throws at startup with a message naming the setting, so a container without a key exits immediately instead of serving. The startup log states the mode: "authentication is enabled" or a warning that the server is serving without credentials.

## 4. The header and the comparison

- **`Authorization: Bearer <key>` vs `X-Api-Key: <key>`.** Bearer is the standard credential slot and comes with the spec's `WWW-Authenticate` challenge, and proxies tend to redact it in logs. `X-Api-Key` is the explicit API-key convention, with no scheme parsing. **Chosen: `X-Api-Key`** — one mechanism, one meaning; a 401 needs no challenge header. Consequence to respect: keep this header out of access logs and traces (unlike `Authorization`, infrastructure won't redact it for you).
- **Fixed-time comparison.** The provided value is compared to the configured key with `CryptographicOperations.FixedTimeEquals` over UTF-8 bytes: the key's *length* may leak (that is standard and unavoidable), but nothing about its *contents* does. A plain string comparison leaks how many leading bytes matched — a timing oracle.

## 5. The contract (test map)

`tests/CrowDB.Server.Tests/ApiKeyAuthTests.cs`, 17 cases through the real server in-process:

| Case | Proves |
|---|---|
| no header on `GET /`, `GET /docs`, `GET /docs/{id}`, `PUT`, `POST /docs`, `DELETE`, `POST /compact` → 401 | every route is behind the gate, including writes that carry valid bodies |
| wrong key (prefix, suffix, empty) → 401 | only the exact key passes |
| right key in Production → POST/GET/PUT/DELETE/compact all work | the deployment shape serves normally |
| unauthenticated PUT → 401, then authenticated GET → 404 and list → empty | the gate runs *before* the endpoint, so nothing reaches the store |
| Development, no key → requests served | the local workflow stays open |
| Production with key `null`/`""`/`"   "` → server refuses to start, error names `CrowDB:ApiKey` | fail closed, with a fixable error |

The harness (`CrowDbServer`) gained two knobs — `apiKey` and `environment` — and now pins the environment so tests never inherit the machine's.

## 6. The image

- Options weighed: single-stage vs **multi-stage (chosen)** — the SDK never ships; **non-root (chosen)** — `USER app` (the base image's built-in user); secrets baked in vs **env at run time (chosen)**; source builds in the image (chosen — self-contained, no local publish step).
- `docker/Dockerfile`: `sdk:10.0` restores from the `.csproj` files first (source edits don't invalidate the NuGet layer), publishes Release with `/p:UseAppHost=false`, and the `aspnet:10.0` stage copies only `/out`.
- `appsettings.json` binds `http://localhost:5217`; inside a container that is unreachable from outside, so the image sets `ASPNETCORE_URLS=http://+:8080` (environment configuration wins over `appsettings.json`) and `EXPOSE 8080`; publish with `-p 5217:8080` or anything else.
- `CrowDB__DataDirectory=/data`, `VOLUME /data`, owned by `app` — the only writable spot, so the database file survives container replacement (`-v crowdb-data:/data` pins it; without `-v` Docker creates an anonymous volume that is easy to lose).
- `.dockerignore` (repo root = build context) keeps `.git`, `data/`, `portal/`, and `bin`/`obj` out of the transfer.
- `docker/build.sh [image:tag]` wraps `docker build` and prints the run recipe. The key is never baked into an image or committed to the repo.

```bash
docker/build.sh                                  # crowdb-server:local
export KEY=$(openssl rand -hex 32)
docker run --rm -p 5217:8080 -e CrowDB__ApiKey="$KEY" -v crowdb-data:/data crowdb-server:local
curl -H "X-Api-Key: $KEY" http://localhost:5217/docs
```

## 7. What it changes

- **New:** `src/CrowDB.Server/ApiKeyAuthentication.cs`, `tests/CrowDB.Server.Tests/ApiKeyAuthTests.cs`, `docker/Dockerfile`, `docker/build.sh`, `.dockerignore`, this doc.
- **Changed:** `src/CrowDB.Server/Program.cs` (read/validate the key, warn or enable the middleware), `CrowDbServer.cs` and its call sites (two new optional parameters), `README.md`, `AGENTS.md`.
- **Unchanged:** the engine, the endpoints, the on-disk format, the portal.

## 8. Decisions you will meet

- **401 is empty-bodied and applies to `/` too.** There is no anonymous surface; the running-notice endpoint is not a health check. If probes need one later, it gets its own decision.
- **The portal does not send the key.** Its workflow *is* Development without a key. Configure a key in Development and the portal gets 401s until it learns the header — deliberately out of scope here.
- **TLS is not this server's job.** An API key over plain HTTP is only as private as the network; terminate TLS at a reverse proxy before anything real, or every key is one packet capture away.
- **One key, one scope.** No rotation, no multiple keys, no rate limiting, no audit trail of which key touched what. All later concerns.
- **The engine still knows nothing about any of this.** Auth is bytes-in-front-of-bytes: it runs before `Store` is ever called, and `CrowDB.Engine` gained no reference.

## 9. Done when

- Both suites green for this change (17 new API tests) — the one red step-4 test stays red until the engine implements docs/004.
- The image builds via `docker/build.sh`, refuses to start without `CrowDB__ApiKey`, and serves authenticated requests with it; data written through the API appears in the volume.
- `README.md` / `AGENTS.md` updated.

**Out of scope:** multiple keys/rotation/scopes, hashed-at-rest keys, rate limiting, audit logging, TLS termination, CORS/preflight handling (the portal proxies same-origin), health-check endpoint, portal sending the key, Kubernetes manifests/compose, image signing, registry publishing.
