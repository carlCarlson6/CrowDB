# 003 — Step 3: compaction → atomic rename

*2026-10-17 · Follows [000 — Storage layout](000-storage-layout-options.md) (§3.5 "the file only grows", §3.12) and closes the tombstone footprint of [002 — Step 2](002-step-2-tombstones.md). Status: **contract written (red) — implementation pending**; the tests in both suites define the behavior below.*

**Concept of this step:** **compaction** — rewriting the log so it contains only the live records, then swapping the new file into place with one atomic rename. Appends created the garbage (dead puts, tombstones); compaction is the cleanup. It is also where the first real concurrency questions arrive: the file handle and the index become things readers depend on, not just things writes own.

## 1. Why compaction exists

The log has no forget function. Every step so far profits from that — and pays for it in space:

- an **overwrite** appends a newer record and abandons the old one (dead bytes);
- a **delete** appends a tombstone whose only job is to cancel older records;
- a **torn tail** is skipped by replay and sits in the file forever.

Space grows with the number of *writes*, not the number of *documents*: a store that overwrites one document a million times is a gigabyte file holding one record. That is the family's signature trade (000 §3.5) — buy crash-simplicity with space — and compaction is how the bill gets paid.

The design insight: **the index is the compaction plan.** Every id in the index is live by definition, and every offset that ever left the index (dead puts, tombstones) is exactly the set to reclaim. No garbage scan, no timestamps, no reference counting: copy what the index points at, drop the rest.

```
log before compaction                                log after compaction
┌─────────┬─────────┬─────────┬─────────┬───────┐   ┌─────────┬─────────┐
│ a = v1  │ b = v1  │ a = v2  │ c = v1  │ † b   │ → │ a = v2  │ c = v1  │
└─────────┴─────────┴─────────┴─────────┴───────┘   └─────────┴─────────┘
  dead      dead       live      live    tombstone     live      live
index: a → a = v2, c → c = v1                        same answers, new offsets
```

## 2. The algorithm

`Store.Compact()`, holding the store lock from first byte to last:

1. Create `<log>.compact` **in the log's directory** (same filesystem — the rename in step 5 will be atomic).
2. For every entry of the index, **in offset order**, read the record's bytes and append them to the new file, with `\n` between records — the exact separator rule `Put` uses (newline *before* each record after the first; no trailing newline).
3. `Flush(true)` the new file — contents durable before they can replace the log.
4. Close the new file; close the log's own handle. (The swap must not run while a `FileShare.None` handle is open, and on POSIX the old handle would keep pointing at the retired file identity anyway.)
5. `File.Move(tmp, log, overwrite: true)` — `rename(2)` on macOS/Linux: the path switches from the old file to the new one in one atomic step.
6. Rebuild the index by replaying the new file (`new IdsIndex(path)`), then reopen the log handle — **a voluntary restart**. Same code path as recovery; no special case. (Order matters once more: replay reads the file by path, so it must run while no handle is open — close, rename, replay, open. `FileShare.None` is enforced, so the wrong order throws instead of racing.)

What is dropped: dead puts, tombstones, torn tails — precisely the things replay had already forgotten. That is not a coincidence to wave through: the compacted file must replay to *exactly the index that was copied*, because last-write-wins replayed over one record per id is the identity. "**replay(compacted) = index**" is the invariant behind every test in this step.

Two small decisions with outsized effects:

- **Offset order, not dictionary order.** Copying in old-log order makes the output deterministic — a log with nothing to reclaim compacts to byte-identical bytes, so compaction is idempotent at the byte level. A free backup-friendly property and free debugging sanity.
- **Verbatim bytes.** The record segment is read and written, never decoded and re-encoded: documents stay byte-exact (the step-1 promise) and no serializer drift can creep in. (The engine still holds no documents in RAM — only offsets; compaction is a file-to-file rewrite.)

## 3. Crash safety

The dance has several windows for a crash; each one is boring, because the old file is untouched until the single atomic step:

| Crash… | On disk afterwards | Recovery verdict |
|---|---|---|
| during 2 (half-written temp) | partial `<log>.compact` beside an untouched log | leftover temp is inert; next compaction overwrites it |
| after 3, before 5 | complete temp; log still old | the log is still the truth; temp is reclaimed next attempt |
| during 5 (inside the rename) | rename is atomic: the path names either the old or the new complete file | replay whichever won — both are valid logs |
| after 5, before 6 finishes | compacted file on disk; RAM state dies with the process | startup replay builds the index from the compacted file |
| after 6 | done | — |

Honest caveat, documented rather than hidden: `Flush(true)` makes the temp's **contents** durable, and the rename is **atomic**, but the rename's own durability (the directory entry) is up to the OS — .NET exposes no portable directory fsync. The shipped guarantee: *no one ever sees a half-compacted log; after a power cut you may get either the old or the new one.* Tightening this belongs to a later durability step.

## 4. Design options

### 4.1 When to compact — the trigger

- **Option A — Manual: `Store.Compact()` + `POST /compact` (recommended).** Policy-free: the caller decides. Observable: the file size changes only when someone asked. Testable without timers or thresholds.
- Option B — Automatic at open when dead bytes exceed a threshold. No new endpoint, but it needs dead-byte accounting (the engine tracks none today), a threshold nobody can argue about yet, and it silently extends startup latency.
- Option C — Background / scheduled (Bitcask-style merges). Needs threads, cancellation, and — because compaction swaps the file — the reader-safety machinery of §4.5 in its hard form. Not this step.

**Recommendation: A.** The mechanism *is* the lesson; threshold policies are a layer to add when there is a workload to tune against (and after step 6 makes concurrent reads real).

### 4.2 What goes into the new file

- **Option A — Only the index's records, verbatim (recommended).** Smallest file; identity preserved; the invariant in §2 is trivially true.
- Option B — Live records plus tombstones. Keeps "X was deleted" visible on disk at the cost of bytes nothing reads again. (Replication — far off the roadmap — would decide this the other way: a very old follower might need the tombstone. Nothing here needs it.)
- Option C — Re-encode through `Record`. Can only change bytes, never information; serializer drift for nothing.

### 4.3 Rebuilding the index after the swap

- **Option A — Replay the new file (`new IdsIndex(path)`) (recommended).** One code path with recovery: "compaction then reopen" is literally a restart, and any copy bug surfaces immediately as a missing id in the very next operation.
- Option B — Carry the new offsets out of the copy loop. Saves one scan; introduces a second place that computes offsets, which can disagree with replay — and will be the first place to drift when the format changes (step 4).

### 4.4 The temp file

`<log>.compact`, beside the log, always created fresh (`FileMode.Create`). A leftover file means "a compaction died here": inert at open, overwritten by the next attempt, never read. Tests pin the name.

### 4.5 Concurrency — the first real questions

The state of the world today (the step-1 open item, flagged in `Store.cs`): **writes take a lock; reads take nothing.** Two consequences:

1. *Already true, latent:* `TryGet` does `Seek` + `Read` on one shared `FileStream` — the file position is shared mutable state. Two concurrent GETs can interleave seeks and reads and hand each other's documents back. Untested, therefore merely unfired.
2. *New with compaction:* a reader's two dependencies — the index (offsets) and the file handle — now change **as a pair**. Old offsets against the new file, or new offsets against the old handle, read wrong bytes; and after the swap the old handle points at a file identity that no longer has a name.

- **Option A — One lock for every operation (recommended for this step).** Reads take the lock too; compaction holds it end to end, so no observer sees an intermediate generation. This also closes the step-1 open item, because the shared-seek race and the swap race have the same fix. Cost: concurrent reads serialize — at pet scale, irrelevant.
- Option B — Lock-free reads via snapshots (capture handle + offset together, read outside the lock). Requires positional I/O (`RandomAccess.Read`, no shared position) or per-read handles — that *is* step 6's tool, pulled forward with its lesson spent.
- Option C — Retire handles gracefully (epochs / refcounts until readers drain). What production systems do; machinery a one-file POC cannot justify.

**Recommendation: A, knowingly.** Correctness first; concurrent reads return in step 6 when immutability (records never change) pays its dividend: positional reads against an immutable file need no lock at all. The locked version is the baseline step 6 is measured against.

### 4.6 Surface

Engine:

```csharp
public void Compact();   // rewrite the log as only its live records
```

- Legal on an empty store and on a clean log (byte-level no-op, §2).
- Logs one line: before / after / reclaimed bytes and record count.

HTTP: `POST /compact` → `204 No Content`. A maintenance verb with nothing to return; `ls -l data/` and the engine log line tell the story. (A `200` + JSON stats shape was considered and rejected — cosmetic numbers on the wire.)

## 5. What it changes (implementation checklist)

- **`Store`**: one lock for *all* public operations — reads included (§4.5); `_logFileStream` and `_idsIndex` become swappable; `Compact()` implements §2 with §3's windows covered. Watch the `FileShare.None` ordering of §2 step 6.
- **`Constants`**: the `.compact` temp suffix.
- **`Endpoints`**: `POST /compact`, mapped like the others (a skeleton already exists and throws until implemented).
- **`Record`**: unchanged — verbatim copy uses `ReadRecordSegment`.
- **Portal** (optional, outside the contract): a "Compact log" button once the endpoint is real.

## 6. The contract (written first — red until implemented)

| File | Added cases |
|---|---|
| `CompactionTests` (engine, new) | overwrite → dead bytes reclaimed, newest survives, copy order follows the log; delete → dead put and tombstone physically gone, id stays deleted; delete → re-put → only the newest record remains; clean log → bytes unchanged, repeat compaction stable; empty store → stays empty, still writable; write/delete after compaction land correctly; leftover `.compact` ignored and overwritten; torn tail reclaimed; concurrent writers + readers + compactor → exact final state, nothing threw |
| `DiskFormatTests` | after compaction the log holds exactly the live envelope lines, in log order |
| `CompactionApiTests` (API, new) | `POST /compact` → 204, log shrinks, server still serves the same answers; empty server → 204 and still writable; a log compacted by an earlier engine run boots a fresh server and serves the live documents |

## 7. Decisions you will meet

- **Tombstones die so the records they killed can die too.** Dropping either alone is wrong; dropping both in one pass is the whole trick, and §2's invariant is why it is safe.
- **Offsets are invalidated wholesale.** Compaction is the first operation that moves every record. Rebuild, never patch — which is why rebuilding by replay is attractive: rebuild *is* recovery.
- **The handle is not the file.** After the rename, an open handle refers to a retired file identity; writing through it writes into nothing. Close → rename → replay → reopen is both the correct and the portable order (Windows refuses renaming over an open handle).
- **"Atomic rename" has fine print.** Atomic replacement: yes. Durable directory entry: the OS's call, documented gap. Contents durable before the swap: `Flush(true)`.
- **You cannot see compaction through the API — only through `ls -l`.** Every step-1/2 behavior must answer exactly the same; a changed answer means a broken copy.
- **One lock until step 6** — reads included.

## 8. Done when

- `dotnet build` / `dotnet test` green: both suites, including the new cases (red today — `Compact()` throws).
- `curl -X POST http://localhost:5217/compact` → `204`; after heavy overwrites/deletes, `ls -l data/crowdb.log` shrinks; the compacted data survives a server restart.
- `README.md` / `AGENTS.md` updated; this doc flips to *done* with an as-built note if reality diverges.

**Out of scope:** automatic/threshold/background compaction, segmented logs, directory fsync, tombstone retention for replication, binary format + CRC32 (step 4), lock-free reads / positional I/O (step 6), group commit (step 7), the portal button (optional above).
