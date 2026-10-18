// The contract for app/json.ts, run by Node's built-in test runner (docs/009):
// `npm test` from portal/ runs these against the TypeScript source directly —
// Node strips the types, no test framework. Names follow the repo's
// GIVEN_<setup>_WHEN_<action>_THEN_<outcome> convention.
import assert from "node:assert/strict";
import { test } from "node:test";
import { formatJson, lintJson } from "./json.ts";

function expectValid(text: string): string {
  const result = lintJson(text);
  assert.equal(result.ok, true, `expected valid JSON: ${JSON.stringify(text)}`);
  if (!result.ok) throw new Error("unreachable");
  return result.summary;
}

function expectError(text: string): { message: string; offset: number; line: number; column: number } {
  const result = lintJson(text);
  assert.equal(result.ok, false, `expected invalid JSON: ${JSON.stringify(text)}`);
  if (result.ok) throw new Error("unreachable");
  return result.error;
}

// — the happy path: whole documents, a summary of the root value —

const validDocuments: Array<[string, string]> = [
  ["{}", "object with 0 properties"],
  ["[]", "array with 0 items"],
  ['{ "a": 1, "b": [true, false, null] }', "object with 2 properties"],
  ["[1, 2, 3]", "array with 3 items"],
  ['"hi"', "string"],
  ["42", "number"],
  ["true", "boolean"],
  ["false", "boolean"],
  ["null", "null"],
  ["\n\t {}\r\n ", "object with 0 properties"],
  ["9007199254740993", "number"],
  ['{"nested":{"deep":[]}}', "object with 1 property"],
];

for (const [text, summary] of validDocuments) {
  test(`GIVEN ${JSON.stringify(text)} WHEN linted THEN it is valid (${summary})`, () => {
    assert.equal(expectValid(text), summary);
  });
}

// — the unhappy path: the first problem, with a position a person can use —

test("GIVEN an empty body WHEN linted THEN the error speaks of the server's 400", () => {
  const error = expectError("");
  assert.match(error.message, /empty/);
  assert.equal(error.offset, 0);
  assert.equal(error.line, 1);
  assert.equal(error.column, 1);
});

test("GIVEN a whitespace-only body WHEN linted THEN it is empty too", () => {
  assert.match(expectError("  \n\t ").message, /empty/);
});

test("GIVEN an unclosed object WHEN linted THEN the error points at its opening brace", () => {
  const error = expectError('{\n  "a": 1\n');
  assert.match(error.message, /never closed/);
  assert.equal(error.line, 1);
  assert.equal(error.column, 1);
});

test("GIVEN an unclosed array WHEN linted THEN the error points at its opening bracket", () => {
  const error = expectError("[1,\n  2");
  assert.match(error.message, /never closed/);
  assert.equal(error.offset, 0);
});

test("GIVEN a trailing comma in an object WHEN linted THEN the error points at the closing brace", () => {
  const error = expectError('{"a":1,}');
  assert.match(error.message, /trailing comma/);
  assert.equal(error.line, 1);
  assert.equal(error.column, 8);
});

test("GIVEN a trailing comma in an array WHEN linted THEN the error points at the closing bracket", () => {
  const error = expectError("[1,]");
  assert.match(error.message, /trailing comma/);
  assert.equal(error.column, 4);
});

test("GIVEN a property name without a colon WHEN linted THEN the error points at the value", () => {
  const error = expectError('{\n  "a": 1,\n  "b" 2\n}');
  assert.match(error.message, /':'/);
  assert.equal(error.line, 3);
  assert.equal(error.column, 7);
});

test("GIVEN an unquoted property name WHEN linted THEN the error asks for double quotes", () => {
  const error = expectError("{a:1}");
  assert.match(error.message, /double-quoted/);
  assert.equal(error.column, 2);
});

test("GIVEN a missing value WHEN linted THEN the error asks for a JSON value", () => {
  const error = expectError('{"a":}');
  assert.match(error.message, /value/);
  assert.equal(error.column, 6);
});

test("GIVEN a value cut off by the end of the document WHEN linted THEN the error says so", () => {
  const error = expectError('{"a":');
  assert.match(error.message, /end of document/);
});

test("GIVEN an unterminated string WHEN linted THEN the error points at its opening quote", () => {
  const error = expectError('{"a": "abc}');
  assert.match(error.message, /never closed/);
  assert.equal(error.column, 7);
});

test("GIVEN single-quoted text WHEN linted THEN the error suggests double quotes", () => {
  const error = expectError("'x'");
  assert.match(error.message, /double quotes/);
});

test("GIVEN an unknown escape WHEN linted THEN the error points at the backslash", () => {
  const error = expectError('{"a":"\\x"}');
  assert.match(error.message, /escape/);
  assert.equal(error.column, 7);
});

test("GIVEN a raw control character inside a string WHEN linted THEN the error asks for an escape", () => {
  assert.match(expectError('"a\nb"').message, /control/);
});

test("GIVEN a leading zero in a number WHEN linted THEN the error points at the second digit", () => {
  const error = expectError("01");
  assert.match(error.message, /number/);
  assert.equal(error.column, 2);
});

test("GIVEN a number with a dangling dot WHEN linted THEN the error says a digit is missing", () => {
  assert.match(expectError("1.").message, /digit/);
});

test("GIVEN a number with a dangling exponent WHEN linted THEN the error says digits are missing", () => {
  assert.match(expectError("1e+").message, /digits/);
});

test("GIVEN a misspelled literal WHEN linted THEN the error lists the real ones", () => {
  const error = expectError("tru");
  assert.match(error.message, /true, false and null/);
  assert.equal(error.column, 1);
});

test("GIVEN a plus sign before a number WHEN linted THEN the error calls it unexpected", () => {
  assert.match(expectError("+1").message, /unexpected/);
});

test("GIVEN two documents in one body WHEN linted THEN the error speaks of trailing content", () => {
  const error = expectError("{} {}");
  assert.match(error.message, /trailing content/);
  assert.equal(error.column, 4);
});

test("GIVEN an array element without a comma WHEN linted THEN the error asks for ',' or ']'", () => {
  assert.match(expectError("[1 2]").message, /',' or '\]'/);
});

test("GIVEN a byte-order mark WHEN linted THEN the error names it", () => {
  const error = expectError("\uFEFF{}");
  assert.match(error.message, /byte-order mark/);
  assert.equal(error.offset, 0);
});

test("GIVEN 64 levels of nesting WHEN linted THEN it is valid (the server's limit)", () => {
  assert.equal(expectValid("[".repeat(64) + "1" + "]".repeat(64)), "array with 1 item");
});

test("GIVEN 65 levels of nesting WHEN linted THEN the error names the server's limit", () => {
  const error = expectError("[".repeat(65) + "1" + "]".repeat(65));
  assert.match(error.message, /64 levels/);
  assert.equal(error.offset, 64);
});

// — formatting: whitespace only, everything else byte-for-byte —

test("GIVEN a compact object WHEN formatted THEN it is indented with 2 spaces", () => {
  assert.equal(formatJson('{"a":1,"b":[2,3]}'), '{\n  "a": 1,\n  "b": [\n    2,\n    3\n  ]\n}\n');
});

test("GIVEN integer-like keys WHEN formatted THEN the key order is preserved", () => {
  // JSON.parse + JSON.stringify would reorder these ("1" before "2").
  assert.equal(formatJson('{"2":1,"1":2}'), '{\n  "2": 1,\n  "1": 2\n}\n');
});

test("GIVEN a number beyond double precision WHEN formatted THEN its digits survive", () => {
  // JSON.parse would round 9007199254740993 to ...992.
  assert.equal(formatJson('{"n":9007199254740993}'), '{\n  "n": 9007199254740993\n}\n');
});

test("GIVEN exotic number spellings WHEN formatted THEN they are kept as written", () => {
  assert.equal(formatJson('{"e":1e2,"m":-0.5,"z":0}'), '{\n  "e": 1e2,\n  "m": -0.5,\n  "z": 0\n}\n');
});

test("GIVEN escapes in a string WHEN formatted THEN the raw escape text is kept", () => {
  const text = '{"s":"a\\"b\\u00e9\\\\n"}';
  assert.equal(formatJson(text), '{\n  "s": "a\\"b\\u00e9\\\\n"\n}\n');
});

test("GIVEN empty containers WHEN formatted THEN they stay on one line", () => {
  assert.equal(formatJson('{"a":{},"b":[]}'), '{\n  "a": {},\n  "b": []\n}\n');
});

test("GIVEN a formatted document WHEN formatted again THEN nothing changes", () => {
  const docs = ['{"a":1,"b":[2,3]}', '{"2":1,"1":2}', "[]", "{}", '[{"a":null}]'];
  for (const doc of docs) {
    const once = formatJson(doc);
    assert.notEqual(once, null);
    assert.equal(formatJson(once ?? ""), once);
  }
});

test("GIVEN valid JSON WHEN formatted THEN the parsed meaning is unchanged", () => {
  const text = '{"a":[1,{"b":"c"}],"d":true}';
  assert.deepEqual(JSON.parse(formatJson(text) ?? ""), JSON.parse(text));
});

test("GIVEN invalid JSON WHEN formatted THEN the formatter refuses", () => {
  assert.equal(formatJson("{"), null);
  assert.equal(formatJson(""), null);
  assert.equal(formatJson('{"a":1,}'), null);
});
