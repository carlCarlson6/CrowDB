# 002 — Step 1 plan: append-only log + in-memory index (Option A)

*2026-10-15 · Follows the decision in [001 — Storage layout](001-storage-layout-options.md): Option A. Status: plan — the engine is not implemented yet.*

**Concept of this step:** log-structured storage — an append-only log as the source of truth, an in-memory index as derived data, and recovery by replaying the log at startup.

## What is already in place

- `CrowDB.sln` with `src/CrowDB.Engine` (class library, no web dependencies), `src/CrowDB.Server` (Minimal API), `tests/CrowDB.Engine.Tests` (xUnit).
- `CrowDB.Server` implements the four locked endpoints (§7 of doc 001) against the locked `DocumentStore` surface. It validates that request bodies are JSON and then hands raw bytes to the engine — the engine never parses documents.
- `portal/` — a Next.js debug console: list ids, open a document, edit and save (`PUT`), create under a server-generated id (`POST`). It reaches the API through a same-origin rewrite (`/api/*` → `http://localhost:5217/*`).
- `DocumentStore` is a skeleton whose members throw `NotImplementedException`. **That is the work of this step.** The server cannot serve a request until the engine works.
- `tests/CrowDB.Engine.Tests/` and `tests/CrowDB.Server.Tests/` hold the step's contract: engine tests against the locked `DocumentStore` surface, API tests against the real server in-process. They are red today (the store throws) and turn green when the engine is correct — there is nothing to unskip.

## Build order (suggested — each step ends runnable)

1. **`RecordCodec`** — encode one record and decode it back.
   - Encode: `{"v":1,"id":<escaped id>,"doc":` + raw document bytes + `}\n`, built with `Utf8JsonWriter` and `WriteRawValue` (the document is spliced in, never parsed).
   - Decode: parse the envelope (`JsonDocument`), hand back the id and the raw `doc` bytes.
2. **`DocumentStore` without recovery** — open/create the log, then:
   - `Put`: encode → append at end → `Flush(true)` (fsync) → `index[id] = offset`.
   - `TryGet`: index lookup → seek → read the record → decode → return the document bytes.
   - `ListIds`: the index's keys.
3. **Recovery by replay** — in the constructor: scan from offset 0, decode every record in order, set `index[id] = offset` (later records overwrite earlier ones). Clean shutdown and crash now use the same code path.
4. **Torn-tail tolerance** — stop replaying at the first unparseable record, log a warning, and keep the store usable. Recovery relies on the *valid-prefix invariant*: complete records, possibly followed by one partial record — and the next write must not land *behind* that garbage (truncate it at open, or overwrite it), or the restart after next stops at the garbage and silently drops everything after it. `RecoveryTests` enforces this.
5. **End-to-end** — run `dotnet run --project src/CrowDB.Server` and `npm run dev` in `portal/`, then `cat data/crowdb.log` — that is the Option A payoff.

## The contract (already written — `tests/CrowDB.Engine.Tests/` + `tests/CrowDB.Server.Tests/`)

Black-box at both levels; every test is named `GIVEN_..._WHEN_..._THEN_...`. The suites are red until the engine works — make them green in the order above.

| File | Covers |
|---|---|
| `DocumentStoreTests` (engine) | point lookups, byte-for-byte round-trip (tricky content, 1 MB document), awkward ids, last-write-wins, listing |
| `RecoveryTests` (engine) | reopen → read, replay order, torn-tail tolerance — including that a torn tail must not poison writes that follow it |
| `DiskFormatTests` (engine) | the on-disk contract: one `crowdb.log`, one `v:1` envelope per line, append-only (dead records stay), replay of a hand-written log |
| `DocsApiTests` (API) | the HTTP contract: 201/204/400/404, verbatim bytes over the wire, url-encoded ids, and a restart serving a log left behind by a previous run |

## Decisions you will meet (and where each one bites)

- **Record boundaries on read.** Option A has no lengths: a record ends at the first newline after its offset. Decide how you find it (read from the offset to end-of-file and cut at `\n`? read in chunks?) and how ids containing `\n` are handled (JSON escaping must make that impossible — verify).
- **Offset types.** Byte offsets are `long`; make the index, the seek, and the file math agree.
- **Who guarantees the document is valid JSON.** The HTTP layer validates before calling the engine; the engine trusts the bytes. If invalid bytes reached the log anyway, replay would read them as a torn tail and *later* records would be lost — this is the honest cost of Option A's heuristic ("does it parse?").
- **Tail policy.** Ignore the first unparseable line (assume a torn write). Mid-file garbage cannot happen under the write model; define what you do if it does.
- **File sharing.** Open with the smallest options you can justify (`FileShare.Read` lets `cat` the log while the server runs; per doc 001 §3.8, keep user-space buffering out of the way).
- **Concurrency.** One lock around the read/write path for now — the simplest thing that is correct.

## Done when

- `dotnet build` and `dotnet test` pass — the whole step-1 suite green (it is the contract, and it is red until the engine works).
- `curl` and the portal can PUT, GET, POST, and list; data survives a server restart (the index is rebuilt, not persisted).
- `AGENTS.md` and `README.md` status sections are updated.

**Out of scope for this step:** deletes/tombstones (step 2), compaction (3), binary format + CRC32 (4), lock-free reads (6), group commit (7).
