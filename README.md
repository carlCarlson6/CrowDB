# CrowDB

A document database engine built from scratch as a pet project for learning how databases work internally.

CrowDB stores JSON documents and retrieves them by id. It is built with .NET, exposes a small HTTP API, and comes with a Next.js portal for managing data. Every feature is added one step at a time, and each step introduces exactly one new database concept — the point is understanding, not production readiness.

## Intentions

- **Learn database internals by building a real engine**, not by reading about one.
- **One concept per step.** Tombstones, compaction, checksums, indexes... each arrives only when the previous step is fully understood.
- **Minimum complexity.** The smallest design that fully works; no speculative abstractions, no dependencies without a reason.
- **Everything explainable.** If a line of the engine can't be explained, it doesn't ship.

## What it does (first POC)

- Store a JSON document under an id; read it back by id; list ids; delete it (a delete is a tombstone appended to the log).
- NoSQL: documents are opaque JSON bytes to the engine.
- A small portal to create, view, edit, and delete documents.

## Architecture

| Part | What it is |
|---|---|
| `src/CrowDB.Engine` | The storage engine — pure .NET class library, no web dependencies. The heart of the project. |
| `src/CrowDB.Server` | Thin ASP.NET Core Minimal API exposing the engine over HTTP. |
| `portal/` | Next.js app (npm) for managing documents in the browser. |
| `tests/CrowDB.Engine.Tests` | xUnit tests; recovery tests are first-class. |
| `tests/CrowDB.Server.Tests` | API tests — the real server in-process (`WebApplicationFactory`), one throwaway data directory per test. |
| `docs/` | Design docs and decision records. |

Data lives in `data/` (gitignored) as a single `crowdb.log` file.

## Running it

```bash
# The server refuses to start without a key, in every environment (docs/007):
export CrowDB__ApiKey=local-dev-key
dotnet run --project src/CrowDB.Server    # API on http://localhost:5217

cd portal && npm install && npm run dev   # portal on http://localhost:3000
```

The portal asks for that same key in a dialog on the first visit, keeps it in the browser (`localStorage`), and sends it as `X-Api-Key` with every request — the API answers `401` to anything without it (the public API reference excepted, see below).

The server logs what the store does to the console (`info: CrowDB.Engine.Store`). Failing tests show the same engine logs in their output.

The server documents itself: http://localhost:5217/ lands in the API reference (the Scalar UI — clickable requests included; it also lives at `/scalar`), and the raw OpenAPI document is at http://localhost:5217/openapi/v1.json. It is generated from the routes and their XML comments, and it is **public**: with an API key configured it describes the key (paste it into the UI's authentication panel) while every data route stays gated. The portal shows the same document, rendered plainly, at http://localhost:3000/api-docs.

Data survives restarts: on startup the engine replays `data/crowdb.log` and rebuilds the id index in memory.

## Deploying it

Build the container image and run it with an API key:

```bash
docker/build.sh                                        # → crowdb-server:local
export KEY=$(openssl rand -hex 32)
docker run --rm -p 5217:8080 -e CrowDB__ApiKey="$KEY" -v crowdb-data:/data crowdb-server:local
```

The server **refuses to start without `CrowDB:ApiKey` in every environment** (docs/007) — no deployment, and no local run either, silently serves everyone. Every request then carries the key in the `X-Api-Key` header (`curl -H "X-Api-Key: $KEY" http://localhost:5217/docs`); missing or wrong keys get an empty `401`. Exception: the API reference is served without the key — the root `/` (which redirects into it), `/scalar` and `/openapi/v1.json` — it describes the API; the key still gates every `/docs` route. On the reference, paste the key into the Authentication panel so the "try it" requests carry it. Data lives at `/data` inside the container (the `crowdb-data` volume above), so it survives replacing the container. Keys over plain HTTP are only as private as the network — put TLS in front before using this anywhere real.

On Railway: `railway.json` points the API service at `docker/Dockerfile`; set `CrowDB__ApiKey`, mount a volume at `/data`, and set `RAILWAY_RUN_UID=0` — Railway mounts volumes as root while the image runs as `app`. The server binds all interfaces on the injected `PORT` (8080), so the domain's target port can stay unset. The portal is a second service from the same repo: root directory `portal`, builder Railpack (it detects Next.js), and `CROWDB_API_URL=http://${{CrowDB.RAILWAY_PRIVATE_DOMAIN}}:8080` so the portal reaches the API over Railway's private network. The portal's environment holds no key: each visitor pastes `CrowDB__ApiKey` into the console's dialog, and the browser keeps it.

Design records: [005 — API keys & the container image](docs/005-api-key-and-container.md) · [007 — The key everywhere (portal credential & deployment)](docs/007-portal-key-and-deployment.md).

## Roadmap (one concept per step)

| Step | Feature | Concept introduced |
|---|---|---|
| 1 | Store & read documents by id (HTTP + portal) | append-only log, index, crash recovery |
| 2 | Delete documents | tombstones |
| 3 | Shrink the log | compaction, atomic rename |
| 4 | Binary record format | framing, CRC32, format migration ← *current* |
| 5 | Outgrowing RAM | on-disk sorted index, range scans |
| 6 | Concurrent reads | immutability, positional I/O |
| 7 | Write throughput | group commit, benchmarking |
| 8+ | Collections, secondary indexes, queries | ... |

## Status

**Step 1 is complete.** The engine implements Option A — append-only JSONL log + in-memory `id → offset` index, recovery by replay, torn-tail tolerance — behind `Store` (`TryGet`/`Put`/`ListIds`); the API and portal are built on it, and both suites are green (26 engine + 10 API tests). Data survives a server restart.

**Step 2 is complete.** Deletes are tombstones: a log can't remove a record, so a delete is another write that replay applies in order. Engine, `DELETE /docs/{id}`, and both suites are green (38 engine + 15 API tests); deletions survive restarts, and the portal can delete. Design record: [002 — Step 2: tombstones](docs/002-step-2-tombstones.md).

**Step 3 is complete.** `Store.Compact()` rewrites the log as only its live records and swaps it in with one atomic rename; one store-wide lock covers every operation, so writers, readers, and compaction can run at the same time without losing state. `POST /compact` exposes it (the portal has a Compact log button), and both suites are green (48 engine + 18 API tests). Design record: [003 — Step 3: compaction](docs/003-step-3-compaction-options.md).

**Step 4's contract is written (red).** The log becomes v2: binary frames with declared lengths and a CRC32 (magic `CD`, version, kind, 16 MiB document cap), recovery keeps the valid prefix and truncates an invalid tail, and a v1 log is migrated to v2 in place — automatically, with the same atomic-rename dance as compaction. Design record: [004 — Step 4: binary format v2](docs/004-step-4-binary-format-v2.md). Both suites are extended and **fail until the engine implements it** — that is the contract-first workflow, not a broken build.

**Deployment ready (2026-10-18).** An API key (`X-Api-Key`, config `CrowDB:ApiKey`) gates every endpoint whenever it is configured, compared in fixed time; a deployment that forgot the key refuses to start, while Development stays open for `dotnet run` and the portal. `docker/build.sh` builds a non-root server image with the data on a volume and the key supplied at run time (17 new API tests). Design record: [005 — API keys & the container image](docs/005-api-key-and-container.md).

**API reference ready (2026-10-18).** The server generates its OpenAPI document from the mapped routes (`/openapi/v1.json`, built from handler XML comments and route metadata) and renders it through Scalar — reachable from the root (which redirects) or at `/scalar` — browsable and clickable, no CDN. The reference is public even when a key is configured (docs/005 amended), and the document mirrors the deployment: the `X-Api-Key` scheme is declared whenever the server runs (docs/007 removed the keyless shape), so the UI's auth panel carries the key into "try it" requests, and the advertised server URL follows the edge's `X-Forwarded-Proto`/`-Host` — behind Railway's proxy the reference stays same-origin `https`. Declaring the request bodies switched on .NET 10's content-type validation: a non-JSON `Content-Type` on `POST`/`PUT /docs` now answers `415` (12 new API tests). Design record: [006 — API reference](docs/006-api-reference.md).

**The key is everywhere, and the portal holds it (2026-10-18).** No environment starts without `CrowDB:ApiKey` anymore — `dotnet run` included — so the "open server" shape is gone (docs/005 amended). The portal asks for the key in a modal on first visit, keeps it in the visitor's browser (`localStorage`), and sends it as `X-Api-Key`; a same-origin proxy route (`app/api/[...path]/route.ts`) forwards requests to `CROWDB_API_URL`, read per request — not a next.config rewrite, which would bake the destination into the build. Missing/wrong keys get `401` and the dialog returns. Deploy prep: a second Railway service, root directory `portal`, Railpack-detected Next.js, reaching the API over the private network (`CROWDB_API_URL=http://${{CrowDB.RAILWAY_PRIVATE_DOMAIN}}:8080`). Design record: [007 — The key everywhere (portal credential & deployment)](docs/007-portal-key-and-deployment.md).
