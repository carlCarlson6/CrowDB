# 008 — The console cares about your edits: a UX pass on the portal

*2026-10-18 · A portal concern, not a roadmap step — no engine changes, no HTTP changes, no new dependencies. Status: **as built** — `npx tsc --noEmit` and `npm run build` green, full CRUD smoken through the proxy, and the console driven end to end in headless Chrome (43 checks, §8).*

**Concept of this change:** the console keeps a **baseline** — the id and body the server last agreed to (a GET returned them, or a PUT/POST confirmed them) — and every editor state is derived from comparing against it. "Unsaved changes" stops being a hope and becomes a fact the UI can act on: a `modified` badge, a Revert button, a discard dialog, and the browser's own before-close warning. The rest of this pass serves the same goal — the console should not lose work, and should not hide controls behind one document at a time.

## 1. Why

Reading the console against its own usage, the friction was real:

1. **Edits vanished silently.** Opening another id, pressing *New document*, or re-opening the same document replaced the editor with no warning; closing the tab did the same.
2. **No dirty signal.** Nothing distinguished "this is what the log holds" from "these are unsaved bytes", so nothing could warn, revert, or even tell.
3. **Ids were unfindable.** `ListIds` is unordered (hash index, by design) and only grows; the only navigation was scrolling.
4. **The keyboard had no save.** ⌘/Ctrl+S opened the browser's *save page* dialog — the one save that never means anything here.
5. **Row actions required a load first.** Deleting an id meant opening it, then using the editor's Delete. Copying an id meant selecting text in a field.
6. **Dialogs were mouse-shaped.** Escape relied on focus happening to sit inside the backdrop; clicking dialog text could strand it. Tab could wander behind the backdrop.
7. **One layout.** A fixed 300 px list + editor grid was wrong on narrow windows, where a terminal usually shares the screen.

## 2. The baseline (safety)

- Option A — draft per id: keep unsaved editors keyed by id, restore on return. It never loses anything, but the editor stops showing what the *server* holds — the one thing a debug console exists to show — and the drafts outlive their usefulness. Rejected.
- **Option B — baseline + guard + revert (chosen).** The editor compares against the baseline; when an action would replace the editor (opening any id, including the open one; *New document*) and the baseline disagrees, a discard dialog asks *Keep editing* / *Discard changes*. Revert restores the baseline bytes without a request. `beforeunload` covers reload/tab close, where the browser shows its own dialog.
- Option C — autosave per keystroke (or on a debounce): every keystroke becomes a `PUT` — fsync'd, tombstoned-over, replayable — and the console stops teaching "a write is an event". Rejected.

**As built:** the baseline is `{ id, body } | null` (`null` = a new document never written). Dirty compares the **trimmed** draft id (Save trims too) and the body exactly; a new document counts as clean while it is still `{}` with an empty id. After a POST the baseline adopts the server-generated id, so Save then flips from Create to PUT. Revert is disabled while clean. Success statuses are unchanged.

## 3. Finding documents

The engine has no query yet (step 8), so the filter is **client-side**: a case-insensitive substring match over the id list, rendered only when there is at least one id, cleared with Escape. The hint keeps advertising the real order — `GET /docs · N id(s) · unordered (hash index)` — and adds the match count while filtering. Sorting client-side was considered and rejected *for now*: ordering is step 5's lesson, and the console shows the engine's order on purpose.

## 4. The keyboard

- **⌘/Ctrl+S** saves from anywhere on the page, textarea included; `preventDefault` stops the browser's save-page dialog. It is a no-op while a dialog owns the page or a request is in flight (the listener calls the latest `save` through a ref, so one subscription serves every render).
- **⌘/Ctrl+Enter** saves from the editor section.
- Both are advertised in the Save button's tooltip. No global shortcut for *New* — the browser will not give up ⌘N.

## 5. Row actions

- **copy** — `navigator.clipboard.writeText`; the button answers "copied" for 1.2 s; a clipboard failure (insecure context) lands in the status line, where the full id can still be copied from the field.
- **del** — opens the same tombstone confirmation as the editor's Delete.
- **Deleting is now precise.** The old editor's `confirmDelete` emptied the editor unconditionally; with list deletes that would wipe a document you were not deleting. Only when the deleted id *is* the open document does the editor reset.
- **Duplicate** was considered and left out: typing a new id and saving already writes a copy (PUT to a fresh id), and it keeps the console one gesture smaller.

The actions span appears on row hover or keyboard focus, and is always visible on touch screens (`@media (hover: none)`).

## 6. One dialog component

`app/modal.tsx` replaces the three hand-rolled backdrops. It: closes on backdrop click and on **Escape from anywhere** (a document-level listener — the old backdrop listener died the moment focus left the backdrop's subtree, e.g. after a click on dialog text); traps **Tab** within the dialog; takes focus on open when no child claims it via `autoFocus`; and **returns focus to the opener** on close. Callers keep their own title/body and pass the heading's id for `aria-labelledby`.

## 7. Layout & states

- **Narrow windows (≤ 760 px):** panels stack, the topbar wraps, the textarea keeps a usable height; verified by emulating 500 px and 1280 px widths in the headless pass.
- **Honest list states:** "Loading documents…" until the first `GET /docs` answers, "The list loads again once the API answers." while offline, "No documents yet…" only when that is true.
- **Retry** in the console's offline banner (the API-docs page already had one); a "Fetching the OpenAPI document…" line on the docs page.
- **Status line:** always present (`role="status"` so screen readers announce results), with reserved height so messages do not shift the footer; long messages wrap.
- **Focus visibility** unified: buttons and nav links get the same focus ring the fields had.

## 8. Verified how

No JS test harness exists and none is added (golden rule 7, docs/007 §6) — **(amended by docs/009:** one does now: Node's built-in test runner, one test file for the JSON reader; UI behavior is still smoke-checked, not browser-tested.) `npx tsc --noEmit` and `npm run build` are green. A smoke run — API on a throwaway data directory, portal `next start` on another port pointed at it via `CROWDB_API_URL` — proved pages, proxy, key gate and CRUD end to end. Then the console itself was driven in **headless Chrome over the DevTools protocol** (an ad-hoc script in a scratch directory, nothing committed): 43 passing checks against the built portal, covering the first-visit key dialog, the dirty/`modified` lifecycle, both guard paths (Keep editing / Discard changes), **⌘S**, dialog Escape/Tab/focus-return, copy/del row actions including their editor side effects, the POST and DELETE flows, and the 500 px stacking. Left as manual checks: the `beforeunload` dialog (browser-owned, needs real user activation) and pointer-hover reveals (headless has no hover).

## 9. What it changes

- **New:** `portal/app/modal.tsx`, this doc.
- **Changed:** `portal/app/page.tsx` (baseline/dirty/guard, filter, shortcuts, row actions, status line), `portal/app/globals.css` (row actions, `mini` buttons, `modified` chip, focus ring, narrow-window media query), `portal/app/api-docs/page.tsx` (loading line), `portal/README.md`, root `README.md`, `AGENTS.md`.
- **Unchanged:** `CrowDB.Engine`, `CrowDB.Server`, the HTTP contract, the on-disk format, the proxy route.

## 10. Decisions you will meet

- **Still no editor library** — no syntax highlighting, no schema hints; JSON is checked with one `JSON.parse` on save, and bytes are stored verbatim. **(Amended by docs/009:** the document field is CodeMirror 6 fed by a console-owned JSON reader — live diagnostics with positions, a lint strip, and byte-faithful Format; schema hints and schema validation still don't exist.)
- **Ids stay unsorted** in the console; the filter narrows without reordering.
- **The filter is case-insensitive and client-side** — the engine's `ListIds` is still the only list source.
- **`beforeunload` is the browser's dialog, not ours** — nothing custom can reliably replace it.
- **Unsaved changes are not drafts.** Drift from the baseline is transient by design; leave the editor and the console asks, not remembers.

**Out of scope:** syntax highlighting, autosave, multi-document tabs/drafts, sorting, server-side search (no query API until step 8), dark mode, a JS test harness, any new package.
