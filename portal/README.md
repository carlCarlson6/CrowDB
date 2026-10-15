# CrowDB portal

A thin debug console for the CrowDB engine: list ids, open a document, edit and
save it (`PUT /docs/{id}`), or create one under a server-generated id
(`POST /docs`). No auth, localhost only, minimal UI.

## Run it

1. Start the API from the repo root: `dotnet run --project src/CrowDB.Server` → http://localhost:5217
2. Start the portal: `npm install && npm run dev` → http://localhost:3000

The portal calls the API through a same-origin rewrite configured in
`next.config.ts` (`/api/*` → `http://localhost:5217/*`), so there is no CORS
setup and no API URL in client code. If you change the server port, change that
rewrite too.

## Notes

- Plain `fetch`, no data-fetching library, no CSS framework — it is a debug
  console, not a product.
- The portal pretty-prints documents on load; what you see in the textarea is
  what gets stored (the engine treats it as opaque bytes).
- Deletes arrive with step 2 (tombstones) — there is deliberately no Delete
  button yet.
