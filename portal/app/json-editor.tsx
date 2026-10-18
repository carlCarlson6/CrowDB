"use client";

import { json } from "@codemirror/lang-json";
import { lintGutter, linter, type Diagnostic } from "@codemirror/lint";
import { Prec } from "@codemirror/state";
import { EditorView, keymap } from "@codemirror/view";
import { basicSetup } from "codemirror";
import { useEffect, useImperativeHandle, useRef, type Ref } from "react";
import { lintJson } from "./json";

export type JsonEditorHandle = {
  /** Focus the editor and put the caret on an offset, scrolling it into view. */
  focusOffset(offset: number): void;
};

/**
 * The document editor (docs/009): CodeMirror 6 supplies what a textarea
 * cannot — line numbers, syntax highlighting, undo, bracket matching — and
 * app/json.ts supplies the diagnostics, so a squiggle says exactly what Save
 * will enforce. The page stays the source of truth: `value` flows down (a
 * load, Revert, Format), every edit flows up through `onChange`, and the
 * console's dirty/baseline math (docs/008) keeps working on plain strings.
 */
export default function JsonEditor({
  value,
  label,
  onChange,
  ref,
}: {
  value: string;
  /** Accessible name for the editing surface (the visible label is the page's). */
  label: string;
  onChange: (next: string) => void;
  ref?: Ref<JsonEditorHandle>;
}) {
  const host = useRef<HTMLDivElement | null>(null);
  const view = useRef<EditorView | null>(null);

  // The view is built once, on mount, and lives outside React's render cycle;
  // refs keep the latest props reachable without rebuilding it per render.
  const onChangeRef = useRef(onChange);
  useEffect(() => {
    onChangeRef.current = onChange;
  });
  const initialValue = useRef(value);

  useEffect(() => {
    const parent = host.current;
    if (parent === null) return;

    const instance = new EditorView({
      parent,
      doc: initialValue.current,
      extensions: [
        basicSetup,
        json(),
        // The same reader Save uses. 250 ms after the last keystroke is
        // quick enough to feel live; the default 750 ms feels laggy.
        linter(diagnostics, { delay: 250 }),
        lintGutter(),
        // Cmd/Ctrl+Enter means Save in this console (docs/008). The default
        // keymap would insert a blank line instead, so consume the key here
        // (highest precedence) and let the event bubble to the page's save
        // handler — one save path for textarea-free and in-editor alike.
        Prec.highest(keymap.of([{ key: "Mod-Enter", run: () => true }])),
        // Long documents and narrow windows both happen here (docs/008).
        EditorView.lineWrapping,
        EditorView.contentAttributes.of({
          "aria-label": label,
          spellcheck: "false",
          autocapitalize: "off",
          autocomplete: "off",
        }),
        EditorView.updateListener.of((update) => {
          if (update.docChanged) onChangeRef.current(update.state.doc.toString());
        }),
      ],
    });
    view.current = instance;
    return () => {
      view.current = null;
      instance.destroy();
    };
    // Built once by design: `label` and the callbacks are stable, and the
    // value flows through the sync effect below.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  // Adopt external changes (load, Revert, Format). Skipped when the editor
  // already shows them — otherwise every keystroke would bounce back through
  // onChange and fight the user's cursor.
  useEffect(() => {
    const instance = view.current;
    if (instance === null) return;
    const current = instance.state.doc.toString();
    if (current !== value) {
      instance.dispatch({ changes: { from: 0, to: current.length, insert: value } });
    }
  }, [value]);

  useImperativeHandle(
    ref,
    () => ({
      focusOffset(offset) {
        const instance = view.current;
        if (instance === null) return;
        const length = instance.state.doc.length;
        const position = Math.max(0, Math.min(offset, length));
        instance.focus();
        // Select the offending character when there is one; a bare caret at
        // the end of the document otherwise.
        instance.dispatch({
          selection: { anchor: position, head: Math.min(position + 1, length) },
          scrollIntoView: true,
        });
      },
    }),
    [],
  );

  return <div className="editor-host" ref={host} />;
}

/**
 * app/json.ts answers with the first problem; CodeMirror wants a list. The
 * diagnostic covers the offending character (or nothing, at the end).
 */
function diagnostics(editor: EditorView): Diagnostic[] {
  const text = editor.state.doc.toString();
  const check = lintJson(text);
  if (check.ok) return [];
  const { message, line, column, offset } = check.error;
  const from = Math.min(offset, text.length);
  return [
    {
      from,
      to: Math.min(from + 1, text.length),
      severity: "error",
      message: `${message} (line ${line}, column ${column})`,
    },
  ];
}
