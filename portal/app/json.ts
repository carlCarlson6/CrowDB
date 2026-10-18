/**
 * A small JSON reader for the console's editor (docs/009).
 *
 * It does two jobs in one pass:
 *
 * - `lintJson` answers "is this valid, and if not, where and why?" — the
 *   editor shows the first problem with a line, a column and plain English
 *   (the platform's JSON.parse messages are not the same in every browser).
 * - `formatJson` re-indents a valid document by re-emitting its tokens, so
 *   only the whitespace *between* tokens changes: key order, number spellings
 *   (1e2, 20-digit integers) and escape sequences come out exactly as they
 *   went in. A JSON.parse/stringify round trip cannot promise that — it
 *   reorders integer-like keys and rounds numbers beyond double precision.
 *
 * The reader is deliberately strict, because the server is: an empty body or
 * invalid JSON is a 400, and System.Text.Json's default nesting limit is 64
 * levels. Catching those here saves a round trip and explains the problem in
 * the editor instead of in a status line.
 */

/** A problem with the text, positioned for both machines and people. */
export type JsonDiagnostic = {
  message: string;
  offset: number; // 0-based index into the string (what an editor caret wants)
  line: number; // 1-based line, for humans
  column: number; // 1-based column, for humans
};

export type JsonCheck =
  | { ok: true; summary: string } // e.g. "object with 3 properties"
  | { ok: false; error: JsonDiagnostic };

/** Mirrors the server's answer to an empty body (400, "must be a JSON document"). */
const EMPTY_MESSAGE = "empty body — the server answers 400 (a JSON document is required)";

/** System.Text.Json refuses nesting deeper than 64 by default, so writes must too. */
const MAX_DEPTH = 64;

class JsonSyntaxError extends Error {
  offset: number;

  constructor(message: string, offset: number) {
    super(message);
    this.offset = offset;
  }
}

/** The kind of the root value, plus its member count when it has one. */
type Scanned = { kind: "object" | "array" | "string" | "number" | "boolean" | "null"; count?: number };

export function lintJson(text: string): JsonCheck {
  if (text.trim() === "") {
    return { ok: false, error: { message: EMPTY_MESSAGE, offset: 0, line: 1, column: 1 } };
  }
  try {
    return { ok: true, summary: scan(text).summary };
  } catch (error) {
    if (!(error instanceof JsonSyntaxError)) throw error;
    return {
      ok: false,
      error: { message: error.message, offset: error.offset, ...positionAt(text, error.offset) },
    };
  }
}

/** Pretty-print a valid document, changing whitespace only. `null` when invalid. */
export function formatJson(text: string): string | null {
  if (text.trim() === "") return null;
  try {
    return render(scan(text).tokens);
  } catch (error) {
    if (error instanceof JsonSyntaxError) return null;
    throw error;
  }
}

/**
 * The one pass: a recursive-descent reader that either returns the root's
 * summary and its tokens, or throws JsonSyntaxError at the first problem.
 * Everything is validated as it is read, so "valid" means the server will
 * accept it (modulo its number-range checks).
 */
function scan(text: string): { summary: string; tokens: string[] } {
  let i = 0; // read head, a 0-based offset into text
  let depth = 0; // open containers, for the server's 64-level limit
  const tokens: string[] = []; // every token, raw, in order (the formatter's input)

  // The explicit function type is what lets the compiler treat `fail` as
  // never-returning: every `fail(...)` below ends the path it sits on.
  const fail: (message: string, offset?: number) => never = (message, offset = i) => {
    throw new JsonSyntaxError(message, offset);
  };

  function peekIs(character: string): boolean {
    return i < text.length && text[i] === character;
  }

  function skipWhitespace(): void {
    while (i < text.length && (text[i] === " " || text[i] === "\t" || text[i] === "\n" || text[i] === "\r")) {
      i++;
    }
  }

  function enterContainer(open: number): void {
    depth++;
    if (depth > MAX_DEPTH) {
      fail(`nesting is deeper than ${MAX_DEPTH} levels — the server's JSON reader rejects deeper documents`, open);
    }
  }

  function scanString(): void {
    const start = i; // at the opening quote
    i++;
    while (true) {
      if (i >= text.length) fail("this string is never closed — the closing double quote is missing", start);
      const character = text[i];
      if (character === '"') {
        i++;
        break;
      }
      if (character === "\\") {
        const escapeStart = i; // the backslash: the escape sequence starts here
        i++;
        if (i >= text.length) fail("unexpected end of document — an escape sequence is cut off", escapeStart);
        const escaped = text[i];
        if ('"\\/bfnrt'.includes(escaped)) {
          i++;
          continue;
        }
        if (escaped === "u") {
          for (let step = 1; step <= 4; step++) {
            const hex = text[i + step];
            if (hex === undefined) fail("unexpected end of document — a \\u escape needs four hex digits", escapeStart);
            if (!/[0-9a-fA-F]/.test(hex)) fail("invalid escape — '\\u' must be followed by four hex digits (0-9, a-f)", escapeStart);
          }
          i += 5;
          continue;
        }
        fail(`invalid escape '\\${displayChar(escaped)}' — the valid ones are \\" \\\\ \\/ \\b \\f \\n \\r \\t and \\uXXXX`, escapeStart);
      }
      if (character.charCodeAt(0) < 0x20) {
        fail("raw control character in a string — escape it (like \\n or \\u0000)", i);
      }
      i++;
    }
    tokens.push(text.slice(start, i));
  }

  function scanNumber(): void {
    const start = i;
    if (text[i] === "-") {
      i++;
      if (i >= text.length || !isDigit(text[i])) fail("invalid number — a minus sign must be followed by a digit", i);
    }
    if (text[i] === "0") {
      i++;
      if (i < text.length && isDigit(text[i])) fail("invalid number — leading zeros are not allowed (write 0, not 00)", i);
    } else {
      while (i < text.length && isDigit(text[i])) i++;
    }
    if (text[i] === ".") {
      i++;
      if (i >= text.length || !isDigit(text[i])) fail("invalid number — a '.' must be followed by a digit", i);
      while (i < text.length && isDigit(text[i])) i++;
    }
    if (text[i] === "e" || text[i] === "E") {
      i++;
      if (text[i] === "+" || text[i] === "-") i++;
      if (i >= text.length || !isDigit(text[i])) fail("invalid number — an exponent needs digits (like 1e10)", i);
      while (i < text.length && isDigit(text[i])) i++;
    }
    tokens.push(text.slice(start, i));
  }

  function scanLiteral(): void {
    const start = i;
    for (const word of ["true", "false", "null"]) {
      if (text.startsWith(word, start)) {
        i = start + word.length;
        tokens.push(word);
        return;
      }
    }
    fail("invalid literal — JSON has lowercase true, false and null", start);
  }

  function scanObject(): number {
    const open = i; // at '{'
    enterContainer(open);
    tokens.push("{");
    i++;
    skipWhitespace();
    if (peekIs("}")) {
      tokens.push("}");
      i++;
      depth--;
      return 0;
    }

    let count = 0;
    while (true) {
      skipWhitespace();
      if (i >= text.length) fail("this '{' is never closed", open);
      if (text[i] !== '"') fail("property names must be double-quoted strings", i);
      scanString();
      skipWhitespace();
      if (i >= text.length) fail("this '{' is never closed", open);
      if (text[i] !== ":") fail("expected ':' after the property name", i);
      tokens.push(":");
      i++;
      skipWhitespace();
      scanValue();
      count++;
      skipWhitespace();
      if (i >= text.length) fail("this '{' is never closed", open);
      if (text[i] === ",") {
        tokens.push(",");
        i++;
        skipWhitespace();
        if (peekIs("}")) fail("trailing comma — JSON does not allow a comma before '}'", i);
        continue;
      }
      if (text[i] === "}") {
        tokens.push("}");
        i++;
        depth--;
        return count;
      }
      fail("expected ',' or '}' after the property value", i);
    }
  }

  function scanArray(): number {
    const open = i; // at '['
    enterContainer(open);
    tokens.push("[");
    i++;
    skipWhitespace();
    if (peekIs("]")) {
      tokens.push("]");
      i++;
      depth--;
      return 0;
    }

    let count = 0;
    while (true) {
      skipWhitespace();
      if (i >= text.length) fail("this '[' is never closed", open);
      scanValue();
      count++;
      skipWhitespace();
      if (i >= text.length) fail("this '[' is never closed", open);
      if (text[i] === ",") {
        tokens.push(",");
        i++;
        skipWhitespace();
        if (peekIs("]")) fail("trailing comma — JSON does not allow a comma before ']'", i);
        continue;
      }
      if (text[i] === "]") {
        tokens.push("]");
        i++;
        depth--;
        return count;
      }
      fail("expected ',' or ']' after the array element", i);
    }
  }

  function scanValue(): Scanned {
    skipWhitespace();
    if (i >= text.length) fail("unexpected end of document — a value is missing", i);
    const character = text[i];
    if (character === "{") return { kind: "object", count: scanObject() };
    if (character === "[") return { kind: "array", count: scanArray() };
    if (character === '"') {
      scanString();
      return { kind: "string" };
    }
    if (character === "'") fail("single quotes are not valid in JSON — use double quotes for strings", i);
    if (character === "-" || isDigit(character)) {
      scanNumber();
      return { kind: "number" };
    }
    if (character === "t" || character === "f" || character === "n") {
      scanLiteral();
      return { kind: character === "n" ? "null" : "boolean" };
    }
    if (character === "\uFEFF") {
      fail(
        i === 0
          ? "the document starts with a byte-order mark (BOM) — delete it (the server rejects it)"
          : "a stray byte-order mark (BOM) sits here — delete it",
        i,
      );
    }
    fail(`unexpected character '${displayChar(character)}' — JSON values are objects, arrays, strings, numbers, true, false and null`, i);
  }

  const root = scanValue();
  skipWhitespace();
  if (i < text.length) fail("unexpected trailing content — the server stores one JSON document per request", i);
  return { summary: describe(root), tokens };
}

function describe(root: Scanned): string {
  if (root.kind === "object") return `object with ${root.count} ${root.count === 1 ? "property" : "properties"}`;
  if (root.kind === "array") return `array with ${root.count} ${root.count === 1 ? "item" : "items"}`;
  return root.kind;
}

function isDigit(character: string): boolean {
  return character >= "0" && character <= "9";
}

/** 1-based line and column of an offset (lines break on \n, like the server's reader reports). */
function positionAt(text: string, offset: number): { line: number; column: number } {
  const before = text.slice(0, offset);
  return { line: before.split("\n").length, column: offset - before.lastIndexOf("\n") };
}

/** Control characters would vanish in a message; show their \uXXXX form. */
function displayChar(character: string): string {
  const code = character.codePointAt(0) ?? 0;
  return code < 0x20 ? `\\u${code.toString(16).padStart(4, "0")}` : character;
}

/**
 * Re-emit tokens with 2-space indentation. Rules, in one place:
 * `{`/`[` break the line unless they are immediately closed; `,` breaks the
 * line; `:` becomes ": "; tokens themselves are never rewritten.
 */
function render(tokens: readonly string[]): string {
  let out = "";
  let depth = 0;
  for (let n = 0; n < tokens.length; n++) {
    const token = tokens[n];
    const previous = tokens[n - 1];
    const next = tokens[n + 1];
    if (token === "{" || token === "[") {
      out += token;
      if (next !== "}" && next !== "]") out += "\n" + "  ".repeat(++depth);
    } else if (token === "}" || token === "]") {
      if (previous !== "{" && previous !== "[") out += "\n" + "  ".repeat(--depth);
      out += token;
    } else if (token === ",") {
      out += ",\n" + "  ".repeat(depth);
    } else if (token === ":") {
      out += ": ";
    } else {
      out += token;
    }
  }
  return out + "\n";
}
