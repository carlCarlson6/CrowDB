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
   - **engine tests** (`tests/CrowDB.Engine.Tests/`) drive `DocumentStore` directly, including recovery and the on-disk format;
   - **API tests** (`tests/CrowDB.Server.Tests/`) drive the HTTP contract through the real server in-process (`WebApplicationFactory`, one throwaway data directory per test).
   A step is done when both suites are green — starting with recovery: write → close → reopen → read, plus torn-tail tolerance.
6. **Decisions are documented options, not silent choices.** For non-trivial design points, present options with tradeoffs — `docs/001-storage-layout-options.md` is the model.
7. **Standard library first.** New NuGet/npm packages need a concrete justification tied to the current step.

## Locked decisions (step 1)

- **Stack:** .NET 10, ASP.NET Core Minimal API, `System.Text.Json`. Portal: Next.js App Router + npm. Tests: xUnit; API tests use `Microsoft.AspNetCore.Mvc.Testing` (the official in-process test host) — no other test packages.
- **API:** `PUT /docs/{id}` (client-chosen string id), `GET /docs/{id}`, `GET /docs` (list ids), `POST /docs` (server-generated Guid id).
- **Engine surface:** `DocumentStore` with `TryGet(id, out bytes)`, `Put(id, bytes)`, `ListIds()`; documents as `ReadOnlyMemory<byte>`. The constructor takes the data directory and an `ILogger<Store>`.
- **Logging:** the engine depends on `Microsoft.Extensions.Logging.Abstractions` only (interface-only contract, keeps the engine web-free) — its sole package. The server hands the store the host's logger (console in development; levels in `appsettings*.json`); both test suites hand it a logger that writes into the failing test's output, so a red test shows what the store was doing.
- **Disk format:** record envelope `{"v":1,"id":...,"doc":...}` — documents opaque to the engine, version field from record one. **Record encoding (decided 2026-10-15):** Option A from `docs/001` — append-only JSONL log + in-memory `id → offset` index; all encoding lives behind one `RecordCodec` class; Option C lands in step 4 as a v1→v2 migration.
- **Durability:** fsync (`Flush(true)`) per write for now (group commit is step 7).
- **Concurrency:** one lock around the read/write path for now (lock-free reads are step 6).
- **Data:** single `crowdb.log` under `data/` (gitignored).

**Open:** nothing — the storage layout is decided (Option A). Current task: implement the engine (log, index, recovery by replay) per `docs/002-step-1-plan.md`.

## Working protocol

- **Before coding:** restate the goal in one or two sentences and name the concept being introduced.
- **Tests come first, and the agent writes them.** For every step, extend both suites (engine + API) before the implementation work starts. They are red until the step is correct, and they never test internals a later step will replace.
- **API tests stay in-process and isolated.** `WebApplicationFactory`, no ports or child processes, one throwaway data directory per test — tests never touch `data/` or each other.
- **Scope:** keep changes inside a single step; don't refactor unrelated code.
- **After coding:** `dotnet build` passes and the step's tests pass (`dotnet test`); a step is not done while any of its tests fail.
- **Update this file** when a step completes: move the open item, adjust the current step, record new locked decisions.
- **Keep `README.md` current.** Its Status section (and the roadmap's *current* marker) must always reflect reality — update them when a step starts or completes, or when a decision is locked.
- **Portal:** a thin debug console — no auth, localhost only, minimal UI.

## Roadmap (what "next" means)

1. **POC** — append-only log + id index; store/read/list by id; minimal API; minimal portal. *(current — storage decided: Option A)*
2. Deletes → tombstones (why logs can't just forget).
3. Compaction → atomic rename; space reclamation; first concurrency questions.
4. Binary format v2 + CRC32 → framing, integrity, format migration.
5. On-disk sorted index → range scans; dense vs sparse index choices.
6. Lock-free reads → positional I/O, the immutability dividend.
7. Group commit + benchmarks → the durability/throughput tradeoff.
8. Collections, secondary indexes, simple querying.

## Reference

- `docs/001-storage-layout-options.md` — step-1 storage decision: two options with full explanations, roadmap mapping, and a concepts appendix.
- `docs/002-step-1-plan.md` — step-1 build order for the engine (log, index, replay, torn tail) and its test list.
- `README.md` — project intentions and status.
