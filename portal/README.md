# CrowDB portal

A thin debug console for the CrowDB engine: list, create, edit and delete
documents, compact the log, and read the API reference. The **API docs**
section (`/api-docs`) renders the server's OpenAPI document as a plain
reference — methods, paths, summaries, parameters, request bodies, response
codes — fetched from `/openapi/v1.json` through the same `/api` proxy route, so
it always describes the running server. The interactive Scalar UI stays at the
server's own `/scalar`; no Scalar embed here. Minimal UI.

**Unsaved edits are protected (docs/008).** The console remembers the last
state the server agreed to: a `modified` badge appears while the editor
differs from it, opening another id or starting a new document asks first (a
discard dialog), and closing the tab is the browser's own warning. **Revert**
restores the last loaded or saved bytes without a request. Ids can be filtered
as you type; row actions (shown on hover or keyboard focus, always on touch)
copy an id or delete it straight from the list. **⌘/Ctrl+S** saves from
anywhere, **⌘/Ctrl+Enter** saves from the editor. Dialogs close on Escape and
keep Tab inside them.

**The editor reads your JSON (docs/009).** The document field is CodeMirror 6
(line numbers, syntax highlighting, undo), and a line under it always says what
the buffer is: `valid JSON · object with 3 properties`, or the first problem as
`line 3, column 7 — expected ':' after the property name` — where the position
is a button that jumps the caret onto the problem, and the editor gets a gutter
marker and a squiggle. The same check gates Save, so invalid JSON is never
sent. **Format** re-indents what you wrote, whitespace only — key order, number
spellings and escape sequences survive — and loading a document pretty-prints
through the same reader.

**The key (docs/007).** The server authenticates every request, so the console
asks for the key in a dialog on the first visit and keeps it in this browser
(`localStorage`); every request carries it as `X-Api-Key`. The dialog returns
with a note when the server answers `401` (wrong or rotated key), and the
**API key** button in the topbar changes it. The key never leaves the browser
except as a header — the portal's server proxies it and stores nothing. The
API docs section needs no key: the document itself is public.

## Run it

1. Start the API from the repo root with a key:
   `CrowDB__ApiKey=local-dev-key dotnet run --project src/CrowDB.Server` → http://localhost:5217
2. Start the portal: `npm install && npm run dev` → http://localhost:3000
3. Type `local-dev-key` into the console's key dialog.

The portal calls the API through the proxy route
(`app/api/[...path]/route.ts`), which forwards `/api/*` to `CROWDB_API_URL` —
unset locally, so the default is `http://localhost:5217`. No CORS setup, no
API URL in client code; a deployment sets the variable and the same build
points elsewhere. (A next.config rewrite would not: rewrites are baked into
the build.)

## Deploying (Railway)

A service from this repo with the **root directory set to `/portal`**; builder
Railpack detects Next.js (`npm ci`, `next build`, `next start`, injected
`PORT`). One variable:

```
CROWDB_API_URL=http://${{CrowDB.RAILWAY_PRIVATE_DOMAIN}}:8080
```

— the API service over Railway's private network (the service name is
case-sensitive; 8080 is the API's target port). No key lives in the portal's
environment; visitors paste it into the dialog. Full record:
[docs/007](../docs/007-portal-key-and-deployment.md).

## Test it

`npm test` runs the JSON reader's suite (`app/json.test.ts`) on Node's built-in
test runner — no test framework, no build step (Node executes the TypeScript
directly).

## Notes

- Plain `fetch`, no data-fetching library, no CSS framework — it is a debug
  console, not a product. The document editor is the one exception: CodeMirror
  6, exact-pinned (docs/009).
- The portal pretty-prints documents on load with a whitespace-only formatter;
  what you see in the editor is what gets stored when you save (the engine
  treats it as opaque bytes).
- The API docs section describes the API; it has no "try it" forms. For
  clickable requests, use the server's `/scalar` (the raw document is at
  `/openapi/v1.json`).
