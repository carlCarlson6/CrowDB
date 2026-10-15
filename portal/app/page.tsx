"use client";

import { useCallback, useEffect, useState } from "react";

// The portal talks to CrowDB.Server through its own origin: next.config.ts
// rewrites /api/* to http://localhost:5217/*. No CORS, no API URL here.
const API = "/api";

type Status = { ok: boolean; text: string };

export default function Console() {
  const [ids, setIds] = useState<string[]>([]);
  const [offline, setOffline] = useState<string | null>(null);
  const [openId, setOpenId] = useState<string | null>(null); // document currently loaded
  const [idDraft, setIdDraft] = useState(""); // id that Save will PUT to (empty = POST)
  const [body, setBody] = useState("{}\n");
  const [status, setStatus] = useState<Status | null>(null);
  const [busy, setBusy] = useState(false);

  const refresh = useCallback(async () => {
    try {
      const res = await fetch(`${API}/docs`, { cache: "no-store" });
      if (!res.ok) throw new Error(await describe(res));
      setIds((await res.json()) as string[]);
      setOffline(null);
    } catch (error) {
      setIds([]);
      setOffline(message(error));
    }
  }, []);

  useEffect(() => {
    void refresh();
  }, [refresh]);

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
      const res = await fetch(`${API}/docs/${encodeURIComponent(id)}`, {
        cache: "no-store",
      });
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
        const res = await fetch(`${API}/docs/${encodeURIComponent(id)}`, {
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
      const res = await fetch(`${API}/docs`, {
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

  return (
    <main className="console">
      <header className="topbar">
        <h1>
          CrowDB <span className="muted">debug console</span>
        </h1>
        <span className={offline ? "pill bad" : "pill good"}>
          {offline
            ? "API offline"
            : `connected — ${ids.length} document${ids.length === 1 ? "" : "s"}`}
        </span>
      </header>

      {offline !== null && (
        <p className="banner bad">
          Cannot reach the API: {offline} — is the server running?{" "}
          <code>dotnet run --project src/CrowDB.Server</code>
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
          </div>

          {ids.length === 0 && offline === null ? (
            <p className="muted">
              No documents yet — write one on the right and press Create.
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
            <span className="muted">{byteLength(body)} bytes</span>
            {status !== null && (
              <span className={status.ok ? "status good" : "status bad"}>
                {status.text}
              </span>
            )}
          </div>
        </section>
      </div>
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
