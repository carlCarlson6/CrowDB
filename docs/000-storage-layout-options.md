# 000 — Storage layout: options report

*Revision 3 · 2026-10-15 · Status: decided — Option A (JSONL log + RAM index). Architecture, ids, API, and storage layout all locked (see the decision note below and §7).*

> **Decision (2026-10-15).** Option A — append-only JSONL log + in-memory index — is chosen, with both concessions from §6: a version field in every envelope from record one, and all encoding hidden behind a single `RecordCodec` class. Option C (binary frames + CRC32) is deferred to its earned roadmap slot (step 4) as a migration from the v1 text format. Implementation plan: [001 — Step 1 plan](001-step-1-plan.md).

*Both remaining options belong to the same family — log-structured storage — and differ only in how each record is encoded on disk. (A third candidate, one small file per document, was dropped: it delegates placement, retrieval, mutation, and crash safety wholesale to the filesystem, leaving no engine internals to build.)*

## 1. The four questions every storage engine answers

Whatever layout is built, it is a concrete answer to these four questions:

1. **Placement** — where do a record's bytes live on disk, and what gets written when a record is updated? (Append at the end? Overwrite in place? New file?)
2. **Retrieval** — how do you find the record for id `X` without scanning the whole file? That mechanism is the **index**.
3. **Mutation** — what do update and delete mean? Most real engines never modify bytes in place, and understanding *why* shapes everything.
4. **Crash safety** — if the process dies mid-write, what is on disk afterwards, and how does reopening the file produce a consistent view?

These four answers *are* the engine. The HTTP server and portal only consume them; transactions, concurrency, and replication (later) are consequences of them.

Both options below answer questions 1–3 **identically**:

- one append-only data file, written end-to-end and never modified in the middle;
- a RAM dictionary mapping `id → byte offset` of that id's newest record;
- updates = append a newer record, repoint the index (last-write-wins);
- deletes = append a tombstone (step 2 work, previewed below).

They differ in **record encoding** — and therefore in how corruption is detected, how the format evolves, and which lessons you bank now vs later. This is not a choice between two architectures; it is a choice between two encodings of the same architecture.

## 2. The family: log-structured storage

The core idea: **never modify old bytes; only append new records.** Everything else follows.

- Writes are sequential. Sequential I/O is the friendliest pattern for disks, SSDs, and the OS page cache; random in-place writes are the expensive pattern.
- Crash reasoning collapses to one question: "how much of the tail made it?" Nothing before the tail is ever being mutated, so the middle of the file cannot be in a half-updated state.
- Records become **immutable**, which later gives us concurrent readers and consistent snapshots almost for free.

Real systems in this family: **Bitcask** (the KV engine used by Riak: log + RAM hash index — Option A is "Bitcask-lite"), **Kafka** (a log is the product), and every **LSM tree** (RocksDB, LevelDB, Cassandra) starts with an append-only log and layers sorted files on top. Meanwhile the other mainstream family — page-based **B+trees** (SQLite, InnoDB, PostgreSQL) — mutates pages in place and is only crash-safe because a **write-ahead log (WAL)** exists in front of it. Every serious engine has a log. We are building the log as the database, which is the smallest honest engine there is.

## 3. Option A — Append-only JSONL log + in-memory index ("Bitcask-lite")

### 3.1 What it is

One file per database directory, e.g. `data/crowdb.log`. One record per line:

```
crowdb.log — byte offsets shown on the left
┌──────┬─────────────────────────────────────────────────────┐
│   0  │ {"v":1,"id":"user:1","doc":{"name":"Ada"}}          │ ← dead (superseded)
│  43  │ {"v":1,"id":"user:2","doc":{"name":"Grace"}}        │
│  88  │ {"v":1,"id":"user:1","doc":{"name":"Ada Lovelace"}} │ ← index: "user:1" -> 88
└──────┴─────────────────────────────────────────────────────┘
File size: 140 bytes. Live: 97. Dead: 43 (~31%). (Offsets exact for this ASCII example.)

RAM index:  { "user:1": 88, "user:2": 43 }
```

### 3.2 The record envelope

Each line is an **envelope**, not just your document:

`{"v":1, "id":"user:1", "doc":{...}}`

- `v` — format version. Costs one field now; is the hook that makes a future format migration possible instead of guesswork.
- `id` — the engine's business. The engine owns identity, so it lives in the envelope, not inside your document.
- `doc` — your JSON, stored verbatim. The engine treats it as **opaque bytes**: it never deserializes documents, only envelopes. That is what keeps it a generic document store instead of an app-specific one.

### 3.3 Write path, step by step — and what every crash site does

A `PUT` for id `user:1` with a 2 KB document:

1. Encode the record into a buffer: `{"v":1,"id":<escaped id>,"doc":` + raw document bytes + `}\n`.
2. Take the writer lock (single writer at a time in the POC).
3. Append the buffer at the current end of file.
4. `fsync` — `FileStream.Flush(true)` — the boundary of "durable".
5. Publish: `index["user:1"] = offsetOfThisRecord`.
6. Release the lock.

Now the crash table — the reason append-only earns its keep:

| Crash happens... | On disk afterwards | Recovery verdict |
|---|---|---|
| during step 3 (partial record) | incomplete last line | ignore the broken tail; previous records untouched |
| between 3 and 4 | the record may be fully written, partially written, or absent — the OS decides | if it survives intact → new value; otherwise → old value is still intact. **Both outcomes are correct.** |
| during 4 (fsync interrupted) | same class as above | same reasoning |
| after 4, before 5 | complete new record; RAM index not updated | RAM died with the process; recovery rescans the log, finds the new record → new value |
| during 5 | the index update is in-process memory | irrelevant; RAM is gone anyway; recovery rebuilds |

Why is every row boring? Because **writes never mutate existing bytes**. An in-place updater would have rows like "crash mid-overwrite: the only copy is now half-old, half-new" — which is precisely why in-place engines need write-ahead logs.

Two takeaways to internalize:

- **The log is the source of truth; the index is a derived cache.** If the index is lost or stale, it is rebuilt by replaying the log. This source-of-truth vs. derived-data split is the single most important recovery concept in databases.
- The invariant recovery relies on: **on disk there is always a sequence of complete records, possibly followed by one partial record (the valid prefix).** Recovery = replay the valid prefix, last write wins.

Cost note: fsync is a physical operation — roughly 5–15 ms on a spinning disk, 0.1–0.5 ms on SATA SSD, tens of microseconds on NVMe. Syncing per write caps throughput at hundreds to thousands of writes/sec; that pain is real, and it is exactly why real engines invented batching and group commit (a later step).

### 3.4 Read path

1. Lock, dictionary lookup → offset (miss → HTTP 404).
2. Seek to offset, read the line.
3. Parse the envelope, hand back the raw `doc` bytes.

One lookup plus one short read: O(1), no scan, no rebuild. Repeated reads of hot records hit the OS **page cache** and never touch the disk. Reads need no fsync — durability is a write-path concern only.

`GET /docs` (list ids) is literally the index's keys — free with this design. Ordering is not guaranteed (hash dictionary); ordered listing arrives later with a sorted index.

### 3.5 Updates: last-write-wins, and the garbage it creates

`PUT user:1` twice, as in the §3.1 diagram: the second record is appended, the index repoints from 0 to 88, and the bytes at 0 become **dead** — never read again, never cleaned. In this tiny example 43 of 140 bytes (~31%) are already garbage.

Scale the example: a store with 1M documents of ~1 KB each, each overwritten once, grows a ~2 GB file with ~1 GB of dead bytes. This is not a bug; it is the family's signature tradeoff. The fix — rewriting the log to contain only live records, called **compaction** — is a scheduled future lesson (and a great one: it introduces atomic file swaps and the first real concurrency questions).

### 3.6 Deletes must be logged (a preview you will recognize everywhere)

The obvious delete implementation — remove `user:1` from the RAM index and move on — is wrong, and the reason is instructive: on restart, recovery replays the log, finds the records for `user:1`, and **resurrects the deleted document**. The log does not know what your RAM once forgot.

So deletes must be *in the log*: append a **tombstone** record (`{"v":1,"id":"user:1","deleted":true}`), and make recovery treat "tombstone" as "remove from index". Now the deletion survives restarts and — bonus — deletes have a physical footprint that compaction handles uniformly. This is why tombstones exist in every log-structured system, from Riak to Cassandra.

### 3.7 Startup and recovery

- Clean shutdown and crash run the *same* code path, because the index lives only in RAM. Open the file → scan from offset 0 → for each complete record: parse envelope → `index[id] = offset` (tombstone → remove entry). Later records naturally win, reconstructing last-write-wins for free. This is **replay** — the same trick a WAL uses.
- Tail policy (POC): the first unparseable line is assumed to be a torn write and ignored (log a warning). It can only ever be the last line. Later refinement: truncate it during recovery so it cannot confuse tooling.
- Cost: startup reads the whole file once — O(file size). Trivial at pet scale; at real scale this is why engines persist index snapshots (Bitcask writes "hint files", LSM engines keep manifests/checkpoints) instead of rescanning everything at boot. Good future step.

### 3.8 Durability and performance profile

- fsync per write: correct, slow — deliberately so for the POC. Later steps: group commit (append N records → one fsync), configurable flush policy, and a tiny benchmark harness to feel the difference.
- Open the `FileStream` with minimal user-space buffering; the OS page cache is already a buffer, and double-buffering only blurs what "durable" means.
- One file means one lock, one seek, no segment management. Segments (splitting the log into multiple files) become interesting only when compaction arrives.

### 3.9 Concurrency now, and why it gets easy later

- POC stance: one lock around the read+write path. The simplest thing that is correct; throughput is irrelevant at this scale.
- Later, because records are **immutable**: readers can use positional reads (`RandomAccess.Read` on a `SafeFileHandle`) at known offsets with no lock at all; only the writer's append-plus-repoint needs mutual exclusion. Free lock-free reads are the immutability dividend — a reader can even hold a consistent snapshot while writes proceed.

### 3.10 Pros / cons

**Pros**
- ~150 lines of straightforward C#; every line explainable.
- The log is inspectable with `cat` / `tail` / `wc` — debugging is eyeballing.
- Crash behavior fits in the five-row table above: full conceptual mastery is possible at this size.
- Unlocks the ideal lesson sequence, one concept per step: tombstones → compaction → checksums + binary format → index off-heap → lock-free reads → group commit.
- It is the same building block production systems are made of (Bitcask, Kafka, every LSM's log, every WAL).

**Cons (each with its future fix)**
- **Keys live in RAM**: roughly 50–80 bytes per entry including dictionary overhead. 1M short ids ≈ tens of MB (fine); 100M ≈ several GB (not fine). The wall *is* the lesson; the fix is sorted on-disk indexes (SSTable-style) or B+tree pages — a later chapter.
- **No ordering, no range scans**: a hash index is point-lookup only. Sorted indexes arrive later and retire this limitation.
- **The file only grows**: dead bytes accumulate until compaction exists.
- **Per-write fsync is slow**: intentional for correctness now; group commit later.
- **Text encoding overhead**: every read re-parses the envelope line, and ids must be escaped (or charset-restricted) when embedded. Trivial at pet scale; the binary encoding (Option C) eliminates it.

### 3.11 What you will learn

Log-structured storage; offsets as pointers; index as derived data; crash recovery by replay; last-write-wins; fsync and the durability boundary; why tombstones and compaction exist; immutability as an engineering superpower.

### 3.12 Natural next steps it unlocks (the roadmap)

1. Deletes + tombstones (extends §3.6).
2. Compaction + atomic rename + the first real concurrency questions.
3. Binary record format v2 + CRC32 + the **migration exercise** — this is Option C, earned.
4. Index out of RAM: sorted on-disk table, sparse index; range scans arrive.
5. Lock-free concurrent reads (positional I/O).
6. Flush policy, group commit, benchmarks.
7. Later: collections/namespaces, secondary indexes, queries.

## 4. Option C — Binary framed records + CRC32

### 4.1 Same engine, different codec

Identical log, index, mutation, and recovery structure as Option A. Records stop being JSON lines and become binary frames. Everything in §3.3–§3.9 applies unchanged except where noted; the engine code around the codec is ~90% shared.

### 4.2 The frame, byte by byte

```
┌───────┬─────────┬───────┬────────┬────────┬───────────┬──────────────┐
│ magic │ version │ crc32 │ keyLen │ docLen │ key bytes │ document     │
│ u16   │ u8      │ u32   │ u16    │ u32    │ (utf-8)   │ (raw JSON)   │
└───────┴─────────┴───────┴────────┴────────┴───────────┴──────────────┘
 offset 0        2         3        7        9        13           13+keyLen
Fixed 13-byte header, then payload. All numbers little-endian.
```

| Field | Size | Purpose |
|---|---|---|
| magic | 2 B | "this is a CrowDB frame" — refuse wrong files cleanly instead of parsing garbage |
| version | 1 B | format revision; enables migration, lets readers reject unknown versions |
| crc32 | 4 B | checksum over everything after it (lengths + key + doc): torn writes and bit rot become *detectable* |
| keyLen / docLen | 2 B / 4 B | framing: record boundaries are declared, not discovered |
| key, doc | variable | UTF-8 id; document bytes stay opaque |

Notable consequences: no escaping rules (a document containing raw newlines is just bytes); the next record's start is computable from the header, so locating frame N+1 never requires scanning for delimiters; a length sanity cap (e.g., refuse docLen > 16 MiB) protects against trusting a corrupted header.

> *As contracted for step 4 ([004 — Step 4: binary format v2](004-step-4-binary-format-v2.md)): the v2 frame is 14 bytes, not 13 — an explicit `kind` byte (0 = PUT, 1 = DELETE) sits before the lengths and is covered by the CRC, and tombstones are a kind rather than a document sentinel (the step-2 lesson, anticipated in §3.6).*

### 4.3 Write path

Encode lengths → write header + payload into one buffer → compute CRC32 over bytes 7..end → patch the checksum into the header → single `Write` → fsync → repoint index. The §3.3 crash table applies verbatim. The CRC does not *prevent* torn writes; it makes them *provable* on read.

### 4.4 Read path

Read the 13-byte header at the offset → validate magic, version, length caps → read the payload → verify CRC32 → hand back the doc bytes. Any failed check means corruption (policy in §4.5). Verification requires reading the payload: checksums do not make reads lazier, they make them *trustworthy*.

### 4.5 Recovery: same invariant, stronger evidence

Same valid-prefix scan as Option A. The difference is evidence quality:

- Option A judges a record valid by "does the JSON parse?" — a **heuristic**. A flipped bit inside a JSON string still parses. Option A can therefore silently return wrong data (bit rot) and never know it.
- Option C judges by CRC32 — a **proof** in the probabilistic sense (~2⁻³² false-accept). Bit rot is caught; the record is rejected.

Policy either way for the POC: the first invalid record is assumed to be a torn tail → ignore it (later: truncate). Mid-file corruption implies failing hardware — defining the policy explicitly is enough for now.

### 4.6 The version byte and the migration you would be skipping

Whenever the format changes — add a checksum, add a compression flag, widen lengths — existing files must be **migrated**: read as v-old, rewrite as v-new, swap atomically. With Option A, the first such change you face is exactly text JSONL → binary frames: arguably the single most instructive migration there is, and the `v` field makes it safe (sniff the first byte: `{` means JSONL v1, magic bytes mean binary v2; convert record by record into a new file; fsync; atomic rename; rebuild index). Choosing Option C on day 1 trades that lesson away for earlier robustness. Any future migration still teaches the skill — you just start with a format that is harder to inspect.

### 4.7 Pros / cons

**Pros**
- Provable per-record integrity: torn writes *and* bit rot.
- Framing, encoding, and endianness genuinely learned, not abstracted away.
- No escaping rules; ids and documents are just bytes.
- Versioned from day 1; enables zero-copy/mmap read paths later.
- Forces you to define a corruption policy explicitly — a real database-design activity.

**Cons**
- Roughly 2× the code of Option A.
- Debugging requires hex tooling instead of `cat`.
- Several design decisions must be made upfront (CRC scope, length caps, endianness, corruption policy).
- You design a binary format before having lived with a simple one — slower iteration early, and the text→binary migration ritual is skipped.

### 4.8 What you will learn

Everything in §3.11, plus framing, byte-level layout, endianness, checksums, the torn-write vs bit-rot distinction, and corruption-policy design.

## 5. A vs C, side by side

| | A. JSONL + RAM index | C. Binary frames + CRC32 |
|---|---|---|
| POC effort | ~150 lines | ~300 lines |
| Record boundaries | discovered (newline scan) | declared (length prefix) |
| Human-readable file | yes — cat/tail | no — hex dump |
| Corruption detection | heuristic (JSON parses?) | per-record proof (CRC32) |
| Silent-corruption risk | real (a bit flip inside a string still parses) | essentially eliminated |
| Debug loop | eyeballing | tooling (dump/viewer) |
| Upfront design decisions | few | several (header, CRC scope, caps, policy) |
| Format evolution | text→binary migration becomes a lesson | already binary; future changes are subtler flags |
| Lessons deferred | framing, checksums, encoding | text/binary migration, plain-text iteration |
| End state after roadmap | C, via the earned migration step | C, with the codec already banked |

## 6. Recommendation

**Default to Option A**, with two cheap concessions toward C:

1. Put a version field in every envelope from the first record (`"v":1`).
2. Hide all encoding/decoding behind a single `RecordCodec` class; the engine (log, index, recovery) must never know the encoding.

Rationale: A is the minimum machinery that still teaches the maximum set of concepts, and its gaps are exactly the future lessons, in a sensible order — each con becomes a chapter. Option C is a legitimate choice if binary formats and integrity-first design excite you more than the incremental path; its only true cost is lesson order (and slower early iteration). Either way, switching later means replacing one class and writing one migration — a bounded cost, and itself an educational exercise.

## 7. Already-locked decisions (context for the POC)

- **Architecture:** `CrowDB.Engine` (class library, zero web dependencies) + `CrowDB.Server` (ASP.NET Core Minimal API) + `portal/` (Next.js, npm).
- **Ids:** `PUT /docs/{id}` with client-chosen string id; `POST /docs` auto-generates a Guid and returns it.
- **Endpoints:** `PUT /docs/{id}`, `GET /docs/{id}`, `GET /docs` (list ids).
- **Engine surface sketch** — documents stay opaque to the engine:

```csharp
public sealed class DocumentStore : IDisposable
{
    public DocumentStore(string directory);
    public bool TryGet(string id, out ReadOnlyMemory<byte> document);
    public void Put(string id, ReadOnlyMemory<byte> document); // upsert, last-write-wins
    public IReadOnlyList<string> ListIds();
}
```

- **Tests from day 1:** write → reopen → read (recovery); overwrite visibility; torn-tail tolerance.
- **Repo layout:** `CrowDB.sln`, `src/CrowDB.Engine`, `src/CrowDB.Server`, `tests/CrowDB.Engine.Tests`, `portal/`, `data/` gitignored.

## Appendix — Concepts referenced in this document

*Grouped by theme, one short definition each.*

**Engine fundamentals**

- **Storage engine** — the component that answers placement, retrieval, mutation, and crash safety for bytes on disk. Everything else in a database consumes those answers.
- **Index** — an auxiliary structure mapping logical keys to physical locations. It never holds the data itself; it is derived from the data.
- **Point lookup / range scan** — fetching one record by exact key / fetching all records between two keys in order. Hash indexes do the first; sorted structures do both.
- **Hash index** — a dictionary `key → offset`; O(1) point lookups, unordered, RAM-bound. Our design's index.
- **Sorted index / SSTable** — an on-disk, key-sorted table of entries supporting binary search and range scans even when larger than RAM. The next frontier after our RAM index.
- **Dense vs sparse index** — indexing every key vs. every Nth key (e.g., one per file block). A classic memory/disk trade when indexes outgrow RAM.
- **Derived (rebuildable) data** — state that can be reconstructed from something else. Our RAM index is derived from the log; knowing source-of-truth vs. derived is the core of recovery design.
- **Mapping layer** — the translation between logical items (ids, documents) and physical byte locations (offsets, frames). Database internals *are* this layer.

**Log-structured family**

- **Append-only log** — a file to which records are only ever added; the middle is never modified.
- **Log-structured storage** — organizing a store as an append-only log plus derived structures (index, later sorted files). Sequential-friendly I/O, simple crash reasoning, immutability.
- **Bitcask** — the real-world log + RAM hash index KV engine used by Riak (plus hint files and merge). Option A is "Bitcask-lite" without the maintenance machinery.
- **LSM tree** — Log-Structured Merge tree (RocksDB, LevelDB, Cassandra): append to log/memtable, flush sorted immutable files, merge them in the background. It begins with exactly our log.
- **B+tree / page-based storage** — the other mainstream family (SQLite, InnoDB, PostgreSQL): fixed-size pages (4–8 KiB) mutated in place, kept crash-safe by a WAL. In-place mutation is *why* it needs a log.
- **WAL (write-ahead log)** — an append-and-fsync log of changes recorded *before* the main structure is modified; recovery replays it. In our design the log is not ahead of the database — it *is* the database.
- **Sequential vs random I/O** — appending writes sequentially; in-place updates jump around. Disks, SSDs, and caches all favor sequential: the physical reason logs win.
- **Page cache / buffered I/O** — the OS keeps recent file data in RAM; reads hit cache, writes accumulate until fsync. Explains why "written" is not yet "durable", and why small hot datasets feel fast.

**Records, formats, encoding**

- **Record** — one self-contained unit in the log: envelope plus document bytes.
- **Envelope** — the engine-owned wrapper around the opaque document (version, id). Keeps the engine generic.
- **Byte offset** — a record's 0-based byte position in the file; the pointer our index stores.
- **Framing / length prefix** — declaring record boundaries explicitly (length fields) instead of inferring them (newline scanning).
- **Magic number** — a fixed byte sequence identifying a file or record as ours; lets code fail cleanly on foreign input.
- **Version byte/field** — marks the format revision inside each record or file; the hook that makes migration possible instead of guesswork.
- **Format migration** — reading data in format v1 and rewriting it as v2 (typically new file + atomic rename). A routine database chore; doing one deliberately is a rite of passage.
- **Endianness** — the byte order used for multi-byte numbers; choose one (little-endian here) and stay consistent so files move between machines.
- **CRC32** — a fast 32-bit checksum over record bytes; detects torn writes and bit rot per record. A detector, not security (not cryptographic).
- **Silent corruption / bit rot** — on-disk bytes changing without any error being raised; only checksums catch it. Notably, corrupted JSON still parses — parse success is not integrity.
- **Torn write** — a record only partially written when the process or OS died; under our write model it can only exist at the log's tail.
- **Zero-copy / mmap** — reading without copying or parsing (handing out byte slices, or mapping files into memory); binary framing with length fields makes this possible.

**Durability and crash**

- **fsync / `Flush(true)`** — forces OS buffers to physical media; the line between "written" and "durable". Costs tens of microseconds (NVMe) to tens of milliseconds (spinning disk) per call.
- **Group commit** — appending many records, then a single fsync; amortizes sync cost. What real engines do when per-write fsync is too slow.
- **Crash recovery / replay** — rebuilding state by scanning the log after a crash. O(file) at startup here; hint files and checkpoints are the standard optimization later.
- **Valid-prefix invariant** — because we only append and fsync whole records, the log on disk is always complete records, possibly followed by one partial tail; recovery replays the valid prefix. The heart of log recovery.
- **Atomic rename** — an OS operation replacing a file in one step; used for compaction swaps and migrations. Atomic per file only — two renames are not a transaction, which is precisely why multi-key atomicity requires a log.
- **Hint file / checkpoint** — a persisted dump of the index (Bitcask hint files, LSM manifests) to skip full-log scans at startup; a future step.

**Mutation semantics**

- **Last-write-wins** — the newest record for an id is the truth; our update model.
- **Tombstone** — a log record marking an id as deleted. Needed because merely forgetting an id in RAM would be undone by replay: deletions must *be in the log*.
- **Compaction** — rewriting the log to contain only live records, reclaiming dead bytes (new file + atomic rename). A future step that raises the first real concurrency questions.
- **Immutability** — never modifying existing bytes. Buys trivial crash reasoning, safe concurrent access, and easy snapshots.
- **Consistent snapshot** — an unchanging view of the data while writes proceed; nearly free here because records never change.

**Practical .NET**

- **`FileStream`** — core file handle; `Flush(true)` is fsync; open with minimal user-space buffering (the page cache already buffers).
- **`RandomAccess` / `SafeFileHandle`** — positional reads without a shared file position; the enabler for lock-free concurrent readers later.
- **`System.Text.Json`** — `Utf8JsonWriter` with `WriteRawValue` splices raw document bytes into the envelope without re-encoding; `JsonDocument` parses envelopes on read.
- **`BinaryPrimitives`** — explicit little-endian reads/writes for binary frames.
- **`System.IO.Hashing.Crc32`** — a ready-made CRC32 from a Microsoft NuGet package; avoids hand-rolling tables.
- **GUID vs ULID** — GUIDs are random (arbitrary order); ULIDs are time-ordered, so ids sort by creation time — a small choice that pays off the day a sorted index exists.
