"use client";

import { useEffect, useRef, type ReactNode } from "react";

/**
 * The console's one dialog (docs/008). A backdrop click or Escape closes it,
 * Tab cycles inside it instead of wandering onto the page behind, and closing
 * returns focus to whatever opened it — so no dialog can strand a keyboard.
 * Callers render their own title and body; `labelledBy` ties the dialog to
 * its heading for screen readers (the heading's id).
 */
export default function Modal({
  labelledBy,
  onClose,
  children,
}: {
  labelledBy: string;
  onClose: () => void;
  children: ReactNode;
}) {
  const dialog = useRef<HTMLDivElement>(null);

  // Who had focus before the dialog opened, captured during the first render:
  // the commit that mounts an autoFocus child moves focus before any effect
  // could observe the opener. Closing hands focus back to it.
  const opener = useRef<HTMLElement | null>(null);
  if (opener.current === null) {
    opener.current =
      typeof document === "undefined"
        ? null
        : document.activeElement instanceof HTMLElement
          ? document.activeElement
          : null;
  }

  // Focus on open: a child with autoFocus has it already (see above); only
  // when nothing holds focus does the dialog itself take it (tabIndex -1 lets
  // it). On close, the opener gets focus back.
  useEffect(() => {
    const node = dialog.current;
    if (
      node !== null &&
      (document.activeElement === null || document.activeElement === document.body)
    ) {
      node.focus();
    }
    return () => {
      const target = opener.current;
      if (target !== null && target !== document.body && target.isConnected) target.focus();
    };
  }, []);

  // Escape and the Tab trap listen on the document: focus may sit anywhere
  // after a click on plain text inside the dialog, and the keys must still work.
  useEffect(() => {
    function onKeyDown(event: KeyboardEvent) {
      if (event.key === "Escape") {
        onClose();
        return;
      }
      if (event.key !== "Tab") return;

      const node = dialog.current;
      if (node === null) return;
      const focusable = Array.from(
        node.querySelectorAll<HTMLElement>(
          'button:not(:disabled), input:not(:disabled), textarea:not(:disabled), select:not(:disabled), a[href], [tabindex]:not([tabindex="-1"])',
        ),
      );
      if (focusable.length === 0) {
        event.preventDefault();
        return;
      }

      const first = focusable[0];
      const last = focusable[focusable.length - 1];
      const active = document.activeElement;
      const outside = !node.contains(active);
      if (event.shiftKey && (active === first || outside)) {
        event.preventDefault();
        last.focus();
      } else if (!event.shiftKey && (active === last || outside)) {
        event.preventDefault();
        first.focus();
      }
    }

    document.addEventListener("keydown", onKeyDown);
    return () => document.removeEventListener("keydown", onKeyDown);
  }, [onClose]);

  return (
    <div className="modal-backdrop" onClick={onClose}>
      <div
        ref={dialog}
        className="modal"
        role="dialog"
        aria-modal="true"
        aria-labelledby={labelledBy}
        tabIndex={-1}
        onClick={(event) => event.stopPropagation()}
      >
        {children}
      </div>
    </div>
  );
}
