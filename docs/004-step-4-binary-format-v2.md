# 004 — Step 4: binary format v2 + CRC32

*2026-10-18 · Follows [000 — Storage layout](000-storage-layout-options.md) §§4 (Option C — "earned" in §3.12) and 4.6, and the truncation refinement sketched in §3.7. Status: **contract written (red) — implementation pending**; the tests in both suites define the behavior below.*

**Concept of this step:** **framing + integrity** — records stop being lines discovered by scanning for `\n` and become **frames with declared lengths and a per-frame CRC32**; and the existing v1 log is **migrated** to v2, the format-migration rite of passage. Where v1 could only guess "does this line parse?", v2 can prove "these bytes are exactly what the writer wrote".

## 1. Why leave v1 behind

None of these are bugs — they are the bill for a format chosen to be learned from:

- **Boundaries are discovered, not declared.** Reading a record means scanning to the next newline; ids must be escaped so they cannot contain one.
- **Documents are JSON strings on disk** (escaped by `System.Text.Json`). Byte-exact round-trips hold for valid JSON; anything else — raw newlines, invalid UTF-8 — is mangled or impossible.
- **Corruption is judged by a heuristic.** "The line parses" is not integrity: a flipped bit inside a JSON string still parses, and v1 happily returns the wrong document.
- Every read re-parses envelope text to find the document bytes.

v2 answers with the tools of every real format: a **magic number**, a **version byte**, **declared lengths**, and a **checksum** — plus documents as **raw bytes**, never parsed, never escaped.

## 2. The v2 frame, byte by byte

```
┌───────┬─────────┬───────┬──────┬────────┬────────┬───────────┬──────────────┐
│ magic │ version │ crc32 │ kind │ keyLen │ docLen │ key bytes │ document     │
│ u16   │ u8      │ u32   │ u8   │ u16    │ u32    │ (utf-8)   │ (raw bytes)  │
└───────┴─────────┴───────┴──────┴────────┴────────┴───────────┴──────────────┘
 offset 0        2         3       7        8        10       14           14+keyLen
 14-byte header, then payload. All numbers little-endian.
```

| Field | Size | Purpose |
|---|---|---|
| magic | 2 B | `"CD"` = `0x43 0x44` — "this is a CrowDB frame"; foreign files are refused, never guessed at |
| version | 1 B | `2` — lets the reader reject anything it was not written for |
| crc32 | 4 B | checksum of everything **after** it (kind + lengths + key + document) |
| kind | 1 B | `0` = PUT, `1` = DELETE — the v1 `Kind` lesson, and the sentinel's retirement |
| keyLen | 2 B | id byte length; ids are UTF-8, ≤ 65535 bytes |
| docLen | 4 B | document byte length; capped at **16 MiB** |
| key, document | — | raw bytes, verbatim — a document is never parsed or escaped again |

Rules the reader enforces **before trusting anything** — it accepts exactly what the writer can produce:

- version must be 2; kind must be 0 or 1; keyLen is bounded by its own width;
- a PUT carries 1 B…16 MiB of document (empty documents were already refused);
- a DELETE carries **no** document (`docLen` 0): deletion is a kind, not a reserved string — `--DELETED--` is retired;
- CRC32 is **CRC-32/ISO-HDLC** (the zlib variant; check value `0xCBF43926` for `"123456789"`, pinned by a test). Any mismatch invalidates the frame.

There is no file header and no separator: frames are concatenated, and each frame's declared length *is* the boundary to the next one.

**Divergences from the docs/000 §4.2 sketch** (which was drawn before step 2 existed): the header is 14 bytes, not 13 — the explicit `kind` byte joins it (step 2's as-built ruling, 002 §As built, anticipated exactly this: "roughly where the binary v2 frame will land anyway (a kind byte beside the version)"); and the CRC scope includes `kind`, so a flipped kind bit cannot silently turn a put into a delete. Everything else — magic/version/crc/lengths order, little-endian, raw document — is as sketched.

## 3. Write path, read path, recovery

### 3.1 Write

Encode lengths + payload into one buffer → compute the CRC → patch it into the header → **one `Write`** → `Flush(true)` → repoint the index. The §3.3 crash table of docs/000 applies verbatim: the CRC does not *prevent* torn writes, it makes them *provable*.

A small simplification falls out: `Delete` no longer reads the old record at all — a delete frame needs only the id.

### 3.2 Read

Read the 14-byte header at the offset → validate magic, version, kind, length caps → read the payload → verify the CRC → hand back the document bytes. Checksums do not make reads lazier; they make them trustworthy.

### 3.3 Recovery: the valid prefix, with proof

The §1 invariant is unchanged — *complete records, possibly followed by one partial record* — but the evidence is now a proof instead of a heuristic. Once the file is accepted as v2 by the sniff (§4.7):

1. Scan frames from byte 0. Each one is validated as in §3.2.
2. The **first invalid frame** ends replay. Nothing from it on is read.
3. The file is **truncated** to the end of the last valid frame, and a warning is logged.
4. The index is rebuilt from the frames that survived; the store opens writable.

Why stop instead of skipping (v1's rule)? v1 could resync because `\n` is a delimiter it can trust blindly. A binary frame has no delimiter: the next frame's start is computed from a header the CRC just declared unreliable, and scanning for the magic would eventually match bytes *inside* a document. A corrupt header is a dead end — so the honest policy is: keep the prefix, drop the rest.

Why truncate? Because append happens at end-of-file: writing behind a known-bad tail would produce records that the next replay still cannot reach (they would sit after the first invalid frame). Truncation keeps the file's invariant true — *on disk: valid prefix, at most one torn frame* — so the next append is always reachable. Byte 0 of the file is valid again.

| On disk when the process died | v2 verdict |
|---|---|
| torn write: a prefix of a frame at the tail (short read) | valid prefix kept; tail truncated |
| bit rot: a complete frame whose bytes changed | CRC mismatch; same verdict — everything from it on is dropped and truncated (a loud log; failing hardware is a backup conversation) |
| crash mid-fsync | the frame is either complete or not — both outcomes are correct |
| crash after the write, before the index update | record on disk; replay finds it — same as v1 |

The distinction v1 could never make — **torn write vs bit rot** — is now detectable (short frame vs CRC mismatch). The POC policy treats both as "the valid prefix ends here"; scrubbing, quarantining, and repair are deliberately later.

## 4. Design options

### 4.1 How deletion is encoded

- **Option A — explicit kind byte (recommended).** One byte, CRC-covered, self-describing; mirrors the v1 `Kind` field and the tombstone lesson. `Delete` needs no old record to rewrite.
- Option B — `docLen == 0` means deleted. Saves a byte; makes every empty document impossible forever and reads as a convention, not a declaration.
- Option C — carry the v1 sentinel into v2. Wastes bytes and keeps the wart the format exists to remove.

**Recommendation: A.** The tests pin the byte: `kind` offset 7, `0`/`1`; a DELETE frame byte-exactly has `docLen` 0 and no document.

### 4.2 Where magic/version live

- **Option A — in every frame (recommended).** Three bytes per record buys: sniffing the format anywhere, frames that are self-describing, and no bootstrapping problem when the very first frame is torn (an empty/missing file header would be ambiguous).
- Option B — once per file (a 16-byte file header). Compact records; but a torn first write leaves a header that may or may not be there, and every frame's validity depends on a single earlier artifact.

**Recommendation: A**, as sketched in docs/000 §4.2.

### 4.3 Where CRC32 comes from

- **Option A — `System.IO.Hashing.Crc32` (recommended).** Microsoft's first-party package implementing exactly CRC-32/ISO-HDLC; docs/000's appendix already named it as step 4's tool. The engine gains its second package (still web-free).
- Option B — hand-rolled table-driven CRC32. ~20 lines, zero dependencies, and a nice small exercise (reflected polynomial `0xEDB88320`, init/xor `0xFFFFFFFF`). Only worth it if that exercise is the point.

The tests implement the format **independently** either way (own encoder/parser and CRC in `FrameBytes.cs`), so the choice is invisible to the contract — except that the golden-frame bytes in `DiskFormatTests` would catch a nonstandard variant immediately.

### 4.4 Policy for an invalid frame on read

- **Option A — stop replay at the first invalid frame and truncate the tail (recommended).** One rule for torn writes and bit rot; keeps "append at the end" truthful; keeps the valid-prefix invariant true on disk. Cost: bytes after mid-file corruption are dropped rather than merely unread (documented; loud log).
- Option B — stop but keep the bytes. Same reads, but appends land behind garbage and vanish on the next replay — strictly worse than A.
- Option C — resync by scanning for the magic. Works often; false-positives inside documents make it heuristic, and heuristics are what this step is leaving behind.
- Option D — v1-style skip-and-continue. Needs a delimiter to skip on; binary frames do not have one.

**Recommendation: A.** The v1 reader keeps its own locked skip-and-continue rule, but only for reading v1 files during migration (§5). One exception, handled before replay: a file that *starts* with the magic but names an unknown version is refused outright (§4.7) — at offset 0 an unknown revision may be a future one, and truncating bytes we did not write as if they were corruption would be data destruction, not recovery.

### 4.5 The v1→v2 migration trigger

- **Option A — automatic on open (recommended).** The constructor sniffs the first byte; a v1 file is converted in place before the first operation. No flag, no method, no API change; docs/000 §4.6 sketches exactly this. One-way.
- Option B — an explicit `Migrate()` / CLI action. More control; more state ("did I migrate?") for a pet project to carry.
- Option C — read both formats forever. No migration day, but two readers and two shapes live in every later step.

**Recommendation: A.** Crash safety is the same atomic-rename dance as compaction (§5), so "automatic" costs no extra correctness machinery.

### 4.6 What migration carries over

- **Option A — every parseable record: dead puts and tombstones included (recommended).** Migration is an *encoding* change; information is not lost. It streams (no index needed), and it stays conceptually separate from compaction, whose whole job is dropping dead records.
- Option B — live records only. A "free compaction" during migration; smaller file; but it silently merges two lessons, and it needs the old index while converting.

**Recommendation: A.** Tests pin it: a v1 log with a dead put and a tombstone migrates to four frames, then `Compact()` reduces them to one.

### 4.7 Sniffing the format

| First bytes | Verdict |
|---|---|
| (empty file) | v2; nothing to read or convert |
| `{` (`0x7B`) | v1 JSONL → migrate (§5) |
| `"CD"` + version `2` | v2 — validate and recover from frame 0 (§3) |
| a lone `0x43`, or `"CD"` with fewer than 3 bytes | v2 with a torn first frame — the same handling as any torn tail (the valid prefix is empty) |
| `"CD"` + any other version | **unknown format revision** — `InvalidDataException`, bytes untouched: it may be from the future, and truncating a revision we did not write would be data destruction |
| anything else | **not a CrowDB log** — `InvalidDataException`, bytes untouched |

The last two rows are the magic number and version byte doing their job (docs/000 §4.2): refuse cleanly instead of parsing garbage — and, importantly, never truncate a file that is not ours (or a revision we do not know).

### 4.8 Length guards

- **docLen ≤ 16 MiB** (docs/000 §4.2's "length sanity cap"): a corrupt header must never make the reader allocate four gigabytes. Enforced on read (§3.2) *and* on write — the writer refusing a document the reader would refuse keeps "no frame nobody can read" true.
- **keyLen** is bounded by the u16 it lives in; the writer refuses ids whose UTF-8 form exceeds 65535 bytes (silent truncation by `WriteUInt16` would corrupt a frame).

## 5. The migration, step by step

Holding the store lock (exactly like `Compact`, docs/003 §2):

1. Sniff says v1. Create `<log>.migrate` in the log's directory (same filesystem — the rename will be atomic), `FileMode.Create`.
2. Read the v1 file **with the frozen v1 reader**: every parseable record, in order — dead puts and tombstones included (§4.6), unparseable lines skipped ("skip and continue" — the locked v1 rule).
3. Write each record as a v2 frame (tombstones become `kind` DELETE, `docLen` 0) and `Flush(true)` — contents durable before they can replace the log.
4. Close the temp; **close the log's own handle**.
5. `File.Move(tmp, log, overwrite: true)` — one atomic rename.
6. Continue with the normal v2 open path: replay the migrated file, rebuild the index, open the handle. Replay of the migrated log must yield exactly the index the v1 log yielded — same invariant as compaction (docs/003 §2).

Crash windows (each boring, because the v1 log is untouched until the single rename):

| Crash… | On disk afterwards | Recovery verdict |
|---|---|---|
| during 3 (half-written temp) | partial `<log>.migrate` beside an untouched v1 log | temp is inert; the next open migrates again, overwriting it |
| after 3, before 5 | complete temp; log still v1 | same: v1 is the truth until the rename |
| during 5 | atomic: the path names the old v1 log or the complete new one | both are valid logs for their reader |
| after 5, before 6 | v2 log on disk; RAM state dies | startup replay (or the migration path itself re-checking the sniff) reads it as v2 |
| after 6 | done | — |

Same honest caveat as compaction (docs/003 §3): contents are fsynced and the rename is atomic, but the rename's own durability is the OS's call — after a power cut you get either the whole v1 log or the whole v2 one.

## 6. What it changes (implementation checklist)

- **`Record` / codec:** binary frame encode/decode (magic, version, kind, lengths, CRC32); `Kind` becomes a byte; the document becomes raw `byte[]`/`ReadOnlyMemory<byte>` — `Doc` stops being a string; the `--DELETED--` sentinel leaves the write path.
- **v1 reader (frozen, `FileStreamExtensions`):** still needed — for migration only. Skip-and-continue stays its rule.
- **Reads (`ReadRecordSegment` → frame reader):** header-first reads with validation; no newline scanning.
- **`IdsIndex` / recovery:** scan by frames; validate; stop at the first invalid frame; truncate the file there; rebuild the index.
- **`Store` constructor:** sniff → (migrate) → replay+truncate → open. Same one lock; step 6 still owns lock-free reads.
- **`Store.Delete`:** write the DELETE frame by id — no read of the old record.
- **`Store.Compact`:** unchanged algorithm; it copies frame segments verbatim (the declared length makes the segment obvious) and rebuilds by replay, exactly as today.
- **`Constants`:** magic bytes, version `2`, kinds, caps, the `.migrate` suffix.
- **`CrowDB.Engine.csproj`:** `System.IO.Hashing` (*if* §4.3's recommendation stands — the engine's second package).
- **Logging:** one line per migration (records converted, bytes before/after) and a warning per truncation (offset, bytes dropped).
- **Server / portal:** nothing. The API surface is identical; migration is invisible over HTTP.

## 7. The contract (written first — red until implemented)

| File | Cases |
|---|---|
| `DiskFormatTests` (engine, rewritten) | two puts → two frames, offset 0, no separators; **golden bytes** for a known PUT and a known DELETE (layout, endianness, CRC variant); tombstone = kind DELETE + docLen 0, sentinel gone; appends only (dead frame stays); documents that mimic framing (magic bytes, newlines, invalid UTF-8) round-trip byte-exact; hand-written v2 log replays; doc/key caps rejected on write; compaction output is exactly the live frames in log order |
| `BinaryFormatTests` (engine, new) | CRC check value; unknown version mid-log → stop+truncate; unknown version at byte 0 → refused, bytes untouched; unknown kind → stop (CRC kept honest); DELETE with a document → invalid; bit flip in newest frame → CRC catches it, previous value survives, file truncated; bit flip mid-log → later records not visible, truncated at the bad frame, store still writable; truncated final frame → valid prefix kept; absurd `docLen` → rejected, not trusted; torn first write (frame prefix, lone magic byte) → empty store, truncation makes byte 0 writable again; a foreign file → `InvalidDataException`, bytes untouched |
| `MigrationTests` (engine, new) | v1 log → readable + file is v2 + no v1 text + one frame per record; migration carries dead puts and tombstones (≠ compaction) then `Compact()` reclaims them; second open is byte-identical (idempotent); unicode/escaped documents migrate byte-exact; torn v1 tail dropped; unparseable middle line skipped (v1 rule) and replay continues; leftover `.migrate` overwritten/consumed; new writes land as frames after migrated ones; a v1 file with no parseable record becomes empty + writable; hand-written v1 log with loose whitespace/member order migrates |
| `RecoveryTests` (engine, updated) | torn-tail simulations are now real torn v2 frames; the existing promises (records before it readable, later writes survive, torn tombstone deletes nothing, newline ids) unchanged |
| `CompactionTests` (engine, updated) | the same step-3 promises, inspected as frames: dead frames gone, log order kept, tombstone frames gone, temp leftovers inert; torn tail is reclaimed by recovery at open, then compaction sees only frames |
| `MigrationApiTests` (API, new) | a v1 log with dead records, a tombstone, and a torn tail serves the right documents; after a server runs over a v1 log, the file on disk is v2 and a fresh server over it serves the same data and appends new frames |
| `DocsApiTests` (API, comments) | the two "log from a previous run" tests now name the migration they exercise |

## 8. Decisions you will meet

- **Torn write vs bit rot: now distinguishable, deliberately treated the same.** A short frame is a crash; a CRC mismatch is corruption. Both end the valid prefix; the log line says which. Repair/scrubbing policy is later.
- **The tail is the only place writes go** — truncation at open is what keeps "append at the end" honest after a crash (this is the step-1 warning, now enforced by the format).
- **Reading got stricter.** v1 guessed; v2 refuses. Fail closed: a frame the writer cannot produce is never interpreted.
- **Nothing about placement, retrieval, mutation, or the index changes** — same log, same offsets, same one lock. Only the record encoding does.
- **Migration is one-way and in place.** After the first open of a v1 directory, that directory is v2 forever; there is no downgrade.
- **`cat` is over; hex is in.** `xxd data/crowdb.log` is the new debugging tool — and the golden-frame tests are the readable spec.

## 9. Done when

- `dotnet build` / `dotnet test` green — both suites, including the new cases (red today).
- A hand-written v1 `data/crowdb.log` boots the server: same documents served, and `xxd` shows magic `43 44` at offset 0 afterwards; a restart keeps serving; overwrites and deletes after the migration land as frames.
- `README.md` / `AGENTS.md` updated; this doc flips to *done* with an as-built note if reality diverges.

**Out of scope:** bit-rot scrubbing/quarantine/repair, truncation *reporting* beyond a log line, hint files / checkpoints, compression, per-record TTL, segmented logs, directory fsync, downgrade v2→v1, lock-free reads (step 6), group commit (step 7), collections/queries (8+).
