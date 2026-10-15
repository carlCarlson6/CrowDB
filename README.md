# CrowDB

A document database engine built from scratch as a pet project for learning how databases work internally.

CrowDB stores JSON documents and retrieves them by id. It is built with .NET, exposes a small HTTP API, and comes with a Next.js portal for managing data. Every feature is added one step at a time, and each step introduces exactly one new database concept — the point is understanding, not production readiness.

## Intentions

- **Learn database internals by building a real engine**, not by reading about one.
- **One concept per step.** Tombstones, compaction, checksums, indexes... each arrives only when the previous step is fully understood.
- **Minimum complexity.** The smallest design that fully works; no speculative abstractions, no dependencies without a reason.
- **Everything explainable.** If a line of the engine can't be explained, it doesn't ship.

## What it does (first POC)

- Store a JSON document under an id; read it back by id; list ids.
- NoSQL: documents are opaque JSON bytes to the engine.
- A small portal to create, view, and load documents.

## Architecture

| Part | What it is |
|---|---|
| `src/CrowDB.Engine` | The storage engine — pure .NET class library, no web dependencies. The heart of the project. |
| `src/CrowDB.Server` | Thin ASP.NET Core Minimal API exposing the engine over HTTP. |
| `portal/` | Next.js app (npm) for managing documents in the browser. |
| `tests/CrowDB.Engine.Tests` | xUnit tests; recovery tests are first-class. |
| `docs/` | Design docs and decision records. |

Data lives in `data/` (gitignored) as a single `crowdb.log` file.

## Running it

```bash
dotnet run --project src/CrowDB.Server    # API on http://localhost:5217
cd portal && npm install && npm run dev   # portal on http://localhost:3000
```

The server needs the step-1 engine implemented before it can serve requests — `DocumentStore` is currently a skeleton whose members throw. Build order and test list: [002 — Step 1 plan](docs/002-step-1-plan.md).

## Roadmap (one concept per step)

| Step | Feature | Concept introduced |
|---|---|---|
| 1 | Store & read documents by id (HTTP + portal) | append-only log, index, crash recovery ← *current* |
| 2 | Delete documents | tombstones |
| 3 | Shrink the log | compaction, atomic rename |
| 4 | Binary record format | framing, CRC32, format migration |
| 5 | Outgrowing RAM | on-disk sorted index, range scans |
| 6 | Concurrent reads | immutability, positional I/O |
| 7 | Write throughput | group commit, benchmarking |
| 8+ | Collections, secondary indexes, queries | ... |

## Status

Step 1 planning is complete and the storage decision is **locked — [Option A](docs/001-storage-layout-options.md): append-only JSONL log + in-memory index**. The solution, the API (written against the locked engine surface), and the portal are scaffolded; the engine — the log, the index, recovery by replay — is the next piece to build. Build order: [002 — Step 1 plan](docs/002-step-1-plan.md).
