"use client";

import Link from "next/link";
import { useCallback, useEffect, useState } from "react";

// The portal talks to CrowDB.Server through its own origin: the proxy route
// (app/api/[...path]/route.ts) forwards /api/* to CROWDB_API_URL, or to the
// local server when the variable is unset. No CORS, no API URL in client code.
const API = "/api";

/** Where the visitor's key lives — this browser only (docs/007). */
const KEY_STORAGE = "crowdb:api-key";

type Status = { ok: boolean; text: string };

/**
 * Thrown when a request can't proceed for want of a working key: none stored,
 * or the server answered 401. The console opens the key dialog; nothing about
 * this is "offline".
 */
class Unauthorized extends Error {}

export default function Console() {
  const [ids, setIds] = useState<string[]>([]);
  const [offline, setOffline] = useState<string | null>(null);
  const [openId, setOpenId] = useState<string | null>(null); // document currently loaded
  const [idDraft, setIdDraft] = useState(""); // id that Save will PUT to (empty = POST)
  const [body, setBody] = useState("{}\n");
  const [status, setStatus] = useState<Status | null>(null);
  const [busy, setBusy] = useState(false);
  const [confirmingDelete, setConfirmingDelete] = useState<string | null>(null); // id awaiting the modal's Delete

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

  async function open(id: string) {
    await run(async () => {
      const res = await call(`/docs/${encodeURIComponent(id)}`);
      if (!res.ok) throw new Error(await describe(res));
      const text = await res.text();
      setOpenId(id);
      setIdDraft(id);
      setBody(pretty(text));
      return `GET /docs/${id} → 200 (${byteLength(text)} bytes)`;
    });
  }

  function startNew() {
    setOpenId(null);
    setIdDraft("");
    setBody("{}\n");
    setStatus(null);
  }

  async function save() {
    const id = idDraft.trim();

    // Fail fast with a clear message; the server would reject it anyway.
    try {
      JSON.parse(body);
    } catch (error) {
      setStatus({ ok: false, text: `not valid JSON — ${message(error)}` });
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
        setOpenId(id);
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
      setOpenId(created.id);
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
    const id = idDraft.trim();
    if (id === "") return;
    setConfirmingDelete(id); // the custom modal decides from here
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
      startNew(); // the document is gone — render a fresh one
      await refresh();
      return `DELETE /docs/${id} → ${res.status} (tombstone appended)`;
    });
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
          <code>CrowDB__ApiKey=... dotnet run --project src/CrowDB.Server</code>
        </p>
      )}

      <div className="columns">
        <aside className="panel">
          <div className="row">
            <button onClick={() => void refresh()} disabled={busy}>
              Refresh
            </button>
            <button onClick={startNew} disabled={busy}>
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

          {ids.length === 0 && offline === null ? (
            <p className="muted">
              {apiKey === null
                ? "Enter the API key to load documents."
                : "No documents yet — write one on the right and press Create."}
            </p>
          ) : (
            <ul className="ids">
              {ids.map((id) => (
                <li key={id}>
                  <button
                    className={id === openId ? "id active" : "id"}
                    onClick={() => void open(id)}
                    disabled={busy}
                  >
                    {id}
                  </button>
                </li>
              ))}
            </ul>
          )}

          <p className="hint">
            GET /docs · {ids.length} id(s) · unordered (hash index)
          </p>
        </aside>

        <section className="panel editor">
          <label className="field">
            <span className="label">id</span>
            <input
              value={idDraft}
              onChange={(event) => setIdDraft(event.target.value)}
              placeholder="leave empty and the server generates one (POST)"
              spellCheck={false}
            />
          </label>

          <label className="field grow">
            <span className="label">document (JSON, stored verbatim)</span>
            <textarea
              value={body}
              onChange={(event) => setBody(event.target.value)}
              spellCheck={false}
            />
          </label>

          <div className="row">
            <button className="primary" onClick={() => void save()} disabled={busy}>
              {idDraft.trim() === "" ? "Create (POST)" : "Save (PUT)"}
            </button>
            <button
              className="danger"
              onClick={askDelete}
              disabled={busy || idDraft.trim() === ""}
              title="DELETE /docs/{id} — appends a tombstone"
            >
              Delete
            </button>
            <span className="muted">{byteLength(body)} bytes</span>
            {status !== null && (
              <span className={status.ok ? "status good" : "status bad"}>
                {status.text}
              </span>
            )}
          </div>
        </section>
      </div>

      {confirmingDelete !== null && (
        <div
          className="modal-backdrop"
          onClick={() => setConfirmingDelete(null)}
          onKeyDown={(event) => {
            if (event.key === "Escape") setConfirmingDelete(null);
          }}
        >
          <div
            className="modal"
            role="dialog"
            aria-modal="true"
            aria-labelledby="delete-title"
            onClick={(event) => event.stopPropagation()}
          >
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
          </div>
        </div>
      )}
      {askingForKey && (
        <div
          className="modal-backdrop"
          onClick={() => setAskingForKey(false)}
          onKeyDown={(event) => {
            if (event.key === "Escape") setAskingForKey(false);
          }}
        >
          <div
            className="modal"
            role="dialog"
            aria-modal="true"
            aria-labelledby="key-title"
            onClick={(event) => event.stopPropagation()}
          >
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
          </div>
        </div>
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

function pretty(text: string): string {
  try {
    return JSON.stringify(JSON.parse(text), null, 2) + "\n";
  } catch {
    return text;
  }
}

function byteLength(text: string): number {
  return new TextEncoder().encode(text).length;
}
