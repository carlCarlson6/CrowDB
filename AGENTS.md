# AGENTS.md — how to work on CrowDB

CrowDB is a learning project: a from-scratch NoSQL document database in .NET (JSON documents by id) with a Next.js portal. The owner is learning database internals, so **clarity and small steps outrank features**.

## Repo layout

```
CrowDB/
├── CrowDB.sln
├── src/
│   ├── CrowDB.Engine/        # storage engine — no web dependencies, ever
│   └── CrowDB.Server/        # ASP.NET Core Minimal API (thin HTTP layer)
├── tests/
│   ├── CrowDB.Engine.Tests/  # xUnit — each step's contract (agent-written)
│   └── CrowDB.Server.Tests/  # xUnit — HTTP contract, in-process (agent-written)
├── portal/                   # Next.js (App Router, TypeScript, npm)
├── docs/                     # design docs / decision records
└── data/                     # engine data files (gitignored)
```

## Golden rules

1. **One concept per step.** Implement only what the current roadmap step needs. Nothing "for later" — later steps are where those concepts get learned.
2. **Minimum complexity.** No speculative abstraction, no interface with one implementation, no configuration options without a consumer.
3. **Explain as you go.** Name the database concept in play (tombstone, fsync, compaction...) and keep code readable. If a line can't be explained to a learner, simplify it.
4. **The engine stays web-free.** `CrowDB.Engine` never references ASP.NET or HTTP; it stores opaque bytes and never deserializes user documents.
5. **The agent writes the tests; the owner implements the engine.** Each step's contract arrives as tests written before the implementation, black-box, and named `GIVEN_<setup>_WHEN_<action>_THEN_<outcome>` — at two levels:
   - **engine tests** (`tests/CrowDB.Engine.Tests/`) drive `Store` directly, including recovery and the on-disk format;
   - **API tests** (`tests/CrowDB.Server.Tests/`) drive the HTTP contract through the real server in-process (`WebApplicationFactory`, one throwaway data directory per test).
   A step is done when both suites are green — starting with recovery: write → close → reopen → read, plus torn-tail tolerance.
6. **Decisions are documented options, not silent choices.** For non-trivial design points, present options with tradeoffs — `docs/000-storage-layout-options.md` is the model.
7. **Standard library first.** New NuGet/npm packages need a concrete justification tied to the current step.

## Locked decisions (step 1)

- **Stack:** .NET 10, ASP.NET Core Minimal API, `System.Text.Json`. Portal: Next.js App Router + npm. Tests: xUnit; API tests use `Microsoft.AspNetCore.Mvc.Testing` (the official in-process test host) — no other test packages.
- **API:** `PUT /docs/{id}` (client-chosen string id), `GET /docs/{id}`, `GET /docs` (list ids), `POST /docs` (server-generated Guid id).
- **Engine surface (as built):** `Store` with `TryGet(id, out bytes)`, `Put(id, bytes)`, `ListIds()`; documents as `ReadOnlyMemory<byte>`. The constructor takes the **log file path** and an `ILogger<Store>` — the caller owns the directory.
- **Logging:** the engine depends on `Microsoft.Extensions.Logging.Abstractions` only (interface-only contract, keeps the engine web-free) — its sole package. The server hands the store the host's logger (console in development; levels in `appsettings*.json`); both test suites hand it a logger that writes into the failing test's output, so a red test shows what the store was doing.
- **Disk format (as built):** Option A from `docs/000` — append-only JSONL log + in-memory `id → offset` index; all encoding lives in the `Record` class. Envelope `{"Version":"1","Id":...,"Doc":...,"Kind":...}`, one line per record — `Doc` is the document as a JSON string (escaped by `System.Text.Json`, byte-exact on round-trip; the engine never parses document content), and `Kind` is `"PUT"` or `"DELETE"` (a missing `Kind` reads as PUT, so pre-tombstone logs stay valid). Tombstones are `"Kind":"DELETE"` with the sentinel `"Doc":"--DELETED--"` — `docs/002`'s recommended null-`Doc` shape was not taken; the explicit kind was chosen instead (see the as-built note in `docs/002`). Replay **skips unparseable lines and keeps going**, rather than stopping at the first one. Option C lands in step 4 as a v1→v2 migration.
- **Durability:** fsync (`Flush(true)`) per write for now (group commit is step 7).
- **Concurrency (as built, step 3):** one private readonly `System.Threading.Lock`, taken by every operation — reads included — because compaction swaps both the file handle and the index (`docs/003` §4.5); lock-free reads are step 6. The log is held with `FileShare.None`: reopening a log requires disposing its store first.
- **Data:** single `crowdb.log` under `data/` (gitignored).
- **Deployment (as built, docs/005):** API key in the `X-Api-Key` header, read from `CrowDB:ApiKey` (env `CrowDB__ApiKey`), compared fixed-time; **required in every environment** (docs/007 amended docs/005 — unset refuses to start, Development included), every request enforced (except the public API reference, docs/006). Container: `docker/Dockerfile` (multi-stage, non-root `app` user, `CrowDB__DataDirectory=/data`) built by `docker/build.sh`; `docker/entrypoint.sh` runs the server as a child (a refused start exits the container with its real code, `SIGTERM` shuts down gracefully) and binds `--urls http://+:${PORT:-8080}` — command line, the only source a `Urls` key in `appsettings.json` cannot beat (an env var can; `appsettings.json` carries no URL). On Railway: volume at `/data`, `RAILWAY_RUN_UID=0` (Railway mounts volumes root-owned while the image runs as `app`), no target-port override — it routes to the injected `PORT`. The key is never baked into the image.
- **Portal key & deployment (as built, docs/007):** the console asks for the key in a modal on first visit, keeps it in the visitor's browser (`localStorage`, key `crowdb:api-key`) and sends it as `X-Api-Key` per request; `401` reopens the dialog. The portal's server is a pass-through proxy — `app/api/[...path]/route.ts` reads `CROWDB_API_URL` **per request** (default `http://localhost:5217`) and forwards `content-type`/`x-api-key`, the body, status and content-type; a next.config rewrite was rejected because rewrites are baked into the build's routes. On Railway the portal is a second service from the same repo: root directory `portal`, builder Railpack (auto-detected Next.js — `npm ci`/`next build`/`next start`, injected `PORT`), reaching the API over the private network via `CROWDB_API_URL=http://${{CrowDB.RAILWAY_PRIVATE_DOMAIN}}:8080`; the portal's environment holds no key.
- **API reference (as built, docs/006):** the OpenAPI document is generated from the mapped routes by `Microsoft.AspNetCore.OpenApi` (`/openapi/v1.json`) and rendered by `Scalar.AspNetCore` at `/scalar` (embedded JavaScript — no CDN). Handler XML comments are its summaries/descriptions — which is why the handlers are `public` (the source generator reads public members only; `GenerateDocumentationFile` on) — and `Produces`/`Accepts` metadata supplies status codes and request bodies. `/`, `/scalar` and `/openapi` are public even when a key is configured (the root redirects into the UI); `/scalar-extra` is not (segment matching). The document declares the `X-Api-Key` security scheme only when a key is set, and its `servers` URL honors the edge's `X-Forwarded-Proto`/`-Host` (forwarded-headers middleware, known networks cleared — Railway publishes no stable proxy IPs; a no-op locally). Declaring bodies switched on .NET 10 content-type validation: non-JSON `Content-Type` on writes → `415`; a JSON-declared body that is empty or not JSON → `400`. The portal renders the same document plainly in its `/api-docs` section (fetched through the portal's `/api` proxy route; no Scalar embed; the portal README records it).

- **Portal UX (as built, docs/008):** the console keeps a **baseline** — the id and body the last GET/PUT/POST agreed to — and derives "modified" from it: Revert restores the baseline bytes, opening any id or New document defers to a discard dialog while modified, and a dirty editor arms `beforeunload`. One `Modal` component owns Escape (document-level), a Tab trap, and focus return to the opener. Ids filter client-side (case-insensitive; the engine still lists them unordered); row actions copy/delete without opening a document (and deleting a non-open id no longer empties the editor); ⌘/Ctrl+S and ⌘/Ctrl+Enter save. No new dependencies — still no JS test harness.

**Open:** step 4 — binary format v2 + CRC32: framing, integrity, and the v1→v2 migration (Option C in `docs/000`). Contract written (red) — [004 — Step 4: binary format v2](docs/004-step-4-binary-format-v2.md) contains the design options with recommendations and the test map; both suites are extended and fail until the engine implements it.

## Working protocol

- **Before coding:** restate the goal in one or two sentences and name the concept being introduced.
- **Tests come first, and the agent writes them.** For every step, extend both suites (engine + API) before the implementation work starts. They are red until the step is correct, and they never test internals a later step will replace.
- **API tests stay in-process and isolated.** `WebApplicationFactory`, no ports or child processes, one throwaway data directory per test — tests never touch `data/` or each other.
- **Scope:** keep changes inside a single step; don't refactor unrelated code.
- **After coding:** `dotnet build` passes and the step's tests pass (`dotnet test`); a step is not done while any of its tests fail.
- **Update this file** when a step completes: move the open item, adjust the current step, record new locked decisions.
- **Keep `README.md` current.** Its Status section (and the roadmap's *current* marker) must always reflect reality — update them when a step starts or completes, or when a decision is locked.
- **Portal:** a thin debug console — minimal UI, no frameworks (plain `fetch`, hand-rolled CSS); the visitor's key lives in the browser (docs/007) and the console guards unsaved edits (docs/008).

## Roadmap (what "next" means)

1. **POC** — append-only log + id index; store/read/list by id; minimal API; minimal portal. *(done — Option A as built, suites green)*
2. Deletes → tombstones (why logs can't just forget). *(done — Kind-based tombstones, suites green)*
3. Compaction → atomic rename; space reclamation; first concurrency questions. *(done — compaction under one store-wide lock, suites green)*
4. Binary format v2 + CRC32 → framing, integrity, format migration. *(current)*
5. On-disk sorted index → range scans; dense vs sparse index choices.
6. Lock-free reads → positional I/O, the immutability dividend.
7. Group commit + benchmarks → the durability/throughput tradeoff.
8. Collections, secondary indexes, simple querying.

## Reference

- `docs/000-storage-layout-options.md` — step-1 storage decision: two options with full explanations, roadmap mapping, and a concepts appendix.
- `docs/001-step-1-plan.md` — step-1 build order for the engine (log, index, replay, torn tail) and its test list.
- `docs/002-step-2-tombstones.md` — step-2 design record: why deletes are writes, the tombstone shape options, and the written-first contract.
- `docs/003-step-3-compaction-options.md` — step-3 design record: the compaction algorithm, atomic rename, crash windows, the one-lock decision, and the written-first contract.
- `docs/004-step-4-binary-format-v2.md` — step-4 design record: the v2 frame byte by byte, the CRC32 and corruption policy, the sniff/migration rules, and the written-first contract.
- `docs/005-api-key-and-container.md` — deployment record: the HTTP-edge auth gate, the fail-closed startup rule, the header/comparison choices, the test map, and the container image decisions (command-line URLs, the child-process entrypoint).
- `docs/006-api-reference.md` — API-reference record: route-derived OpenAPI + XML comments (public handlers), the public-reference/gated-API amendment to docs/005, the Scalar choice (embedded assets, no CDN), and the 415 content-type decision.
- `docs/007-portal-key-and-deployment.md` — portal-credential record: the key's path to being required everywhere (amending docs/005 §3), the modal/localStorage choices, why a proxy route replaces the rewrite (build-baked destinations), and the portal's Railway service (Railpack, private networking).
- `docs/008-portal-ux.md` — portal UX record: the baseline/dirty model and discard guard, the id filter, keyboard shortcuts, row actions, the shared Modal (Escape/Tab/focus), the narrow-window layout, and what was deliberately left out.
- `README.md` — project intentions and status.
