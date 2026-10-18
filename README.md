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
dotnet run --project src/CrowDB.Server    # API on http://localhost:5217
cd portal && npm install && npm run dev   # portal on http://localhost:3000
```

The server logs what the store does to the console (`info: CrowDB.Engine.Store`). Failing tests show the same engine logs in their output.

Data survives restarts: on startup the engine replays `data/crowdb.log` and rebuilds the id index in memory.

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
