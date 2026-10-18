# 009 — The editor reads your JSON: live linting and honest formatting

*2026-10-18 · A portal concern, not a roadmap step — no engine changes, no HTTP changes. Status: **as built** — `npx tsc --noEmit`, `npm test` (45 cases) and `npm run build` green, and the editor driven end to end in headless Chrome (27 checks, §7).*

**Concept of this change:** the console now treats the editor's buffer as *input to be understood* instead of text to forward blindly. One console-owned JSON reader (`portal/app/json.ts`) answers "is this valid, and if not, where and why?" — and that one answer drives three things that used to disagree or stay silent: the editor's squiggles and gutter markers (through CodeMirror's lint extension), the position strip under the editor, and the Save gate. Formatting flows through the same reader, which re-emits *tokens* rather than reserializing values, so whitespace is the only thing it can ever change.

## 1. Why

The old editor was a textarea with one `JSON.parse` on save (docs/008 §10: "no editor library"). Reading it against its usage:

1. **Broken JSON was revealed late.** You learned the document didn't parse when Save refused it — after typing, not while typing.
2. **The message pointed nowhere.** `JSON.parse` gives offsets (`…at position 17`) in the shapes each browser prefers; Safari may give no position at all. "not valid JSON — Unexpected token '}'" doesn't say which line, and the editor didn't either.
3. **Pretty-printing could change the data.** Loading a document went through `JSON.parse` + `JSON.stringify`. That round trip is lossy in two exact places: integer-like keys are reordered (`{"2":…,"1":…}` lists `"1"` first) and numbers beyond double precision are rounded (`9007199254740993` becomes `…992`). In a console whose whole promise is "the editor shows the engine's bytes", that was a data-fidelity bug wearing a UX costume.
4. **Formatting didn't exist.** Paste compact JSON and it stayed compact unless you reindented by hand.

## 2. Options

- **Option A — keep the textarea, hand-roll a strip.** No dependencies; live linting with position and caret is genuinely doable. Rejected: line numbers, highlighting and undo were all still missing, and every one of them has a well-tested answer.
- **Option B — CodeMirror 6 with the console's own reader (chosen).** The editor supplies what a textarea cannot (line numbers, syntax highlighting, undo/redo, bracket matching and closing, search, folding, lint markers and tooltips); the reader stays the console's own so the diagnosis is browser-independent, the formatter is byte-faithful, and the test suite (§7) has pure logic to pin down.
- **Option C — CodeMirror with its bundled `jsonParseLinter`.** One extension less to write, but it re-introduces `JSON.parse`'s messages and positions (the exact problem in §1.2) and offers nothing for formatting. Rejected.

**Dependencies added** (exact pins, like Next and React): `codemirror` 6.0.2 (the standard `basicSetup` — line numbers, history and keymaps, highlighting, bracket matching, search, lint keymap), `@codemirror/lang-json` 6.0.2 (the JSON grammar and highlighting), `@codemirror/lint` 6.9.7 (the linter extension, gutter markers, tooltips), plus `@codemirror/state` 6.7.6 and `@codemirror/view` 6.43.14, which the component imports directly (precedence, the editor view) and which are therefore declared, not borrowed from the tree. This is the portal's first dependency beyond Next/React — golden rule 7's justification is docs/009 itself: an editor library chosen deliberately, narrowly, and pinned.

## 3. The reader (`portal/app/json.ts`)

A recursive-descent reader, one pass, first error wins:

- `lintJson(text)` returns either `{ ok: true, summary }` — `object with 3 properties`, `array with 5 items`, `string`, … — or `{ ok: false, error }` with a plain-English `message`, a 0-based `offset` (what an editor caret wants) and 1-based `line`/`column` (what a person reads).
- Messages teach in the console's tone: `expected ':' after the property name`, `trailing comma — JSON does not allow a comma before '}'`, `invalid escape '\x' — the valid ones are …`, `this '{' is never closed` (reported at the opening brace, not at the end of the document), `single quotes are not valid in JSON — use double quotes for strings`.
- **It is deliberately as strict as the server.** An empty or whitespace-only body gets `empty body — the server answers 400`. Nesting deeper than 64 levels is refused because System.Text.Json's default `MaxDepth` is 64 — the file would be rejected on write anyway, so the editor says so first. (One known gap: number *range* checks — `1e999` passes lint and still gets a 400 from the server. Documented here, not hidden.)
- `formatJson(text)` returns the re-indented text or `null`. It shares the reader's tokens; §4 explains why that matters.

## 4. Formatting without reinterpretation

The formatter never parses values into JavaScript. It re-emits the token stream the reader already validated: `{`/`[` break the line unless the container is empty, `,` breaks, `:` becomes `": "`, two-space indent, one trailing newline. Token text — string bodies, escape sequences, number spellings, key order — is copied byte-for-byte.

That is the difference from `JSON.stringify(JSON.parse(text), null, 2)`: no key reordering for integer-like names, no rounding of `9007199254740993`, `1e2` stays `1e2`. The file's tests pin exactly these cases as regressions.

**As built:** the **Format** button sits next to Revert, disabled while the buffer doesn't parse; formatting marks the document `modified` through the existing baseline model (docs/008) and writes nothing until Save. Loading a document goes through the same `formatJson` (falling back to the raw text if the reader refuses it — possible only for records that bypassed the server), which *fixes* the load-time corruption described in §1.3 rather than reproducing it with a nicer face.

## 5. The editor component (`portal/app/json-editor.tsx`)

React state stays the source of truth. `value` flows down (a load, Revert, Format — adopted only when it differs from the buffer, so typing doesn't bounce through two sources), every edit flows up through `onChange`, and the console's dirty/baseline math keeps operating on plain strings. The CodeMirror view is created once on mount; callbacks travel through refs so no keystroke rebuilds it.

Details worth knowing:

- **Linting** is `linter(…)` with a 250 ms delay (the library default, 750 ms, feels laggy next to typing) plus `lintGutter()`. The diagnostics come from `lintJson` on the full buffer — a console-sized document is a cheap pass; incremental parsing is the next step up, and not needed.
- **⌘/Ctrl+Enter collision.** CodeMirror's default keymap binds `Mod-Enter` to `insertBlankLine`, which would fight the console's save shortcut (docs/008). The editor consumes the key with a highest-precedence keymap and lets the event bubble to the page's save handler — one save path, no blank line, verified in the smoke (§7).
- The editing surface carries `aria-label` (the page's visible label text), `spellcheck="false"`, and `lineWrapping` — long documents and the narrow-window layout (docs/008) both depend on wrapping.
- `focusOffset(offset)` is the imperative handle behind "click the position to jump": it focuses the editor, selects the offending character (a bare caret at end-of-document), and scrolls it into view.

## 6. The strip and the Save gate (`portal/app/page.tsx`)

Under the editor, one calm line replaces silence:

- `valid JSON · object with 3 properties` in green, while it parses;
- `line 3, column 7 — expected ':' after the property name` in red while it doesn't, where the position is a button that jumps the caret to the problem. **No red border on the editor while typing** — half-written JSON is invalid for most keystrokes, and shouting at every one of them teaches people to ignore the shout.

Save fails fast with the same diagnosis (the status line repeats position and reason) and jumps the caret; nothing is sent. Save stays clickable rather than disabled — pressing it is a legitimate question, and the answer arrives in the editor, not as a dead button.

## 7. Tests: the portal's first harness

docs/008 said "no JS test harness exists and none is added". A JSON reader is exactly where that stops paying: it is pure logic with subtle invariants (positions, byte fidelity) that a browser smoke cannot pin down case by case. The owner chose **Node's built-in test runner** — zero new packages:

- `portal/app/json.test.ts`, run by `npm test` → `node --test`. Node 26 executes the TypeScript directly (type stripping, no flags); the test imports `./json.ts` with its real extension, which is why `tsconfig.json` gained `allowImportingTsExtensions` (allowed under `noEmit`).
- Test names follow the repo's `GIVEN_… WHEN_… THEN_…` convention. 45 cases: every message/position contract above, the formatter's fidelity cases and idempotence, the 64-level limit, the formatter's refusal on invalid input.
- The suite is the contract; the implementation (`app/json.ts`) was written against it, red first.

## 8. Verified how

`npx tsc --noEmit` and `npm run build` green (CodeMirror is import-safe under SSR; the editor is created in an effect). Then the built portal in headless Chrome over the DevTools protocol — API on a throwaway data directory, a seeded document (`{"2":"two","1":"one","big":9007199254740993}`, stored compact) — 27 checks, all passing: editor and gutter mount; the strip's position and reason for a syntax error; the gutter marker; Save gated with the same diagnosis; click-to-jump focusing the editor; Format indenting while preserving key order and the 20-digit number and lighting `modified`; POST; ⌘Enter saving *without* inserting a blank line; GET pretty-printing without touching bytes; the empty-body diagnosis; the discard dialog still guarding edits; no console errors. Screenshots reviewed for layout at 1280 px. The smoke script lived in a scratch directory and is not committed (docs/008 §8's pattern).

## 9. What it changes

- **New:** `portal/app/json.ts` (the reader/formatter), `portal/app/json.test.ts` (its contract), `portal/app/json-editor.tsx` (the CodeMirror component), this doc.
- **Changed:** `portal/app/page.tsx` (editor mount, lint strip, Format, Save gate, load-time format), `portal/app/globals.css` (editor host, lint strip, `goto` button; the textarea rules are gone), `portal/package.json` (CodeMirror pins, `npm test`), `portal/tsconfig.json` (`allowImportingTsExtensions`), both READMEs, `AGENTS.md`, and docs/008's §8/§10 notes (amended, below).
- **Unchanged:** `CrowDB.Engine`, `CrowDB.Server`, the HTTP contract, the on-disk format, the proxy route, the bytes the console stores.

## 10. Decisions you will meet

- **An editor library, deliberately.** docs/008's "still no editor library" bullet is amended: CodeMirror 6 with exact pins, only for the JSON document field, with the console's own reader as the diagnostic source. The dependencies are the payment for gutter markers, highlighting, undo, bracket matching and search.
- **The portal has JS tests now.** docs/008's "no harness" line is amended: Node's built-in runner, no new packages, one test file for the one pure module. UI behavior is still verified by smoke, not by a browser test framework.
- **Format is explicit, never automatic.** Saving stores what you see, verbatim (the engine's contract); the console may re-indent for reading, but only the button — or an edit — changes what Save will write.
- **Byte fidelity is the feature.** The formatter changes whitespace and nothing else; loading cannot reorder keys or round numbers. If a future step wants canonicalization (sorted keys, one true spelling), that's a *new* decision with its own rationale.
- **The strip is quiet by design** — no live region, no red border while typing. Save's status line remains the announced outcome (`role="status"`).

**Out of scope:** JSON Schema validation or schema-aware completion, auto-format-on-save, canonicalizing output, Tab-to-indent (Tab still moves focus out, which stays accessible), sort/keys/minify actions, a browser test framework, any engine or HTTP change.
