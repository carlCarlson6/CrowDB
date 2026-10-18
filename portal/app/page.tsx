"use client";

import Link from "next/link";
import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import JsonEditor, { type JsonEditorHandle } from "./json-editor";
import { formatJson, lintJson } from "./json";
import Modal from "./modal";

// The portal talks to CrowDB.Server through its own origin: the proxy route
// (app/api/[...path]/route.ts) forwards /api/* to CROWDB_API_URL, or to the
// local server when the variable is unset. No CORS, no API URL in client code.
const API = "/api";

/** Where the visitor's key lives — this browser only (docs/007). */
const KEY_STORAGE = "crowdb:api-key";

/** What a brand-new document starts from. */
const BLANK_BODY = "{}\n";

/** The document field's visible label — and the editing surface's accessible name. */
const DOCUMENT_LABEL = "document (JSON, stored verbatim)";

type Status = { ok: boolean; text: string };

/**
 * The editor's last agreement with the server: the id and body a GET returned
 * or a PUT/POST confirmed (docs/008). `null` means the editor holds a new
 * document that was never written. Comparing the editor against this baseline
 * answers "are there unsaved changes?" without keeping a history.
 */
type Baseline = { id: string; body: string };

/**
 * An action that would replace the editor's contents, held back by the
 * discard dialog while the editor has unsaved changes.
 */
type PendingEdit = { action: "open"; id: string } | { action: "new" };

/**
 * Thrown when a request can't proceed for want of a working key: none stored,
 * or the server answered 401. The console opens the key dialog; nothing about
 * this is "offline".
 */
class Unauthorized extends Error {}

export default function Console() {
  // — the document list —
  const [ids, setIds] = useState<string[]>([]);
  const [idsLoaded, setIdsLoaded] = useState(false); // the first GET /docs has answered
  const [offline, setOffline] = useState<string | null>(null);
  const [filter, setFilter] = useState(""); // narrows the list, client-side only
  const [copiedId, setCopiedId] = useState<string | null>(null); // whose "copy" just succeeded

  // — the editor —
  const [baseline, setBaseline] = useState<Baseline | null>(null); // last state the server agreed to
  const [idDraft, setIdDraft] = useState(""); // id that Save will PUT to (empty = POST)
  const [body, setBody] = useState(BLANK_BODY);
  const [status, setStatus] = useState<Status | null>(null);
  const [busy, setBusy] = useState(false);
  const [confirmingDelete, setConfirmingDelete] = useState<string | null>(null); // id awaiting the modal's Delete
  const [pendingEdit, setPendingEdit] = useState<PendingEdit | null>(null); // action waiting behind the discard dialog
  const editor = useRef<JsonEditorHandle | null>(null); // bridge into the CodeMirror editor

  // The API key (docs/007): the server authenticates every request, so the
  // console asks for the key once, keeps it in localStorage, and sends it as
  // X-Api-Key. The portal's proxy forwards it but stores nothing.
  const [apiKey, setApiKey] = useState<string | null>(null);
  const [keyDraft, setKeyDraft] = useState("");
  const [askingForKey, setAskingForKey] = useState(false);
  const [keyRejected, setKeyRejected] = useState(false); // the server said 401 to the stored key
  const [loaded, setLoaded] = useState(false); // localStorage was read (after mount)

  useEffect(() => {
    let stored: string | null = null;
    try {
      stored = window.localStorage.getItem(KEY_STORAGE);
    } catch {
      // Storage can be unavailable (private windows): the key then lasts only
      // until this tab reloads, and the dialog asks again.
    }
    setApiKey(stored);
    setAskingForKey(stored === null); // first visit — ask before anything else
    setLoaded(true);
  }, []);

  // What the editor currently disagrees with the server about (docs/008).
  const activeId = baseline?.id ?? null; // the document the list marks
  const draftId = idDraft.trim();
  const dirty =
    baseline === null
      ? draftId !== "" || body !== BLANK_BODY
      : draftId !== baseline.id || body !== baseline.body;

  // What the buffer currently amounts to: recomputed on every keystroke, shown
  // as a strip under the editor, and enforced by Save — one source of truth
  // for squiggles and writes alike (docs/009).
  const lint = useMemo(() => lintJson(body), [body]);

  const needle = filter.trim().toLowerCase();
  const visibleIds = needle === "" ? ids : ids.filter((id) => id.toLowerCase().includes(needle));

  /** fetch, through the proxy route, with the visitor's key attached. */
  const call = useCallback(
    async (path: string, init?: RequestInit): Promise<Response> => {
      if (apiKey === null) {
        setAskingForKey(true);
        throw new Unauthorized("no API key yet — enter it to continue");
      }

      const headers = new Headers(init?.headers);
      headers.set("X-Api-Key", apiKey);
      const res = await fetch(`${API}${path}`, { ...init, headers, cache: "no-store" });

      if (res.status === 401) {
        // The stored key is wrong, or the server was restarted with another:
        // ask for it again instead of failing silently.
        setKeyRejected(true);
        setAskingForKey(true);
        throw new Unauthorized("HTTP 401 — the server rejected this key");
      }

      return res;
    },
    [apiKey],
  );

  const refresh = useCallback(async () => {
    try {
      const res = await call("/docs");
      if (!res.ok) throw new Error(await describe(res));
      setIds((await res.json()) as string[]);
      setIdsLoaded(true);
      setOffline(null);
    } catch (error) {
      setIds([]);
      setOffline(error instanceof Unauthorized ? null : message(error));
    }
  }, [call]);

  // Requests wait for a key: not read yet, ask; read, use it.
  useEffect(() => {
    if (apiKey !== null) void refresh();
  }, [apiKey, refresh]);

  // The edit loss the page cannot ask about itself is a reload or a tab close:
  // the browser shows its own dialog for that (docs/008).
  useEffect(() => {
    if (!dirty) return;
    function warn(event: BeforeUnloadEvent) {
      event.preventDefault();
    }
    window.addEventListener("beforeunload", warn);
    return () => window.removeEventListener("beforeunload", warn);
  }, [dirty]);

  // The "copied" feedback in the list resets itself.
  useEffect(() => {
    if (copiedId === null) return;
    const timer = window.setTimeout(() => setCopiedId(null), 1200);
    return () => window.clearTimeout(timer);
  }, [copiedId]);

  // Cmd/Ctrl+S writes the editor from anywhere, the textarea included — the
  // browser's "save page" dialog is never the save anyone means here. The ref
  // keeps one listener subscribed while always calling the latest save.
  const saveRef = useRef<() => Promise<void>>(async () => {});
  useEffect(() => {
    saveRef.current = save;
  });
  useEffect(() => {
    function onKeyDown(event: KeyboardEvent) {
      if ((event.metaKey || event.ctrlKey) && event.key.toLowerCase() === "s") {
        event.preventDefault();
        void saveRef.current();
      }
    }
    document.addEventListener("keydown", onKeyDown);
    return () => document.removeEventListener("keydown", onKeyDown);
  }, []);

  function askForKey() {
    setKeyDraft(apiKey ?? "");
    setKeyRejected(false);
    setAskingForKey(true);
  }

  function saveKey() {
    const key = keyDraft.trim();
    if (key === "") return;
    try {
      window.localStorage.setItem(KEY_STORAGE, key);
    } catch {
      // Not storable — see the read above.
    }
    setApiKey(key); // the effect above refetches with it
    setKeyRejected(false);
    setAskingForKey(false);
  }

  async function run(action: () => Promise<string>) {
    setBusy(true);
    try {
      setStatus({ ok: true, text: await action() });
    } catch (error) {
      setStatus({ ok: false, text: message(error) });
    } finally {
      setBusy(false);
    }
  }

  /** The editor as brand new: nothing loaded, nothing to compare against. */
  function resetEditor() {
    setBaseline(null);
    setIdDraft("");
    setBody(BLANK_BODY);
  }

  function startNew() {
    resetEditor();
    setStatus(null);
  }

  /** "New document" goes through the discard dialog while edits would be lost. */
  function requestNew() {
    if (dirty) {
      setPendingEdit({ action: "new" });
      return;
    }
    startNew();
  }

  /** GET a document into the editor — call it through requestOpen. */
  async function open(id: string) {
    await run(async () => {
      const res = await call(`/docs/${encodeURIComponent(id)}`);
      if (!res.ok) throw new Error(await describe(res));
      const text = await res.text();
      // Re-indent for reading without touching content: key order, number
      // spellings and escapes survive (docs/009). Text the reader refuses
      // (only possible from writes that bypassed the server) is shown as-is.
      const shown = formatJson(text) ?? text;
      setBaseline({ id, body: shown });
      setIdDraft(id);
      setBody(shown);
      return `GET /docs/${id} → 200 (${byteLength(text)} bytes)`;
    });
  }

  function requestOpen(id: string) {
    if (dirty) {
      setPendingEdit({ action: "open", id });
      return;
    }
    void open(id);
  }

  /** The discard dialog's "Discard changes": perform the action it held back. */
  function discardPendingEdit() {
    const pending = pendingEdit;
    setPendingEdit(null);
    if (pending === null) return;
    if (pending.action === "new") startNew();
    else void open(pending.id);
  }

  /** Put the editor back to its last server-agreed contents. */
  function revert() {
    if (baseline === null) {
      resetEditor();
      return;
    }
    setIdDraft(baseline.id);
    setBody(baseline.body);
  }

  /** Re-indent the buffer — whitespace only; nothing is written until Save. */
  function format() {
    const formatted = formatJson(body);
    if (formatted === null || formatted === body) return;
    setBody(formatted);
  }

  async function save() {
    // A shortcut can fire while a dialog owns the page or a request is in
    // flight: silently do nothing in those moments.
    if (busy || askingForKey || confirmingDelete !== null || pendingEdit !== null) return;

    const id = idDraft.trim();

    // Fail fast with the reader's own diagnosis — the same one the squiggles
    // show — and put the caret on the problem; the server would answer 400
    // anyway (docs/009).
    if (!lint.ok) {
      setStatus({
        ok: false,
        text: `not valid JSON — line ${lint.error.line}, column ${lint.error.column}: ${lint.error.message}`,
      });
      editor.current?.focusOffset(lint.error.offset);
      return;
    }

    if (id !== "") {
      await run(async () => {
        const res = await call(`/docs/${encodeURIComponent(id)}`, {
          method: "PUT",
          headers: { "content-type": "application/json" },
          body,
        });
        if (!res.ok) throw new Error(await describe(res));
        setBaseline({ id, body });
        setIdDraft(id);
        await refresh();
        return `PUT /docs/${id} → ${res.status} (${byteLength(body)} bytes, last write wins)`;
      });
      return;
    }

    await run(async () => {
      const res = await call("/docs", {
        method: "POST",
        headers: { "content-type": "application/json" },
        body,
      });
      if (!res.ok) throw new Error(await describe(res));
      const created = (await res.json()) as { id: string };
      setBaseline({ id: created.id, body });
      setIdDraft(created.id);
      await refresh();
      return `POST /docs → ${res.status}, server-generated id "${created.id}"`;
    });
  }

  async function compact() {
    await run(async () => {
      const res = await call("/compact", { method: "POST" });
      if (!res.ok) throw new Error(await describe(res));
      return `POST /compact → ${res.status} (dead puts and tombstones reclaimed)`;
    });
  }

  function askDelete() {
    if (draftId === "") return;
    setConfirmingDelete(draftId); // the custom modal decides from here
  }

  async function confirmDelete() {
    const id = confirmingDelete;
    if (id === null) return;
    setConfirmingDelete(null);

    await run(async () => {
      const res = await call(`/docs/${encodeURIComponent(id)}`, {
        method: "DELETE",
      });
      if (!res.ok) throw new Error(await describe(res));
      // Only when the id on screen is the one that vanished does the editor
      // need emptying; deleting another id from the list leaves it alone.
      if (baseline !== null && baseline.id === id) startNew();
      await refresh();
      return `DELETE /docs/${id} → ${res.status} (tombstone appended)`;
    });
  }

  /** The list's copy button: the id lands on the clipboard, briefly "copied". */
  async function copyId(id: string) {
    try {
      await navigator.clipboard.writeText(id);
      setCopiedId(id);
    } catch (error) {
      setStatus({ ok: false, text: `could not copy the id — ${message(error)}` });
    }
  }

  return (
    <main className="console">
      <header className="topbar">
        <h1>
          CrowDB <span className="muted">debug console</span>
        </h1>
        <nav className="nav">
          <Link href="/" className="active" aria-current="page">
            Console
          </Link>
          <Link href="/api-docs">API docs</Link>
        </nav>
        <button
          onClick={askForKey}
          title="Set the key sent as X-Api-Key; it stays in this browser (localStorage)"
        >
          API key
        </button>
        <span className={offline ? "pill bad" : apiKey !== null ? "pill good" : "pill"}>
          {offline
            ? "API offline"
            : !loaded
              ? "loading…"
              : apiKey === null
                ? "API key required"
                : `connected — ${ids.length} document${ids.length === 1 ? "" : "s"}`}
        </span>
      </header>

      {offline !== null && (
        <p className="banner bad">
          Cannot reach the API: {offline} — is the server running, and does it use
          the key from the dialog?{" "}
          <code>CrowDB__ApiKey=... dotnet run --project src/CrowDB.Server</code>{" "}
          <button onClick={() => void refresh()} disabled={busy}>
            Retry
          </button>
        </p>
      )}

      <div className="columns">
        <aside className="panel">
          <div className="row">
            <button onClick={() => void refresh()} disabled={busy}>
              Refresh
            </button>
            <button onClick={requestNew} disabled={busy}>
              New document
            </button>
            <button
              onClick={() => void compact()}
              disabled={busy}
              title="POST /compact — rewrites the log as only its live records"
            >
              Compact log
            </button>
          </div>

          {ids.length > 0 && (
            <input
              type="search"
              value={filter}
              onChange={(event) => setFilter(event.target.value)}
              onKeyDown={(event) => {
                if (event.key === "Escape") setFilter("");
              }}
              placeholder="filter ids…"
              aria-label="Filter ids"
              spellCheck={false}
            />
          )}

          {ids.length === 0 ? (
            <p className="muted">
              {apiKey === null
                ? "Enter the API key to load documents."
                : offline !== null
                  ? "The list loads again once the API answers."
                  : !idsLoaded
                    ? "Loading documents…"
                    : "No documents yet — write one on the right and press Create."}
            </p>
          ) : visibleIds.length === 0 ? (
            <p className="muted">No id matches “{filter.trim()}”.</p>
          ) : (
            <ul className="ids">
              {visibleIds.map((id) => (
                <li key={id} className={id === activeId ? "id-row active" : "id-row"}>
                  <button
                    className="id"
                    onClick={() => requestOpen(id)}
                    disabled={busy}
                    title={id}
                    aria-current={id === activeId ? "true" : undefined}
                  >
                    {id}
                  </button>
                  <span className="row-actions">
                    <button
                      className={copiedId === id ? "mini copied" : "mini"}
                      onClick={() => void copyId(id)}
                      disabled={busy}
                      title={`Copy the id "${id}" to the clipboard`}
                      aria-label={`Copy id ${id}`}
                    >
                      {copiedId === id ? "copied" : "copy"}
                    </button>
                    <button
                      className="mini danger"
                      onClick={() => setConfirmingDelete(id)}
                      disabled={busy}
                      title={`DELETE /docs/${id} — appends a tombstone`}
                      aria-label={`Delete document ${id}`}
                    >
                      del
                    </button>
                  </span>
                </li>
              ))}
            </ul>
          )}

          <p className="hint">
            GET /docs · {ids.length} id(s)
            {needle !== "" ? ` · ${visibleIds.length} shown` : ""} · unordered (hash
            index)
          </p>
        </aside>

        <section
          className="panel editor"
          onKeyDown={(event) => {
            if ((event.metaKey || event.ctrlKey) && event.key === "Enter") {
              event.preventDefault();
              void save();
            }
          }}
        >
          <label className="field">
            <span className="label">id</span>
            <input
              value={idDraft}
              onChange={(event) => setIdDraft(event.target.value)}
              placeholder="leave empty and the server generates one (POST)"
              spellCheck={false}
            />
          </label>

          <div className="field grow">
            <span className="label">{DOCUMENT_LABEL}</span>
            <JsonEditor ref={editor} value={body} label={DOCUMENT_LABEL} onChange={setBody} />
          </div>

          {lint.ok ? (
            <p className="lint good">valid JSON · {lint.summary}</p>
          ) : (
            <p className="lint bad">
              <button
                className="goto"
                onClick={() => editor.current?.focusOffset(lint.error.offset)}
                title="Put the cursor on the problem"
              >
                line {lint.error.line}, column {lint.error.column}
              </button>
              {" — "}
              {lint.error.message}
            </p>
          )}

          <div className="row">
            <button
              className="primary"
              onClick={() => void save()}
              disabled={busy}
              title={
                draftId === ""
                  ? "POST /docs — the server generates the id (⌘/Ctrl+S)"
                  : `PUT /docs/${draftId} (⌘/Ctrl+S)`
              }
            >
              {draftId === "" ? "Create (POST)" : "Save (PUT)"}
            </button>
            <button
              className="danger"
              onClick={askDelete}
              disabled={busy || draftId === ""}
              title="DELETE /docs/{id} — appends a tombstone"
            >
              Delete
            </button>
            <button
              onClick={revert}
              disabled={busy || !dirty}
              title="Undo unsaved edits — back to what the last load or save stored"
            >
              Revert
            </button>
            <button
              onClick={format}
              disabled={busy || !lint.ok}
              title="Re-indent with 2 spaces — whitespace only; Save (⌘/Ctrl+S) stores the bytes you see"
            >
              Format
            </button>
            <span className="muted">{byteLength(body)} bytes</span>
            {dirty && (
              <span
                className="pill warn"
                title="Not written to the log yet — Save (⌘/Ctrl+S) writes it, Revert discards it"
              >
                modified
              </span>
            )}
          </div>

          <p
            className={status === null ? "status" : status.ok ? "status good" : "status bad"}
            role="status"
          >
            {status?.text ?? ""}
          </p>
        </section>
      </div>

      {confirmingDelete !== null && (
        <Modal labelledBy="delete-title" onClose={() => setConfirmingDelete(null)}>
          <h2 id="delete-title">Delete this document?</h2>
          <p>
            The engine appends a <strong>tombstone</strong>: the old bytes stay in
            the log, but the id disappears from reads and listings.
          </p>
          <code>{confirmingDelete}</code>
          <div className="row">
            <button autoFocus onClick={() => setConfirmingDelete(null)}>
              Cancel
            </button>
            <button
              className="danger"
              onClick={() => void confirmDelete()}
              disabled={busy}
            >
              Delete (DELETE)
            </button>
          </div>
        </Modal>
      )}

      {pendingEdit !== null && (
        <Modal labelledBy="discard-title" onClose={() => setPendingEdit(null)}>
          <h2 id="discard-title">Discard unsaved changes?</h2>
          <p>
            This editor holds changes that were never written to the log.
            {pendingEdit.action === "open" ? (
              <>
                {" "}
                Opening <code>{pendingEdit.id}</code> would replace them.
              </>
            ) : (
              " Starting a new document would clear them."
            )}
          </p>
          <div className="row">
            <button autoFocus onClick={() => setPendingEdit(null)}>
              Keep editing
            </button>
            <button className="danger" onClick={discardPendingEdit}>
              Discard changes
            </button>
          </div>
        </Modal>
      )}

      {askingForKey && (
        <Modal labelledBy="key-title" onClose={() => setAskingForKey(false)}>
          <h2 id="key-title">API key</h2>
          <p>
            This server authenticates every request with the{" "}
            <code>X-Api-Key</code> header. The key stays in this browser
            (<code>localStorage</code>); the portal&apos;s proxy passes it to the
            API, nothing more.
          </p>
          {keyRejected && (
            <p className="status bad">
              The server rejected this key — check it and try again.
            </p>
          )}
          <label className="field">
            <span className="label">key</span>
            <input
              type="password"
              value={keyDraft}
              onChange={(event) => setKeyDraft(event.target.value)}
              onKeyDown={(event) => {
                if (event.key === "Enter") saveKey();
              }}
              placeholder="the deployment's CrowDB__ApiKey"
              spellCheck={false}
              autoFocus
            />
          </label>
          <div className="row">
            <button onClick={() => setAskingForKey(false)}>Cancel</button>
            <button
              className="primary"
              onClick={saveKey}
              disabled={keyDraft.trim() === ""}
            >
              Save key
            </button>
          </div>
        </Modal>
      )}
    </main>
  );
}

function message(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

async function describe(res: Response): Promise<string> {
  const text = (await res.text()).trim();
  const detail = text.length > 0 ? ` — ${text.slice(0, 300)}` : "";
  return `HTTP ${res.status} ${res.statusText}${detail}`;
}

function byteLength(text: string): number {
  return new TextEncoder().encode(text).length;
}
