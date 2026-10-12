import { isTouchKeyboard } from './touchKeyboard';

// A field the user may be typing in, and a dialog over the page: neither loses the keys to a project opened (#559)
const EDITABLE = 'input, textarea, select, [contenteditable]:not([contenteditable="false"])';
const DIALOG = '.modal-overlay, dialog[open], [role="dialog"], [role="alertdialog"]';

/**
 * A project just opened (a click in the left list, an inbox item's Go to, a tile) puts the keys in its composer
 * (#559), so the user types at once. Not on a touch keyboard, which it would raise over the transcript; not while a
 * question's options take the keys (`keysTaken`, #240); and never from a field the user is typing in elsewhere (the
 * inbox's reply box, the create form) or while a dialog is open.
 */
export function focusOnOpen(input: HTMLTextAreaElement | null, keysTaken: boolean): void {
  if (!input || input.disabled || keysTaken || isTouchKeyboard()) return;
  const active = document.activeElement;
  if (active instanceof Element && active !== input && active.matches(EDITABLE)) return;
  if (document.querySelector(DIALOG)) return;
  input.focus({ preventScroll: true });
}
