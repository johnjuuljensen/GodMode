import { useCallback, useRef, type MouseEvent, type RefObject } from 'react';

// A press and release further apart than this is a drag (a selection, or one tried), not a click
const DRAG_PX = 4;
// What a click on is that control's: a link, a button (a tool call's or thinking's fold), a field, anything focusable.
// Virtuoso's scroller takes the focus (tabIndex 0) for its keys, and is the output itself
const OWN_CONTROLS = 'a[href], button, input, textarea, select, summary, label, [contenteditable], [tabindex]:not([data-virtuoso-scroller]), [role="button"], [role="link"]';

/**
 * Handlers for the output above the composer (#435): a plain mouse click there focuses the composer, so
 * typing goes on with no click on the input. The caret stays where it was.
 *
 * Not on a click that ends a selection or a drag (copying from the output keeps working), nor on a
 * click on a control of its own, nor while `keysTaken` (a question card whose options take keys, #240).
 * Only with a fine pointer: on a phone a tap in the output would raise the keyboard.
 */
export function useClickToCompose(inputRef: RefObject<HTMLTextAreaElement | null>, keysTaken: boolean) {
  const pressedAt = useRef<{ x: number; y: number } | null>(null);

  const onMouseDown = useCallback((e: MouseEvent) => {
    pressedAt.current = e.button === 0 ? { x: e.clientX, y: e.clientY } : null;
  }, []);

  const onClick = useCallback((e: MouseEvent) => {
    const pressed = pressedAt.current;
    pressedAt.current = null;
    const input = inputRef.current;
    if (!input || input.disabled || keysTaken || e.button !== 0) return;
    if (e.shiftKey || e.ctrlKey || e.metaKey || e.altKey) return;
    if (!window.matchMedia('(pointer: fine)').matches) return;
    if ((e.nativeEvent as Partial<PointerEvent>).pointerType === 'touch') return;
    if (pressed && Math.hypot(e.clientX - pressed.x, e.clientY - pressed.y) > DRAG_PX) return;
    if (e.target instanceof Element && e.target.closest(OWN_CONTROLS)) return;
    const selection = window.getSelection();
    if (selection && !selection.isCollapsed && selection.toString() !== '') return;
    input.focus({ preventScroll: true });
  }, [inputRef, keysTaken]);

  return { onMouseDown, onClick };
}
