// The app's toasts, stacked at the foot of the screen: "Deleted · Undo" after a session-only delete, or what
// went wrong. `showToast` adds one beside any showing, so a second quick delete keeps the first one's Undo;
// <Toast /> (mounted once, in the Shell) renders them and ends each after its own time. The pattern of
// confirmDialog.ts: module state, read with useSyncExternalStore.

/** How long "Deleted · Undo" offers its undo. The server keeps the trash far longer (a day): this is only the offer. */
export const UNDO_WINDOW_MS = 10_000;

/** How long a toast with nothing to undo stays. */
export const MESSAGE_MS = 6_000;

export interface ToastAction {
  label: string;
  run: () => void | Promise<void>;
}

export interface ToastRequest {
  text: string;
  /** Undo, for one; the toast ends when it is tapped. */
  action?: ToastAction;
  tone?: 'info' | 'error';
  /** How long it shows; UNDO_WINDOW_MS with an action, else MESSAGE_MS. */
  durationMs?: number;
}

export interface OpenToast extends ToastRequest {
  id: number;
}

const NONE: readonly OpenToast[] = [];
let open: readonly OpenToast[] = NONE;
let ids = 0;
const listeners = new Set<() => void>();

function setOpen(next: readonly OpenToast[]) {
  open = next.length ? next : NONE;
  listeners.forEach(l => l());
}

export function subscribeToast(listener: () => void) {
  listeners.add(listener);
  return () => { listeners.delete(listener); };
}

/** The toasts showing, oldest first. */
export const getToasts = () => open;

/** Shows the toast below any showing. Returns its id, for dismissToast. */
export function showToast(request: ToastRequest): number {
  const id = ++ids;
  setOpen([...open, { ...request, id }]);
  return id;
}

/** Ends the toast with this id, if it still shows; with none, every one showing. */
export function dismissToast(id?: number) {
  if (id === undefined) { if (open.length) setOpen(NONE); return; }
  if (open.some(t => t.id === id)) setOpen(open.filter(t => t.id !== id));
}

export const durationOf = (toast: ToastRequest) => toast.durationMs ?? (toast.action ? UNDO_WINDOW_MS : MESSAGE_MS);
