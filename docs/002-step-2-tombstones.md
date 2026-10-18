# 002 — Step 2: deletes → tombstones

*2026-10-16 · Follows [000 — Storage layout](000-storage-layout-options.md) §3.6 and closes out [001 — Step 1 plan](001-step-1-plan.md). Status: **done — implemented 2026-10-17**; the shipped record shape is an explicit `Kind` field, not the recommended null `Doc` — see [As built](#as-built-2026-10-17) at the end.*

**Concept of this step:** the **tombstone** — a record whose only job is to say "this id is deleted". It is the step-1 lesson seen from the other side: the log is the source of truth, so *forgetting* something is itself a write.

## 1. Why a delete must be a write

The obvious implementation — remove the id from the in-memory index and move on — is wrong, and the failure is worth spelling out: on the next startup, recovery replays the log, finds the put records for that id, and **resurrects the document**. RAM forgot; the log did not.

So a delete appends a **tombstone**: one more record, same envelope family, meaning "gone". Recovery treats it like any other record — the last one for an id wins — except that its effect is *remove the id from the index* instead of *point at me*. The whole semantics table falls out of that single mechanism, no extra rules:

| Records for an id, in order | After replay                                          |
|-----------------------------|-------------------------------------------------------|
| put → delete                | gone                                                  |
| put → delete → put          | back — the newest record is a put                     |
| delete → put → delete       | gone                                                  |
| delete (id never existed)   | still gone — a tombstone for an absent id is harmless |

Deletes are **operations, not states**: "deleted" is not a permanent flag on an id; it is simply the current winning record in a sequence. That is why a put after a delete just works — nothing needs to un-delete.

## 2. Engine surface

```csharp
public bool Delete(string id);
```

- `true` — the id was live; a tombstone was appended, fsynced, and the id left the index.
- `false` — the id was unknown (never written, or already deleted); **nothing is appended** — an operation with no effect does not need a record.

Why `bool` and not `void`: the server maps it straight to HTTP — `true` → `204 No Content`, `false` → `404 Not Found` — without a check-then-delete dance in the router, and the "did it exist?" answer stays inside the same lock as the write.

**API addition for this step:** `DELETE /docs/{id}` → `204` when the id was deleted, `404` when unknown. Deleting an already-deleted id is a `404`, same as any unknown id. This joins the endpoint list locked in 000 §7.

## 3. The record shape — options

The as-built envelope (001) is `{"Version":"1","Id":...,"Doc":...}`, one line per record. A tombstone needs a representation on disk. Three candidates:

### Option A — `"Doc":null` (recommended)

```json
{"Id":"user:1","Version":"1","Doc":null}
```

The put path can never produce this: `Doc` is always a JSON *string* (empty documents are rejected, and the HTTP layer rejects non-JSON bodies — a document that is the JSON literal `null` is stored as the string `"null"`, not as JSON null). So `Doc:null` is unambiguous, costs zero new fields, and reads clearly in `cat data/crowdb.log`: `"Doc":null` looks like "no document" because it is one. In code, `Record.Doc` becomes nullable and "tombstone" is `Doc is null`; the put-path constructor still never produces it.

- **Pros:** no new machinery; the log stays self-explanatory; nothing existing changes.
- **Cons:** meaning lives in a *convention* (a reserved JSON value), not a declared field — future features must remember `Doc:null` is spoken for.

### Option B — explicit `"Deleted":true`

```json
{"Id":"user:1","Version":"1","Doc":null,"Deleted":true}
```

What 000 §3.6 sketched (in that era's envelope): a dedicated marker field. Self-describing and greppable; no reserved value.

- **Pros:** declared, not implied; immune to any future `Doc` null-ness.
- **Cons:** a serialization policy to design (omit the field when false? then encoding becomes conditional), plus a conflict rule for the impossible-but-say-it line `"Doc":{...},"Deleted":true`. More machinery than the step needs.

### Option C — record kind

`{"Kind":"put",...}` / `{"Kind":"delete",...}` — the most explicit shape, and roughly where the binary v2 frame (step 4) will land anyway (a kind byte beside the version).

- **Pros:** scales to future record types (collections, metadata).
- **Cons:** reshapes *every* record, including all existing logs, for a two-state distinction. That reshuffle belongs to step 4's migration.

**Recommendation: Option A.** All three encode the same behavior; A is the smallest change that keeps every line explainable, and step 4's binary frame is the natural moment to introduce an explicit kind byte. If A feels too implicit, B is a five-minute change to `Record` and the tests — that call belongs to the owner. *The tests pin the shape: `DiskFormatTests` asserts a tombstone line is an envelope whose `Doc` is JSON null.*

## 4. What it changes (implementation checklist)

- **`Record`**: `Doc` becomes nullable; the put path is unchanged; there is a way to build a tombstone for an id.
- **`Store.Delete`**: the same write path as `Put` — encode tombstone → append at end → `Flush(true)` → remove from the index → return whether the id was live; under the same lock.
- **Replay (`IdsIndex`)**: last record per id still wins; a winning tombstone means the id is *absent* from the rebuilt index instead of present at that offset.
- **`Endpoints`**: add `DELETE /docs/{id}` → `204` / `404`.
- **Portal (optional)**: a delete button is a one-liner once the endpoint exists; not part of this step's contract.

## 5. The contract (written first — red until implemented)

| File                        | Added cases                                                                                                                                                                                            |
|-----------------------------|--------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| `DeleteTests` (engine, new) | delete → gone from reads and listing, returns `true`; unknown id → `false`; delete twice → `false`; put after delete revives; deleting one id leaves the others                                        |
| `RecoveryTests`             | a delete survives reopen; delete → put → reopen brings the document back; a **torn tombstone tail** leaves the id alive; an id containing a newline stays deleted after replay (escaping)              |
| `DiskFormatTests`           | a delete appends one `Doc:null` envelope line and keeps the dead put line; deleting an unknown id appends nothing; a hand-written log with a tombstone replays as deleted and replay continues past it |
| `DocsApiTests`              | `DELETE` → `204`, then GET `404` and absent from the list; unknown id → `404`; delete twice → `404`; delete → re-PUT revives; a log left behind by a previous run with a tombstone stays deleted       |

## 6. Decisions you will meet

- **Tombstones are forever — until compaction.** A delete appends bytes; it frees none. The dead put *and* the tombstone both stay in the log. Reclaiming them is compaction, step 3. This is expected, not a leak.
- **Delete of an unknown id appends nothing.** There is no state to contradict, so a record would only add noise. (A tombstone for an absent id would *replay* harmlessly — the rule is "remove if present" — which is a nice invariant to notice.)
- **Order is the clock.** Replay decides by position in the log, never by timestamps. Sequence is the one ordering every component agrees on.
- **The tail is still the only danger.** A crash can tear a tombstone exactly like a put; the valid-prefix rule is unchanged, and an unparseable tombstone must leave the id alive.

## 7. Done when

- `dotnet build` and `dotnet test` pass — both suites green (the new cases are red until `Delete` exists).
- `curl -X DELETE http://localhost:5217/docs/{id}` → 204, GET → 404, and the deletion survives a server restart.
- `README.md` / `AGENTS.md` status updated; this doc's status flips to done.

**Out of scope:** compaction and space reclamation (step 3), binary format + CRC32 (step 4), lock-free reads (step 6), group commit (step 7), soft-delete / undelete / retention semantics (not on the roadmap).

## As built (2026-10-17)

The tombstone works and both suites are green (38 engine + 15 API tests). The behavior contract (§1, §2, §5) is exactly as designed; the record shape landed differently from §3's recommendation:

- **Option C was chosen, with a sentinel:** every record carries a `Kind` field (`"PUT"` or `"DELETE"`), and a tombstone writes `"Doc":"--DELETED--"` instead of JSON null. A record without `Kind` deserializes as `PUT`, so logs written before tombstones still replay. The engine keys off `Kind`; the sentinel exists so a tombstone is readable in `cat data/crowdb.log`.
- **Tests adapted at the shape boundary only:** two assertions changed (`DiskFormatTests`' tombstone line and the `DocsApiTests` hand-written tombstone) to pin the `Kind` envelope. No behavioral case was weakened or removed — delete/recover/resurrect/torn-tail/escaping all still assert what §5 lists.
- **Verified end-to-end:** through the portal's rewrite — `DELETE /docs/{id}` → 204, GET → 404, delete → re-`PUT` revives, and tombstones replay as gone after a real server restart.
