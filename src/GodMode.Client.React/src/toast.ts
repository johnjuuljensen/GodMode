// The app's one toast, at the foot of the screen: "Deleted · Undo" after a session-only delete, or what
// went wrong. `showToast` replaces the one showing; <Toast /> (mounted once, in the Shell) renders it
// and ends it after its time. The pattern of confirmDialog.ts: module state, read with useSyncExternalStore.

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

let open: OpenToast | null = null;
let ids = 0;
const listeners = new Set<() => void>();

function setOpen(next: OpenToast | null) {
  open = next;
  listeners.forEach(l => l());
}

export function subscribeToast(listener: () => void) {
  listeners.add(listener);
  return () => { listeners.delete(listener); };
}

export const getToast = () => open;

/** Shows the toast in place of any showing. Returns its id, for dismissToast. */
export function showToast(request: ToastRequest): number {
  const id = ++ids;
  setOpen({ ...request, id });
  return id;
}

/** Ends the toast with this id, if it is still the one showing; with none, whatever shows. */
export function dismissToast(id?: number) {
  if (open && (id === undefined || open.id === id)) setOpen(null);
}

export const durationOf = (toast: ToastRequest) => toast.durationMs ?? (toast.action ? UNDO_WINDOW_MS : MESSAGE_MS);
