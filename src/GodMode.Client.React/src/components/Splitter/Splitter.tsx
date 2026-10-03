import { useEffect, useRef, type KeyboardEvent, type PointerEvent } from 'react';
import './Splitter.css';

/** An arrow key's step, and Shift's. */
const STEP = 16;
const BIG_STEP = 64;

interface Props {
  /** The width of the pane on its left, as laid out. */
  value: number;
  min: number;
  max: number;
  /** A width asked for, which the owner fits to its bounds. */
  onResize: (px: number) => void;
  /** Back to the default width: a double-click. */
  onReset: () => void;
  label: string;
  /** The id of the pane it sizes. */
  controls?: string;
}

/**
 * A vertical splitter on a pane's right edge (#436): drag it, or focus it and use the arrow keys (Shift for bigger
 * steps), Home and End; a double-click resets the pane.
 */
export function Splitter({ value, min, max, onResize, onReset, label, controls }: Props) {
  // The drag in progress's teardown: unmounting mid-drag ends it
  const endDrag = useRef<(() => void) | null>(null);
  useEffect(() => () => endDrag.current?.(), []);

  const onPointerDown = (e: PointerEvent<HTMLDivElement>) => {
    if (e.button !== 0) return;
    // No text selection, and the focus stays where it was (the composer)
    e.preventDefault();
    endDrag.current?.();
    const startX = e.clientX;
    const startWidth = value;
    const move = (ev: globalThis.PointerEvent) => onResize(startWidth + ev.clientX - startX);
    const end = () => {
      window.removeEventListener('pointermove', move);
      window.removeEventListener('pointerup', end);
      window.removeEventListener('pointercancel', end);
      document.body.classList.remove('splitter-dragging');
      endDrag.current = null;
    };
    window.addEventListener('pointermove', move);
    window.addEventListener('pointerup', end);
    window.addEventListener('pointercancel', end);
    document.body.classList.add('splitter-dragging');
    endDrag.current = end;
  };

  const onKeyDown = (e: KeyboardEvent<HTMLDivElement>) => {
    const step = e.shiftKey ? BIG_STEP : STEP;
    const next = e.key === 'ArrowLeft' ? value - step
      : e.key === 'ArrowRight' ? value + step
      : e.key === 'Home' ? min
      : e.key === 'End' ? max
      : null;
    if (next === null) return;
    e.preventDefault();
    onResize(next);
  };

  return (
    <div
      className="splitter"
      role="separator"
      aria-orientation="vertical"
      aria-label={label}
      aria-controls={controls}
      aria-valuenow={value}
      aria-valuemin={min}
      aria-valuemax={max}
      tabIndex={0}
      title={`${label}: drag, or the arrow keys. Double-click resets it.`}
      onPointerDown={onPointerDown}
      onKeyDown={onKeyDown}
      onDoubleClick={onReset}
    />
  );
}
